using System.IO;
using System.IO.Compression;
using System.Windows;

namespace JpegZipToJxl;

public partial class MainWindow : Window
{
    // エンコード努力量。大きいほど一般に遅くなる。
    private const int Effort = 7;

    private bool _running;

    public MainWindow()
    {
        InitializeComponent();
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

        await ConvertZipAsync(files[0]);
    }

    private async Task ConvertZipAsync(string inputZip)
    {
        _running = true;
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

            await Task.Run(() => ZipFile.ExtractToDirectory(inputZip, extractDir, overwriteFiles: true));

            var jpegFiles = Directory.EnumerateFiles(
                    extractDir, "*", SearchOption.AllDirectories)
                .Where(IsJpeg)
                .ToList();

            if (jpegFiles.Count == 0)
            {
                throw new InvalidOperationException("ZIP内にJPEGファイルがありません。");
            }

            Log($"JPEG: {jpegFiles.Count} ファイル");
            Log("JPEG XLへ変換します。");
            Log("※ JPEG入力ではlibjxlのlossless JPEG transcodingを使用します。");

            int completed = 0;

            foreach (var inputFile in jpegFiles)
            {
                string relative = Path.GetRelativePath(extractDir, inputFile);
                string outputFile = Path.Combine(
                    outputDir,
                    Path.ChangeExtension(relative, ".jxl"));

                Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

                await TranscodeAsync(inputFile, outputFile);

                completed++;
                Progress.Value = completed * 100.0 / jpegFiles.Count;
                StatusText.Text = $"{completed}/{jpegFiles.Count}: {relative}";
            }

            // JPEG以外のファイル（txt等）はそのままコピー
            foreach (var other in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                if (IsJpeg(other))
                    continue;

                string relative = Path.GetRelativePath(extractDir, other);
                string dest = Path.Combine(outputDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(other, dest, overwrite: true);
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

            _running = false;
        }
    }

    private static async Task TranscodeAsync(string input, string output)
    {
        byte[] jpeg = await File.ReadAllBytesAsync(input);

        byte[] jxl;
        try
        {
            jxl = await Task.Run(() => JxlTranscoder.TranscodeJpeg(jpeg, Effort));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"変換に失敗しました: {Path.GetFileName(input)}\n{ex.Message}", ex);
        }

        await File.WriteAllBytesAsync(output, jxl);
    }

    private static bool IsJpeg(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
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
