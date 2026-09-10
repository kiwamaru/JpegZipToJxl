using System.Globalization;
using System.IO;
using System.IO.Compression;
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

    private bool _running;

    public MainWindow()
    {
        InitializeComponent();

        PresetBox.ItemsSource = JxlPreset.All;
        PresetBox.SelectedIndex = JxlPreset.DefaultIndex;
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

    // JxlPreset は internal のため、説明文はバインディングではなくコードで反映する。
    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PresetDescription.Text = ((JxlPreset)PresetBox.SelectedItem).Description;
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

        await ConvertZipsAsync(files, (JxlPreset)PresetBox.SelectedItem,
                               OverwriteOption.IsChecked == true);
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
        int Converted, int Copied, string? OutputPath, string? Error, bool CancelAll);

    private async Task ConvertZipsAsync(string[] inputZips, JxlPreset preset, bool overwriteSource)
    {
        _running = true;
        SettingsBox.IsEnabled = false;
        Progress.Value = 0;
        LogBox.Clear();

        try
        {
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
            string? lastOutput = null;
            var failures = new List<string>();

            for (int i = 0; i < inputZips.Length; i++)
            {
                progress.ZipIndex = i + 1;
                progress.ZipName = Path.GetFileName(inputZips[i]);

                if (inputZips.Length > 1)
                    Log($"--- [{progress.ZipIndex}/{progress.ZipCount}] {progress.ZipName}");

                ZipOutcome outcome =
                    await ConvertOneZipAsync(inputZips[i], preset, overwriteSource, progress);

                convertedTotal += outcome.Converted;
                copiedTotal += outcome.Copied;

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

                if (outcome.CancelAll)
                {
                    Log("残りのZIPの処理を中止しました。");
                    break;
                }
            }

            Progress.Value = 100;
            StatusText.Text = failures.Count == 0 ? "完了" : "完了（一部失敗）";
            Log($"変換 {convertedTotal} ファイル / コピー {copiedTotal} ファイル");

            ShowSummary(inputZips.Length, succeededZips, convertedTotal, copiedTotal,
                        lastOutput, failures);
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
    }

    private void ShowSummary(int zipCount, int succeededZips, int converted, int copied,
                             string? lastOutput, List<string> failures)
    {
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
        string inputZip, JxlPreset preset, bool overwriteSource, ConversionProgress progress)
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
                return new ZipOutcome(0, 0, null, null, CancelAll: true);

            if (answer != MessageBoxResult.Yes)
            {
                Log($"スキップしました: {Path.GetFileName(inputZip)}");
                return new ZipOutcome(0, 0, null, null, CancelAll: false);
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
                inputZip, extractDir, ZipEntryNameEncoding, overwriteFiles: true));

            var allFiles = Directory.EnumerateFiles(
                extractDir, "*", SearchOption.AllDirectories).ToList();

            var targets = allFiles.Where(JxlTranscoder.IsSupported).ToList();

            if (targets.Count == 0)
                throw new InvalidOperationException("変換できる画像ファイルがありません。");

            // 拡張子を .jxl へ変えると別形式の同名ファイル同士が衝突しうるため、
            // 使用済みの出力パスを覚えておく。
            var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int converted = 0;
            int copied = 0;

            foreach (var inputFile in targets)
            {
                string relative = Path.GetRelativePath(extractDir, inputFile);
                string outputFile = Path.Combine(
                    outputDir, ResolveOutputName(relative, usedOutputs));

                Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

                string? failure = await EncodeAsync(inputFile, outputFile, preset.Options, Log);

                if (failure == null)
                {
                    converted++;
                }
                else
                {
                    // デコードできない形式や壊れたファイルは、変換せずそのまま残す。
                    Log($"変換できないためコピーします: {relative} ({failure})");

                    string dest = Path.Combine(outputDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(inputFile, dest, overwrite: true);
                    copied++;
                }

                ReportProgress(progress, relative);
            }

            // 画像以外のファイル（txt等）はそのままコピー
            foreach (var other in allFiles)
            {
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

            await Task.Run(() => ZipFile.CreateFromDirectory(
                outputDir, zipTarget, CompressionLevel.Optimal, includeBaseDirectory: false));

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

            return new ZipOutcome(converted, copied, finalZip, null, CancelAll: false);
        }
        catch (Exception ex)
        {
            return new ZipOutcome(0, 0, null, ex.Message.ReplaceLineEndings(" "), CancelAll: false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);

                // 置き換えに至らなかった場合、一時ファイルが残る
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
    /// 全ZIPの変換対象数を分母として進捗を更新する。
    /// </summary>
    private void ReportProgress(ConversionProgress progress, string relative)
    {
        progress.CompletedFiles++;

        // 事前に数えた件数と実際の件数が食い違っても、バーが振り切れないようにする。
        Progress.Value = Math.Min(
            100.0, progress.CompletedFiles * 100.0 / progress.TotalFiles);

        StatusText.Text = progress.ZipCount == 1
            ? $"{progress.CompletedFiles}/{progress.TotalFiles}: {relative}"
            : $"[{progress.ZipIndex}/{progress.ZipCount}] {progress.ZipName} — "
              + $"{progress.CompletedFiles}/{progress.TotalFiles}: {relative}";
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
        string input, string output, JxlOptions options, Action<string> log)
    {
        byte[] source = await File.ReadAllBytesAsync(input);

        JxlResult result;
        try
        {
            result = await Task.Run(() => JxlTranscoder.Encode(source, options));
        }
        catch (Exception ex)
        {
            return ex.Message.ReplaceLineEndings(" ");
        }

        if (result.Warning != null)
            log($"注意: {Path.GetFileName(input)} — {result.Warning}");

        await File.WriteAllBytesAsync(output, result.Data);
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
