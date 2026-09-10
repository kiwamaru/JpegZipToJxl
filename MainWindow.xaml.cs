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
            e.Effects = files.Length == 1 &&
                        string.Equals(Path.GetExtension(files[0]), ".zip",
                                      StringComparison.OrdinalIgnoreCase)
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
        if (files.Length != 1 ||
            !string.Equals(Path.GetExtension(files[0]), ".zip",
                           StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("ZIPファイルを1つだけドロップしてください。",
                            "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ConvertZipAsync(files[0], (JxlPreset)PresetBox.SelectedItem);
    }

    private async Task ConvertZipAsync(string inputZip, JxlPreset preset)
    {
        _running = true;
        SettingsBox.IsEnabled = false;
        Progress.Value = 0;
        LogBox.Clear();

        string tempRoot = Path.Combine(Path.GetTempPath(),
            "JpegZipToJxl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        string extractDir = Path.Combine(tempRoot, "in");
        string outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(extractDir);
        Directory.CreateDirectory(outputDir);

        string outputZip = Path.Combine(
            Path.GetDirectoryName(inputZip)!,
            Path.GetFileNameWithoutExtension(inputZip) + "_jxl.zip");

        try
        {
            Log($"入力: {inputZip}");
            Log("ZIPを展開しています...");

            await Task.Run(() => ZipFile.ExtractToDirectory(
                inputZip, extractDir, ZipEntryNameEncoding, overwriteFiles: true));

            var allFiles = Directory.EnumerateFiles(
                extractDir, "*", SearchOption.AllDirectories).ToList();

            var targets = allFiles.Where(JxlTranscoder.IsSupported).ToList();

            if (targets.Count == 0)
            {
                throw new InvalidOperationException("ZIP内に変換できる画像ファイルがありません。");
            }

            Log($"変換対象: {targets.Count} ファイル");

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

            // 拡張子を .jxl へ変えると別形式の同名ファイル同士が衝突しうるため、
            // 使用済みの出力パスを覚えておく。
            var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int completed = 0;
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

                completed++;
                Progress.Value = completed * 100.0 / targets.Count;
                StatusText.Text = $"{completed}/{targets.Count}: {relative}";
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

            if (File.Exists(outputZip))
            {
                var result = MessageBox.Show(
                    $"出力先が既に存在します。\n\n{outputZip}\n\n上書きしますか？",
                    "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;

                File.Delete(outputZip);
            }

            Log("変換後ZIPを作成しています...");
            await Task.Run(() => ZipFile.CreateFromDirectory(
                outputDir, outputZip, CompressionLevel.Optimal, includeBaseDirectory: false));

            Progress.Value = 100;
            StatusText.Text = "完了";
            Log($"変換 {converted} ファイル / コピー {copied} ファイル");
            Log($"出力: {outputZip}");

            MessageBox.Show(
                $"変換が完了しました。\n\n{outputZip}",
                "完了", MessageBoxButton.OK, MessageBoxImage.Information);
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
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // 後始末失敗は本処理の失敗にはしない
            }

            SettingsBox.IsEnabled = true;
            _running = false;
        }
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
