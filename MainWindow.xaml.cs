using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace JpegZipToJxl;

public partial class MainWindow : Window
{
    /// <summary>
    /// ZIPのエントリ名にUTF-8フラグが無いときの解釈に使うエンコーディング。
    /// Windowsのエクスプローラなど多くのツールは、システム既定のANSIコードページで書き込む。
    /// UTF-8フラグが立っているエントリは、この指定に関わらずUTF-8として読まれる。
    /// </summary>
    private static readonly Encoding ZipEntryNameEncoding = CreateZipEntryNameEncoding();

    /// <summary>
    /// 同時に変換するファイル数。コア数を増やしても頭打ちになる一方で
    /// 画像1枚ぶんのメモリを並列数だけ抱えるため、上限を設ける。
    /// </summary>
    private static readonly int MaxParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8);

    private CancellationTokenSource? _cancellation;
    private bool _running;

    public MainWindow()
    {
        InitializeComponent();

        BuildPresetLabels();

        // ラベルを作ってから動かす。ValueChanged で選択中の表示が更新される。
        PresetSlider.Value = JxlPreset.DefaultIndex;

        VersionText.Text = $"v{AppVersion}   libjxl {JxlTranscoder.LibraryVersion}";
    }

    /// <summary>csproj の &lt;Version&gt; から埋め込まれたバージョン。</summary>
    private static string AppVersion
    {
        get
        {
            string? version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            // ビルド構成によっては "1.0.0+<commit hash>" の形になる。
            return version?.Split('+')[0] ?? "?";
        }
    }

    private JxlPreset SelectedPreset => JxlPreset.All[(int)PresetSlider.Value];

    /// <summary>スライダの目盛りに対応するプリセット名を並べる。</summary>
    private void BuildPresetLabels()
    {
        int count = JxlPreset.All.Count;

        for (int i = 0; i < count; i++)
        {
            bool isEnd = i == 0 || i == count - 1;

            // 両端の目盛りはトラックの端に来るため、端の列は中間の半分の幅にして
            // ラベルの位置を目盛りに合わせる。
            PresetLabels.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(isEnd ? 1 : 2, GridUnitType.Star)
            });

            var label = new TextBlock
            {
                Text = JxlPreset.All[i].Name,
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = i == 0 ? HorizontalAlignment.Left
                                    : i == count - 1 ? HorizontalAlignment.Right
                                    : HorizontalAlignment.Center
            };

            Grid.SetColumn(label, i);
            PresetLabels.Children.Add(label);
        }
    }

    private static Encoding CreateZipEntryNameEncoding()
    {
        // .NET Core 以降、CP932 などのレガシーコードページは既定では使えない。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }

    private void PresetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAMLの初期化順によっては、ラベルより先に値が入ることがある。
        if (PresetLabels is null || PresetLabels.Children.Count == 0)
            return;

        int index = (int)PresetSlider.Value;
        PresetDescription.Text = JxlPreset.All[index].Description;

        for (int i = 0; i < PresetLabels.Children.Count; i++)
        {
            var label = (TextBlock)PresetLabels.Children[i];
            bool selected = i == index;

            label.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
            label.Foreground = selected
                ? SystemColors.ControlTextBrush
                : SystemColors.GrayTextBrush;
        }
    }

    // 元のZIPが失われる設定のため、オンにした時点で一度だけ確認する。
    private void OverwriteOption_Checked(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "変換に成功すると、元のZIPファイルが変換後のZIPで置き換えられます。\n" +
            "元のファイルは残らず、この操作は取り消せません。\n\n" +
            "この設定を有効にしますか？",
            "元のZIPを上書きする", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            OverwriteOption.IsChecked = false;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();

        CancelButton.IsEnabled = false;
        StatusText.Text = "中止しています...";
    }

    private static bool IsZip(string path)
        => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (_running)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            e.Effects = files.Length > 0 && files.All(IsZip)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_running || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (files.Length == 0 || !files.All(IsZip))
        {
            MessageBox.Show("ZIPファイルだけをドロップしてください。",
                            "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ConvertZipsAsync(files, SelectedPreset, OverwriteOption.IsChecked == true);
    }

    /// <summary>処理中のZIPをまたいで共有する進捗。</summary>
    private sealed class ConversionProgress
    {
        public int TotalFiles;
        public int CompletedFiles;
        public int ZipIndex;
        public int ZipCount;
        public string ZipName = "";
    }

    /// <summary>1つのZIPを処理した結果。</summary>
    private readonly record struct ZipOutcome(
        int Converted, int Copied, string? OutputPath, string? Error, bool Cancelled);

    private async Task ConvertZipsAsync(string[] inputZips, JxlPreset preset, bool overwriteSource)
    {
        _running = true;
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;

        SettingsBox.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.Value = 0;
        LogBox.Clear();

        try
        {
            // 不具合の報告を受けたときに、どの版で起きたかを追えるようにする
            Log($"JpegZipToJxl v{AppVersion} (libjxl {JxlTranscoder.LibraryVersion})");

            Log(inputZips.Length == 1
                ? $"入力: {inputZips[0]}"
                : $"入力: {inputZips.Length} 個のZIP");

            LogSettings(preset, overwriteSource);

            // 展開せずに中央ディレクトリだけ読み、全体の変換対象数を数える。
            var progress = new ConversionProgress { ZipCount = inputZips.Length };

            foreach (var zip in inputZips)
            {
                try
                {
                    progress.TotalFiles += CountTargets(zip);
                }
                catch (Exception ex)
                {
                    Log($"読み取れません: {Path.GetFileName(zip)} ({ex.Message})");
                }
            }

            if (progress.TotalFiles == 0)
                throw new InvalidOperationException("ZIP内に変換できる画像ファイルがありません。");

            Log($"変換対象: 合計 {progress.TotalFiles} ファイル");

            int convertedTotal = 0;
            int copiedTotal = 0;
            int succeededZips = 0;
            bool cancelled = false;
            string? lastOutput = null;
            var failures = new List<string>();

            for (int i = 0; i < inputZips.Length; i++)
            {
                progress.ZipIndex = i + 1;
                progress.ZipName = Path.GetFileName(inputZips[i]);

                if (inputZips.Length > 1)
                    Log($"--- [{progress.ZipIndex}/{progress.ZipCount}] {progress.ZipName}");

                ZipOutcome outcome = await ConvertOneZipAsync(
                    inputZips[i], preset, overwriteSource, progress, token);

                convertedTotal += outcome.Converted;
                copiedTotal += outcome.Copied;

                if (outcome.Cancelled)
                {
                    cancelled = true;
                    Log("中止しました。作成中のZIPは残していません。");
                    break;
                }

                if (outcome.Error != null)
                {
                    failures.Add($"{progress.ZipName}: {outcome.Error}");
                    Log($"ERROR: {progress.ZipName} — {outcome.Error}");
                }
                else if (outcome.OutputPath != null)
                {
                    succeededZips++;
                    lastOutput = outcome.OutputPath;
                    Log($"出力: {outcome.OutputPath}");
                }
            }

            if (!cancelled)
                Progress.Value = 100;

            StatusText.Text = cancelled ? "中止" : failures.Count == 0 ? "完了" : "完了（一部失敗）";
            Log($"変換 {convertedTotal} ファイル / コピー {copiedTotal} ファイル");

            ShowSummary(inputZips.Length, succeededZips, convertedTotal, copiedTotal,
                        lastOutput, failures, cancelled);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            StatusText.Text = "エラー";
            MessageBox.Show(ex.ToString(), "変換エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;

            CancelButton.IsEnabled = false;
            SettingsBox.IsEnabled = true;
            _running = false;
        }
    }

    private void LogSettings(JxlPreset preset, bool overwriteSource)
    {
        if (preset.Options.Mode == JxlMode.Lossless)
        {
            Log($"画質: {preset.Name}");
            Log("※ JPEGは元のファイルをバイト単位で復元できます。");
            Log("※ JPEG以外は画素が一致しますが、元のファイル形式へは戻せません。");
        }
        else
        {
            Log($"画質: {preset.Name} (distance = {preset.Options.Distance:F1})");
            Log("※ 不可逆変換です。EXIFなどのメタデータは引き継がれません。");
        }

        if (overwriteSource)
            Log("出力: 元のZIPを上書きします。");

        Log($"同時変換数: {MaxParallelism}");
    }

    private void ShowSummary(int zipCount, int succeededZips, int converted, int copied,
                             string? lastOutput, List<string> failures, bool cancelled)
    {
        if (cancelled)
        {
            MessageBox.Show(
                $"変換を中止しました。\n\n変換済み {converted} ファイル分は破棄されています。",
                "中止", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string message;

        if (zipCount == 1 && failures.Count == 0 && lastOutput != null)
        {
            message = $"変換が完了しました。\n\n{lastOutput}";
        }
        else
        {
            message = $"{succeededZips}/{zipCount} 個のZIPを変換しました。\n"
                    + $"変換 {converted} ファイル / コピー {copied} ファイル";

            if (failures.Count > 0)
                message += "\n\n処理できなかったZIP:\n" + string.Join("\n", failures);
        }

        MessageBox.Show(message,
            failures.Count > 0 ? "完了（一部失敗）" : "完了",
            MessageBoxButton.OK,
            failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    /// <summary>
    /// ZIPを展開せずに、中央ディレクトリから変換対象の数を数える。
    /// </summary>
    private static int CountTargets(string zipPath)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read, ZipEntryNameEncoding);

        // ディレクトリのエントリは Name が空になる。
        return archive.Entries.Count(
            entry => !string.IsNullOrEmpty(entry.Name) && JxlTranscoder.IsSupported(entry.Name));
    }

    private async Task<ZipOutcome> ConvertOneZipAsync(
        string inputZip, JxlPreset preset, bool overwriteSource,
        ConversionProgress progress, CancellationToken token)
    {
        string inputDir = Path.GetDirectoryName(inputZip)!;
        string outputZip = Path.Combine(
            inputDir, Path.GetFileNameWithoutExtension(inputZip) + "_jxl.zip");

        // 変換に時間をかけてから止まらないよう、出力先の確認は先に済ませる。
        if (!overwriteSource && File.Exists(outputZip))
        {
            var answer = MessageBox.Show(
                $"出力先が既に存在します。\n\n{outputZip}\n\n"
                + "上書きしますか？\n"
                + "「いいえ」でこのZIPをスキップ、「キャンセル」で全体を中止します。",
                "確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
                return new ZipOutcome(0, 0, null, null, Cancelled: true);

            if (answer != MessageBoxResult.Yes)
            {
                Log($"スキップしました: {Path.GetFileName(inputZip)}");
                return new ZipOutcome(0, 0, null, null, Cancelled: false);
            }

            File.Delete(outputZip);
        }

        // 上書き時は、まず同じフォルダの一時ファイルへ書き出す。
        // 変換が最後まで成功したときだけ、元のZIPと置き換える。
        string zipTarget = overwriteSource
            ? Path.Combine(inputDir, $".jxlconv_{Guid.NewGuid():N}.tmp")
            : outputZip;

        string tempRoot = Path.Combine(Path.GetTempPath(),
            "JpegZipToJxl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        string extractDir = Path.Combine(tempRoot, "in");
        string outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(extractDir);
        Directory.CreateDirectory(outputDir);

        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(
                inputZip, extractDir, ZipEntryNameEncoding, overwriteFiles: true), token);

            token.ThrowIfCancellationRequested();

            var allFiles = Directory.EnumerateFiles(
                extractDir, "*", SearchOption.AllDirectories).ToList();

            var targets = allFiles.Where(JxlTranscoder.IsSupported).ToList();

            if (targets.Count == 0)
                throw new InvalidOperationException("変換できる画像ファイルがありません。");

            // 出力名の解決は既存名との衝突判定を伴うため、並列化する前に済ませる。
            // 拡張子を .jxl へ変えると別形式の同名ファイル同士が衝突しうる。
            var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jobs = new List<(string Input, string Relative, string Output)>(targets.Count);

            foreach (var inputFile in targets)
            {
                string relative = Path.GetRelativePath(extractDir, inputFile);
                string output = Path.Combine(outputDir, ResolveOutputName(relative, usedOutputs));

                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                jobs.Add((inputFile, relative, output));
            }

            int converted = 0;
            int copied = 0;

            await Parallel.ForEachAsync(jobs, new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelism,
                CancellationToken = token
            },
            async (job, ct) =>
            {
                string? failure = await EncodeAsync(job.Input, job.Output, preset.Options, Log, ct);

                if (failure == null)
                {
                    Interlocked.Increment(ref converted);
                }
                else
                {
                    // デコードできない形式や壊れたファイルは、変換せずそのまま残す。
                    Log($"変換できないためコピーします: {job.Relative} ({failure})");

                    string dest = Path.Combine(outputDir, job.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(job.Input, dest, overwrite: true);
                    Interlocked.Increment(ref copied);
                }

                ReportProgress(progress, job.Relative);
            });

            // 画像以外のファイル（txt等）はそのままコピー
            foreach (var other in allFiles)
            {
                token.ThrowIfCancellationRequested();

                if (JxlTranscoder.IsSupported(other))
                    continue;

                string relative = Path.GetRelativePath(extractDir, other);
                string dest = Path.Combine(outputDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(other, dest, overwrite: true);
                copied++;
            }

            if (overwriteSource && converted == 0)
            {
                throw new InvalidOperationException(
                    "変換できたファイルが1つも無いため、元のZIPは変更していません。");
            }

            token.ThrowIfCancellationRequested();

            await Task.Run(() => ZipFile.CreateFromDirectory(
                outputDir, zipTarget, CompressionLevel.Optimal, includeBaseDirectory: false),
                CancellationToken.None);

            string finalZip = outputZip;

            if (overwriteSource)
            {
                // 書き出しが終わってから置き換えるため、途中で失敗しても元のZIPは残る。
                try
                {
                    File.Replace(zipTarget, inputZip, destinationBackupFileName: null);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    throw new InvalidOperationException(
                        "元のZIPを置き換えられませんでした。元のZIPはそのまま残っています。"
                        + "読み取り専用になっていないか、他のプログラムが開いていないかご確認ください。",
                        ex);
                }

                finalZip = inputZip;
                Log("元のZIPを置き換えました。");
            }

            return new ZipOutcome(converted, copied, finalZip, null, Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            return new ZipOutcome(0, 0, null, null, Cancelled: true);
        }
        catch (Exception ex)
        {
            return new ZipOutcome(0, 0, null, ex.Message.ReplaceLineEndings(" "), Cancelled: false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);

                // 中止したときや置き換えに至らなかったとき、一時ファイルが残る
                if (overwriteSource && File.Exists(zipTarget))
                    File.Delete(zipTarget);
            }
            catch
            {
                // 後始末失敗は本処理の失敗にはしない
            }
        }
    }

    /// <summary>
    /// 全ZIPの変換対象数を分母として進捗を更新する。並列変換から呼ばれる。
    /// </summary>
    private void ReportProgress(ConversionProgress progress, string relative)
    {
        int completed = Interlocked.Increment(ref progress.CompletedFiles);

        // 事前に数えた件数と実際の件数が食い違っても、バーが振り切れないようにする。
        double percent = Math.Min(100.0, completed * 100.0 / progress.TotalFiles);

        string text = progress.ZipCount == 1
            ? $"{completed}/{progress.TotalFiles}: {relative}"
            : $"[{progress.ZipIndex}/{progress.ZipCount}] {progress.ZipName} — "
              + $"{completed}/{progress.TotalFiles}: {relative}";

        Dispatcher.InvokeAsync(() =>
        {
            Progress.Value = percent;
            StatusText.Text = text;
        });
    }

    /// <summary>
    /// 変換後の相対パスを決める。既に同じ名前を使っていれば元の拡張子を残す。
    /// </summary>
    private static string ResolveOutputName(string relative, HashSet<string> used)
    {
        string candidate = Path.ChangeExtension(relative, ".jxl");

        if (used.Contains(candidate))
            candidate = relative + ".jxl";

        string baseName = candidate;
        for (int n = 2; used.Contains(candidate); n++)
            candidate = Path.ChangeExtension(baseName, null) + $"_{n}.jxl";

        used.Add(candidate);
        return candidate;
    }

    /// <summary>
    /// 1ファイルを変換する。変換できなかった場合はその理由を返す。
    /// </summary>
    private static async Task<string?> EncodeAsync(
        string input, string output, JxlOptions options, Action<string> log, CancellationToken token)
    {
        byte[] source = await File.ReadAllBytesAsync(input, token);

        JxlResult result;
        try
        {
            result = await Task.Run(() => JxlTranscoder.Encode(source, options), token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message.ReplaceLineEndings(" ");
        }

        if (result.Warning != null)
            log($"注意: {Path.GetFileName(input)} — {result.Warning}");

        await File.WriteAllBytesAsync(output, result.Data, token);
        return null;
    }

    private void Log(string text)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(text + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }
}
