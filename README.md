# JpegZipToJxl

WPF / C# (.NET 10) の簡易ツールです。

## 機能

1. ZIPをウィンドウへドラッグ＆ドロップ
2. ZIPを一時フォルダへ展開
3. `.jpg` / `.jpeg` を libjxl で `.jxl` 化
4. JPEG以外のファイルはそのままコピー
5. 元のフォルダ構成を維持して再ZIP
6. 元ZIPと同じフォルダに `元ファイル名_jxl.zip` を作成

## JPEG XLへの変換方法

外部プロセス (`cjxl.exe`) は使用せず、
[JxlNet](https://www.nuget.org/packages/JxlNet) 経由で libjxl を直接呼び出します。

`JxlTranscoder` は以下のAPIを使用します。

- `JxlEncoderStoreJPEGMetadata` … 元JPEGを復元するための再構成データを保持
- `JxlEncoderUseContainer` … 再構成データの格納にはコンテナ形式が必要
- `JxlEncoderAddJPEGFrame` … JPEGを画素へデコードせずlossless transcode

そのため、生成された `.jxl` からは元のJPEGをバイト単位で復元できます。
（lossy再エンコードではありません）

## 必要なもの

- Windows (x64 または ARM64)
- .NET 10 SDK

libjxlのネイティブDLL (`jxl.dll` など) はJxlNetパッケージに同梱されているため、
`cjxl.exe` の配置は不要です。

## ビルド

```powershell
dotnet build -c Release
```

実行：

```powershell
dotnet run -c Release
```

## 注意

このツールは「JPEGをJXLへ変換する」ことに特化しています。
PNGなどはそのままZIPへ残します。
