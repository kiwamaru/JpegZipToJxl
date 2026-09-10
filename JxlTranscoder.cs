using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JxlNet;

namespace JpegZipToJxl;

/// <summary>変換モード。</summary>
internal enum JxlMode
{
    /// <summary>不可逆に再圧縮する。</summary>
    Recompress,

    /// <summary>可逆変換する。</summary>
    Lossless
}

/// <summary>
/// 変換設定。
/// </summary>
/// <param name="Mode">変換モード。</param>
/// <param name="Effort">エンコード努力量 (cjxl の --effort 相当)。大きいほど一般に遅くなる。</param>
/// <param name="Distance">再圧縮時の画質。0に近いほど高画質で、1.0前後が視覚的に劣化なしとされる。</param>
internal sealed record JxlOptions(JxlMode Mode, int Effort = 7, float Distance = 1.0f);

/// <summary>変換結果。<c>Warning</c> は変換自体は成功した場合の注意点。</summary>
internal readonly record struct JxlResult(byte[] Data, string? Warning);

/// <summary>
/// libjxl (JxlNet) による JPEG XL への変換。
///
/// libjxl 本体は画像ファイルのデコーダを持たず、エンコーダは画素を受け取る。
/// そのため JPEG 以外の入力は WIC (WPFのBitmapDecoder) で画素へ展開してから渡す。
/// JPEG のみ、画素へ戻さない可逆transcodeが使える。
/// </summary>
internal static unsafe class JxlTranscoder
{
    /// <summary>
    /// 変換を試みる拡張子。WICのコーデックに依存するため、
    /// 環境によってはデコードできず、その場合は呼び出し側でコピーへフォールバックする。
    /// </summary>
    private static readonly string[] SupportedExtensions =
    [
        ".jpg", ".jpeg", ".jpe", ".jfif",
        ".png", ".bmp", ".dib", ".gif",
        ".tif", ".tiff", ".ico",
        ".jxr", ".wdp", ".hdp",
        ".webp", ".heic", ".heif", ".avif"
    ];

    public static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 実際に読み込まれた libjxl のバージョン。
    /// libjxl は major * 1000000 + minor * 1000 + patch の形で返す。
    /// </summary>
    public static string LibraryVersion
    {
        get
        {
            uint version = Jxl.JxlEncoderVersion();
            return $"{version / 1000000}.{version / 1000 % 1000}.{version % 1000}";
        }
    }

    public static JxlResult Encode(byte[] source, JxlOptions options)
    {
        // 画素へ戻さない可逆transcodeはJPEG入力にしか使えない。
        if (options.Mode == JxlMode.Lossless && IsJpeg(source))
            return new JxlResult(EncodeJpegLossless(source, options), null);

        return EncodeFromPixels(source, options);
    }

    private static bool IsJpeg(byte[] data)
        => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    /// <summary>
    /// JPEG を画素へデコードせず、元の JPEG を復元可能な形で JXL へ格納する。
    /// </summary>
    private static byte[] EncodeJpegLossless(byte[] jpegData, JxlOptions options)
    {
        JxlEncoder* enc = Jxl.JxlEncoderCreate(null);
        if (enc == null)
            throw new InvalidOperationException("JxlEncoder を作成できませんでした。");

        try
        {
            // 元 JPEG を復元するための再構成データを保持する。
            // このデータはコンテナ形式でのみ格納できる。
            Check(Jxl.JxlEncoderUseContainer(enc, 1), enc, "JxlEncoderUseContainer");
            Check(Jxl.JxlEncoderStoreJPEGMetadata(enc, 1), enc, "JxlEncoderStoreJPEGMetadata");

            JxlEncoderFrameSettings* settings = CreateFrameSettings(enc, options.Effort);

            fixed (byte* input = jpegData)
            {
                Check(Jxl.JxlEncoderAddJPEGFrame(settings, input, (nuint)jpegData.Length),
                      enc, "JPEG フレームの追加");
            }

            Jxl.JxlEncoderCloseInput(enc);

            return ReadOutput(enc);
        }
        finally
        {
            Jxl.JxlEncoderDestroy(enc);
        }
    }

