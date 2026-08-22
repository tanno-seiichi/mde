// ChromiumPdfExporter.cs
//
// mde (MarkDown インラインエディタ) の一部。
// PuppeteerSharp（MITライセンス）でheadless Chromiumを起動し、HTMLとして組み立てた文書を
// 「印刷してPDF化」する。以前はMigraDoc（PDFsharp）で直接PDFを生成する方式を使っていたが、
// 実際のChromiumのレンダリングエンジンを使うこの方式には次の利点があり、比較検証の結果、
// こちらを正式な書き出し方式として採用した。
//   ・実際のChromiumのレンダリングエンジンを使うため、游ゴシック UIなど画面表示と同じ
//     フォントがそのまま使え、フォントの見た目のズレが実質なくなる（MigraDoc版は、
//     PDFsharpがWindows標準のTrueTypeコレクション形式の日本語フォントを正式サポートして
//     おらず、代替フォントの同梱が必要だった上に、画面とはフォントの見た目が異なっていた）。
//   ・取り消し線はCSSのtext-decoration:line-throughで実現でき、本物の線になる
//     （MigraDoc版では、そのプロパティ自体が存在せず灰色文字で代替していた）。
//   ・見出しへのジャンプリンク（本文中の「#見出し名」形式のリンク）も、実機での動作確認で
//     問題なく機能することを確認済み。
// 一方、初回実行時にはChromium本体（数百MB）のダウンロードが発生するため、
// インターネット接続が必要で、多少時間がかかる点には注意が必要。
//
// ブラウザ自体（Chromiumのプロセス）は、書き出しのたびに起動・終了すると内容量に関係なく
// 毎回数秒単位の待ち時間が発生するため、ChromiumBrowserPoolでアプリの起動中ずっと
// 使い回すようにしている。このクラスは、書き出しのたびに軽量なページ（IPage）だけを
// 新しく作って使い捨てる。

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Documents;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace mde
{
    /// <summary>現在の文書をheadless Chromium経由でPDFへ書き出す。</summary>
    public class ChromiumPdfExporter
    {
        private readonly ImageManager m_imageManager;

        /// <param name="a_imageManager">画像のsrcから実ファイルパスを解決するために使う。</param>
        public ChromiumPdfExporter(ImageManager a_imageManager)
        {
            m_imageManager = a_imageManager;
        }

        /// <summary>指定したFlowDocumentの内容を、headless ChromiumでPDFとして書き出す。
        /// アプリ起動後、一度もブラウザを起動していない状態での最初の呼び出しだけは、
        /// Chromium本体のダウンロード・起動が発生するため数十秒〜数分かかることがある
        /// （インターネット接続が必要）。2回目以降は、起動済みのブラウザを使い回すため、
        /// 短時間で完了する。</summary>
        /// <param name="a_doc">書き出す文書。</param>
        /// <param name="a_outputPath">保存先の絶対パス。</param>
        /// <param name="a_marginTopPx">上余白（px）。</param>
        /// <param name="a_marginBottomPx">下余白（px）。</param>
        /// <param name="a_marginLeftPx">左余白（px）。</param>
        /// <param name="a_marginRightPx">右余白（px）。</param>
        public async Task ExportAsync(FlowDocument a_doc, string a_outputPath,
            double a_marginTopPx, double a_marginBottomPx, double a_marginLeftPx, double a_marginRightPx)
        {
            string html = new HtmlDocumentBuilder(m_imageManager).BuildHtml(a_doc);

            // ブラウザ自体はアプリ起動中ずっと使い回す（ChromiumBrowserPool参照）。
            // 書き出しごとに新しくプロセスを起動・終了すると、それ自体に数秒単位の時間が
            // かかり、内容量に関係なく毎回待たされる原因になるため。
            IBrowser browser = await ChromiumBrowserPool.GetBrowserAsync();
            await using var page = await browser.NewPageAsync();
            await page.SetContentAsync(html);

            var pdfOptions = new PdfOptions
            {
                Format = PaperFormat.A4,
                PrintBackground = true,
                MarginOptions = new MarginOptions
                {
                    Top = FormatPx(a_marginTopPx),
                    Bottom = FormatPx(a_marginBottomPx),
                    Left = FormatPx(a_marginLeftPx),
                    Right = FormatPx(a_marginRightPx)
                }
            };
            await page.PdfAsync(a_outputPath, pdfOptions);
        }

        /// <summary>px単位の余白の値を、Puppeteer（Chromium）のMarginOptionsが受け付ける
        /// 「数値+単位」形式の文字列に変換する。実行環境のカルチャ（小数点の書式）に
        /// 依存しないよう、常にInvariantCultureで整形する。</summary>
        private static string FormatPx(double a_valuePx)
        {
            return a_valuePx.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
        }
    }
}
