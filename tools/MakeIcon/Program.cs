using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// JpegZipToJxl のアプリアイコン (.ico) を生成する。
//
//   dotnet run --project tools/MakeIcon
//   dotnet run --project tools/MakeIcon -- <出力ico> <プレビューpng>
//
// 既定ではリポジトリ直下の JpegZipToJxl.ico を上書きする。

int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];

string icoPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "JpegZipToJxl.ico"));

string? previewPath = args.Length > 1 ? Path.GetFullPath(args[1]) : null;

var pngs = sizes.Select(IconArtwork.Render).ToList();
IcoWriter.Write(icoPath, pngs);

Console.WriteLine($"{icoPath}: {sizes.Length} images, {new FileInfo(icoPath).Length:N0} bytes");
foreach (var (size, png) in sizes.Zip(pngs))
    Console.WriteLine($"  {size,3}x{size,-3} {png.Length,7:N0} bytes");

if (previewPath != null)
{
    IconArtwork.SavePreview(icoPath, previewPath);
    Console.WriteLine($"preview: {previewPath}");
}

/// <summary>
/// アイコンの図案。紫の角丸背景に、白い写真枠と「JXL」の文字を置く。
/// </summary>
internal static class IconArtwork
{
    /// <summary>この寸法で設計し、各サイズへは一様に拡大縮小する。</summary>
    private const double Design = 256.0;

    /// <summary>これより小さいと「JXL」の文字が潰れて読めないため、写真だけの図案にする。</summary>
    private const int MinSizeForText = 48;

    public static byte[] Render(int size)
    {
        double scale = size / Design;
        bool withText = size >= MinSizeForText;

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));

            DrawBackground(dc);

            // 文字を入れない小サイズでは、その分だけ写真を大きく使う
            Rect photo = withText
                ? new Rect(40, 46, 176, 138)
                : new Rect(34, 52, 188, 152);

            DrawPhoto(dc, photo);

            if (withText)
                DrawText(dc);

            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void DrawBackground(DrawingContext dc)
    {
        var brush = new LinearGradientBrush(
            Color.FromRgb(0x63, 0x66, 0xF1),
            Color.FromRgb(0xA8, 0x55, 0xF7),
            new Point(0, 0), new Point(1, 1));

        dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, Design, Design), 56, 56);
    }

    private static void DrawPhoto(DrawingContext dc, Rect photo)
    {
        dc.DrawRoundedRectangle(Brushes.White, null, photo, 14, 14);

        // 枠からはみ出さないように、中身は写真の内側でクリップする
        var inner = new Rect(photo.X + 8, photo.Y + 8, photo.Width - 16, photo.Height - 16);
        dc.PushClip(new RectangleGeometry(inner, 8, 8));

        // 太陽
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), null,
            new Point(inner.X + inner.Width * 0.26, inner.Y + inner.Height * 0.28),
            inner.Width * 0.11, inner.Width * 0.11);

        // 山（奥・手前）
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7)), null,
            Triangle(inner, 0.30, 1.02, 0.62, 0.34, 0.94, 1.02));
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)), null,
            Triangle(inner, -0.04, 1.02, 0.34, 0.52, 0.72, 1.02));

        dc.Pop();
    }

    private static void DrawText(DrawingContext dc)
    {
        var text = new FormattedText(
            "JXL",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                         FontWeights.Bold, FontStretches.Normal),
            54,
            Brushes.White,
            pixelsPerDip: 1.0);

        dc.DrawText(text, new Point((Design - text.Width) / 2, 190));
    }

    /// <summary>相対座標 (0〜1) で三角形を作る。</summary>
    private static Geometry Triangle(
        Rect r, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        Point P(double x, double y) => new(r.X + r.Width * x, r.Y + r.Height * y);

        var figure = new PathFigure { StartPoint = P(x1, y1), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(P(x2, y2), false));
        figure.Segments.Add(new LineSegment(P(x3, y3), false));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>生成したICOの各サイズを1枚に並べて、確認用に書き出す。</summary>
    public static void SavePreview(string icoPath, string outPath)
    {
        var decoder = new IconBitmapDecoder(
            new Uri(icoPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        var frames = decoder.Frames.OrderBy(f => f.PixelWidth).ToList();
        int width = frames.Sum(f => f.PixelWidth + 12) + 12;
        int height = frames.Max(f => f.PixelHeight) + 24;

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, height));

            double x = 12;
            foreach (var frame in frames)
            {
                dc.DrawImage(frame, new Rect(x, 12, frame.PixelWidth, frame.PixelHeight));
                x += frame.PixelWidth + 12;
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var file = File.Create(outPath);
        encoder.Save(file);
    }
}

/// <summary>
/// ICO ファイルの書き出し。
/// 「6バイトのヘッダ + 16バイトのエントリ × 枚数 + 画像データ」という単純な構造で、
/// Vista 以降は各画像を PNG のまま格納できる。
/// </summary>
internal static class IcoWriter
{
    public static void Write(string path, List<byte[]> pngImages)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write((ushort)0);                // reserved
        writer.Write((ushort)1);                // type: icon
        writer.Write((ushort)pngImages.Count);

        int offset = 6 + 16 * pngImages.Count;

        foreach (var png in pngImages)
        {
            (int width, int height) = ReadPngSize(png);

            writer.Write((byte)(width >= 256 ? 0 : width));    // 256 は 0 で表す
            writer.Write((byte)(height >= 256 ? 0 : height));
            writer.Write((byte)0);              // パレット数（トゥルーカラーは0）
            writer.Write((byte)0);              // reserved
            writer.Write((ushort)1);            // color planes
            writer.Write((ushort)32);           // bits per pixel
            writer.Write(png.Length);
            writer.Write(offset);

            offset += png.Length;
        }

        foreach (var png in pngImages)
            writer.Write(png);
    }

    /// <summary>PNG の IHDR から幅と高さを読む（ビッグエンディアン）。</summary>
    private static (int Width, int Height) ReadPngSize(byte[] png)
    {
        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (width, height);
    }
}