    /// <summary>
    /// 画像を画素へ展開してからエンコードする。
    /// ロスレス指定なら画素が完全に一致し、そうでなければ distance に従って劣化する。
    /// いずれの場合も EXIF などのメタデータは引き継がれない。
    /// </summary>
    private static JxlResult EncodeFromPixels(byte[] source, JxlOptions options)
    {
        DecodedImage image = DecodeImage(source);
        bool lossless = options.Mode == JxlMode.Lossless;

        JxlEncoder* enc = Jxl.JxlEncoderCreate(null);
        if (enc == null)
            throw new InvalidOperationException("JxlEncoder を作成できませんでした。");

        try
        {
            JxlBasicInfo info;
            Jxl.JxlEncoderInitBasicInfo(&info);
            info.xsize = (uint)image.Width;
            info.ysize = (uint)image.Height;
            info.bits_per_sample = 8;
            info.num_color_channels = 3;
            info.num_extra_channels = image.HasAlpha ? 1u : 0u;
            info.alpha_bits = image.HasAlpha ? 8u : 0u;

            // ロスレスでは元の色空間のまま符号化する必要がある。
            // 不可逆では XYB 色空間を許可した方が小さくなる。
            info.uses_original_profile = lossless ? 1 : 0;

            Check(Jxl.JxlEncoderSetBasicInfo(enc, &info), enc, "基本情報の設定");

            JxlColorEncoding color;
            Jxl.JxlColorEncodingSetToSRGB(&color, is_gray: 0);
            Check(Jxl.JxlEncoderSetColorEncoding(enc, &color), enc, "色空間の設定");

            JxlEncoderFrameSettings* settings = CreateFrameSettings(enc, options.Effort);

            if (lossless)
                Check(Jxl.JxlEncoderSetFrameLossless(settings, 1), enc, "ロスレスの指定");
            else
                Check(Jxl.JxlEncoderSetFrameDistance(settings, options.Distance),
                      enc, "distance の設定");

            var format = new JxlPixelFormat
            {
                num_channels = image.HasAlpha ? 4u : 3u,
                data_type = JxlDataType.JXL_TYPE_UINT8,
                endianness = JxlEndianness.JXL_NATIVE_ENDIAN,
                align = 0
            };

            fixed (byte* buffer = image.Pixels)
            {
                Check(Jxl.JxlEncoderAddImageFrame(settings, &format, buffer, (nuint)image.Pixels.Length),
                      enc, "画像フレームの追加");
            }

            Jxl.JxlEncoderCloseInput(enc);

            return new JxlResult(ReadOutput(enc), image.Warning);
        }
        finally
        {
            Jxl.JxlEncoderDestroy(enc);
        }
    }

    private readonly record struct DecodedImage(
        byte[] Pixels, int Width, int Height, bool HasAlpha, string? Warning);

    /// <summary>
    /// 画像を 8bit RGB / RGBA の連続バッファへ展開する。
    /// </summary>
    private static DecodedImage DecodeImage(byte[] data)
    {
        using var stream = new MemoryStream(data);

        // OnLoad を指定してストリームを閉じても参照できるようにする。
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        BitmapSource frame = decoder.Frames[0];

        var warnings = new List<string>();
        if (decoder.Frames.Count > 1)
            warnings.Add($"{decoder.Frames.Count}フレームのうち1枚目のみ変換");
        if (IsHighBitDepth(frame.Format))
            warnings.Add("8bitへ変換");

        // WICはEXIFのOrientationを反映しないため、ここで画素を回しておく。
        // これを行わないと、スマートフォンの写真などが横倒しのまま変換される。
        ushort orientation = ReadOrientation(frame);

        // グレースケール・パレット・CMYKなどもあるため、8bitのBGRAへ揃える。
        // Pbgra32 (乗算済みアルファ) もここで非乗算へ戻る。
        if (frame.Format != PixelFormats.Bgra32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        frame = ApplyOrientation(frame, orientation);

        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        int pixelCount = width * height;

        var bgra = new byte[pixelCount * 4];
        frame.CopyPixels(bgra, width * 4, 0);

        bool hasAlpha = false;
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0xFF)
            {
                hasAlpha = true;
                break;
            }
        }

        // libjxl は RGB / RGBA の順で受け取るため詰め替える。
        int channels = hasAlpha ? 4 : 3;
        var pixels = new byte[pixelCount * channels];

