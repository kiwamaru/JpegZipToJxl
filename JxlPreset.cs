namespace JpegZipToJxl;

/// <summary>
/// 画質プリセット。括弧内のサイズはJPEG入力9枚（0.27〜8.07 bpp）で実測した目安。
/// PNGなど元々ロスレスな形式では比率が大きく変わる。
/// </summary>
internal sealed record JxlPreset(string Name, string Description, JxlOptions Options)
{
    public static IReadOnlyList<JxlPreset> All { get; } =
    [
        new("ロスレス",
            "画質を落としません。JPEGは元のファイルをバイト単位で復元でき、"
            + "その他の形式も画素が完全に一致します（JPEG入力の目安: 元の60〜85%）。",
            new JxlOptions(JxlMode.Lossless)),

        new("超高画質",
            "distance 0.5。ほぼ劣化を検出できません。"
            + "元が高圧縮のJPEGやPNGなどでは、ロスレスより大きくなることがあります"
            + "（JPEG入力の目安: 元の27〜130%）。",
            new JxlOptions(JxlMode.Recompress, Distance: 0.5f)),

        new("高画質",
            "distance 1.0。視覚的に劣化がないとされるlibjxlの既定値です"
            + "（JPEG入力の目安: 元の15〜76%）。",
            new JxlOptions(JxlMode.Recompress, Distance: 1.0f)),

        new("軽量",
            "distance 2.0。等倍表示では気付きにくい程度に劣化します"
            + "（JPEG入力の目安: 元の9〜41%）。",
            new JxlOptions(JxlMode.Recompress, Distance: 2.0f)),

        new("最高圧縮",
            "distance 4.0。サイズを最優先します。はっきりと劣化します"
            + "（JPEG入力の目安: 元の4〜27%）。",
            new JxlOptions(JxlMode.Recompress, Distance: 4.0f)),
    ];

    /// <summary>初期選択（高画質）。</summary>
    public static int DefaultIndex => 2;

    /// <summary>ComboBoxにはプリセット名だけを表示する。</summary>
    public override string ToString() => Name;
}
