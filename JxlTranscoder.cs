using System.IO;
using JxlNet;

namespace JpegZipToJxl;

/// <summary>
/// libjxl (JxlNet) による JPEG → JPEG XL の可逆トランスコード。
/// 画素へデコードせず、元の JPEG を復元可能な形で JXL コンテナへ格納する。
/// </summary>
internal static unsafe class JxlTranscoder
{
    /// <summary>
    /// JPEG のバイト列を JXL のバイト列へ変換する。
    /// </summary>
    /// <param name="jpegData">入力 JPEG。</param>
    /// <param name="effort">エンコード努力量 (cjxl の --effort 相当)。</param>
    public static byte[] TranscodeJpeg(byte[] jpegData, int effort)
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

            JxlEncoderFrameSettings* settings = Jxl.JxlEncoderFrameSettingsCreate(enc, null);
            if (settings == null)
                throw new InvalidOperationException("JxlEncoderFrameSettings を作成できませんでした。");

            Check(Jxl.JxlEncoderFrameSettingsSetOption(
                      settings, JxlEncoderFrameSettingId.JXL_ENC_FRAME_SETTING_EFFORT, effort),
                  enc, "effort の設定");

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