        for (int i = 0, src = 0, dst = 0; i < pixelCount; i++, src += 4, dst += channels)
        {
            pixels[dst] = bgra[src + 2];
            pixels[dst + 1] = bgra[src + 1];
            pixels[dst + 2] = bgra[src];

            if (hasAlpha)
                pixels[dst + 3] = bgra[src + 3];
        }

        return new DecodedImage(pixels, width, height, hasAlpha,
            warnings.Count == 0 ? null : string.Join(" / ", warnings));
    }

    /// <summary>
    /// EXIF の Orientation (1〜8) を読む。無ければ 1 を返す。
    /// </summary>
    private static ushort ReadOrientation(BitmapSource frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata)
            return 1;

        // JPEGはAPP1、TIFFは先頭のIFDに入っている。
        foreach (string query in (string[])["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}"])
        {
            try
            {
                if (metadata.GetQuery(query) is ushort value)
                    return value;
            }
            catch (Exception)
            {
                // クエリに対応しない形式では例外になるため、次を試す
            }
        }

        return 1;
    }

    /// <summary>
    /// EXIF の Orientation に従って画素を並べ替える。
    /// </summary>
    private static BitmapSource ApplyOrientation(BitmapSource source, ushort orientation)
    {
        if (orientation is <= 1 or > 8)
            return source;

        var transform = new TransformGroup();

        // 5〜8 は「反転してから回す」と定義されている。
        if (orientation is 2 or 4 or 5 or 7)
            transform.Children.Add(new ScaleTransform(-1, 1));

        double angle = orientation switch
        {
            3 or 4 => 180,
            6 or 7 => 90,
            5 or 8 => 270,
            _ => 0
        };

        if (angle != 0)
            transform.Children.Add(new RotateTransform(angle));

        var rotated = new TransformedBitmap(source, transform);
        rotated.Freeze();
        return rotated;
    }

    /// <summary>
    /// 1チャンネルあたり8bitを超える形式か。該当する場合、8bitへ落として変換する。
    /// </summary>
    private static bool IsHighBitDepth(PixelFormat format)
        => format == PixelFormats.Gray16 || format == PixelFormats.Gray32Float
        || format == PixelFormats.Rgb48 || format == PixelFormats.Rgba64
        || format == PixelFormats.Prgba64 || format == PixelFormats.Rgb128Float
        || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float
        || format == PixelFormats.Bgr101010;

    private static JxlEncoderFrameSettings* CreateFrameSettings(JxlEncoder* enc, int effort)
    {
        JxlEncoderFrameSettings* settings = Jxl.JxlEncoderFrameSettingsCreate(enc, null);
        if (settings == null)
            throw new InvalidOperationException("JxlEncoderFrameSettings を作成できませんでした。");

        Check(Jxl.JxlEncoderFrameSettingsSetOption(
                  settings, JxlEncoderFrameSettingId.JXL_ENC_FRAME_SETTING_EFFORT, effort),
              enc, "effort の設定");

        return settings;
    }

    private static byte[] ReadOutput(JxlEncoder* enc)
    {
        // libjxl は出力バッファが尽きるたびに NEED_MORE_OUTPUT を返すため、
        // 固定長バッファへ小分けに書き出して連結する。
        var chunk = new byte[1 << 16];
        using var output = new MemoryStream(chunk.Length);

        JxlEncoderStatus status;
        do
        {
            long written;
            fixed (byte* buffer = chunk)
            {
                byte* next = buffer;
                nuint available = (nuint)chunk.Length;

                status = Jxl.JxlEncoderProcessOutput(enc, &next, &available);
                written = chunk.Length - (long)available;
            }

            if (written > 0)
                output.Write(chunk, 0, (int)written);
        }
        while (status == JxlEncoderStatus.JXL_ENC_NEED_MORE_OUTPUT);

        Check(status, enc, "エンコード結果の取り出し");

        return output.ToArray();
    }

    private static void Check(JxlEncoderStatus status, JxlEncoder* enc, string operation)
    {
        if (status == JxlEncoderStatus.JXL_ENC_SUCCESS)
            return;

        JxlEncoderError error = Jxl.JxlEncoderGetError(enc);
        throw new InvalidOperationException($"libjxl: {operation} に失敗しました ({error})。");
    }
}
