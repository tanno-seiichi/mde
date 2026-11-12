// MarkdownConverter.cs
//
// mde (Markdown インラインエディタ) の一部。
// MarkdownテキストとFlowDocument（画面表示用の内部構造）を相互に変換するクラス。
// 画像の生成・解決にはImageManagerを、「編集していないブロックは元テキストのまま保存」の
// 仕組みにはOriginalTextTrackerを、それぞれ協力オブジェクトとして利用する。

using mde.logger;
using mde.manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace mde.common
{
    /// <summary>
    /// Markdown文字列 ⇔ FlowDocument の相互変換一式。見出し・段落・箇条書き（入れ子・順序付き
    /// 含む）・表・コードブロック・インライン装飾（太字/取り消し線/インラインコード/リンク/画像）
    /// に対応する。
    /// </summary>
    public class MarkdownConverter
    {
        // 表のヘッダー行の背景色。アウトラインビュー・フォルダペインの背景色とは意図的に別にしている。
        private static readonly Brush m_cellBorder = new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xE2));
        /// <summary>リンクの文字の色。メニュー「表示」→「文字の色」で色を変える（MainWindow.ApplyTextColor）ため、
        /// すべてのリンク（InlineStyleEditor で作るものも含む）でこの1つのブラシを共有する（凍結しない）。</summary>
        internal static readonly SolidColorBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x09, 0x69, 0xDA));
        private static readonly Brush m_linkBrush = LinkBrush;

        /// <summary>
        /// 表の列揃えで、「明示的な左揃え（:---）」と「揃え指定なし（---）」を区別して保存する
        /// ために、表セルの段落（Paragraph）のTagに設定するマーカー文字列。WPFの
        /// Paragraph.TextAlignment は Left/Center/Right/Justify の4値しか持てず、この2つは
        /// どちらも TextAlignment.Left になってしまい区別できないため、別途Tagで「明示的に
        /// 左揃えだった」ことだけを覚えておく（Center/Rightは TextAlignmentの値自体が
        /// 一意に対応するので、この印は不要）。
        /// </summary>
        private const string ALIGN_LEFT_EXPLICIT_TAG = "align-left-explicit";

        /// <summary>
        /// 文中の画像（HTML/Markdown）、`コード`、**太字**、~~取り消し線~~、&lt;u&gt;下線&lt;/u&gt;、
        /// ==ハイライト==、[リンク](a_url)、&lt;https://...&gt; 自動リンク、
        /// &lt;email@example.com&gt; メールアドレス自動リンク を検出する正規表現。
        /// 画像を囲むリンクは名前付きグループ linkedImage で先に検出し、既存の数値グループ番号を維持する。
        /// CommonMark/GFMには下線の標準的な記法がないため、下線はHTMLの&lt;u&gt;タグをそのまま
        /// 埋め込む方式にしている（既存の&lt;a id="..."&gt;アンカー・&lt;url&gt;自動リンクと同じ
        /// 「HTMLタグをそのまま埋め込む」という前例に倣った）。
        /// グループ番号は以下の通り。
        /// 1:imgタグ／2,3,4:![alt](src)（alt,src)／5,6:`code`（`` co`de `` のように2個以上のバッククォートで
        /// 囲んだものも。囲みのバッククォートは名前付きグループ bt で、.NET では名前付きグループの番号は
        /// 名前なしのグループの後になるため、6以降の番号は変わらない）／7,8:**bold**／9,10:~~strike~~／
        /// 11,12:&lt;u&gt;underline&lt;/u&gt;／13,14:==highlight==／15,16,17:[text](url)（text,url)／
        /// 18,19:&lt;http(s)://url&gt;／20,21:&lt;email&gt;／22,23:&lt;a id="..."&gt;&lt;/a&gt;／
        /// 24,25:文中にそのまま書かれたメールアドレス（BARE_EMAIL_PATTERN）
        /// </summary>
        /// <summary>m_inlineTokenRegex のどの書き方にも、必ず1つは含まれる文字（画像・リンク [ !、HTML の &lt;、コード `、
        /// 太字・斜体 * _、取り消し線 ~、強調 =、メールアドレス @）。これを1つも含まない文字は、どの書き方にも当てはまらない
        /// （AppendInlineMarkdownToParagraph で、正規表現で探すのを省くために使う）。m_inlineTokenRegex に書き方を
        /// 足す時は、ここも見直すこと。</summary>
        private static readonly char[] m_inlineMarkupChars = { '[', '!', '<', '`', '*', '_', '~', '=', '@' };

        /// <summary>画像・HTML の画像タグ・Markdown の画像・インラインコード（m_inlineTokenRegex の 1〜6 番のグループ）。</summary>
        private const string INLINE_TOKEN_HEAD_PATTERN =
            "(?<linkedImage>\\[(?<linkedImageImage>!\\[(?<linkedImageAlt>[^\\]]*)\\]\\((?<linkedImageSrc>(?:[^()]|\\([^()]*\\))+)\\))\\]\\((?<linkedImageHref>(?:[^()]|\\([^()]*\\))+)\\))|" +
            "(<img\\s+[^>]*?/?>)|(!\\[([^\\]]*)\\]\\(((?:[^()]|\\([^()]*\\))+)\\))|((?<!`)(?<bt>`+)(?!`)([\\s\\S]+?)(?<!`)\\k<bt>(?!`))";

        /// <summary>リンク・山括弧の URL とメールアドレス・アンカー・文中のメールアドレス（15〜25 番のグループ）。</summary>
        private const string INLINE_TOKEN_TAIL_PATTERN =
            "((?<!!)\\[([^\\]]*)\\]\\(((?:[^()]|\\([^()]*\\))+)\\))|(<(https?://[^\\s<>]+)>)|(<([^\\s<>@]+@[^\\s<>@]+\\.[^\\s<>@]+)>)|(<a\\s+id=\"([^\"]+)\"\\s*>\\s*</a>)|(" +
            BARE_EMAIL_PREV_PATTERN + "(" + BARE_EMAIL_PATTERN + ")" + BARE_EMAIL_NEXT_PATTERN + ")";

        /// <summary>
        /// 文中の書式の読み込み（ParseInlineSpecs）で使う、太字・斜体・取り消し線・下線・ハイライト以外の書き方
        /// （画像・コード・リンク等）だけを探す正規表現。グループの番号は m_inlineTokenRegex と同じにするため、
        /// 書式の書き方の所（7〜14 番のグループと it・itc・itu）は、何にも一致しない形にしてある。書式は、
        /// 入れ子（「~~**太字の取り消し線**~~」等）を読むため、TryParseEmphasis で別に読む。
        /// </summary>
        private static readonly Regex m_inlineTokenRegex = new Regex(
            INLINE_TOKEN_HEAD_PATTERN +
            "|((?!)())|((?!)())|((?!)())|((?!)())|" +
            INLINE_TOKEN_TAIL_PATTERN +
            "|(?<it>(?!)(?<itc>)(?<itu>))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 斜体（*斜体* / _斜体_）。m_inlineTokenRegex の最後の選択肢（名前付きグループ it。中身は
        /// itc（* で囲んだもの）か itu（_ で囲んだもの））。名前付きグループの番号は名前なしのグループの後に
        /// なるため、1〜25 の番号は変わらない。太字（**…**）の選択肢より後ろにあるので、** は太字として先に
        /// 解釈される。mdew・mdee（markdown.js）と同じ考え方：
        ///  - * は、文字の間でも使える（日本語の文中の *斜体* も斜体にする）。ただし ** の一部・中身の前後が
        ///    空白のもの（「2 * 3 * 4」等）は斜体にしない。
        ///  - _ は、英数字の単語の途中では使わない（snake_case 等のファイル名・変数名を斜体にしない）。
        /// </summary>
        public const string ITALIC_PATTERN =
            "(?<it>(?<!\\*)\\*(?![\\s*])(?<itc>[^*\\n]*?[^*\\s])\\*(?!\\*)" +
            "|(?<![\\w_])_(?![\\s_])(?<itu>[^_\\n]*?[^_\\s])_(?![\\w_]))";

        /// <summary>文中にそのまま（山括弧なしで）書かれたメールアドレス。表示だけ mailto のリンクにする
        /// （GitHub と同じ。保存時は書いたまま書き戻す。LinkInfo.IsBareEmailFlg参照）。ドメインは「.」で
        /// 区切られた2つ以上の部分で、最後は英字2文字以上。終わりの「.」（文の句点）は含めない。
        /// mdew の markdown.js の RE_BARE_EMAIL と同じ。</summary>
        public const string BARE_EMAIL_PATTERN =
            "[A-Za-z0-9._%+-]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*\\.[A-Za-z]{2,}";

        /// <summary>BARE_EMAIL_PATTERNの直前の条件：メールアドレス・URLの一部になる文字の直後からは始めない
        /// （http://user@host 等をメールアドレスとみなさないため）。</summary>
        public const string BARE_EMAIL_PREV_PATTERN = "(?<![A-Za-z0-9._%+\\-/@])";

        /// <summary>BARE_EMAIL_PATTERNの直後の条件：ドメインの途中で終わらない。</summary>
        public const string BARE_EMAIL_NEXT_PATTERN = "(?![A-Za-z0-9-]|\\.[A-Za-z0-9])";

        /// <summary>表のセルの行内改行を表す&lt;br&gt;（大文字小文字・&lt;br /&gt;のような
        /// スペースや自己終了スラッシュの有無を問わない）を検出する正規表現。TableToMarkdownで
        /// セル内の改行を&lt;br&gt;として書き出していることの対になる、読み込み側の処理
        /// （ConvertTableCellBrToLineBreak）で使う。</summary>
        private static readonly Regex m_tableCellBrRegex = new Regex("<br\\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 表のセルのMarkdownソース文字列（ParseTableRowで分割した1セル分）に含まれる
        /// &lt;br&gt;を、通常の段落と同じ行内改行の表現である実際の改行文字（'\n'）へ戻す。
        /// これにより、セルのテキストをAppendInlineMarkdownToParagraphへそのまま渡すだけで、
        /// 通常の段落（JoinParagraphSourceLines参照）と全く同じ仕組みで本物のLineBreakへ
        /// 復元される。TableToMarkdownでの書き出し側の処理と対になっている（詳細は同メソッドの
        /// コメント参照）。
        /// </summary>
        /// <param name="a_cellText">表のセル1つ分のMarkdownソース文字列。</param>
        /// <returns>&lt;br&gt;を'\n'に置き換えた文字列。</returns>
        private static string ConvertTableCellBrToLineBreak(string a_cellText)
        {
            return m_tableCellBrRegex.Replace(a_cellText, "\n");
        }

        /// <summary>
        /// 表のセル1つ分のMarkdownソース文字列（&lt;br&gt;を含む、ParseTableRowで分割した
        /// 生の文字列）から、TableCellを組み立てる。&lt;br&gt;で区切られた行それぞれを、
        /// 独立したParagraph（Marginを0にして、見た目には1つのセルの中の複数行にしか見えない
        /// ようにする）として追加する。
        /// LineBreakではなく複数のParagraphに分けているのは、WPFのLineBreakとTSF（IME）の
        /// 組み合わせで、改行直後にIME確定した文字が1つ手前に入ってしまう不具合があるため
        /// （HeadingCodeBlockEditor.InsertParagraphSplitAtCaretのコメント参照）。ライブ編集時に
        /// 同メソッドが作る構造と一致させることで、ファイルを開いた直後と実際に改行を入力した
        /// 直後とで内部構造が食い違わないようにしている。Markdownとしての書き出しはこの内部
        /// 表現の違いに影響されない（TableToMarkdown参照）。
        /// </summary>
        /// <param name="a_cellMarkdownText">セル1つ分のMarkdownソース文字列（&lt;br&gt;を含む）。</param>
        /// <param name="a_alignment">このセルが属する列の文字揃え。</param>
        /// <param name="a_tag">各段落のTagに設定する値（「明示的な左揃え」の印。通常はnull）。
        /// Markdown書き出し時に参照されるのはセルの最初の段落のTagだけだが（TableToMarkdownの
        /// ヘッダー行のTextAlignment/Tag検出部分参照）、すべての段落に同じ値を設定しておいても
        /// 書き出し結果には影響しない。</param>
        /// <param name="a_taskCheckboxFlg">セルの先頭の"[ ]"/"[x]"（後ろは空白かセルの終わり）をチェックボックスに
        /// するか（見出しの行以外のセルはtrue。箇条書きのタスクリストと同じ見た目・操作。書き出しは、チェックボックスの
        /// InlineUIContainerがAppendInlinesMarkdownで"[ ] "/"[x] "に戻る）。</param>
        /// <returns>組み立てたTableCell（罫線・Padding・背景色等、内容以外の見た目は
        /// 呼び出し側で設定すること）。</returns>
        private TableCell BuildTableCellFromMarkdown(string a_cellMarkdownText, TextAlignment a_alignment, object a_tag, bool a_taskCheckboxFlg)
        {
            string converted = ConvertTableCellBrToLineBreak(a_cellMarkdownText);
            string[] cellLines = converted.Split('\n');
            var cell = new TableCell();
            bool firstLineFlg = true;
            foreach (string cellLineText in cellLines)
            {
                string lineText = cellLineText;
                // セルの先頭の"[ ]"/"[x]"（チェックボックス）
                bool? taskChecked = null;
                if (a_taskCheckboxFlg && firstLineFlg)
                {
                    var taskMatch = Regex.Match(lineText, "^\\[([ xX])\\](?:[ \\t]+|$)");
                    if (taskMatch.Success)
                    {
                        taskChecked = " " != taskMatch.Groups[1].Value;
                        lineText = lineText.Substring(taskMatch.Length);
                    }
                }
                firstLineFlg = false;

                var p = new Paragraph
                {
                    Margin = new Thickness(0),
                    // 表のセルは「行間（EditorLineHeight）」の設定値を継承させない。
                    // Style TargetType="Paragraph"はFlowDocument内のすべての段落へ
                    // 暗黙的に適用されるため、ここで明示的にLineHeightを上書きしない限り、
                    // ユーザーが本文用に設定した行間がそのまま表のセルにも反映されてしまい、
                    // 1行しかない短いセルまで縦に間延びして見える。double.NaNを指定すると、
                    // WPF標準の「未指定（フォントの自然な行の高さ）」の挙動に戻るため、
                    // 表は常にコンパクトな行の高さで表示される。
                    // VSCodeスタイルの時は VSCode と同じ行の高さにする（BlockStyles.TableCellLineHeight参照）。
                    LineHeight = BlockStyles.TableCellLineHeight,
                    KeepTogether = true,
                    TextAlignment = a_alignment,
                    Tag = a_tag
                };
                AppendInlineMarkdownToParagraph(p, lineText, false);
                if (taskChecked.HasValue)
                {
                    // 箇条書きのタスクリスト（BuildNestedList）と同じく、チェックボックスを段落の先頭に置く。
                    // 段落の中身がチェックボックスだけになる時は、空のRunも一緒に入れる（理由は
                    // ListEditor.ConvertListItemTextToTaskCheckbox参照）
                    var container = BlockStyles.CreateTaskCheckboxContainer(taskChecked.Value);
                    if (p.Inlines.Count > 0)
                    {
                        p.Inlines.InsertBefore(p.Inlines.FirstInline, container);
                    }
                    else
                    {
                        p.Inlines.Add(container);
                        p.Inlines.Add(new Run(""));
                    }
                }
                cell.Blocks.Add(p);
            }
            return cell;
        }

        /// <summary>
        /// [text](url "title") / ![alt](src "title") の "(...)" の中身（URLとタイトルの両方を
        /// 含む生文字列）を、URL部分とタイトル部分（あれば）に分割する。
        /// </summary>
        /// <param name="a_raw">"(...)" の中身の生文字列。</param>
        /// <param name="a_url">分割後のURL部分。</param>
        /// <param name="a_title">分割後のタイトル部分（タイトルが無ければnull）。</param>
        private static void SplitUrlAndTitle(string a_raw, out string a_url, out string a_title)
        {
            var m = Regex.Match(a_raw, "^(.*?)\\s+[\"']([^\"']*)[\"']\\s*$");
            if (m.Success)
            {
                a_url = m.Groups[1].Value;
                a_title = m.Groups[2].Value;
            }
            else
            {
                a_url = a_raw;
                a_title = null;
            }
        }

        private readonly OriginalTextTracker m_originalTextTracker;

        /// <summary>連続する1つ以上のHTMLコメントをまとめて保持するクラス。各コメントごとに、
        /// その直前にあった空行の数（直前の要素がコメントなら1つ前のコメントとの間、先頭の
        /// コメントなら直前の実際のBlockとの間、またはファイル先頭からの空行数）も一緒に
        /// 覚えておく。0は「空行なしで直接連続していた」ことを意味する。DocumentToMarkdown・
        /// GetMarkdownOffsetForBlockで、元のソースにあった空行の数をそのまま再現するために
        /// 使う（詳細は<see cref="m_blankLinesBeforeBlock"/>のコメントも参照）。</summary>
        private class CommentGroupInfo
        {
            public readonly List<(int BlankLinesBefore, string Text)> m_items = new List<(int, string)>();
        }

        /// <summary>HTMLコメント（&lt;!-- --&gt;）を、Markdownモードで完全に非表示にする
        /// （空行すら残さない）ための仕組み。コメントは独立したBlockとしては表示に追加せず、
        /// 直後（無ければ直前）の実際のBlockに対して、書き出し時にだけ差し込む文字列群として
        /// 覚えておく。キーはBlock参照（ConditionalWeakTableで、不要になれば自動的に解放
        /// される）。MarkdownToDocument（記録）とDocumentToMarkdown・GetMarkdownOffsetForBlock
        /// （書き出し）で共有する。</summary>
        private readonly ConditionalWeakTable<Block, CommentGroupInfo> m_leadingComments = new ConditionalWeakTable<Block, CommentGroupInfo>();

        /// <summary>ファイル末尾がコメントだけで終わる（直後に実際のBlockが無い）場合に、
        /// 直前のBlockに対して「後に付くコメント」として覚えておく先。詳細は
        /// <see cref="m_leadingComments"/>参照。</summary>
        private readonly ConditionalWeakTable<Block, CommentGroupInfo> m_trailingComments = new ConditionalWeakTable<Block, CommentGroupInfo>();

        /// <summary>各トップレベルBlockの「直前（直前のBlock、直前のHTMLコメント、または
        /// ファイル先頭）との間にあった空行の数」を覚えておく仕組み。Markdownモードへの変換を
        /// 経ても、元のソースにあった空行の数（0個＝空行なしで直接連続、1個、2個以上…）を
        /// そのまま復元できるようにするために使う。これが無いと、Markdownモードとソースモードを
        /// 何度も往復するだけで、実際には何も編集していない（元々ソースモードに存在しなかった
        /// 改行が増える）のに保存内容が変わってしまい（空行の数が常に1個に揃えられてしまう
        /// ため）、ファイルを開いてモードを切り替えただけで「変更あり」の警告が出る、といった
        /// 問題が起きる。記録するのはMarkdownToDocument（ソースの読み込み）で作られたBlockの
        /// みで、Markdownモードでの編集によって新しく作られたBlock（段落の分割など）には
        /// 記録が無い。書き出し時（DocumentToMarkdown・GetMarkdownOffsetForBlock）、記録が
        /// 無いBlockについては、空行1個（従来通りの既定の区切り方）を使う。これにより、
        /// 「読み込んだファイルの、編集していない箇所の空行の数はそのまま保たれる」ことと、
        /// 「Markdownモードでの編集によって新しく生まれる区切りは、これまで通り空行1個になる」
        /// ことの両方を満たす。キーはBlock参照（ConditionalWeakTableで自動的に解放される）。
        /// 値はint1つを保持するためのSystem.Runtime.CompilerServices.StrongBox&lt;int&gt;
        /// （ConditionalWeakTableの値は参照型である必要があるための入れ物。intそのものは
        /// 値型のため直接は使えない）。なお、ファイルの一番最初のBlock（またはコメント）の
        /// 直前にあった空行や、ファイル末尾の空行は、これまでと同様に保持・復元の対象外
        /// （常に読み飛ばされる）である。</summary>
        private readonly ConditionalWeakTable<Block, StrongBox<int>> m_blankLinesBeforeBlock = new ConditionalWeakTable<Block, StrongBox<int>>();

        /// <summary>あるBlockについて<see cref="m_blankLinesBeforeBlock"/>に記録が無い場合
        /// （Markdownモードでの編集によって新しく作られたBlockなど、ソースの読み込みを
        /// 経由していない場合）に使う、既定の「直前の空行の数」。1（＝空行1個）にすることで、
        /// 記録が無いBlockの前後は、これまで通りの区切り方（常に空行1個）になる。</summary>
        private const int DEFAULT_BLANK_LINES_BEFORE_BLOCK = 1;

        /// <summary>読み込み元のMarkdownソースが、ファイル末尾に改行を持っていたか
        /// どうか（多くのテキストエディタ・Gitなどの慣習で、最後の行の末尾にも改行を
        /// 1つ付けるのが一般的）。MarkdownToDocumentで記録し、DocumentToMarkdownで
        /// 保存時に再現する。これが無いと、無編集でファイルを開いて上書き保存しただけ
        /// でファイル末尾の改行が失われてしまう。</summary>
        private bool m_hasTrailingNewlineAtEof;

        /// <summary>読み込み元のMarkdownソースで、最後の実際の内容（Block・HTMLコメント）
        /// より後、ファイル末尾までにあった、本当に空行だった行の数（ファイル末尾の
        /// 改行そのもの＝<see cref="m_hasTrailingNewlineAtEof"/>とは別に数える）。
        /// MarkdownToDocumentで記録し、DocumentToMarkdownで保存時に再現する。</summary>
        private int m_trailingBlankLinesAtEof;

        private readonly ImageManager m_imageManager;
        private readonly Func<bool> m_preserveSourceLineBreaksFlg;
        private readonly Func<bool> m_correctColumnWidthsFlg;
        private readonly Func<double> m_availableTableWidthFunc;
        private readonly Func<bool> m_uniformMarkerStyleFlg;

        /// <summary>
        /// MarkdownConverterを構築する。
        /// </summary>
        /// <param name="a_originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="a_imageManager">画像の生成・解決を担当するクラス。</param>
        /// <param name="a_preserveSourceLineBreaksFlg">段落中の途中改行（空行を伴わない、ソース上の
        /// 単純な改行）を、そのまま見た目の改行として表示するか（true。mde/Typora従来の表示）、
        /// それとも空行が入るまでは改行しない、CommonMark/VSCodeのMarkdownプレビュー標準の表示に
        /// するか（false）を返すデリゲート。省略時（null）はtrue（従来動作）として扱う。</param>
        /// <param name="a_correctColumnWidthsFlg">メニュー「表示」→「列幅を補正する」の現在の
        /// 状態を返すデリゲート。省略時（null）はtrue（列幅調整ダイアログでの調整・未調整の表への
        /// 自動計算比率のどちらも有効）として扱う。詳細はBlockStyles.ApplyEffectiveColumnWidths
        /// 参照。</param>
        /// <param name="a_availableTableWidthFunc">未調整（自動計算）の表を、コンパクトな固定幅
        /// （Pixel）にできるかどうかの判断に使う、表を表示できる実際の幅（px）を返すデリゲート。
        /// 省略時（null）は常にnullとして扱われ（実際の表示幅が分からないため）、常にこれまでと
        /// 同じStar比率になる（保存専用の使い捨て文書やPDF書き出し用の一時文書に対する変換など、
        /// 実際の表示幅を持たない・見た目が結果に影響しない呼び出し側向け）。詳細はBlockStyles.
        /// ApplyAutoCalculatedColumnWidthsのコメント参照。</param>
        /// <param name="a_uniformMarkerStyleFlg">メニュー「表示」→「箇条書きの記号」の
        /// 現在の状態を返すデリゲート。true＝段数によらずすべての階層で同じ記号（Disc）を使う、
        /// false＝WPF標準の割り当て（1段目Disc・2段目Circle・3段目以降Box）。省略時（null）は
        /// false（従来動作）として扱う。詳細はBlockStyles.UnorderedMarkerStyleForDepth参照。</param>
        public MarkdownConverter(OriginalTextTracker a_originalTextTracker, ImageManager a_imageManager,
            Func<bool> a_preserveSourceLineBreaksFlg = null, Func<bool> a_correctColumnWidthsFlg = null,
            Func<double> a_availableTableWidthFunc = null, Func<bool> a_uniformMarkerStyleFlg = null)
        {
            this.m_originalTextTracker = a_originalTextTracker;
            this.m_imageManager = a_imageManager;
            this.m_preserveSourceLineBreaksFlg = a_preserveSourceLineBreaksFlg;
            this.m_correctColumnWidthsFlg = a_correctColumnWidthsFlg;
            this.m_availableTableWidthFunc = a_availableTableWidthFunc;
            this.m_uniformMarkerStyleFlg = a_uniformMarkerStyleFlg;
        }

        // ======================================================================
        //  FlowDocument → Markdown
        // ======================================================================

        /// <summary>
        /// 元のソースで、あるトップレベル要素（Block・HTMLコメント）の直前にあった空行の数から、
        /// 書き出し時に実際に使う区切りの改行文字列を求める。文書の一番最初の要素の前には
        /// 区切りを入れない（空文字列）。0個（空行なしで直接連続）なら改行1つ、N個なら
        /// 改行(N+1)個（=空行N個）を挟む。DocumentToMarkdown・GetMarkdownOffsetForBlockの
        /// 両方から、同じ規則で使う。</summary>
        /// <param name="a_blankLinesBefore">元のソースで直前にあった空行の数。</param>
        /// <param name="a_isFirstFlg">文書内で最初に書き出す要素かどうか。</param>
        /// <returns>区切りとして挿入する文字列。</returns>
        private static string BuildElementSeparator(int a_blankLinesBefore, bool a_isFirstFlg)
        {
            if (a_isFirstFlg)
            {
                return "";
            }
            return new string('\n', Math.Max(1, a_blankLinesBefore + 1));
        }

        /// <summary>
        /// FlowDocumentをMarkdown文字列へ書き出す。編集されていないブロックは元テキストを
        /// そのまま使い（OriginalTextTracker参照）、編集済み・新規のブロックは現在の構造から
        /// 新しく組み立て直す。要素同士の間の空行の数は、元のソースにあった数をそのまま
        /// 再現する（<see cref="m_blankLinesBeforeBlock"/>参照）。これにより、実際には何も
        /// 編集していないのにMarkdownモードとソースモードを往復しただけで保存内容が変わって
        /// しまう、という問題を防いでいる。
        /// </summary>
        /// <param name="a_doc">対象の文書。</param>
        /// <returns>改行はすべて "\n" のMarkdownテキスト（実ファイルへの改行コード変換は保存時に行う）。</returns>
        public string DocumentToMarkdown(FlowDocument a_doc)
        {
            if (!DebugLogger.IsEnabled)
            {
                return DocumentToMarkdownCore(a_doc);
            }
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            string md = DocumentToMarkdownCore(a_doc);
            DebugLogger.Log($"DocumentToMarkdown: {stopwatch.Elapsed.TotalMilliseconds:F1}ms blocks={a_doc.Blocks.Count} chars={md.Length}");
            return md;
        }

        /// <summary>DocumentToMarkdown の本体（DocumentToMarkdown は、ログが有効な時に、かかった時間を記録する）。</summary>
        /// <param name="a_doc">対象の文書。</param>
        /// <returns>Markdownテキスト。</returns>
        private string DocumentToMarkdownCore(FlowDocument a_doc)
        {
            var sb = new StringBuilder();
            bool firstFlg = true;

            // HTMLコメントのグループ（連続する1つ以上のコメント）を、各コメント自身が覚えている
            // 「直前の空行の数」をそのまま再現しながら書き出す。
            void AppendCommentGroup(CommentGroupInfo a_group)
            {
                foreach (var item in a_group.m_items)
                {
                    sb.Append(BuildElementSeparator(item.BlankLinesBefore, firstFlg));
                    sb.Append(item.Text);
                    firstFlg = false;
                }
            }

            foreach (Block block in a_doc.Blocks)
            {
                // <br>による段落内改行の「続き」段落（MarkdownToDocument参照）は、通常の
                // ブロック区切り（空行）ではなく、直前の行へ<br>で連結する。文書の一番最初の
                // 要素がいきなり続き段落になることはないはずだが（続き段落の前には必ず、その
                // 分割元となった段落の先頭部分があるため）、念のためfirstFlgでも判定する。
                if (block is Paragraph contPara && contPara.Tag is BrContinuationInfo)
                {
                    string contS = BlockToMarkdown(block);
                    if (!firstFlg)
                    {
                        sb.Append("<br>\n");
                    }
                    sb.Append(contS);
                    firstFlg = false;
                    // <br>の続き段落が文書の最後のBlockで、その直後（ファイル末尾）にHTMLコメント
                    // だけがあった場合、そのコメントはこの続き段落へ「後に付くコメント」として
                    // 結び付けられている（MarkdownToDocument参照）。上のcontinueでこの分岐を
                    // 抜けてしまうと、下のm_trailingCommentsのチェックに到達せず、コメントの
                    // 内容が保存時に失われてしまうため、ここでも必ずチェックする。
                    if (m_trailingComments.TryGetValue(block, out var trailingGroupForCont))
                    {
                        AppendCommentGroup(trailingGroupForCont);
                    }
                    continue;
                }
                // 引用の2行目以降の続き段落（QuoteContinuationInfo。MarkdownToDocument・
                // HeadingCodeBlockEditor.InsertQuoteContinuationParagraph参照）も、上の
                // BrContinuationInfoと同じ考え方で、通常のブロック区切り（空行）ではなく、
                // 単純な"\n"で直前の行へ連結する（それぞれの行は既にBlockToMarkdown側で
                // "> "が付くため、ここでは行区切りの"\n"だけを挟めばよい）。
                if (block is Paragraph quoteContPara && quoteContPara.Tag is QuoteContinuationInfo)
                {
                    string quoteContS = BlockToMarkdown(block);
                    if (!firstFlg)
                    {
                        sb.Append("\n");
                    }
                    sb.Append(quoteContS);
                    firstFlg = false;
                    if (m_trailingComments.TryGetValue(block, out var trailingGroupForQuoteCont))
                    {
                        AppendCommentGroup(trailingGroupForQuoteCont);
                    }
                    continue;
                }
                // コードブロックの2行目以降の続き段落（CodeBlockContinuationInfo。
                // MarkdownToDocument・HeadingCodeBlockEditor.InsertCodeBlockContinuation
                // Paragraph参照）も、上のQuoteContinuationInfoと全く同じ考え方で、通常の
                // ブロック区切り（空行）ではなく、単純な"\n"で直前の行へ連結する（閉じ
                // フェンスの有無はBlockToMarkdown側で、自分がグループの最後の行かどうかに
                // 応じて付けるため、ここでは行区切りの"\n"だけを挟めばよい）。
                if (block is Paragraph codeContPara && codeContPara.Tag is CodeBlockContinuationInfo)
                {
                    string codeContS = BlockToMarkdown(block);
                    if (!firstFlg)
                    {
                        sb.Append("\n");
                    }
                    sb.Append(codeContS);
                    firstFlg = false;
                    if (m_trailingComments.TryGetValue(block, out var trailingGroupForCodeCont))
                    {
                        AppendCommentGroup(trailingGroupForCodeCont);
                    }
                    continue;
                }
                // 大きな表を分けた続きのかたまり（TableChunks）は、先頭のかたまりが表全体を書き出すため、ここでは何も書き出さない
                if (TableChunks.IsContinuation(block))
                {
                    if (m_trailingComments.TryGetValue(block, out var trailingGroupForTableCont))
                    {
                        AppendCommentGroup(trailingGroupForTableCont);
                    }
                    continue;
                }
                // このBlockの直前にあったHTMLコメント（Blockとしては存在しない。
                // m_leadingComments参照）を、元のソースにあった空行の数を再現しながら先に出力する。
                if (m_leadingComments.TryGetValue(block, out var leadingGroup))
                {
                    AppendCommentGroup(leadingGroup);
                }
                string s = m_originalTextTracker.TryGetOriginal(block, out var original) ? original : BlockToMarkdown(block);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    int blankLinesBeforeBlock = m_blankLinesBeforeBlock.TryGetValue(block, out var box) ? box.Value : DEFAULT_BLANK_LINES_BEFORE_BLOCK;
                    sb.Append(BuildElementSeparator(blankLinesBeforeBlock, firstFlg));
                    sb.Append(s);
                    firstFlg = false;
                }
                // ファイル末尾がコメントだけで終わる場合の、このBlockに付随する「後に付く
                // コメント」（m_trailingComments参照）。
                if (m_trailingComments.TryGetValue(block, out var trailingGroup))
                {
                    AppendCommentGroup(trailingGroup);
                }
            }
            // 元のソースがファイル末尾に持っていた改行・空行を再現する（m_hasTrailingNewlineAtEof・
            // m_trailingBlankLinesAtEof参照）。何も書き出していない（空の文書の）場合は何も付け
            // 加えない。
            if (!firstFlg && m_hasTrailingNewlineAtEof)
            {
                sb.Append('\n');
                sb.Append('\n', m_trailingBlankLinesAtEof);
            }
            return sb.ToString();
        }

        /// <summary>
        /// DocumentToMarkdownが生成する文字列の中で、指定したトップレベルブロックの内容が
        /// 開始する文字位置を求める。モード切替時（Markdownモード→ソースモード）のカーソル
        /// 位置復元のために使う。DocumentToMarkdownと全く同じ規則（空白のみのブロックは
        /// 出力されない、要素間は元のソースにあった空行の数をそのまま再現する）に従って
        /// 計算するが、DocumentToMarkdown自体は変更しない（既存の呼び出し箇所への影響を
        /// 避けるため）。
        /// </summary>
        /// <param name="a_doc">対象の文書。</param>
        /// <param name="a_targetBlock">位置を求めたいトップレベルブロック。</param>
        /// <returns>該当ブロックの開始文字位置。対象ブロックが見つからない、または
        /// （空白のみで）出力されない場合は -1。</returns>
        public int GetMarkdownOffsetForBlock(FlowDocument a_doc, Block a_targetBlock)
        {
            int offset = 0;
            bool firstFlg = true;

            void AddCommentGroupOffset(CommentGroupInfo a_group)
            {
                foreach (var item in a_group.m_items)
                {
                    offset += BuildElementSeparator(item.BlankLinesBefore, firstFlg).Length;
                    offset += item.Text.Length;
                    firstFlg = false;
                }
            }

            // 大きな表を分けた続きのかたまり（TableChunks）：表の先頭のかたまりの位置から、そのかたまりの最初の行の位置まで
            // 進めた位置（表の1行目は見出しの行、2行目は区切りの行で、表全体で n 番目（0始まり）の行は n + 1 行目）
            if (a_targetBlock is Table targetTable && TableChunks.IsContinuation(targetTable))
            {
                Table head = TableChunks.GetHead(targetTable);
                int headOffset = GetMarkdownOffsetForBlock(a_doc, head);
                if (headOffset < 0)
                {
                    return -1;
                }
                string tableText = m_originalTextTracker.TryGetOriginal(head, out var headOriginal) ? headOriginal : BlockToMarkdown(head);
                int rowsBefore = 0;
                foreach (Table chunk in TableChunks.GetChunks(head))
                {
                    if (ReferenceEquals(chunk, targetTable))
                    {
                        break;
                    }
                    rowsBefore += TableChunks.CountRows(chunk);
                }
                int pos = 0;
                for (int line = 0; line < rowsBefore + 1; line++)
                {
                    int newline = tableText.IndexOf('\n', pos);
                    if (newline < 0)
                    {
                        return headOffset;
                    }
                    pos = newline + 1;
                }
                return headOffset + pos;
            }

            foreach (Block block in a_doc.Blocks)
            {
                // DocumentToMarkdownの<br>連結規則（直前の行へ"<br>\n"で連結）と一致させる。
                if (block is Paragraph contPara && contPara.Tag is BrContinuationInfo)
                {
                    if (!firstFlg)
                    {
                        offset += "<br>\n".Length;
                    }
                    string contS = BlockToMarkdown(block);
                    if (ReferenceEquals(block, a_targetBlock))
                    {
                        return offset;
                    }
                    offset += contS.Length;
                    firstFlg = false;
                    // DocumentToMarkdownと同じく、この続き段落に「後に付くコメント」
                    // （m_trailingComments）が結び付いている場合、以降のオフセットがずれない
                    // よう加算しておく（このBlockが文書の最後の場合、実際には後続ブロックは
                    // 存在しないため影響しないが、一貫性のため処理しておく）。
                    if (m_trailingComments.TryGetValue(block, out var trailingGroupForCont))
                    {
                        AddCommentGroupOffset(trailingGroupForCont);
                    }
                    continue;
                }
                // DocumentToMarkdownの引用の続き段落の連結規則（直前の行へ単純な"\n"で連結）と
                // 一致させる。
                if (block is Paragraph quoteContPara && quoteContPara.Tag is QuoteContinuationInfo)
                {
                    if (!firstFlg)
                    {
                        offset += "\n".Length;
                    }
                    string quoteContS = BlockToMarkdown(block);
                    if (ReferenceEquals(block, a_targetBlock))
                    {
                        return offset;
                    }
                    offset += quoteContS.Length;
                    firstFlg = false;
                    if (m_trailingComments.TryGetValue(block, out var trailingGroupForQuoteCont))
                    {
                        AddCommentGroupOffset(trailingGroupForQuoteCont);
                    }
                    continue;
                }
                // DocumentToMarkdownのコードブロックの続き段落の連結規則（直前の行へ単純な
                // "\n"で連結）と一致させる。
                if (block is Paragraph codeContPara && codeContPara.Tag is CodeBlockContinuationInfo)
                {
                    if (!firstFlg)
                    {
                        offset += "\n".Length;
                    }
                    string codeContS = BlockToMarkdown(block);
                    if (ReferenceEquals(block, a_targetBlock))
                    {
                        return offset;
                    }
                    offset += codeContS.Length;
                    firstFlg = false;
                    if (m_trailingComments.TryGetValue(block, out var trailingGroupForCodeCont))
                    {
                        AddCommentGroupOffset(trailingGroupForCodeCont);
                    }
                    continue;
                }
                // DocumentToMarkdownと同じく、大きな表の続きのかたまりは何も書き出さない
                if (TableChunks.IsContinuation(block))
                {
                    if (m_trailingComments.TryGetValue(block, out var trailingGroupForTableCont))
                    {
                        AddCommentGroupOffset(trailingGroupForTableCont);
                    }
                    continue;
                }
                // DocumentToMarkdownと同じく、このBlockの直前のHTMLコメント分もオフセットに
                // 加算してから本体へ進む（対象ブロック自身がコメントになることはない）。
                if (m_leadingComments.TryGetValue(block, out var leadingGroup))
                {
                    AddCommentGroupOffset(leadingGroup);
                }
                string s = m_originalTextTracker.TryGetOriginal(block, out var original) ? original : BlockToMarkdown(block);
                if (string.IsNullOrWhiteSpace(s))
                {
                    if (ReferenceEquals(block, a_targetBlock))
                    {
                        return -1;
                    }
                    continue;
                }
                int blankLinesBeforeBlock = m_blankLinesBeforeBlock.TryGetValue(block, out var box) ? box.Value : DEFAULT_BLANK_LINES_BEFORE_BLOCK;
                offset += BuildElementSeparator(blankLinesBeforeBlock, firstFlg).Length;
                if (ReferenceEquals(block, a_targetBlock))
                {
                    return offset;
                }
                offset += s.Length;
                firstFlg = false;
                // このBlockに付随する「後に付くコメント」（ファイル末尾がコメントだけの場合）分も、
                // 以降のブロックのオフセットがずれないよう加算しておく。
                if (m_trailingComments.TryGetValue(block, out var trailingGroup))
                {
                    AddCommentGroupOffset(trailingGroup);
                }
            }
            return -1;
        }

        /// <summary>
        /// コピーした範囲のブロックの並びをMarkdownにする（mde どうしのコピー・貼り付け用。MainWindow.
        /// TryBuildSelectionMarkdown）。DocumentToMarkdownと同じ規則でつなぐが、元のソースの空行の数・
        /// HTMLコメント・元の文字（OriginalTextTracker）は使わず、ブロックの間は常に空行1つにする。
        /// </summary>
        /// <param name="a_blocks">ブロックの並び（文書の中のブロックと、範囲に合わせて切り詰めた複製が混ざってよい）。</param>
        /// <returns>Markdown。</returns>
        public string BlocksToMarkdown(IEnumerable<Block> a_blocks)
        {
            var sb = new StringBuilder();
            bool firstFlg = true;
            foreach (Block block in a_blocks)
            {
                // 大きな表を分けた続きのかたまり（TableChunks）は、先頭のかたまりが表全体を書き出す
                if (TableChunks.IsContinuation(block))
                {
                    continue;
                }
                string s = BlockToMarkdown(block);
                if (block is Paragraph contPara &&
                    (contPara.Tag is BrContinuationInfo || contPara.Tag is QuoteContinuationInfo || contPara.Tag is CodeBlockContinuationInfo))
                {
                    if (!firstFlg)
                    {
                        sb.Append(contPara.Tag is BrContinuationInfo ? "<br>\n" : "\n");
                    }
                    sb.Append(s);
                    firstFlg = false;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(s))
                {
                    continue;
                }
                sb.Append(BuildElementSeparator(DEFAULT_BLANK_LINES_BEFORE_BLOCK, firstFlg));
                sb.Append(s);
                firstFlg = false;
            }
            return sb.ToString();
        }

        /// <summary>段落の中の文字を、書式付きのMarkdown（インライン）にする（段落の種類の記号は付けない。
        /// mde どうしのコピー・貼り付けで、1つの段落の中だけを選んでいた時に使う）。</summary>
        /// <param name="a_p">段落。</param>
        /// <returns>Markdown。</returns>
        public string ParagraphInlinesToMarkdown(Paragraph a_p)
        {
            var sb = new StringBuilder();
            AppendInlinesMarkdown(a_p.Inlines, sb);
            return sb.ToString().Trim('\r', '\n');
        }

        /// <summary>1つのトップレベルブロックを、種類（見出し/段落・コードブロック・引用・箇条書き・
        /// 表）に応じて適切な変換関数へ振り分ける。コードブロック丸ごとコピー機能からも利用される。</summary>
        /// <param name="a_block">対象のブロック。</param>
        /// <returns>変換後のMarkdown文字列。</returns>
        public string BlockToMarkdown(Block a_block)
        {
            if (a_block is Paragraph p)
            {
                if (p.Tag is HorizontalRuleInfo)
                {
                    return "---";
                }
                if (p.Tag is CodeBlockInfo codeInfo)
                {
                    // コードブロックの先頭行。1つのParagraphは常に1行分の内容だけを持つ
                    // （Enterによる続きの行は別のParagraph＝CodeBlockContinuationInfoとして
                    // 表現しており、LineBreakは使わない。CodeBlockContinuationInfoのコメント
                    // 参照）。後ろに続きの行が無ければ（単独の1行だけのコードブロックなら）
                    // ここで閉じフェンスまで書き出す。続きの行がある場合は閉じフェンスを
                    // 書かず、グループの最後の行（下のCodeBlockContinuationInfo分岐）に
                    // 任せる。続き行どうしの間（コメント・空行を挟まない、単純な"\n"での
                    // 連結）はDocumentToMarkdown・GetMarkdownOffsetForBlockのCodeBlock
                    // ContinuationInfo分岐が担当する。
                    var sb = new StringBuilder();
                    AppendInlinesMarkdown(p.Inlines, sb);
                    string codeLineText = sb.ToString().Trim('\r', '\n');
                    bool codeHasContinuationFlg = p.NextBlock is Paragraph nextCodePara && nextCodePara.Tag is CodeBlockContinuationInfo;
                    string opening = "```" + codeInfo.Language + "\n" + codeLineText;
                    return codeHasContinuationFlg ? opening : opening + "\n```";
                }
                if (p.Tag is CodeBlockContinuationInfo)
                {
                    // コードブロックの2行目以降。自分がグループの最後の行であれば閉じフェンスを
                    // 付け、まだ後ろに続きの行があれば付けない（上のCodeBlockInfo分岐と同じ
                    // 考え方）。
                    var sb = new StringBuilder();
                    AppendInlinesMarkdown(p.Inlines, sb);
                    string codeLineText = sb.ToString().Trim('\r', '\n');
                    bool codeHasMoreContinuationFlg = p.NextBlock is Paragraph nextCodeContPara && nextCodeContPara.Tag is CodeBlockContinuationInfo;
                    return codeHasMoreContinuationFlg ? codeLineText : codeLineText + "\n```";
                }
                if (p.Tag is QuoteInfo || p.Tag is QuoteContinuationInfo)
                {
                    // 引用の先頭行（QuoteInfo）・続きの行（QuoteContinuationInfo）のいずれも、
                    // 1つのParagraphは常に1行分の内容だけを持つ（Shift+Enterによる続きの行は
                    // 別のParagraphとして表現しており、LineBreakは使わない。QuoteContinuation
                    // Infoのコメント参照）。このParagraph自身の内容の先頭に"> "を付けるだけで
                    // よく、複数行への分割は不要だが、念のためParagraphInlineToMarkdownの
                    // 結果に"\n"が含まれていた場合（想定外の経路で混入した場合）にも対応できる
                    // よう、行ごとに分割してそれぞれへ"> "を付ける処理のままにしている。
                    // 続き行どうしの間（コメント・空行を挟まない、単純な"\n"での連結）は
                    // DocumentToMarkdown・GetMarkdownOffsetForBlockのQuoteContinuationInfo
                    // 分岐が担当する。
                    string quoteText = ParagraphInlineToMarkdown(p);
                    return string.Join("\n", quoteText.Split('\n').Select(l => "> " + l));
                }
                int level = p.Tag is int lv ? lv : 0;
                string text = ParagraphInlineToMarkdown(p);
                return level > 0 ? new string('#', level) + " " + text : text;
            }
            if (a_block is List list)
            {
                return ListToMarkdown(list, 0);
            }
            if (a_block is Table table)
            {
                return TableToMarkdown(table);
            }
            return "";
        }

        /// <summary>
        /// 箇条書き（入れ子含む）をMarkdownへ書き出す。順序付きリストは連番で書き出すが、
        /// リストが「常に同じ数字を使う」スタイル（Tag=="const"）としてマークされている場合は
        /// すべての項目を "1." として書き出す。
        /// </summary>
        /// <param name="a_list">対象のリスト。</param>
        /// <param name="a_level">見出しレベル。</param>
        /// <returns>変換後のMarkdown文字列。</returns>
        private string ListToMarkdown(List a_list, int a_level)
        {
            string indent = new string(' ', a_level * 3);
            bool orderedFlg = a_list.MarkerStyle == TextMarkerStyle.Decimal;
            bool constantNumberingFlg = orderedFlg && (a_list.Tag as string) == "const";
            string bulletMarker = orderedFlg ? null : ((a_list.Tag as string) ?? "*");
            var lines = new List<string>();
            int number = 1;
            foreach (ListItem li in a_list.ListItems)
            {
                // 項目自身の内容は、Shift+Enterによる段落分割（HeadingCodeBlockEditor.
                // InsertParagraphSplitAtCaret。理由はBuildTableCellFromMarkdownのコメント参照）
                // により、入れ子のサブリストより前に複数のParagraphが並ぶことがある。そのため
                // サブリストが現れるまでのown-paragraphをすべて集め、念のため各段落内の'\n'
                // （通常は発生しない）でも分割して、1項目分の継続行一覧を組み立てる。
                var parts = new List<string>();
                foreach (Block b in li.Blocks)
                {
                    if (b is List)
                    {
                        break; // 入れ子のサブリストより後ろは対象外
                    }
                    if (b is Paragraph ownPara)
                    {
                        parts.AddRange(ParagraphInlineToMarkdown(ownPara).Split('\n'));
                    }
                }
                if (0 == parts.Count)
                {
                    parts.Add("");
                }
                string prefix = orderedFlg ? ((constantNumberingFlg ? 1 : number) + ". ") : (bulletMarker + " ");
                lines.Add(indent + prefix + parts[0]);
                string contIndent = indent + new string(' ', prefix.Length);
                for (int k = 1; k < parts.Count; k++)
                {
                    lines.Add(contIndent + parts[k]);
                }

                foreach (Block b in li.Blocks)
                {
                    if (b is List nested)
                    {
                        lines.Add(ListToMarkdown(nested, a_level + 1));
                    }
                }
                number++;
            }
            return string.Join("\n", lines);
        }

        /// <summary>表をGitHub-flavored Markdown形式の表構文へ書き出す。</summary>
        /// <param name="a_table">対象の表。</param>
        /// <returns>変換後のMarkdown文字列。</returns>
        private string TableToMarkdown(Table a_table)
        {
            // 大きな表を分けたかたまり（TableChunks）は、先頭のかたまりが、すべてのかたまりの行を1つの表として書き出す
            // （続きのかたまりは何も書き出さない）
            if (TableChunks.IsContinuation(a_table))
            {
                return "";
            }
            var rows = TableChunks.GetRows(a_table);
            if (0 == rows.Count)
            {
                return "";
            }

            var mdRows = new List<string>();
            foreach (var row in rows)
            {
                var cells = new List<string>();
                foreach (TableCell cell in row.Cells)
                {
                    var sb = new StringBuilder();
                    // セル内の改行はLineBreakではなく複数のParagraphとして保持されている
                    // （理由はBuildTableCellFromMarkdownのコメント参照）。各Paragraphのテキストを
                    // '\n'で連結する。
                    bool firstParaFlg = true;
                    foreach (Block b in cell.Blocks)
                    {
                        if (b is Paragraph cp)
                        {
                            if (!firstParaFlg)
                            {
                                sb.Append('\n');
                            }
                            string paraMarkdown = ParagraphInlineToMarkdown(cp);
                            if (firstParaFlg)
                            {
                                // セルの先頭のチェックボックス（"[ ] "/"[x] "）の直後に打った空白は、その後ろの空白と
                                // 重なるので1つにする
                                paraMarkdown = Regex.Replace(paraMarkdown, "^(\\[[ x]\\]) +", "$1 ");
                            }
                            sb.Append(paraMarkdown);
                            firstParaFlg = false;
                        }
                    }
                    // セル内の改行（'\n'）は<br>に変換して1つの物理行に収める。表の読み込みは
                    // 「|で始まる行が続く間は表」という行単位の判定のため（ParseTableRow・
                    // MarkdownToDocumentの表解析部分を参照）、生の'\n'を残すとその行がそこで
                    // 終わり、続きが表として認識されず構造が壊れる。GFMの慣例に合わせた形。
                    // 読み込み側（ConvertTableCellBrToLineBreak）で元の'\n'へ戻してから
                    // AppendInlineMarkdownToParagraphへ渡し、通常の段落と同じ仕組みで
                    // LineBreakへ復元する。
                    cells.Add(sb.ToString().Replace("\n", "<br>").Replace("|", "\\|"));
                }
                mdRows.Add("| " + string.Join(" | ", cells) + " |");
            }
            // ヘッダー行の各セルの段落のTextAlignmentから、区切り行のコロンを組み立てる。
            // 中央揃えは":---:"、右揃えは"---:"。左揃えは、Paragraph.Tagに付けた印
            // （ALIGN_LEFT_EXPLICIT_TAG）を見て、「明示的な左揃え（:---）」だったか
            // 「揃え指定なし（---）」だったかを区別する（TextAlignmentだけではこの2つを
            // 区別できないため、Tagで別途覚えておくようにした）。
            // ダッシュの数（3個が既定）は、列幅調整ダイアログ（ステップ2）で明示的に幅が
            // 設定されている列については、その値をそのままダッシュ数として書き出す。
            // ここで参照するBlockStyles.GetSourceDashCountsは、区切り行のダッシュ数
            // （＝実際に保存されるべき値）を、表示用の列幅（TableColumn.Width）とは独立して
            // 記録している値である（詳細は同メソッドのコメント参照）。表示用の列幅だけを見て
            // 判断しないのは、メニュー「列幅を補正する」がオフの間は表示が常に既定の均等幅
            // （Auto）に戻るため、それだけを見ると調整済みの表の情報がオフの間の保存で失われて
            // しまうから（トグルは見た目だけのはずなのに、保存内容まで書き換えてしまう不具合に
            // なる）。まだ一度もSetSourceDashCountsが呼ばれていない表（GetSourceDashCountsが
            // null。挿入直後でまだ未調整の表など）は、これまでと同じ既定の3個のまま。
            var sourceDashCounts = BlockStyles.GetSourceDashCounts(a_table);
            var sepCells = new List<string>();
            int sepColIndex = 0;
            foreach (TableCell headerCell in rows[0].Cells)
            {
                var headerPara = headerCell.Blocks.FirstBlock as Paragraph;
                TextAlignment alignment = headerPara?.TextAlignment ?? TextAlignment.Left;
                bool explicitLeftFlg = ALIGN_LEFT_EXPLICIT_TAG == (headerPara?.Tag as string);

                int dashCount = BlockStyles.TABLE_COLUMN_DEFAULT_DASH_COUNT;
                if (null != sourceDashCounts && sepColIndex < sourceDashCounts.Count)
                {
                    dashCount = BlockStyles.ClampDashCount(sourceDashCounts[sepColIndex]);
                }
                sepCells.Add(BuildTableSeparatorCell(alignment, explicitLeftFlg, dashCount));
                sepColIndex++;
            }
            string sep = "| " + string.Join(" | ", sepCells) + " |";

            var result = new List<string> { mdRows[0], sep };
            result.AddRange(mdRows.Skip(1));
            return string.Join("\n", result);
        }

        /// <summary>
        /// 表の区切り行の1セル分の文字列を組み立てる。ダッシュの数（<paramref name="a_dashCount"/>、
        /// 3未満にはならない）はそのまま出力し、コロンは文字揃えに応じて前後に追加するだけ
        /// （ダッシュの数からは差し引かない）。例：中央揃え・ダッシュ3個なら":---:"、
        /// 揃え指定なし・ダッシュ10個なら"----------"。これにより、揃え指定の有無に関わらず、
        /// 「ダッシュの数＝ParseColumnDashCountで読み取れる値」という対応が常に保たれる
        /// （ParseColumnAlignment・ParseColumnDashCountと対になる書き出し処理）。
        /// </summary>
        /// <param name="a_alignment">列の文字揃え。</param>
        /// <param name="a_explicitLeftFlg">「明示的な左揃え（:---）」だったかどうか。</param>
        /// <param name="a_dashCount">ダッシュの数（列幅調整・ステップ2で使う単位）。</param>
        private static string BuildTableSeparatorCell(TextAlignment a_alignment, bool a_explicitLeftFlg, int a_dashCount)
        {
            int n = a_dashCount < 3 ? 3 : a_dashCount;
            string dashes = new string('-', n);
            if (TextAlignment.Center == a_alignment)
            {
                return ":" + dashes + ":";
            }
            if (TextAlignment.Right == a_alignment)
            {
                return dashes + ":";
            }
            if (a_explicitLeftFlg)
            {
                return ":" + dashes;
            }
            return dashes;
        }

        /// <summary>段落のInlinesをMarkdownテキストへ書き出す（見出し・段落・箇条書き項目・
        /// 表セルの内容で共通して使う）。</summary>
        /// <param name="a_p">対象の段落。</param>
        /// <returns>変換後のMarkdown文字列。</returns>
        private string ParagraphInlineToMarkdown(Paragraph a_p)
        {
            var sb = new StringBuilder();
            AppendInlinesMarkdown(a_p.Inlines, sb);
            return sb.ToString().Trim();
        }

        /// <summary>書き出す文中の要素1つ分（文字・リンク・画像・改行等の Markdown と、重ねた書式）。</summary>
        private struct MarkdownInlineItem
        {
            /// <summary>Markdown の文字。</summary>
            public string Text;
            /// <summary>重ねた書式（記号で囲む書式だけ）。</summary>
            public InlineStyleFlags Flags;
            /// <summary>書式に関わらない要素（改行・画像・アンカー）。前後の要素に共通の書式の中に置く。</summary>
            public bool TransparentFlg;
            /// <summary>普通の文字（前後の空白を、書式の記号の外に出してよい）。</summary>
            public bool PlainTextFlg;
        }

        /// <summary>
        /// インライン要素の中心となる書き出し処理。書式（太字・斜体・取り消し線・下線・ハイライト）は重ねて持てるため、
        /// 要素ごとの書式から、記号を入れ子にして書き出す（「~~**太字の取り消し線**~~」・「**a~~b~~c**」等）。
        /// 改行・画像は前後に共通の書式の中に置くので、改行をまたいで続く書式は、以前と同じく1つの記号にまとめる。
        /// 書式の記号の内側の端に空白が来る時は、空白を記号の外に出す（「**a **b」のような、読み直すと書式に
        /// ならない Markdown を書かないため）。リンクのRunは [a_text](a_url) または &lt;a_url&gt; に戻す。
        /// </summary>
        /// <param name="a_inlines">対象のInlineコレクション。</param>
        /// <param name="a_sb">追記先のStringBuilder。</param>
        private void AppendInlinesMarkdown(InlineCollection a_inlines, StringBuilder a_sb)
        {
            var items = new List<MarkdownInlineItem>();
            CollectMarkdownInlineItems(a_inlines, items);
            WriteMarkdownInlineItems(items, a_sb);
        }

        /// <summary>Inline の並びを、書き出す要素の並びにする（Span の中も順にたどる）。</summary>
        /// <param name="a_inlines">Inline の並び。</param>
        /// <param name="a_items">追加先。</param>
        private void CollectMarkdownInlineItems(InlineCollection a_inlines, List<MarkdownInlineItem> a_items)
        {
            var inlineList = a_inlines.Cast<Inline>().ToList();
            for (int i = 0; i < inlineList.Count; i++)
            {
                Inline inline = inlineList[i];
                if (inline is LineBreak)
                {
                    a_items.Add(new MarkdownInlineItem { Text = "\n", TransparentFlg = true });
                    continue;
                }
                if (inline is Span span)
                {
                    CollectMarkdownInlineItems(span.Inlines, a_items);
                    continue;
                }
                if (inline is InlineUIContainer iuc)
                {
                    if (iuc.Child is Image img)
                    {
                        a_items.Add(new MarkdownInlineItem { Text = ImageInlineToMarkdown(img), TransparentFlg = true });
                    }
                    else if (iuc.Child is CheckBox taskCheckBox)
                    {
                        // タスクリスト・表のセルのチェックボックス。
                        a_items.Add(new MarkdownInlineItem { Text = true == taskCheckBox.IsChecked ? "[x] " : "[ ] " });
                    }
                    continue;
                }
                if (!(inline is Run run))
                {
                    continue;
                }
                if (run.Tag is LinkInfo linkInfo)
                {
                    a_items.Add(new MarkdownInlineItem
                    {
                        Text = LinkRunToMarkdown(run, linkInfo),
                        Flags = InlineStyles.Parse(linkInfo.Styles) & InlineStyles.STACKABLE_MASK
                    });
                    continue;
                }
                if (run.Tag is AnchorInfo anchorInfo)
                {
                    a_items.Add(new MarkdownInlineItem { Text = "<a id=\"" + anchorInfo.Id + "\"></a>", TransparentFlg = true });
                    continue;
                }
                InlineStyleFlags flags = InlineStyles.Parse(run.Tag);
                InlineStyleFlags stackable = flags & InlineStyles.STACKABLE_MASK;
                if (0 != (flags & InlineStyleFlags.Code))
                {
                    // インラインコード：改行をまたいで続く、同じ書式のコードは1つのコードにまとめる
                    var code = new StringBuilder(BlockStyles.StripUrlBreakJoiners(run.Text.Replace("​", "")));
                    while (i + 2 < inlineList.Count &&
                           inlineList[i + 1] is LineBreak &&
                           inlineList[i + 2] is Run nextRun &&
                           InlineStyles.Parse(nextRun.Tag) == flags)
                    {
                        code.Append('\n').Append(BlockStyles.StripUrlBreakJoiners(nextRun.Text.Replace("​", "")));
                        i += 2;
                    }
                    if (0 < code.Length)
                    {
                        a_items.Add(new MarkdownInlineItem { Text = BuildCodeSpanMarkdown(code.ToString()), Flags = stackable });
                    }
                    continue;
                }
                if (0 != (flags & InlineStyleFlags.Escaped))
                {
                    a_items.Add(new MarkdownInlineItem { Text = "\\" + run.Text, Flags = stackable });
                    continue;
                }
                string text = BlockStyles.StripUrlBreakJoiners(run.Text.Replace("​", ""));
                if (0 < text.Length)
                {
                    a_items.Add(new MarkdownInlineItem { Text = text, Flags = stackable, PlainTextFlg = true });
                }
            }
        }

        /// <summary>
        /// 要素の並びを Markdown にする。開いている書式を積んでおき、次の要素に無い書式が出てきたら、それより内側の
        /// 書式もいったん閉じる（記号が正しく入れ子になるように）。新しく開く書式は、後ろへ長く続くものから外側に開く。
        /// </summary>
        /// <param name="a_items">要素の並び。</param>
        /// <param name="a_sb">追記先。</param>
        private static void WriteMarkdownInlineItems(List<MarkdownInlineItem> a_items, StringBuilder a_sb)
        {
            // 改行・画像等の書式：前後の要素に共通の書式（片方が無ければ書式なし）
            for (int i = 0; i < a_items.Count; i++)
            {
                if (!a_items[i].TransparentFlg)
                {
                    continue;
                }
                InlineStyleFlags prev = InlineStyleFlags.None;
                InlineStyleFlags next = InlineStyleFlags.None;
                bool prevFoundFlg = false;
                bool nextFoundFlg = false;
                for (int k = i - 1; 0 <= k; k--)
                {
                    if (!a_items[k].TransparentFlg)
                    {
                        prev = a_items[k].Flags;
                        prevFoundFlg = true;
                        break;
                    }
                }
                for (int k = i + 1; k < a_items.Count; k++)
                {
                    if (!a_items[k].TransparentFlg)
                    {
                        next = a_items[k].Flags;
                        nextFoundFlg = true;
                        break;
                    }
                }
                MarkdownInlineItem item = a_items[i];
                item.Flags = prevFoundFlg && nextFoundFlg ? prev & next : InlineStyleFlags.None;
                a_items[i] = item;
            }

            var open = new List<InlineStyleFlags>();
            var pendingSpace = new StringBuilder();
            for (int i = 0; i < a_items.Count; i++)
            {
                MarkdownInlineItem item = a_items[i];
                InlineStyleFlags flags = item.Flags;
                // この要素に無い書式を閉じる（それより内側に開いている書式も、いったん閉じる）
                int lowest = open.FindIndex(f => 0 == (flags & f));
                if (0 <= lowest)
                {
                    for (int k = open.Count - 1; lowest <= k; k--)
                    {
                        a_sb.Append(InlineStyles.CloseMarker(open[k]));
                    }
                    open.RemoveRange(lowest, open.Count - lowest);
                }
                a_sb.Append(pendingSpace);
                pendingSpace.Clear();

                string text = item.Text;
                InlineStyleFlags openedFlags = InlineStyleFlags.None;
                foreach (InlineStyleFlags f in open)
                {
                    openedFlags |= f;
                }
                InlineStyleFlags toOpen = flags & ~openedFlags;
                if (InlineStyleFlags.None != toOpen && item.PlainTextFlg)
                {
                    // 先頭の空白は、書式の開きの記号の前に出す（空白だけなら、書式を開かない）
                    int lead = 0;
                    while (lead < text.Length && char.IsWhiteSpace(text[lead]))
                    {
                        lead++;
                    }
                    if (lead == text.Length)
                    {
                        pendingSpace.Append(text);
                        continue;
                    }
                    a_sb.Append(text, 0, lead);
                    text = text.Substring(lead);
                }
                if (InlineStyleFlags.None != toOpen)
                {
                    var toOpenList = new List<InlineStyleFlags>();
                    foreach (InlineStyleFlags f in InlineStyles.STACKABLE_ORDER)
                    {
                        if (0 != (toOpen & f))
                        {
                            toOpenList.Add(f);
                        }
                    }
                    // 後ろへ長く続く書式ほど外側（先）に開く（同じ長さなら STACKABLE_ORDER の順）
                    var lengths = toOpenList.ToDictionary(f => f, f =>
                    {
                        int count = 0;
                        for (int k = i; k < a_items.Count && 0 != (a_items[k].Flags & f); k++)
                        {
                            count++;
                        }
                        return count;
                    });
                    foreach (InlineStyleFlags f in toOpenList.OrderByDescending(f => lengths[f]))
                    {
                        a_sb.Append(InlineStyles.OpenMarker(f));
                        open.Add(f);
                    }
                }
                if (item.PlainTextFlg && 0 < open.Count)
                {
                    // 末尾の空白は、次の要素を書く時まで持っておく（書式を閉じるなら、閉じの記号の後ろに出す）
                    int trail = 0;
                    while (trail < text.Length && char.IsWhiteSpace(text[text.Length - 1 - trail]))
                    {
                        trail++;
                    }
                    a_sb.Append(text, 0, text.Length - trail);
                    pendingSpace.Append(text, text.Length - trail, trail);
                }
                else
                {
                    a_sb.Append(text);
                }
            }
            for (int k = open.Count - 1; 0 <= k; k--)
            {
                a_sb.Append(InlineStyles.CloseMarker(open[k]));
            }
            a_sb.Append(pendingSpace);
        }

        /// <summary>リンクの Run を Markdown にする（[文字](URL)・&lt;URL&gt;・&lt;メールアドレス&gt;・書いたままのメールアドレス）。</summary>
        /// <param name="a_run">リンクの Run。</param>
        /// <param name="a_linkInfo">リンクの情報。</param>
        /// <returns>Markdown の文字。</returns>
        private static string LinkRunToMarkdown(Run a_run, LinkInfo a_linkInfo)
        {
            string content = BlockStyles.StripUrlBreakJoiners(a_run.Text.Replace("\u200B", ""));
            if (a_linkInfo.IsBareEmailFlg)
            {
                // 文中にそのまま書かれていたメールアドレス：書かれたままの文字を書き出す
                return content;
            }
            if (a_linkInfo.IsEmailAutoLinkFlg)
            {
                // メールアドレス自動リンク。保存されているURLは"mailto:"付きなので、書き戻す際は取り除く。
                string email = null != a_linkInfo.Url && a_linkInfo.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                    ? a_linkInfo.Url.Substring("mailto:".Length)
                    : a_linkInfo.Url;
                return "<" + email + ">";
            }
            if (a_linkInfo.IsAutoLinkFlg && content == a_linkInfo.Url)
            {
                return "<" + a_linkInfo.Url + ">";
            }
            var sb = new StringBuilder();
            sb.Append('[').Append(content).Append("](").Append(a_linkInfo.Url);
            if (!string.IsNullOrEmpty(a_linkInfo.Title))
            {
                // タイトル属性。
                sb.Append(" \"").Append(a_linkInfo.Title).Append('"');
            }
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>画像（InlineUIContainer の中の Image）を Markdown にする（画像を囲むリンクも含める）。</summary>
        /// <param name="a_img">画像。</param>
        /// <returns>Markdown の文字。</returns>
        private string ImageInlineToMarkdown(Image a_img)
        {
            string imageMarkdown = ImageToMarkdownString(a_img);
            if (!(a_img.Tag is ImageInfo imageInfo) || string.IsNullOrEmpty(imageInfo.LinkUrl))
            {
                return imageMarkdown;
            }
            var sb = new StringBuilder();
            sb.Append('[').Append(imageMarkdown).Append("](").Append(imageInfo.LinkUrl);
            if (!string.IsNullOrEmpty(imageInfo.LinkTitle))
            {
                sb.Append(" \"").Append(imageInfo.LinkTitle).Append('"');
            }
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>埋め込み画像を、元の記法（Markdownの![]()、またはHTMLの&lt;a_img&gt;）に戻す。</summary>
        /// <param name="a_img">対象の画像。</param>
        /// <returns>変換後のMarkdown文字列（画像の記法）。</returns>
        private string ImageToMarkdownString(Image a_img)
        {
            var info = a_img.Tag as ImageInfo;
            string src = info?.OriginalSrc ?? "";
            string alt = info?.Alt ?? "";
            if ("md" == info?.Format)
            {
                string result = "![" + alt + "](" + src;
                if (!string.IsNullOrEmpty(info?.Title))
                {
                    // タイトル属性。
                    result += " \"" + info.Title + "\"";
                }
                result += ")";
                return result;
            }

            string tag = "<img src=\"" + src + "\" alt=\"" + alt + "\"";
            if (!string.IsNullOrEmpty(info?.Style))
            {
                tag += " style=\"" + info.Style + "\"";
            }
            tag += " />";
            return tag;
        }

        // ======================================================================
        //  Markdown → FlowDocument
        // ======================================================================

        /// <summary>
        /// Markdown文字列全体を解析し、FlowDocumentへ反映する。見出し・フェンス付き
        /// コードブロック・箇条書き（入れ子・「緩い」リストの空行含む）・順序付きリスト・表・
        /// 通常の段落に対応する。各ブロックの元のソーステキストも記憶し、
        /// あとで無編集のまま保存する場合にそのまま使えるようにする。
        /// </summary>
        /// <param name="a_md">解析するMarkdownソース。</param>
        /// <param name="a_doc">反映先の文書（最初にクリアされる）。</param>
        public void MarkdownToDocument(string a_md, FlowDocument a_doc)
        {
            if (!DebugLogger.IsEnabled)
            {
                MarkdownToDocumentCore(a_md, a_doc);
                return;
            }
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            MarkdownToDocumentCore(a_md, a_doc);
            DebugLogger.Log($"MarkdownToDocument: {stopwatch.Elapsed.TotalMilliseconds:F1}ms chars={a_md?.Length} blocks={a_doc.Blocks.Count}");
        }

        /// <summary>MarkdownToDocument の本体（MarkdownToDocument は、ログが有効な時に、かかった時間を記録する）。</summary>
        /// <param name="a_md">解析するMarkdownソース。</param>
        /// <param name="a_doc">反映先の文書（最初にクリアされる）。</param>
        private void MarkdownToDocumentCore(string a_md, FlowDocument a_doc)
        {
            a_doc.Blocks.Clear();
            m_originalTextTracker.Clear();
            m_leadingComments.Clear();
            m_trailingComments.Clear();
            // 直前に読み取った、まだどのBlockにも結び付けていないHTMLコメント（複数可）。
            // 各要素は(そのコメントの直前にあった空行の数, コメントの元のテキスト)の組。
            // 詳細は下のHTMLコメント解析箇所のコメント参照。
            var pendingComments = new List<(int BlankLinesBefore, string Text)>();
            // 直前の要素（Block・HTMLコメント・ファイル先頭）から数えて、まだどの要素にも
            // 「消費」されていない空行の数。空行をまたぐたびに加算し、次のコメント・Blockが
            // 見つかった時点でその要素の「直前の空行の数」として記録し、0へリセットする
            // （m_blankLinesBeforeBlock・CommentGroupInfo.BlankLinesBefore参照）。
            int pendingBlankLines = 0;
            // 新しいトップレベルBlockをa_doc.Blocksへ追加する直前に呼ぶ。貯まっている
            // pendingCommentsがあれば、そのBlockの「直前のコメント」として結び付ける。
            void AttachPendingComments(Block a_newBlock)
            {
                if (pendingComments.Count > 0)
                {
                    var group = new CommentGroupInfo();
                    group.m_items.AddRange(pendingComments);
                    m_leadingComments.Add(a_newBlock, group);
                    pendingComments.Clear();
                }
            }
            // 新しいトップレベルBlockをa_doc.Blocksへ追加する直前に呼ぶ。このBlockの直前
            // （直前のBlock・直前のHTMLコメント・またはファイル先頭）との間にあった空行の数を
            // 記録し、pendingBlankLinesを0へリセットする。
            void RecordBlankLinesBeforeBlock(Block a_newBlock)
            {
                m_blankLinesBeforeBlock.Add(a_newBlock, new StrongBox<int>(pendingBlankLines));
                pendingBlankLines = 0;
            }
            var lines = a_md.Replace("\r\n", "\n").Split('\n');
            // ソースがファイル末尾に改行を持っていたか（Split('\n')の最後の要素が空文字列に
            // なるのは、末尾に改行があった場合のみ）。DocumentToMarkdownでの再現に使う。
            m_hasTrailingNewlineAtEof = lines.Length > 0 && "" == lines[lines.Length - 1];
            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (IsEffectivelyBlankLine(line)) { pendingBlankLines++; i++; continue; }

                int blockStart = i;

                if (line.TrimStart().StartsWith("```"))
                {
                    string language = Regex.Match(line.TrimStart(), "^```(\\S*)").Groups[1].Value;
                    i++;
                    var codeLines = new List<string>();
                    while (i < lines.Length &&
                           "```" != lines[i].Trim())
                    {
                        codeLines.Add(lines[i]);
                        i++;
                    }
                    if (i < lines.Length)
                    {
                        i++; // 閉じフェンスを読み飛ばす
                    }

                    // 複数行のコードブロックは、1行につき1つのParagraphにする（先頭行は
                    // CodeBlockInfo、2行目以降はCodeBlockContinuationInfo）。LineBreakでは
                    // なく別々のParagraphにするのは、引用・箇条書き項目・表のセルと同じ理由
                    // （WPFのLineBreakとIMEの組み合わせの不具合。実機で「コードブロック内で
                    // Enterを押して行を増やしても、新しい行にカーソルを移動して入力すると
                    // 前の行の末尾に入力されてしまう」という形で確認された。
                    // CodeBlockContinuationInfoのコメント参照）。書き出し側はBlockToMarkdown・
                    // DocumentToMarkdownのCodeBlockContinuationInfo分岐を参照。
                    bool codeHasContinuationFlg = codeLines.Count > 1;
                    var codePara = new Paragraph();
                    BlockStyles.ApplyCodeBlockStyle(codePara, language, !codeHasContinuationFlg);
                    if (codeLines.Count > 0)
                    {
                        codePara.Inlines.Add(new Run(codeLines[0]));
                    }
                    RecordBlankLinesBeforeBlock(codePara);
                    AttachPendingComments(codePara);
                    a_doc.Blocks.Add(codePara);
                    if (!codeHasContinuationFlg)
                    {
                        m_originalTextTracker.Record(codePara, lines, blockStart, i);
                    }
                    else
                    {
                        // <br>続き段落（brSegments処理）・引用の続き段落（quoteLineTexts処理）
                        // と同じ理由で、複数行に分かれる場合は元テキストの丸ごと保存を使わず、
                        // 常に現在の構造から書き出し直す。
                        for (int lineIdx = 1; lineIdx < codeLines.Count; lineIdx++)
                        {
                            bool lastLineFlg = lineIdx == codeLines.Count - 1;
                            var contPara = new Paragraph();
                            BlockStyles.ApplyCodeBlockContinuationStyle(contPara, lastLineFlg);
                            contPara.Inlines.Add(new Run(codeLines[lineIdx]));
                            a_doc.Blocks.Add(contPara);
                        }
                    }
                    continue;
                }

                // HTMLコメント（<!-- ... -->。複数行にまたがる場合を含む）。Markdownモードでは
                // 空行すら残さず完全に非表示にしたいため、Blockとしては一切追加しない
                // （中身の無いBlockは、たとえ見た目を小さくしても空行に近いものが残ってしまう
                // ため）。代わりに、pendingCommentsへ貯めておき、次に実際のBlockが追加される
                // 直前にそのBlockへ「直前のコメント」として結び付ける（m_leadingComments。
                // すぐ下のBlock追加箇所を参照）。ファイル末尾がコメントだけで終わる場合は、
                // ループを抜けた後で直前のBlockへ「後に付くコメント」として結び付ける
                // （m_trailingComments）。書き出し時はDocumentToMarkdown・
                // GetMarkdownOffsetForBlockを参照。ソースモードでは通常のテキストとして
                // 普通に見える・編集できる。
                if (line.TrimStart().StartsWith("<!--"))
                {
                    int blankLinesBeforeThisComment = pendingBlankLines;
                    pendingBlankLines = 0;
                    var commentLines = new List<string> { line };
                    bool closedFlg = line.Contains("-->");
                    i++;
                    while (!closedFlg && i < lines.Length)
                    {
                        commentLines.Add(lines[i]);
                        closedFlg = lines[i].Contains("-->");
                        i++;
                    }
                    pendingComments.Add((blankLinesBeforeThisComment, string.Join("\n", commentLines)));
                    continue;
                }

                var hMatch = Regex.Match(line, "^(#{1,6})\\s+(.*)$");
                if (hMatch.Success)
                {
                    var p = new Paragraph();
                    BlockStyles.ApplyHeadingStyle(p, hMatch.Groups[1].Value.Length);
                    AppendInlineMarkdownToParagraph(p, hMatch.Groups[2].Value, false);
                    RecordBlankLinesBeforeBlock(p);
                    AttachPendingComments(p);
                    a_doc.Blocks.Add(p);
                    i++;
                    m_originalTextTracker.Record(p, lines, blockStart, i);
                    continue;
                }

                // 引用（"> "で始まる行）。連続する"> "行をまとめて、1行につき1つのParagraphに
                // する（先頭行はQuoteInfo、2行目以降はQuoteContinuationInfo）。LineBreakでは
                // なく別々のParagraphにするのは、箇条書き項目・表のセルと同じ理由（WPFの
                // LineBreakとIMEの組み合わせの不具合。実機で「Shift+Enterで行を増やしても、
                // 次の行にカーソルを移動して入力すると1行目の末尾に入力されてしまう」という
                // 形で確認された。QuoteContinuationInfoのコメント参照）。書き出し側は
                // BlockToMarkdown・DocumentToMarkdownのQuoteContinuationInfo分岐を参照。
                var qMatch = Regex.Match(line, "^>\\s?(.*)$");
                if (qMatch.Success)
                {
                    var quoteLineTexts = new List<string> { qMatch.Groups[1].Value };
                    i++;
                    while (i < lines.Length)
                    {
                        var nextQMatch = Regex.Match(lines[i], "^>\\s?(.*)$");
                        if (!nextQMatch.Success)
                        {
                            break;
                        }
                        quoteLineTexts.Add(nextQMatch.Groups[1].Value);
                        i++;
                    }
                    var qp = new Paragraph();
                    BlockStyles.ApplyQuoteStyle(qp, true, 1 == quoteLineTexts.Count);
                    AppendInlineMarkdownToParagraph(qp, quoteLineTexts[0], false);
                    RecordBlankLinesBeforeBlock(qp);
                    AttachPendingComments(qp);
                    a_doc.Blocks.Add(qp);
                    if (1 == quoteLineTexts.Count)
                    {
                        m_originalTextTracker.Record(qp, lines, blockStart, i);
                    }
                    else
                    {
                        // <br>続き段落（brSegments処理）と同じ理由で、複数行に分かれる場合は
                        // 元テキストの丸ごと保存を使わず、常に現在の構造から書き出し直す。
                        // 先頭の段落（qp）の下マージンも0にする（続きの段落との間を詰め、
                        // 1つの引用ブロック内の複数行に見せるため）。
                        qp.Margin = new Thickness(qp.Margin.Left, qp.Margin.Top, qp.Margin.Right, 0);
                        for (int lineIdx = 1; lineIdx < quoteLineTexts.Count; lineIdx++)
                        {
                            bool lastLineFlg = lineIdx == quoteLineTexts.Count - 1;
                            var contPara = new Paragraph();
                            BlockStyles.ApplyQuoteContinuationStyle(contPara, lastLineFlg);
                            // グループ内の段落どうしの間は0にして1つの引用ブロックの複数行に
                            // 見せるが、グループの最後の段落だけは、次のブロックとの間の余白
                            // （ApplyQuoteStyleのa_quoted=trueの場合の下マージンと同じ値）を
                            // 持たせる。
                            contPara.Margin = lastLineFlg ? new Thickness(0, 0, 0, BlockStyles.QuoteBottomMargin) : new Thickness(0);
                            AppendInlineMarkdownToParagraph(contPara, quoteLineTexts[lineIdx], false);
                            a_doc.Blocks.Add(contPara);
                        }
                    }
                    continue;
                }

                // 水平線（---/***/___。3個以上の同じ文字が行全体を占める場合のみ）。
                // "- - -" のように間にスペースが入る書き方には対応しない。箇条書きの判定
                // ロジック（マーカーの直後に空白が必要）と衝突しないよう、意図的にこの
                // 「間にスペースなし」の書き方のみをサポート対象にしている。
                if (Regex.IsMatch(line, "^ {0,3}([-*_])\\1{2,}\\s*$"))
                {
                    var hrPara = new Paragraph();
                    BlockStyles.ApplyHorizontalRuleStyle(hrPara);
                    RecordBlankLinesBeforeBlock(hrPara);
                    AttachPendingComments(hrPara);
                    a_doc.Blocks.Add(hrPara);
                    i++;
                    m_originalTextTracker.Record(hrPara, lines, blockStart, i);
                    continue;
                }

                if (Regex.IsMatch(line, "^\\s*([*+-]|\\d+\\.)\\s+"))
                {
                    var listLines = new List<string>();
                    // 1段目の項目の字下げと種類（箇条書きの記号／番号付き）。1段目で種類が
                    // 変わったら、そこから別のリストにする（標準Markdown・mdew・mdeeと同じ。
                    // 以前は、箇条書きの後に空行を挟んで番号付きリストを書くと、番号付きの
                    // 項目まで箇条書きとして同じリストに取り込まれていた）。
                    TryGetListItemKind(line, out int rootIndent, out string rootKind);
                    while (i < lines.Length)
                    {
                        if (TryGetListItemKind(lines[i], out int itemIndent, out string itemKind))
                        {
                            if (0 < listLines.Count && itemIndent < rootIndent + LIST_NEST_MIN_INDENT && itemKind != rootKind)
                            {
                                break;
                            }
                            listLines.Add(lines[i]);
                            i++;
                            continue;
                        }
                        if (listLines.Count > 0 && !string.IsNullOrWhiteSpace(lines[i]) &&
                            Regex.IsMatch(lines[i], "^\\s+\\S") &&
                            !lines[i].TrimStart().StartsWith("|") &&
                            !Regex.IsMatch(lines[i], "^\\s*#{1,6}\\s"))
                        {
                            listLines.Add(lines[i]);
                            i++;
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(lines[i]))
                        {
                            // 空行だけでリストが終わるのは、その後に非リスト行が続く場合のみ。
                            // 空行の先にもリスト項目が続くなら（標準Markdownの「緩いリスト」）、
                            // 同じリストとして扱い続ける。
                            int j = i;
                            while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j]))
                            {
                                j++;
                            }
                            if (j < lines.Length && TryGetListItemKind(lines[j], out int nextIndent, out string nextKind) &&
                                !(nextIndent < rootIndent + LIST_NEST_MIN_INDENT && nextKind != rootKind))
                            {
                                while (i < j) { listLines.Add(lines[i]); i++; }
                                continue;
                            }
                        }
                        break;
                    }
                    var list = BuildNestedList(listLines);
                    RecordBlankLinesBeforeBlock(list);
                    AttachPendingComments(list);
                    a_doc.Blocks.Add(list);
                    m_originalTextTracker.Record(list, lines, blockStart, i);
                    continue;
                }

                if (line.TrimStart().StartsWith("|") && i + 1 < lines.Length &&
                    Regex.IsMatch(lines[i + 1], "^[\\s|:-]+$") && lines[i + 1].Contains("-"))
                {
                    var headerCells = ParseTableRow(line);
                    // 区切り行（|:---:|---:|...|）のコロンから列ごとの文字揃えを読み取る。
                    var alignSepCells = ParseTableRow(lines[i + 1]);
                    var columnAlignments = alignSepCells.Select(ParseColumnAlignment).ToList();
                    // 同じ区切り行のダッシュの数から、列幅調整・ステップ2で保存した列幅を
                    // 読み取る（詳細はParseColumnDashCount・BuildTableSeparatorCell参照）。
                    var columnDashCounts = alignSepCells.Select(ParseColumnDashCount).ToList();
                    i += 2;
                    var table = new Table();
                    foreach (var _ in headerCells)
                    {
                        table.Columns.Add(new TableColumn());
                    }
                    var rg = new TableRowGroup();
                    table.RowGroups.Add(rg);

                    (TextAlignment alignment, bool explicitLeftFlg) GetColumnAlignment(int a_colIndex) =>
                        a_colIndex < columnAlignments.Count ? columnAlignments[a_colIndex] : (TextAlignment.Left, false);

                    var headerRow = new TableRow();
                    int headerColIndex = 0;
                    foreach (var txt in headerCells)
                    {
                        // KeepTogether: ページの余白付近で表の行が跨ると、跨いだ先のセルは
                        // 罫線（BorderBrush/BorderThickness）が描画されないままテキストだけが
                        // 続いてしまうというWPFの制約がある。セル内の段落をページ内で分割
                        // させないようにすることで、間に合わなければ行ごと次のページへ送られる
                        // ようにし、行の途中で表が壊れて見える問題を避ける。
                        var (headerAlignment, headerExplicitLeftFlg) = GetColumnAlignment(headerColIndex);
                        var cell = BuildTableCellFromMarkdown(
                            txt, headerAlignment, headerExplicitLeftFlg ? ALIGN_LEFT_EXPLICIT_TAG : null, false);
                        cell.FontWeight = FontWeights.Bold;
                        cell.Background = BlockStyles.TableHeaderBackground;
                        // 罫線（BorderBrush/BorderThickness）は、表全体を組み立て終えた後に
                        // BlockStyles.ApplyTableCellBordersでまとめて設定する（隣接セルとの
                        // 境界線が二重に重ならないようにするため。詳細は同メソッド参照）。
                        cell.Padding = BlockStyles.TableCellPadding;
                        headerRow.Cells.Add(cell);
                        headerColIndex++;
                    }
                    rg.Rows.Add(headerRow);

                    while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                    {
                        var cellTexts = ParseTableRow(lines[i]);
                        var row = new TableRow();
                        int bodyColIndex = 0;
                        foreach (var txt in cellTexts)
                        {
                            // KeepTogether: 上のヘッダーセルと同じ理由（罫線が崩れる問題への対策）。
                            var (bodyAlignment, bodyExplicitLeftFlg) = GetColumnAlignment(bodyColIndex);
                            var cell = BuildTableCellFromMarkdown(
                                txt, bodyAlignment, bodyExplicitLeftFlg ? ALIGN_LEFT_EXPLICIT_TAG : null, true);
                            // 罫線は表全体の組み立て後にBlockStyles.ApplyTableCellBordersで
                            // まとめて設定する（ヘッダーセルと同じ理由）。
                            cell.Padding = BlockStyles.TableCellPadding;
                            row.Cells.Add(cell);
                            bodyColIndex++;
                        }
                        rg.Rows.Add(row);
                        i++;
                    }
                    // BlockStyles.ApplyContentBasedColumnWidths（列幅を内容に合わせてコンパクトに
                    // する機能）は無効化中。Pixel指定列が広がりStar指定列が潰れる、意図と逆の
                    // 現象が確認されたため（原因未特定）。
                    // BlockStyles.ApplyContentBasedColumnWidths(table);
                    // 列幅調整ダイアログ（ステップ2）で明示的に幅が調整された表か、あるいは
                    // メニュー「表示」→「列幅を補正する」がオンの場合に、区切り行のダッシュ数と
                    // トグルの状態から実際の列幅を決定する（詳細はBlockStyles.
                    // ApplyEffectiveColumnWidthsのコメント参照）。区切り行の列数と実際の列数が
                    // 一致しない異常な入力の場合は、これまで通り列幅を一切設定しない
                    // （Autoのまま。挙動に変化なし）。
                    if (columnDashCounts.Count == table.Columns.Count)
                    {
                        // 区切り行から読み取ったダッシュ数（＝Markdownソースに実際に保存されて
                        // いる、保存されるべき「本当の値」）を記録しておく。これは表示上の
                        // 列幅（この直後に適用するApplyEffectiveColumnWidths）とは独立していて、
                        // 「列幅を補正する」がオフで見た目が均等幅に戻っている間も保持され続け、
                        // TableToMarkdownでの書き出し時に参照される（詳細はBlockStyles.
                        // SetSourceDashCountsのコメント参照。オフの間に保存しても調整済みの
                        // 表の情報が失われないようにするための対策）。
                        BlockStyles.SetSourceDashCounts(table, columnDashCounts);
                        bool correctColumnWidthsFlg = m_correctColumnWidthsFlg?.Invoke() ?? true;
                        // 表示できる幅が分からない呼び出し側（保存専用の一時変換・PDF書き出し用の
                        // 一時変換など）ではデリゲートを渡していないため、null（＝常にStar比率。
                        // 詳細はBlockStyles.ApplyAutoCalculatedColumnWidthsのコメント参照）になる。
                        double? availableTableWidthPx = m_availableTableWidthFunc?.Invoke();
                        BlockStyles.ApplyEffectiveColumnWidths(table, columnDashCounts, correctColumnWidthsFlg, availableTableWidthPx);
                    }
                    // 隣接セルの境界線が二重に重ならないよう、表全体に対して罫線をまとめて
                    // 設定する（詳細はBlockStyles.ApplyTableCellBordersのコメント参照）。
                    BlockStyles.ApplyTableCellBorders(table, m_cellBorder);
                    // 閉じ括弧で終わるセルは、mdew・mdee と同じく括弧の半分の幅だけ右の余白にはみ出してよいようにする
                    BlockStyles.ApplyClosingBracketLineEndAllowance(table);
                    RecordBlankLinesBeforeBlock(table);
                    AttachPendingComments(table);
                    // 大きな表は、内部では複数のかたまり（Table）に分ける（TableChunks 参照。表のセルに1文字打つたびに、表全体が
                    // 組み直されて遅くなるのを防ぐ）。元テキスト・前の空行の数・直前のコメントは、先頭のかたまりに結び付ける
                    List<Table> chunks = TableChunks.SplitIfLarge(table);
                    foreach (Table chunk in chunks)
                    {
                        a_doc.Blocks.Add(chunk);
                    }
                    // 最後のかたまりの下の余白は、前のブロックで決まる（文書に入れた後に決める。TableChunks.ApplyChunkFrame）
                    TableChunks.ApplyChunkFrame(chunks);
                    m_originalTextTracker.Record(table, lines, blockStart, i);
                    continue;
                }

                // 通常の段落：空行や、次の別種のブロックを示す行が現れるまで、連続する行を
                // まとめて1つの段落にする（内部的には行ごとにLineBreakでつなぐ）。1行ずつ
                // 別々の段落にしてしまうと、元のファイルでは単純な改行だった箇所が、保存や
                // ソースモード表示のたびに「空行付きの段落区切り」として書き出されてしまい、
                // 元のファイルに存在しない空行が増えてしまう。
                var paraLines = new List<string> { line };
                i++;
                while (i < lines.Length && !IsBlockBoundaryLine(lines[i]))
                {
                    paraLines.Add(lines[i]);
                    i++;
                }
                string joinedText = JoinParagraphSourceLines(paraLines);

                // <br>（表のセルの改行と同じ記法。m_tableCellBrRegex）による、段落中の明示的な
                // 行区切りに対応する。LineBreakではなく独立したParagraph（見た目のプロパティは
                // 分割元から引き継ぎ、見た目には1つの段落内の複数行にしか見えないようにする）
                // として表現するのは、表のセル・箇条書き項目と同じ理由（WPFのLineBreakとIMEの
                // 組み合わせの不具合対策。HeadingCodeBlockEditor.InsertParagraphSplitAtCaretの
                // コメント参照）。書き出し側はDocumentToMarkdownのBrContinuationInfo分岐を参照。
                string[] brSegments = m_tableCellBrRegex.Split(joinedText);
                // 末尾が「<br>で終わり、その後に何も続かない」場合、分割結果の最後に意味のない
                // 空文字列の要素が残る（例："abc<br>" → ["abc", ""]）。これをそのまま続き段落に
                // すると、書き出し時に余分な空行が生じるため、末尾の空要素は読み捨てる
                // （note: <br>そのものが本文の末尾にあった場合、その<br>自体も無視される。
                // 元々「その後に何もない改行」はHTML/Markdownの表示上も意味を持たないため実害はない）。
                while (brSegments.Length > 1 &&
                       string.IsNullOrEmpty(brSegments[brSegments.Length - 1].TrimStart(' ', '\t', '\r', '\n')))
                {
                    brSegments = brSegments.Take(brSegments.Length - 1).ToArray();
                }
                var para = new Paragraph();
                AppendInlineMarkdownToParagraph(para, brSegments[0], false);
                RecordBlankLinesBeforeBlock(para);
                AttachPendingComments(para);
                a_doc.Blocks.Add(para);
                if (1 == brSegments.Length)
                {
                    m_originalTextTracker.Record(para, lines, blockStart, i);
                }
                else
                {
                    // <br>で分割した場合は、元テキストの丸ごと保存（OriginalTextTracker）を使わず、
                    // 常に現在の構造から書き出し直す（DocumentToMarkdown参照）。分割位置がソース
                    // 行の境界と一致するとは限らない（1行の途中に<br>がある場合等）ため、行番号
                    // ベースの元テキスト記録では個々の段落を正しく表現できないための措置。
                    // 先頭段落（para）の下余白も0にする（続き段落との間を詰め、1つの段落内の
                    // 複数行に見せるため。既定の下余白はまだ反映されていないスタイル値のため
                    // 依存せず明示的に上書きする）。
                    para.Margin = new Thickness(0);
                    for (int segIdx = 1; segIdx < brSegments.Length; segIdx++)
                    {
                        string segText = brSegments[segIdx].TrimStart(' ', '\t', '\r', '\n');
                        // Marginは明示的に指定する（段落の既定の下余白はEditorBlockSpacing
                        // リソース経由で後から適用されるスタイル値であり、生成直後のこの時点で
                        // para.Marginを読んでもまだ反映されていないため、依存せず明示的に指定
                        // する）。グループ内の段落どうしの間は0にして、見た目には1つの段落内の
                        // 複数行にしか見えないようにするが、グループの最後の段落だけは、次の
                        // ブロックとの間の余白（MainWindow.xamlのEditorBlockSpacingと
                        // 同じ固定値）を通常の段落と同様に持たせる。
                        bool lastSegmentFlg = segIdx == brSegments.Length - 1;
                        var contPara = new Paragraph
                        {
                            Margin = lastSegmentFlg ? new Thickness(0, 0, 0, BlockStyles.ParagraphBottomSpacing) : new Thickness(0),
                            LineHeight = para.LineHeight,
                            KeepTogether = para.KeepTogether,
                            TextAlignment = para.TextAlignment,
                            Tag = new BrContinuationInfo()
                        };
                        AppendInlineMarkdownToParagraph(contPara, segText, false);
                        a_doc.Blocks.Add(contPara);
                    }
                }
            }

            // ファイル末尾がHTMLコメントだけで終わっていた場合（直後に実際のBlockが無い）、
            // 直前のBlockへ「後に付くコメント」として結び付ける。1つもBlockが無い（文書全体が
            // コメントだけだった）場合は、結び付け先として空の段落を1つ作る。
            if (pendingComments.Count > 0)
            {
                var trailingGroup = new CommentGroupInfo();
                trailingGroup.m_items.AddRange(pendingComments);
                if (a_doc.Blocks.Count > 0)
                {
                    // 最後が大きな表の続きのかたまり（TableChunks）なら、表の先頭のかたまりに結び付ける（表全体は先頭のかたまりが
                    // 書き出し、続きのかたまりは行の削除等で取り除かれることがあるため）
                    Block lastBlock = a_doc.Blocks.LastBlock;
                    if (lastBlock is Table lastTable)
                    {
                        lastBlock = TableChunks.GetHead(lastTable);
                    }
                    m_trailingComments.Add(lastBlock, trailingGroup);
                }
                else
                {
                    var onlyPara = new Paragraph();
                    m_leadingComments.Add(onlyPara, trailingGroup);
                    a_doc.Blocks.Add(onlyPara);
                }
                pendingComments.Clear();
            }

            // ファイル末尾（最後の実際の内容の後）にあった空行の数。pendingBlankLinesは
            // この時点で、最後に消費された要素（Block・HTMLコメント）から数えて、まだ
            // 消費されていない空行の数を表す。ただし、ソースがファイル末尾に改行を持って
            // いた場合（m_hasTrailingNewlineAtEof）、その分割結果の末尾には必ず1つ余分な
            // 空文字列要素が生じる（例："X\n"→["X",""]）ため、これは「本当の空行」では
            // ない（単なる末尾の改行）として1つ差し引く。DocumentToMarkdownでの再現に使う。
            m_trailingBlankLinesAtEof = (m_hasTrailingNewlineAtEof && pendingBlankLines > 0)
                ? pendingBlankLines - 1
                : pendingBlankLines;

            // ファイル末尾に本当の空行があった場合、Markdownモード（WYSIWYG表示）でも
            // その位置へカーソルを移動できるよう、目印として空の段落を1つ追加しておく
            // （Typora等、他のエディタでは末尾の空行にカーソルを置けるため、それに合わせた）。
            // 保存時に書き出す文字列自体は、この段落の有無に関わらずm_hasTrailingNewlineAtEof・
            // m_trailingBlankLinesAtEofの記録からそのまま再現される（DocumentToMarkdown参照。
            // この段落自身は空白のみのため、書き出し対象からは除外される）ため、この段落は
            // 表示・カーソル移動のためだけの目印である。空行が複数あった場合でも、末尾へ
            // カーソルを移動できれば十分なため、追加する空の段落は常に1つだけとする。
            // ごく普通の空のParagraph（Tagなし、LineBreakも一切含まない）であり、LineBreakと
            // IMEの組み合わせで生じる不具合（BuildTableCellFromMarkdownのコメント参照）とは
            // 無関係のため、IME面での懸念はない。
            if (a_doc.Blocks.Count > 0 && m_trailingBlankLinesAtEof > 0)
            {
                a_doc.Blocks.Add(new Paragraph());
            }

            if (0 == a_doc.Blocks.Count)
            {
                a_doc.Blocks.Add(new Paragraph());
            }

            // 表が文書の最後のBlockのまま残っていると、表の最後のセルの末尾にキャレットが
            // ある状態でEnterキーを押した時、その位置がどのセルの範囲にも一致せず（後ろに
            // 段落が存在しないため）、MainWindow.ResolveParagraphが段落を解決できずnullを
            // 返してしまい、結局WPF標準の表編集機能（行の自動追加）にEnterキーの処理が
            // 渡ってしまって表の罫線が壊れる不具合があった（詳細はMainWindow.
            // EditorPreviewKeyDown・ResolveParagraphのコメント参照）。TableEditor.
            // InsertTableで表を新規挿入する際は、常に表の直後へ空の段落を追加することで
            // この状態を避けているが、Markdownソースから文書を再構築するこの経路
            // （ファイルを開く時、ソースモード⇔Markdownモードの切り替え時）では、その保証が
            // 抜けていた。表の直後にカーソルを置いて入力できるようにする目的も兼ねて、
            // ここでも同じように空の段落を1つ追加し、表が常に空でない後続の段落を伴う状態を
            // 保つ（ごく普通の空のParagraphであり、IME面での懸念はない）。
            if (a_doc.Blocks.LastBlock is Table)
            {
                a_doc.Blocks.Add(new Paragraph());
            }

            m_imageManager.ResolveImages(a_doc);
        }

        /// <summary>
        /// ソースモードのテキスト中の指定行番号（0始まり）が、直前のMarkdownToDocument呼び
        /// 出しで生成されたどのトップレベルブロックに対応するかを、ブロックのインデックス
        /// （0始まり）で返す。モード切替時（ソースモード→Markdownモード）のカーソル位置
        /// 復元のために使う。MarkdownToDocumentが各ブロックについて記憶する元テキスト
        /// （OriginalTextTracker）を使って、変換後の行範囲を逆算する仕組みのため、
        /// MarkdownToDocument呼び出し直後（まだ何も編集されていない状態）でのみ正しく
        /// 動作する。
        /// </summary>
        /// <param name="a_doc">MarkdownToDocumentの呼び出し直後のFlowDocument。</param>
        /// <param name="a_md">MarkdownToDocumentに渡したのと同じソーステキスト。</param>
        /// <param name="a_targetLine">カーソルがあった行番号（0始まり）。</param>
        /// <returns>対応するトップレベルブロックのインデックス。ブロックが1つもなければ -1。</returns>
        public int FindBlockIndexForSourceLine(FlowDocument a_doc, string a_md, int a_targetLine)
        {
            var lines = a_md.Replace("\r\n", "\n").Split('\n');
            int cursor = 0;
            int idx = 0;
            int lastIdx = -1;
            foreach (Block block in a_doc.Blocks)
            {
                while (cursor < lines.Length && IsEffectivelyBlankLine(lines[cursor]))
                {
                    cursor++;
                }
                if (!m_originalTextTracker.TryGetOriginal(block, out var original))
                {
                    idx++;
                    continue;
                }
                var blockLines = original.Replace("\r\n", "\n").Split('\n');
                int start = cursor;
                int end = start + blockLines.Length;
                if (a_targetLine < end)
                {
                    return idx;
                }
                cursor = end;
                lastIdx = idx;
                idx++;
            }
            return lastIdx;
        }

        /// <summary>
        /// 行が「実質的に空行」かどうかを判定する。単純な空白文字だけの行に加え、ゼロ幅
        /// スペース（U+200B）など、見た目には空行にしか見えない書式文字（Unicodeの
        /// Cfカテゴリ）のみで構成される行も空行として扱う。string.IsNullOrWhiteSpaceは
        /// これらの文字を空白とは判定しないため、そのままでは（例えばエディタ内での
        /// 編集時の副作用などにより）このような文字が紛れ込んだ行が、見た目上は空行なのに
        /// 段落の区切りとして認識されない、という不具合につながりうる。
        /// </summary>
        /// <param name="a_line">判定したい行。</param>
        /// <returns>実質的に空行とみなせるなら true。</returns>
        private static bool IsEffectivelyBlankLine(string a_line)
        {
            if (string.IsNullOrEmpty(a_line))
            {
                return true;
            }
            string stripped = a_line
                .Replace("​", "")
                .Replace("‌", "")
                .Replace("‍", "")
                .Replace("﻿", "");
            return string.IsNullOrWhiteSpace(stripped);
        }

        /// <summary>
        /// 段落を組み立てている、空行を挟まない連続したソース行を、1つの文字列へ結合する。
        /// 「表示」→「段落中の改行」の設定に応じて、結合のしかたを切り替える。設定が
        /// 「ソースの通りに改行する」（既定）なら、行を"\n"でつなぐ（AppendInlineMarkdown
        /// ToParagraphが後段でこれを実際のLineBreakへ変換し、mde/Typora従来通り、ソース上の
        /// 改行がそのまま見た目の改行になる）。「空行が入るまで改行しない」なら、各行の前後の
        /// 空白を落としたうえで単純な半角スペース1つでつなぐ（結果に"\n"が含まれなくなるため、
        /// 後段でLineBreakは一切生成されず、CommonMark/VSCodeのMarkdownプレビューと同じく、
        /// 空行が入るまでは改行されずに1つの段落として続けて表示される）。
        /// </summary>
        /// <param name="a_lines">結合対象のソース行一覧。</param>
        /// <returns>結合した文字列。</returns>
        private string JoinParagraphSourceLines(List<string> a_lines)
        {
            bool preserveFlg = null == m_preserveSourceLineBreaksFlg || m_preserveSourceLineBreaksFlg();
            if (preserveFlg)
            {
                return string.Join("\n", a_lines);
            }
            return string.Join(" ", a_lines.Select(l => l.Trim()));
        }

        /// <summary>"| a | b | c |" 形式の表の行を、各セルのテキストへ分割する。</summary>
        /// <param name="a_line">対象の行。</param>
        /// <returns>各セルのテキストの一覧。</returns>
        /// <summary>
        /// 通常の段落を組み立てている最中に、この行で段落を打ち切るべきかどうかを判定する。
        /// 空行のほか、見出し・コードブロック・箇条書き・表・HTMLコメントなど、他の種類の
        /// ブロック（またはコメント）の開始を示す行であれば true を返す。
        /// </summary>
        /// <param name="a_line">判定したい行。</param>
        /// <returns>この行より前で段落を打ち切るべきなら true。</returns>
        private bool IsBlockBoundaryLine(string a_line)
        {
            if (IsEffectivelyBlankLine(a_line))
            {
                return true;
            }
            if (a_line.TrimStart().StartsWith("```"))
            {
                return true;
            }
            if (Regex.IsMatch(a_line, "^(#{1,6})\\s+"))
            {
                return true;
            }
            if (Regex.IsMatch(a_line, "^\\s*([*+-]|\\d+\\.)\\s+"))
            {
                return true;
            }
            if (a_line.TrimStart().StartsWith("|"))
            {
                return true;
            }
            if (Regex.IsMatch(a_line, "^ {0,3}([-*_])\\1{2,}\\s*$"))
            {
                return true;
            }
            // 引用（"> "）。この判定が無いと、空行を挟まずに段落の直後へ引用を書いた場合、
            // 通常の段落側の継続スキャン（この関数を使う側のwhileループ）が"> "で始まる行を
            // そのまま本文として取り込んでしまい、引用として認識されなくなってしまう
            // （見出し・箇条書き・表・水平線と同じ理由。MarkdownToDocumentの引用解析分岐は、
            // トップレベルの新しいブロックを探すタイミングでのみ働くため）。判定に使う
            // 正規表現は、引用の解析分岐自体（MarkdownToDocumentの"qMatch"）と同じ
            // "^>\\s?"にそろえている。
            if (Regex.IsMatch(a_line, "^>\\s?"))
            {
                return true;
            }
            // HTMLコメント（<!-- -->）。空行を挟まずに段落の直後へコメントを書いた場合でも、
            // このチェックが無いと段落側の継続スキャン（この関数を使う側のwhileループ）が
            // コメント行をただの本文として段落へ取り込んでしまい、コメントとして認識されなく
            // なってしまう（MarkdownToDocumentのコメント解析分岐は、トップレベルの新しい
            // ブロックを探すタイミングでのみ働くため）。空行の有無にかかわらずコメントを
            // 正しく認識できるよう、ここで段落の境界として扱う。
            if (a_line.TrimStart().StartsWith("<!--"))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// 表の1行分のソーステキストを、セルごとの文字列に分割する。単純に「|」で分割すると、
        /// セルの中のインラインコード（`&amp;&amp;`/`||` のように、コードの中に「|」自体が
        /// 含まれる場合）まで区切ってしまい、列がずれて表が壊れてしまう。そのため、バック
        /// クォートで囲まれたコード区間の中にある「|」は区切りとして扱わないようにしている。
        /// コード区間は、開きと同じ個数のバッククォートの並びで閉じる（`` Ctrl+Shift+` `` のように、
        /// 中にバッククォートを含むコードは2個以上のバッククォートで囲んで書くため。CommonMark と同じ）。
        /// 閉じる並びが見つからないバッククォートは、ただの文字として扱う。コード区間の外の「\」の
        /// 直後の文字（「\|」等）は区切りとして扱わない（TableToMarkdown はセルの中の「|」を「\|」と
        /// 書き出すため）。
        /// </summary>
        /// <param name="a_line">表の1行分のソーステキスト。</param>
        /// <returns>セルごとに分割した文字列。</returns>
        private List<string> ParseTableRow(string a_line)
        {
            string t = a_line.Trim();
            if (t.StartsWith("|"))
            {
                t = t.Substring(1);
            }
            if (t.EndsWith("|"))
            {
                t = t.Substring(0, t.Length - 1);
            }

            var cells = new List<string>();
            var current = new StringBuilder();
            int i = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if ('\\' == c && i + 1 < t.Length)
                {
                    // エスケープ（「\|」「\`」等）。2文字ともそのまま残す（後で PreprocessEscapes が処理する）。
                    current.Append(c).Append(t[i + 1]);
                    i += 2;
                }
                else if ('`' == c)
                {
                    int runLength = CountBacktickRun(t, i);
                    int closeIndex = FindClosingBacktickRun(t, i + runLength, runLength);
                    if (closeIndex >= 0)
                    {
                        // コード区間（閉じる並びまで）をそのまま残す。中の「|」では区切らない。
                        current.Append(t, i, closeIndex + runLength - i);
                        i = closeIndex + runLength;
                    }
                    else
                    {
                        current.Append('`', runLength);
                        i += runLength;
                    }
                }
                else if ('|' == c)
                {
                    cells.Add(current.ToString().Trim());
                    current.Clear();
                    i++;
                }
                else
                {
                    current.Append(c);
                    i++;
                }
            }
            cells.Add(current.ToString().Trim());
            return cells;
        }

        /// <summary>a_textのa_start文字目から続くバッククォートの個数を数える。</summary>
        /// <param name="a_text">対象の文字列。</param>
        /// <param name="a_start">数え始める位置。</param>
        /// <returns>続くバッククォートの個数。</returns>
        private static int CountBacktickRun(string a_text, int a_start)
        {
            int end = a_start;
            while (end < a_text.Length && '`' == a_text[end])
            {
                end++;
            }
            return end - a_start;
        }

        /// <summary>a_textのa_start文字目以降で、ちょうどa_runLength個のバッククォートの並び
        /// （前後にバッククォートが続かないもの）を探す。インラインコードの閉じる並びを探すのに使う。</summary>
        /// <param name="a_text">対象の文字列。</param>
        /// <param name="a_start">探し始める位置。</param>
        /// <param name="a_runLength">探すバッククォートの個数。</param>
        /// <returns>見つかった並びの先頭の位置。見つからなければ -1。</returns>
        private static int FindClosingBacktickRun(string a_text, int a_start, int a_runLength)
        {
            int i = a_start;
            while (i < a_text.Length)
            {
                if ('`' == a_text[i])
                {
                    int runLength = CountBacktickRun(a_text, i);
                    if (runLength == a_runLength)
                    {
                        return i;
                    }
                    i += runLength;
                }
                else
                {
                    i++;
                }
            }
            return -1;
        }

        /// <summary>インラインコードの中身の前後の空白を、CommonMark と同じ規則で1つずつ取り除く
        /// （中身の最初と最後が両方とも空白で、空白だけではない場合）。`` `code` `` のように、
        /// バッククォートで始まる・終わるコードを書くために入れた空白を取り除くため。</summary>
        /// <param name="a_content">コード区間の中身（囲みのバッククォートを除いたもの）。</param>
        /// <returns>前後の空白を取り除いた中身。</returns>
        public static string StripCodeSpanPadding(string a_content)
        {
            if (a_content.Length >= 2 &&
                ' ' == a_content[0] &&
                ' ' == a_content[a_content.Length - 1] &&
                a_content.Trim(' ').Length > 0)
            {
                return a_content.Substring(1, a_content.Length - 2);
            }
            return a_content;
        }

        /// <summary>インラインコードの中身を Markdown に書き出す。中にバッククォートがあれば、中の
        /// いちばん長い並びより1個多いバッククォートで囲み、中身がバッククォートで始まる・終わる場合等は
        /// 前後に空白を1つずつ入れる（StripCodeSpanPadding で読み込み時に取り除かれる。CommonMark と同じ）。</summary>
        /// <param name="a_content">コードの文字列。</param>
        /// <returns>`` で囲んだ Markdown の文字列。</returns>
        public static string BuildCodeSpanMarkdown(string a_content)
        {
            int longestRun = 0;
            for (int i = 0; i < a_content.Length;)
            {
                if ('`' == a_content[i])
                {
                    int runLength = CountBacktickRun(a_content, i);
                    longestRun = Math.Max(longestRun, runLength);
                    i += runLength;
                }
                else
                {
                    i++;
                }
            }
            string fence = new string('`', longestRun + 1);
            bool padFlg = a_content.StartsWith("`") || a_content.EndsWith("`") ||
                          (a_content.Length >= 2 && a_content.StartsWith(" ") && a_content.EndsWith(" ") && a_content.Trim(' ').Length > 0);
            return padFlg ? fence + " " + a_content + " " + fence : fence + a_content + fence;
        }

        /// <summary>
        /// 表の区切り行（例: ":---:" / "---:" / ":---" / "---"）の1セル分の文字列から、
        /// 列の文字揃えを判定する。
        /// TextAlignment自体はLeft/Center/Right/Justifyの4値しか持てず、この2つはどちらも
        /// TextAlignment.Leftになってしまうため、区別が必要な「明示的な左揃え」だけを
        /// 呼び出し元でParagraph.Tag（ALIGN_LEFT_EXPLICIT_TAG）に記録できるよう、
        /// 戻り値にその旨のフラグを含める。
        /// </summary>
        /// <param name="a_sepCell">区切り行の1セル分の文字列。</param>
        /// <returns>対応するTextAlignmentと、「明示的な左揃え（:---）」だったかどうか。</returns>
        private static (TextAlignment alignment, bool explicitLeftFlg) ParseColumnAlignment(string a_sepCell)
        {
            string s = a_sepCell.Trim();
            bool leftColonFlg = s.StartsWith(":");
            bool rightColonFlg = s.EndsWith(":");
            if (leftColonFlg && rightColonFlg)
            {
                return (TextAlignment.Center, false);
            }
            if (rightColonFlg)
            {
                return (TextAlignment.Right, false);
            }
            if (leftColonFlg)
            {
                return (TextAlignment.Left, true);
            }
            return (TextAlignment.Left, false);
        }

        /// <summary>
        /// 表の区切り行の1セル分の文字列から、ダッシュ（-）の数を数える（列幅調整・
        /// ステップ2で使う単位。BuildTableSeparatorCellと対になる読み取り処理）。
        /// コロンは数えず、ダッシュだけを数える。区切り行として意味を持つ最小値（3）未満
        /// だった場合は3に読み替える（手で書いたMarkdownで"|--|"のようにダッシュが2個
        /// しかない場合等への配慮）。
        /// </summary>
        /// <param name="a_sepCell">区切り行の1セル分の文字列。</param>
        /// <returns>ダッシュの数（3以上）。</returns>
        private static int ParseColumnDashCount(string a_sepCell)
        {
            int count = 0;
            foreach (char ch in a_sepCell)
            {
                if ('-' == ch)
                {
                    count++;
                }
            }
            return count < BlockStyles.TABLE_COLUMN_MIN_DASH_COUNT ? BlockStyles.TABLE_COLUMN_MIN_DASH_COUNT : count;
        }

        /// <summary>直前の段の項目より、この文字数以上深く字下げされた項目を1段深い入れ子とする。</summary>
        private const int LIST_NEST_MIN_INDENT = 2;

        /// <summary>リスト項目の行頭の空白の幅を返す（タブは4文字として数える）。</summary>
        /// <param name="a_leading">行頭の空白。</param>
        /// <returns>幅（文字数）。</returns>
        private static int ListIndentWidth(string a_leading)
        {
            int width = 0;
            foreach (char ch in a_leading)
            {
                width += '\t' == ch ? 4 - (width % 4) : 1;
            }
            return width;
        }

        /// <summary>リスト項目の行なら、その字下げの幅と種類（番号付きなら "."、箇条書きなら記号）を返す。</summary>
        /// <param name="a_line">行。</param>
        /// <param name="a_indent">字下げの幅。</param>
        /// <param name="a_kind">種類。</param>
        /// <returns>リスト項目の行ならtrue。</returns>
        private static bool TryGetListItemKind(string a_line, out int a_indent, out string a_kind)
        {
            var m = Regex.Match(a_line, "^(\\s*)(?:([*+-])|\\d+\\.)\\s+");
            if (!m.Success)
            {
                a_indent = 0;
                a_kind = null;
                return false;
            }
            a_indent = ListIndentWidth(m.Groups[1].Value);
            a_kind = m.Groups[2].Success ? m.Groups[2].Value : ".";
            return true;
        }

        /// <summary>
        /// 箇条書き項目のソース行の連なりから、（入れ子を含む）Listを組み立てる。
        /// ネストの各段で箇条書き記号か順序付き数字かを判定し、各項目の1行目と継続行を
        /// すべて結合してからインライン装飾を解析する（**太字**などが継続行をまたいでいても
        /// 正しく認識できるようにするため）。また、すべての項目が同じ数字を使っている
        /// 順序付きリストには印を付け、保存時にそのスタイルを維持できるようにする。
        /// </summary>
        /// <param name="a_listLines">箇条書きのソース行一覧。</param>
        /// <returns>組み立てた（入れ子を含む）List。</returns>
        private List BuildNestedList(List<string> a_listLines)
        {
            var rootList = new List { MarkerStyle = TextMarkerStyle.Disc };
            // 入れ子の段は、字下げの「段数」（以前は3文字＝1段と決め打ちしていた）ではなく、
            // 各段の項目の字下げの幅（indent）を覚えておき、直前の段より2文字以上深ければ
            // 1段深い入れ子とする（標準Markdown・mdew・mdeeと同じ考え方）。3文字単位で丸める
            // 方式では、2文字ずつ字下げしたソース（"- a" / "  - b" / "    - c"）の3段目
            // （4文字）が2段目と同じ段に丸められ、入れ子が1段消えていた。
            int firstIndent = a_listLines
                .Select(l => Regex.Match(l, "^(\\s*)(?:[*+-]|\\d+\\.)\\s+"))
                .Where(mm => mm.Success)
                .Select(mm => ListIndentWidth(mm.Groups[1].Value))
                .DefaultIfEmpty(0)
                .First();
            var stack = new List<(List list, int level)> { (rootList, firstIndent) };
            bool rootMarkerSetFlg = false;
            var numbersByList = new Dictionary<List, List<int>>();

            Paragraph pendingPara = null;
            ListItem pendingListItem = null;
            var pendingTextLines = new List<string>();
            // 直前に確定した箇条書き項目がタスク項目だったかどうか（未確定ならnull）。
            bool? pendingTaskChecked = null;

            // 保留中の項目のテキストを解析して段落へ反映し、タスク項目ならチェックボックスを
            // 段落の先頭に挿入する。新しい項目の開始時／ループ終了後の両方から呼ばれる。
            void FlushPendingItem()
            {
                if (null == pendingPara)
                {
                    return;
                }
                // 項目の1行目＋継続行を結合した文字列（JoinParagraphSourceLines）を、ライブ編集時の
                // 改行と同じ構造（LineBreakではなく複数のParagraph。理由はBuildTableCellFrom
                // Markdownのコメント参照）に揃えて復元する。'\n'で分割し、1行目は（すでに
                // ListItemへ追加済みの）pendingPara自身へ、2行目以降は新しく作るown-paragraphへ
                // 振り分ける。
                string joinedText = JoinParagraphSourceLines(pendingTextLines);
                string[] ownLines = joinedText.Split('\n');
                AppendInlineMarkdownToParagraph(pendingPara, ownLines[0], false);
                Block insertAfter = pendingPara;
                for (int k = 1; k < ownLines.Length; k++)
                {
                    var contPara = new Paragraph { Margin = new Thickness(0) };
                    AppendInlineMarkdownToParagraph(contPara, ownLines[k], false);
                    pendingListItem.Blocks.InsertAfter(insertAfter, contPara);
                    insertAfter = contPara;
                }
                if (pendingTaskChecked.HasValue)
                {
                    // チェックボックスの見た目はBlockStyles.CreateTaskCheckboxContainerに
                    // 一本化し、ライブ入力変換側（ListEditor.ConvertListItemTextToTaskCheckbox）
                    // と食い違わないようにしている。
                    var container = BlockStyles.CreateTaskCheckboxContainer(pendingTaskChecked.Value);
                    if (pendingPara.Inlines.Count > 0)
                    {
                        pendingPara.Inlines.InsertBefore(pendingPara.Inlines.FirstInline, container);
                    }
                    else
                    {
                        // 空のRunも一緒に入れる理由はListEditor.ConvertListItemTextToTaskCheckbox
                        // の説明を参照（段落の中身がチェックボックスだけだと、行の高さが本文と
                        // 違う基準で計算されてしまうため）。
                        pendingPara.Inlines.Add(container);
                        pendingPara.Inlines.Add(new Run(""));
                    }
                }
            }

            foreach (var line in a_listLines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue; // 「緩い」リストの項目間の空行
                }

                var m = Regex.Match(line, "^(\\s*)(?:([*+-])|(\\d+)\\.)\\s+(.*)$");
                if (m.Success)
                {
                    FlushPendingItem();

                    int indent = ListIndentWidth(m.Groups[1].Value);
                    bool orderedFlg = m.Groups[3].Success;
                    string bulletMarker = orderedFlg ? null : m.Groups[2].Value;
                    string text = m.Groups[4].Value;

                    // タスクリスト項目（"[ ] " / "[x] " / "[X] "）かどうかを判定する。
                    bool? taskChecked = null;
                    var taskMatch = Regex.Match(text, "^\\[([ xX])\\]\\s+(.*)$");
                    if (taskMatch.Success)
                    {
                        taskChecked = " " != taskMatch.Groups[1].Value;
                        text = taskMatch.Groups[2].Value;
                    }

                    if (!rootMarkerSetFlg)
                    {
                        rootList.MarkerStyle = orderedFlg ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                        rootList.Tag = orderedFlg ? null : bulletMarker;
                        rootMarkerSetFlg = true;
                    }

                    while (stack.Count > 1 &&
                           stack[stack.Count - 1].level > indent)
                        stack.RemoveAt(stack.Count - 1);

                    var top = stack[stack.Count - 1];
                    int level = indent;
                    if (top.level + LIST_NEST_MIN_INDENT <= indent &&
                        top.list.ListItems.Count > 0)
                    {
                        var lastLi = top.list.ListItems.Cast<ListItem>().Last();
                        List nestedList = lastLi.Blocks.Count > 1 ? lastLi.Blocks.LastBlock as List : null;
                        if (null == nestedList)
                        {
                            // VSCode同様「1段目Disc・2段目Circle・3段目以降Box」にするため、
                            // BlockStyles.UnorderedMarkerStyleForDepthで段数に応じたマーカーを
                            // 決める（ListEditor.IndentListItem等と同じ理由）。この時点で
                            // stack.Count は「これから作る新しいnestedListの1つ手前までの段数」と
                            // 一致するため、新しいnestedList自身の段数はstack.Count + 1になる。
                            // Marginは明示的に0にする（ListEditor.IndentListItem等と同じ理由）。
                            // RichTextBoxのStyle（TargetType="List"）はList自身にも既定の下余白を
                            // 与えるため、それを残すと、入れ子リストの最後の項目自身の段落が持つ
                            // 下余白と、この入れ子List自身の下余白が二重に加算されてしまい、
                            // 字下げを戻した直後の間隔だけ他より不自然に広くなる。
                            nestedList = new List
                            {
                                MarkerStyle = orderedFlg
                                    ? TextMarkerStyle.Decimal
                                    : BlockStyles.UnorderedMarkerStyleForDepth(
                                        stack.Count + 1, m_uniformMarkerStyleFlg?.Invoke() ?? false),
                                Tag = orderedFlg ? null : bulletMarker,
                                Margin = new Thickness(0)
                            };
                            lastLi.Blocks.Add(nestedList);
                        }
                        stack.Add((nestedList, level));
                        top = stack[stack.Count - 1];
                    }

                    if (orderedFlg)
                    {
                        if (!numbersByList.TryGetValue(top.list, out var nums))
                        {
                            nums = new List<int>();
                            numbersByList[top.list] = nums;
                        }
                        nums.Add(int.Parse(m.Groups[3].Value));
                    }

                    var para = new Paragraph { Margin = new Thickness(0) };
                    var newLi = new ListItem(para);
                    top.list.ListItems.Add(newLi);

                    pendingPara = para;
                    pendingListItem = newLi;
                    pendingTextLines = new List<string> { text };
                    pendingTaskChecked = taskChecked;
                }
                else
                {
                    if (null != pendingPara)
                    {
                        pendingTextLines.Add(line.Trim());
                    }
                }
            }

            FlushPendingItem();

            // ソースがすべての項目で同じ数字を使っていた場合（例: "1." / "1." / "1."。
            // レンダラーに自動採番させる一般的なMarkdownの書き方）は、そのスタイルを維持する。
            // ListToMarkdown は連番ではなく常に "1." で書き出すようになる。
            foreach (var kv in numbersByList)
            {
                if (kv.Value.Count > 1 &&
                    1 == kv.Value.Distinct().Count())
                    kv.Key.Tag = "const";
            }

            return rootList;
        }

        /// <summary>
        /// 1行分のテキストからインライン装飾（画像、`コード`、**太字**、~~取り消し線~~、
        /// [リンク](a_url)、&lt;自動リンク&gt;）を解析し、段落のInlinesへ追加する。
        /// </summary>
        /// <param name="p">追加先の段落。</param>
        /// <param name="text">解析対象のテキスト（項目の1行目＋継続行を結合した、
        /// 改行を含む複数行テキストの場合もある）。</param>
        /// <param name="append">falseなら、追加する前に段落の既存Inlinesをクリアする。</param>
        // \ に続く1文字をエスケープする際、正規表現の特殊文字マッチングから守るために一時的に
        // 使うプレースホルダ文字（Unicode私用領域）。マッチング後、実際の文字に戻す。
        private static readonly Dictionary<char, char> m_escapePlaceholders = new Dictionary<char, char>
        {
            ['*'] = '',
            ['~'] = '',
            ['`'] = '',
            ['\\'] = '',
            ['['] = '',
            [']'] = '',
            ['('] = '',
            [')'] = '',
            ['<'] = '',
            ['>'] = '',
            ['_'] = '',
            ['='] = '',
        };
        private static readonly Dictionary<char, char> m_placeholderToChar =
            m_escapePlaceholders.ToDictionary(kv => kv.Value, kv => kv.Key);

        /// <summary>
        /// エスケープ記法（\ + 1文字）を解決する。\の直後の1文字を、以降の**/~~/`/[]などの
        /// パターンマッチングに巻き込まれないよう一時的なプレースホルダ文字に置き換える
        /// （実際の文字への復元はRun生成時に行う）。
        /// ・\が2つ以上連続する場合（例: \\\\）は、1つ目の\だけが「エスケープする側」として
        ///   消費され、2つ目以降の\はすべてそのまま表示される（さらなるエスケープ処理はしない）。
        /// ・[リンク文字列](URL) のリンク文字列（[]の中）では、\はエスケープ文字として扱わず
        ///   そのまま表示する。
        /// ・インラインコード（`...`）の中では、\はエスケープ文字として扱わずそのまま表示する
        ///   （CommonMark と同じ。`%LocalAppData%\mdew\settings.json` 等のパスの \ が消えないように）。
        /// ・\の直後が記号（ASCII の句読点）でない場合は、エスケープではないので \ もそのまま表示する
        ///   （CommonMark と同じ。C:\src のような文中のパスの \ が消えないように）。
        /// </summary>
        /// <param name="a_text">1行分の生テキスト。</param>
        /// <returns>エスケープ処理を適用した後のテキスト（プレースホルダ文字を含む）。</returns>
        private string PreprocessEscapes(string a_text)
        {
            if (a_text.IndexOf('\\') < 0)
            {
                return a_text;
            }

            // リンク/ファイルリンクの [表示文字] 部分は、エスケープ処理の対象外とする範囲として
            // 先に洗い出しておく。
            var exemptRanges = new List<(int start, int end)>();
            foreach (Match m in Regex.Matches(a_text, "(?<!!)\\[([^\\]]*)\\]\\((?:[^()]|\\([^()]*\\))+\\)"))
            {
                var g = m.Groups[1];
                exemptRanges.Add((g.Index, g.Index + g.Length));
            }

            // 画像記法 ![alt](パス) の「パス」部分（()の中身）も対象外にする。ここは表示文字
            // ではなくファイルパス/URLであり、Windowsの画像パスは"\"区切りで書かれることも
            // あるため、\をエスケープ文字として解釈して消してしまうとパスが壊れてしまうため。
            foreach (Match m in Regex.Matches(a_text, "!\\[[^\\]]*\\]\\(((?:[^()]|\\([^()]*\\))+)\\)"))
            {
                var g = m.Groups[1];
                exemptRanges.Add((g.Index, g.Index + g.Length));
            }
            // インラインコードの中（囲みのバッククォートを含む）も対象外にする。コード区間の見つけ方は ParseTableRow・
            // m_inlineTokenRegex と同じ（開きと同じ個数のバッククォートの並びで閉じる）。コードの外の \` は
            // コードの始まりではない（エスケープされたバッククォート）。
            for (int ci = 0; ci < a_text.Length;)
            {
                if ('\\' == a_text[ci])
                {
                    ci += 2;
                }
                else if ('`' == a_text[ci])
                {
                    int runLength = CountBacktickRun(a_text, ci);
                    int closeIndex = FindClosingBacktickRun(a_text, ci + runLength, runLength);
                    if (closeIndex >= 0)
                    {
                        exemptRanges.Add((ci, closeIndex + runLength));
                        ci = closeIndex + runLength;
                    }
                    else
                    {
                        ci += runLength;
                    }
                }
                else
                {
                    ci++;
                }
            }
            bool IsExempt(int a_idx)
            {
                foreach (var (s, e) in exemptRanges)
                {
                    if (a_idx >= s &&
                        a_idx < e)
                    {
                        return true;
                    }
                }
                return false;
            }

            var sb = new StringBuilder();
            int i = 0;
            while (i < a_text.Length)
            {
                if ('\\' != a_text[i]) { sb.Append(a_text[i]); i++; continue; }

                int runStart = i;
                int runLen = 0;
                while (i < a_text.Length &&
                       '\\' == a_text[i]) { runLen++; i++; }

                if (IsExempt(runStart))
                {
                    for (int k = 0; k < runLen; k++)
                    {
                        sb.Append('\\');
                    }
                    continue;
                }

                if (1 == runLen)
                {
                    if (i < a_text.Length)
                    {
                        char next = a_text[i];
                        if (m_escapePlaceholders.TryGetValue(next, out char ph))
                        {
                            sb.Append(ph);
                        }
                        else if (IsAsciiPunctuation(next))
                        {
                            sb.Append(next);
                        }
                        else
                        {
                            // 記号以外の前の \ はエスケープではない（\ もそのまま表示する）
                            sb.Append('\\').Append(next);
                        }
                        i++;
                    }
                    else
                    {
                        sb.Append('\\'); // 行末の孤立した\は、エスケープ対象がないのでそのまま表示
                    }
                }
                else
                {
                    // 2つ以上連続する場合：1つ目が2つ目を「エスケープ」して消費し（結果として
                    // \が1つ分表示される）、3つ目以降はさらなるエスケープ処理をせずそのまま表示する。
                    sb.Append(m_escapePlaceholders['\\']);
                    for (int k = 0; k < runLen - 2; k++)
                    {
                        sb.Append('\\');
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>CommonMark で \ でエスケープできる文字（ASCII の句読点・記号）か。</summary>
        /// <param name="a_c">文字。</param>
        /// <returns>エスケープできる文字なら true。</returns>
        private static bool IsAsciiPunctuation(char a_c)
        {
            return (a_c >= '!' && a_c <= '/') || (a_c >= ':' && a_c <= '@') || (a_c >= '[' && a_c <= '`') || (a_c >= '{' && a_c <= '~');
        }

        /// <summary>エスケープ処理で使ったプレースホルダ文字を、実際の文字へ戻す。</summary>
        /// <param name="a_text">対象の文字列。</param>
        /// <returns>実際の文字に戻したテキスト。</returns>
        private string RestorePlaceholders(string a_text)
        {
            if (0 == a_text.Length)
            {
                return a_text;
            }
            var sb = new StringBuilder(a_text.Length);
            foreach (char c in a_text)
            {
                sb.Append(m_placeholderToChar.TryGetValue(c, out char real) ? real : c);
            }
            return sb.ToString();
        }

        /// <summary>画像を内包するMarkdownリンクを、表示用画像とリンク情報を持つImageへ変換する。</summary>
        private Image BuildLinkedImageFromMarkdown(Match a_match)
        {
            SplitUrlAndTitle(a_match.Groups["linkedImageSrc"].Value, out string imageSrcRaw, out string imageTitleRaw);
            SplitUrlAndTitle(a_match.Groups["linkedImageHref"].Value, out string linkUrlRaw, out string linkTitleRaw);
            var image = m_imageManager.BuildImageFromMarkdown(
                RestorePlaceholders(a_match.Groups["linkedImageAlt"].Value),
                RestorePlaceholders(imageSrcRaw),
                null == imageTitleRaw ? null : RestorePlaceholders(imageTitleRaw));
            if (image.Tag is ImageInfo info)
            {
                info.LinkUrl = RestorePlaceholders(linkUrlRaw);
                info.LinkTitle = null == linkTitleRaw ? null : RestorePlaceholders(linkTitleRaw);
            }
            return image;
        }

        /// <summary>
        /// 1行分（段落・見出し・セル等の中身）の Markdown を読んで、段落の末尾へ追加する。書式は入れ子でも読む
        /// （「~~**太字の取り消し線**~~」・「***太字の斜体***」等。ParseInlineSpecs）。
        /// </summary>
        /// <param name="a_p">追加先の段落。</param>
        /// <param name="a_text">Markdown の文字。</param>
        /// <param name="a_appendFlg">false の時は、段落の中身を消してから追加する。</param>
        public void AppendInlineMarkdownToParagraph(Paragraph a_p, string a_text, bool a_appendFlg)
        {
            if (!a_appendFlg)
            {
                a_p.Inlines.Clear();
            }
            a_text = PreprocessEscapes(a_text);
            // 書式の記号（m_inlineTokenRegex のどの書き方にも必ず含まれる文字）を1つも含まない文字（普通の文章だけの
            // 表のセル等）は、正規表現で探さずに、そのまま文字として追加する（探しても何も見つからないので、結果は同じ）
            if (a_text.IndexOfAny(m_inlineMarkupChars) < 0)
            {
                if (0 < a_text.Length)
                {
                    AppendPlainTextWithLineBreaks(a_p, a_text);
                }
                BlockStyles.ApplyImageParagraphLineHeight(a_p);
                return;
            }
            var specs = new List<InlineSpec>();
            ParseInlineSpecs(a_text, InlineStyleFlags.None, specs, 0);
            foreach (InlineSpec spec in specs)
            {
                a_p.Inlines.Add(CreateInlineFromSpec(spec));
            }
            // 画像を含む段落は行の高さの固定を外す（画像が上の文章に重なって描画されるのを防ぐ。
            // BlockStyles.ApplyImageParagraphLineHeight参照）。
            BlockStyles.ApplyImageParagraphLineHeight(a_p);
        }

        /// <summary>読み込んだ文中の要素1つ分（Run・改行・画像等を作るための情報）。</summary>
        private sealed class InlineSpec
        {
            /// <summary>要素の種類。</summary>
            public InlineSpecKind Kind;
            /// <summary>文字（Text・Link の時）。</summary>
            public string Text;
            /// <summary>書式（Text の時は Run の書式。Link の時は重ねた書式）。</summary>
            public InlineStyleFlags Flags;
            /// <summary>画像（Image の時）。</summary>
            public Image Image;
            /// <summary>リンクの情報（Link の時）。</summary>
            public LinkInfo Link;
            /// <summary>リンクのツールチップ（Link の時）。</summary>
            public string ToolTip;
            /// <summary>アンカーの id（Anchor の時）。</summary>
            public string AnchorId;
        }

        /// <summary>InlineSpec の種類。</summary>
        private enum InlineSpecKind
        {
            Text,
            LineBreak,
            Image,
            Link,
            Anchor,
        }

        /// <summary>入れ子の書式を読む深さの上限（これより深い所は、書式として読まずに文字のまま置く）。</summary>
        private const int MAX_EMPHASIS_DEPTH = 16;

        /// <summary>
        /// 1行分の Markdown（PreprocessEscapes 済み）を読んで、要素の並び（InlineSpec）にする。画像・コード・リンク等は
        /// m_inlineTokenRegex で探し、太字・斜体・取り消し線・下線・ハイライトは TryParseEmphasis で読む。書式の中身は
        /// 同じ方法で読み直す（入れ子の書式は、外側の書式に重ねる。a_flags）。どちらも、文字の前の方にあるものを先に
        /// 読む（以前の、1つの正規表現で前から探していた時と同じ順番）。
        /// </summary>
        /// <param name="a_text">Markdown の文字（エスケープはプレースホルダの文字になっている）。</param>
        /// <param name="a_flags">外側の書式（中身の文字すべてに重ねる）。</param>
        /// <param name="a_out">結果の追加先。</param>
        /// <param name="a_depth">入れ子の深さ。</param>
        private void ParseInlineSpecs(string a_text, InlineStyleFlags a_flags, List<InlineSpec> a_out, int a_depth)
        {
            int i = 0;
            int plainStart = 0;
            Match token = null;
            while (i < a_text.Length)
            {
                // 次の画像・コード・リンク等（前に探した結果がまだ先にあれば、それを使う：その間には無いことが分かっている）
                if (null == token || (token.Success && token.Index < i))
                {
                    token = m_inlineTokenRegex.Match(a_text, i);
                }
                int tokenIndex = token.Success ? token.Index : a_text.Length;
                bool emphasisFoundFlg = false;
                if (a_depth < MAX_EMPHASIS_DEPTH)
                {
                    for (int p = i; p < tokenIndex; p++)
                    {
                        char c = a_text[p];
                        if ('*' != c && '_' != c && '~' != c && '=' != c && '<' != c)
                        {
                            continue;
                        }
                        if (TryParseEmphasis(a_text, p, i, out InlineStyleFlags flag, out int innerStart, out int innerEnd, out int end))
                        {
                            AppendPlainSpecs(a_text.Substring(plainStart, p - plainStart), a_flags, a_out);
                            ParseInlineSpecs(a_text.Substring(innerStart, innerEnd - innerStart), a_flags | flag, a_out, a_depth + 1);
                            i = end;
                            plainStart = end;
                            emphasisFoundFlg = true;
                            break;
                        }
                    }
                }
                if (emphasisFoundFlg)
                {
                    continue;
                }
                if (!token.Success)
                {
                    break;
                }
                AppendPlainSpecs(a_text.Substring(plainStart, token.Index - plainStart), a_flags, a_out);
                AppendTokenSpecs(token, a_flags, a_out);
                i = token.Index + token.Length;
                plainStart = i;
                token = null;
            }
            if (plainStart < a_text.Length)
            {
                AppendPlainSpecs(a_text.Substring(plainStart), a_flags, a_out);
            }
        }

        /// <summary>
        /// a_pos から始まる書式（**太字**・*斜体*・_斜体_・~~取り消し線~~・==ハイライト==・&lt;u&gt;下線&lt;/u&gt;）を読む。
        /// 以前の読み方（1つの正規表現）と同じく、太字・取り消し線・ハイライト・下線は、記号の内側の空白を問わない。
        /// 中身には別の書式を含められる（入れ子）。閉じの記号は、インラインコードの中のものは使わない。
        /// </summary>
        /// <param name="a_text">文字。</param>
        /// <param name="a_pos">開きの記号の位置。</param>
        /// <param name="a_regionStart">まだ読んでいない部分の先頭（これより前の文字は、記号の直前の文字として見ない）。</param>
        /// <param name="a_flag">読んだ書式。</param>
        /// <param name="a_innerStart">中身の先頭の位置。</param>
        /// <param name="a_innerEnd">中身の終わりの位置（閉じの記号の位置）。</param>
        /// <param name="a_end">閉じの記号の次の位置。</param>
        /// <returns>書式として読めたら true。</returns>
        private static bool TryParseEmphasis(string a_text, int a_pos, int a_regionStart, out InlineStyleFlags a_flag,
            out int a_innerStart, out int a_innerEnd, out int a_end)
        {
            a_flag = InlineStyleFlags.None;
            a_innerStart = 0;
            a_innerEnd = 0;
            a_end = 0;
            char c = a_text[a_pos];
            char prev = a_pos > a_regionStart ? a_text[a_pos - 1] : '\0';
            switch (c)
            {
                case '<':
                    if (a_pos + 3 <= a_text.Length && 0 == string.Compare(a_text, a_pos, "<u>", 0, 3, StringComparison.OrdinalIgnoreCase))
                    {
                        int close = IndexOfOutsideCode(a_text, "</u>", a_pos + 3, StringComparison.OrdinalIgnoreCase);
                        if (close > a_pos + 3)
                        {
                            a_flag = InlineStyleFlags.Underline;
                            a_innerStart = a_pos + 3;
                            a_innerEnd = close;
                            a_end = close + 4;
                            return true;
                        }
                    }
                    return false;
                case '~':
                case '=':
                    {
                        // ~~…~~・==…==（記号の並びの先頭からだけ読む）
                        if (a_pos + 1 >= a_text.Length || c != a_text[a_pos + 1] || c == prev)
                        {
                            return false;
                        }
                        string marker = new string(c, 2);
                        int k = IndexOfOutsideCode(a_text, marker, a_pos + 3, StringComparison.Ordinal);
                        if (k < 0)
                        {
                            return false;
                        }
                        a_flag = '~' == c ? InlineStyleFlags.Strikethrough : InlineStyleFlags.Highlight;
                        a_innerStart = a_pos + 2;
                        a_innerEnd = k;
                        a_end = k + 2;
                        return true;
                    }
                case '*':
                    {
                        if ('*' == prev)
                        {
                            return false;
                        }
                        int runLength = CountRun(a_text, a_pos, '*');
                        if (runLength >= 3)
                        {
                            // ***…***：斜体を外側として読む（中の **…** は中身として読み直される）。読めなければ太字を外側にする
                            return TryParseStarItalic(a_text, a_pos, true, out a_flag, out a_innerStart, out a_innerEnd, out a_end) ||
                                TryParseBold(a_text, a_pos, out a_flag, out a_innerStart, out a_innerEnd, out a_end);
                        }
                        if (2 == runLength)
                        {
                            return TryParseBold(a_text, a_pos, out a_flag, out a_innerStart, out a_innerEnd, out a_end);
                        }
                        return TryParseStarItalic(a_text, a_pos, false, out a_flag, out a_innerStart, out a_innerEnd, out a_end);
                    }
                case '_':
                    return TryParseUnderscoreItalic(a_text, a_pos, prev, out a_flag, out a_innerStart, out a_innerEnd, out a_end);
                default:
                    return false;
            }
        }

        /// <summary>
        /// **太字** を読む。閉じの「**」が3つ以上の * の並び（「**a *b***」の最後等）の一部の時は、中身に閉じていない
        /// * が残る（中身の中の1つだけの * の並びが奇数個）なら、並びの最後の2つを太字の閉じとする（「*b*」を中身に入れる）。
        /// </summary>
        private static bool TryParseBold(string a_text, int a_pos, out InlineStyleFlags a_flag, out int a_innerStart, out int a_innerEnd, out int a_end)
        {
            a_flag = InlineStyleFlags.None;
            a_innerStart = a_pos + 2;
            a_innerEnd = 0;
            a_end = 0;
            int from = a_pos + 3; // 中身は1文字以上
            while (from <= a_text.Length)
            {
                int k = IndexOfOutsideCode(a_text, "**", from, StringComparison.Ordinal);
                if (k < 0)
                {
                    return false;
                }
                int runLength = CountRun(a_text, k, '*');
                if (runLength >= 3 && 1 == CountSingleStars(a_text, a_innerStart, k) % 2)
                {
                    k += runLength - 2;
                }
                a_flag = InlineStyleFlags.Bold;
                a_innerEnd = k;
                a_end = k + 2;
                return true;
            }
            return false;
        }

        /// <summary>
        /// *斜体* を読む（以前の読み方と同じく、開きの * の後ろと閉じの * の前は空白でないこと・中身に改行を含まないこと）。
        /// 中身の中の **太字** は飛ばして閉じの * を探す。閉じの * が3つ以上の並びの先頭の時は、その1つ目を閉じとする
        /// （残りの ** は外側の太字の閉じ）。
        /// </summary>
        /// <param name="a_tripleFlg">開きが *** の時（開きの直後の * を許す）。</param>
        private static bool TryParseStarItalic(string a_text, int a_pos, bool a_tripleFlg, out InlineStyleFlags a_flag,
            out int a_innerStart, out int a_innerEnd, out int a_end)
        {
            a_flag = InlineStyleFlags.None;
            a_innerStart = a_pos + 1;
            a_innerEnd = 0;
            a_end = 0;
            if (a_pos + 1 >= a_text.Length || char.IsWhiteSpace(a_text[a_pos + 1]) || (!a_tripleFlg && '*' == a_text[a_pos + 1]))
            {
                return false;
            }
            int from = a_pos + 1;
            while (from < a_text.Length)
            {
                int k = IndexOfOutsideCode(a_text, "*", from, StringComparison.Ordinal);
                if (k < 0 || 0 <= a_text.IndexOf('\n', a_pos, k - a_pos))
                {
                    return false;
                }
                int runLength = CountRun(a_text, k, '*');
                if (2 == runLength || (k == a_pos + 1 && a_tripleFlg))
                {
                    // 中身の中の太字：その閉じの後ろから探し直す
                    if (!TryParseBold(a_text, k, out _, out _, out _, out int boldEnd))
                    {
                        return false;
                    }
                    from = boldEnd;
                    continue;
                }
                if (k == a_pos + 1 || char.IsWhiteSpace(a_text[k - 1]))
                {
                    return false;
                }
                a_flag = InlineStyleFlags.Italic;
                a_innerEnd = k;
                a_end = k + 1;
                return true;
            }
            return false;
        }

        /// <summary>
        /// _斜体_ を読む（以前の読み方と同じく、英数字の単語の途中の _ は使わない・中身に _ と改行を含まない・開きの _ の
        /// 後ろと閉じの _ の前は空白でないこと）。
        /// </summary>
        private static bool TryParseUnderscoreItalic(string a_text, int a_pos, char a_prev, out InlineStyleFlags a_flag,
            out int a_innerStart, out int a_innerEnd, out int a_end)
        {
            a_flag = InlineStyleFlags.None;
            a_innerStart = a_pos + 1;
            a_innerEnd = 0;
            a_end = 0;
            if (IsWordChar(a_prev) || a_pos + 1 >= a_text.Length || char.IsWhiteSpace(a_text[a_pos + 1]) || '_' == a_text[a_pos + 1])
            {
                return false;
            }
            int k = IndexOfOutsideCode(a_text, "_", a_pos + 1, StringComparison.Ordinal);
            if (k < 0 || 0 <= a_text.IndexOf('\n', a_pos, k - a_pos) || char.IsWhiteSpace(a_text[k - 1]) ||
                (k + 1 < a_text.Length && IsWordChar(a_text[k + 1])))
            {
                return false;
            }
            a_flag = InlineStyleFlags.ItalicUnderscore;
            a_innerEnd = k;
            a_end = k + 1;
            return true;
        }

        /// <summary>正規表現の \w と同じく、単語の文字（文字・数字・_）か。</summary>
        private static bool IsWordChar(char a_c)
        {
            return '\0' != a_c && (char.IsLetterOrDigit(a_c) || '_' == a_c);
        }

        /// <summary>a_pos から続く a_c の並びの長さ。</summary>
        private static int CountRun(string a_text, int a_pos, char a_c)
        {
            int k = a_pos;
            while (k < a_text.Length && a_c == a_text[k])
            {
                k++;
            }
            return k - a_pos;
        }

        /// <summary>a_from〜a_to の間の、1つだけの * の並びの数（インラインコードの中は数えない）。</summary>
        private static int CountSingleStars(string a_text, int a_from, int a_to)
        {
            int count = 0;
            int k = a_from;
            while (k < a_to)
            {
                int star = IndexOfOutsideCode(a_text, "*", k, StringComparison.Ordinal);
                if (star < 0 || star >= a_to)
                {
                    break;
                }
                int runLength = Math.Min(CountRun(a_text, star, '*'), a_to - star);
                if (1 == runLength)
                {
                    count++;
                }
                k = star + runLength;
            }
            return count;
        }

        /// <summary>a_from から a_value を探す。インラインコード（`…`）の中にあるものは飛ばす。</summary>
        /// <returns>見つかった位置。無ければ -1。</returns>
        private static int IndexOfOutsideCode(string a_text, string a_value, int a_from, StringComparison a_comparison)
        {
            int from = a_from;
            while (from <= a_text.Length)
            {
                int k = a_text.IndexOf(a_value, from, a_comparison);
                int backtick = a_text.IndexOf('`', from);
                if (backtick < 0 || (0 <= k && k < backtick))
                {
                    return k;
                }
                // k はコードの開きより後ろ：コードを飛ばす（閉じの無い ` は文字として飛ばす）
                int runLength = CountRun(a_text, backtick, '`');
                int close = FindBacktickRun(a_text, backtick + runLength, runLength);
                from = close < 0 ? backtick + runLength : close + runLength;
            }
            return -1;
        }

        /// <summary>a_from から、ちょうど a_length 個のバッククォートの並びを探す。</summary>
        /// <returns>見つかった位置。無ければ -1。</returns>
        private static int FindBacktickRun(string a_text, int a_from, int a_length)
        {
            int k = a_from;
            while (k < a_text.Length)
            {
                int backtick = a_text.IndexOf('`', k);
                if (backtick < 0)
                {
                    return -1;
                }
                int runLength = CountRun(a_text, backtick, '`');
                if (runLength == a_length)
                {
                    return backtick;
                }
                k = backtick + runLength;
            }
            return -1;
        }

        /// <summary>
        /// 書式の記号を含まない文字を、要素の並びに追加する。改行（\n）は LineBreak にし、エスケープした文字
        /// （プレースホルダ）は1文字ずつの Run（書式に Escaped を重ねる。保存時に \ を付けて書き戻す）にする。
        /// </summary>
        /// <param name="a_text">文字。</param>
        /// <param name="a_flags">書式。</param>
        /// <param name="a_out">追加先。</param>
        private void AppendPlainSpecs(string a_text, InlineStyleFlags a_flags, List<InlineSpec> a_out)
        {
            if (0 == a_text.Length)
            {
                return;
            }
            var plain = new StringBuilder();
            void flush()
            {
                if (0 < plain.Length)
                {
                    a_out.Add(new InlineSpec { Kind = InlineSpecKind.Text, Text = plain.ToString(), Flags = a_flags });
                    plain.Clear();
                }
            }
            foreach (char c in a_text)
            {
                if ('\n' == c)
                {
                    flush();
                    a_out.Add(new InlineSpec { Kind = InlineSpecKind.LineBreak });
                }
                else if (m_placeholderToChar.TryGetValue(c, out char real))
                {
                    flush();
                    a_out.Add(new InlineSpec { Kind = InlineSpecKind.Text, Text = real.ToString(), Flags = a_flags | InlineStyleFlags.Escaped });
                }
                else
                {
                    plain.Append(c);
                }
            }
            flush();
        }

        /// <summary>画像・コード・リンク等（m_inlineTokenRegex に一致したもの）を、要素の並びに追加する。</summary>
        /// <param name="a_match">一致した結果。</param>
        /// <param name="a_flags">外側の書式（コード・リンクに重ねる。画像・アンカーには付けない）。</param>
        /// <param name="a_out">追加先。</param>
        private void AppendTokenSpecs(Match a_match, InlineStyleFlags a_flags, List<InlineSpec> a_out)
        {
            InlineStyleFlags stackable = a_flags & InlineStyles.STACKABLE_MASK;
            Match m = a_match;
            if (m.Groups["linkedImage"].Success)
            {
                a_out.Add(new InlineSpec { Kind = InlineSpecKind.Image, Image = BuildLinkedImageFromMarkdown(m) });
            }
            else if (m.Groups[1].Success)
            {
                a_out.Add(new InlineSpec { Kind = InlineSpecKind.Image, Image = m_imageManager.BuildImageFromHtmlTag(m.Groups[1].Value) });
            }
            else if (m.Groups[2].Success)
            {
                SplitUrlAndTitle(m.Groups[4].Value, out string imgSrc, out string imgTitle);
                a_out.Add(new InlineSpec { Kind = InlineSpecKind.Image, Image = m_imageManager.BuildImageFromMarkdown(m.Groups[3].Value, imgSrc, imgTitle) });
            }
            else if (m.Groups[5].Success)
            {
                string[] lines = StripCodeSpanPadding(m.Groups[6].Value).Split('\n');
                for (int k = 0; k < lines.Length; k++)
                {
                    if (0 < k)
                    {
                        a_out.Add(new InlineSpec { Kind = InlineSpecKind.LineBreak });
                    }
                    a_out.Add(new InlineSpec { Kind = InlineSpecKind.Text, Text = RestorePlaceholders(lines[k]), Flags = stackable | InlineStyleFlags.Code });
                }
            }
            else if (m.Groups[15].Success)
            {
                SplitUrlAndTitle(m.Groups[17].Value, out string linkUrlRaw, out string linkTitleRaw);
                string linkTitle = null == linkTitleRaw ? null : RestorePlaceholders(linkTitleRaw);
                string url = RestorePlaceholders(linkUrlRaw);
                a_out.Add(new InlineSpec
                {
                    Kind = InlineSpecKind.Link,
                    Text = RestorePlaceholders(m.Groups[16].Value),
                    Flags = stackable,
                    Link = new LinkInfo { Url = url, IsAutoLinkFlg = false, Title = linkTitle, Styles = InlineStyles.ToTag(stackable) },
                    ToolTip = string.IsNullOrEmpty(linkTitle) ? url : linkTitle
                });
            }
            else if (m.Groups[18].Success)
            {
                string url = RestorePlaceholders(m.Groups[19].Value);
                a_out.Add(new InlineSpec
                {
                    Kind = InlineSpecKind.Link,
                    Text = url,
                    Flags = stackable,
                    Link = new LinkInfo { Url = url, IsAutoLinkFlg = true, Styles = InlineStyles.ToTag(stackable) },
                    ToolTip = url
                });
            }
            else if (m.Groups[20].Success)
            {
                string email = RestorePlaceholders(m.Groups[21].Value);
                a_out.Add(new InlineSpec
                {
                    Kind = InlineSpecKind.Link,
                    Text = email,
                    Flags = stackable,
                    Link = new LinkInfo { Url = "mailto:" + email, IsAutoLinkFlg = false, IsEmailAutoLinkFlg = true, Styles = InlineStyles.ToTag(stackable) },
                    ToolTip = "mailto:" + email
                });
            }
            else if (m.Groups[22].Success)
            {
                a_out.Add(new InlineSpec { Kind = InlineSpecKind.Anchor, AnchorId = m.Groups[23].Value });
            }
            else if (m.Groups[24].Success)
            {
                // 文中にそのまま書かれたメールアドレス（表示だけ mailto のリンクにする）
                string email = RestorePlaceholders(m.Groups[25].Value);
                a_out.Add(new InlineSpec
                {
                    Kind = InlineSpecKind.Link,
                    Text = email,
                    Flags = stackable,
                    Link = new LinkInfo { Url = "mailto:" + email, IsBareEmailFlg = true, Styles = InlineStyles.ToTag(stackable) },
                    ToolTip = "mailto:" + email
                });
            }
        }

        /// <summary>要素の情報（InlineSpec）から、段落に入れる Inline を作る。</summary>
        /// <param name="a_spec">要素の情報。</param>
        /// <returns>作った Inline。</returns>
        private Inline CreateInlineFromSpec(InlineSpec a_spec)
        {
            switch (a_spec.Kind)
            {
                case InlineSpecKind.LineBreak:
                    return new LineBreak();
                case InlineSpecKind.Image:
                    return new InlineUIContainer(a_spec.Image);
                case InlineSpecKind.Anchor:
                    return new Run("") { Tag = new AnchorInfo { Id = a_spec.AnchorId } };
                case InlineSpecKind.Link:
                    {
                        var run = new Run(BlockStyles.AddUrlBreakJoiners(a_spec.Text));
                        SetLinkProperties(run, a_spec);
                        return run;
                    }
                default:
                    return InlineStyles.CreateRun(a_spec.Text, a_spec.Flags);
            }
        }

        /// <summary>リンクの Run に、リンクの見た目・情報と、重ねた書式の見た目を設定する。</summary>
        /// <param name="a_run">リンクの Run。</param>
        /// <param name="a_spec">リンクの要素の情報。</param>
        private void SetLinkProperties(Run a_run, InlineSpec a_spec)
        {
            a_run.Foreground = m_linkBrush;
            a_run.TextDecorations = BlockStyles.LinkTextDecorations;
            a_run.Tag = a_spec.Link;
            a_run.ToolTip = a_spec.ToolTip;
            if (InlineStyleFlags.None != a_spec.Flags)
            {
                InlineStyles.ApplyLinkVisual(a_run, a_spec.Flags);
            }
        }

        /// <summary>スタイル付きのクリック可能なリンクRunを組み立てる。</summary>
        /// <param name="a_linkText">表示文字。</param>
        /// <param name="a_url">リンク先URL。</param>
        /// <param name="a_isAutoLinkFlg">[a_text](a_url)ではなく&lt;a_url&gt;形式から来た場合はtrue。</param>
        /// <param name="a_title">タイトル属性（[a_text](a_url "title")のtitle部分。省略時はnull）。</param>
        /// <param name="a_isEmailAutoLinkFlg">&lt;email@example.com&gt;形式のメールアドレス
        /// 自動リンクから来た場合はtrue。</param>
        /// <param name="a_isBareEmailFlg">文中にそのまま書かれたメールアドレスから来た場合はtrue。</param>
        /// <returns>組み立てたリンクのRun。</returns>
        public Run BuildLinkRun(string a_linkText, string a_url, bool a_isAutoLinkFlg, string a_title = null, bool a_isEmailAutoLinkFlg = false,
            bool a_isBareEmailFlg = false)
        {
            return new Run(BlockStyles.AddUrlBreakJoiners(a_linkText))
            {
                Foreground = m_linkBrush,
                TextDecorations = BlockStyles.LinkTextDecorations,
                Tag = new LinkInfo
                {
                    Url = a_url,
                    IsAutoLinkFlg = a_isAutoLinkFlg,
                    Title = a_title,
                    IsEmailAutoLinkFlg = a_isEmailAutoLinkFlg,
                    IsBareEmailFlg = a_isBareEmailFlg
                },
                ToolTip = string.IsNullOrEmpty(a_title) ? a_url : a_title
            };
        }

        /// <summary>プレーンテキストを段落へ追加する。埋め込まれた "\n" は実際のLineBreakへ
        /// 変換する（Run単体では改行文字を改行として描画できないため）。エスケープ処理で
        /// 実際の文字に戻された部分は、保存時に再び \ を付けて書き戻せるよう
        /// Tag="escaped" を付けた専用のRunに分けておく。</summary>
        /// <param name="a_p">対象の段落。</param>
        /// <param name="a_text">対象の文字列。</param>
        public void AppendPlainTextWithLineBreaks(Paragraph a_p, string a_text)
        {
            var segments = a_text.Split('\n');
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    a_p.Inlines.Add(new LineBreak());
                }
                AppendPlainSegmentWithEscapeTags(a_p, segments[i]);
            }
        }

        /// <summary>1行分（改行を含まない）のプレーンテキストを、エスケープされた文字ごとに
        /// Runを分けながら段落へ追加する。</summary>
        /// <param name="a_p">対象の段落。</param>
        /// <param name="a_segment">対象の1行分のテキスト。</param>
        private void AppendPlainSegmentWithEscapeTags(Paragraph a_p, string a_segment)
        {
            if (0 == a_segment.Length)
            {
                return;
            }
            var plain = new StringBuilder();
            foreach (char c in a_segment)
            {
                if (m_placeholderToChar.TryGetValue(c, out char real))
                {
                    if (plain.Length > 0) { a_p.Inlines.Add(new Run(plain.ToString())); plain.Clear(); }
                    a_p.Inlines.Add(new Run(real.ToString()) { Tag = "escaped" });
                }
                else
                {
                    plain.Append(c);
                }
            }
            if (plain.Length > 0)
            {
                a_p.Inlines.Add(new Run(plain.ToString()));
            }
        }

        // ======================================================================
        //  生のMarkdownテキストを、任意の位置へその場で挿入する（貼り付け用）
        // ======================================================================

        /// <summary>
        /// 文字を、Markdown の記号として読まずに、そのまま指定した位置へ挿入する（書式なしで貼り付け。Ctrl+Shift+V）。
        /// 改行は、<see cref="InsertInlineMarkdownAtPosition"/> と同じく段落の中の改行（LineBreak）にする。
        /// </summary>
        /// <param name="a_position">挿入する位置。</param>
        /// <param name="a_text">挿入する文字（改行は LF）。</param>
        /// <returns>挿入した内容の末尾の位置。</returns>
        public TextPointer InsertPlainTextAtPosition(TextPointer a_position, string a_text)
        {
            return InsertPlainTextWithLineBreaksAt(a_position, a_text ?? "");
        }

        /// <summary>
        /// Markdown の文字を、見出し・リスト・表・コードブロック等のブロックの並びにする（貼り付け用。mdew・mdee の
        /// pasteMarkdown と同じく、複数のブロックを含む文字を書式にして貼るために使う）。今の文書の読み込みの記録
        /// （元テキストの保持・空行の数等）を壊さないよう、使い捨ての MarkdownConverter・OriginalTextTracker で、
        /// 使い捨ての FlowDocument に読み込んでから取り出す。返す Block はどの文書にも属していないため、そのまま
        /// 編集中の文書に入れられる（元テキストの記録が無いため、保存時は今の構造から書き出される）。
        /// </summary>
        /// <param name="a_md">Markdown の文字（改行は LF）。</param>
        /// <returns>ブロックの並び（空の文字なら空のリスト）。</returns>
        public List<Block> ParseMarkdownBlocks(string a_md)
        {
            var tempConverter = new MarkdownConverter(new OriginalTextTracker(null), m_imageManager,
                m_preserveSourceLineBreaksFlg, m_correctColumnWidthsFlg, m_availableTableWidthFunc, m_uniformMarkerStyleFlg);
            var tempDoc = new FlowDocument();
            tempConverter.MarkdownToDocument(a_md ?? "", tempDoc);
            var blocks = tempDoc.Blocks.ToList();
            tempDoc.Blocks.Clear();
            return blocks;
        }

        /// <summary>
        /// 他アプリ等からの生のMarkdownテキスト（**太字**・~~取り消し線~~・&lt;u&gt;下線&lt;/u&gt;・
        /// `コード`・[リンク](url)等）を、インラインの装飾記法として解釈しながら、指定した
        /// 位置（キャレット位置）へその場で挿入する。<see cref="AppendInlineMarkdownToParagraph"/>
        /// と同じ正規表現・解釈ロジックを使うが、あちらが「段落の末尾へ追記する」動作なのに
        /// 対し、こちらは段落の途中の任意の位置へ挿入できる点が異なる（貼り付け機能向け）。
        /// 各要素は、mdeの他のインライン装飾機能（InlineStyleEditor）と同じ、TextPointerを
        /// 直接渡すRunコンストラクタの方式で挿入する（Inlinesコレクション間でオブジェクトを
        /// 移動させるような操作は行わない）。
        /// </summary>
        /// <param name="a_position">挿入する位置。</param>
        /// <param name="a_text">解釈する生のテキスト。</param>
        /// <returns>挿入した内容の末尾の位置（挿入後のキャレット移動先として使う）。</returns>
        public TextPointer InsertInlineMarkdownAtPosition(TextPointer a_position, string a_text)
        {
            TextPointer cursor = a_position;
            a_text = PreprocessEscapes(a_text);
            var specs = new List<InlineSpec>();
            ParseInlineSpecs(a_text, InlineStyleFlags.None, specs, 0);
            foreach (InlineSpec spec in specs)
            {
                switch (spec.Kind)
                {
                    case InlineSpecKind.LineBreak:
                        {
                            var lb = new LineBreak(cursor);
                            cursor = lb.ElementEnd;
                            break;
                        }
                    case InlineSpecKind.Image:
                        {
                            var img = new InlineUIContainer(spec.Image, cursor);
                            BlockStyles.ApplyImageParagraphLineHeightFor(img);
                            cursor = img.ElementEnd;
                            break;
                        }
                    case InlineSpecKind.Anchor:
                        {
                            var run = new Run("", cursor) { Tag = new AnchorInfo { Id = spec.AnchorId } };
                            cursor = run.ContentEnd;
                            break;
                        }
                    case InlineSpecKind.Link:
                        {
                            var run = new Run(BlockStyles.AddUrlBreakJoiners(spec.Text), cursor);
                            SetLinkProperties(run, spec);
                            cursor = run.ContentEnd;
                            break;
                        }
                    default:
                        {
                            var run = new Run(spec.Text, cursor) { Tag = InlineStyles.ToTag(spec.Flags) };
                            InlineStyles.ApplyVisual(run, spec.Flags);
                            cursor = run.ContentEnd;
                            break;
                        }
                }
            }
            return cursor;
        }

        /// <summary>AppendPlainTextWithLineBreaksの位置指定版。改行は本物のLineBreakとして挿入する。</summary>
        /// <param name="a_position">挿入位置。</param>
        /// <param name="a_text">挿入するプレーンテキスト。</param>
        /// <returns>挿入後の末尾位置。</returns>
        private TextPointer InsertPlainTextWithLineBreaksAt(TextPointer a_position, string a_text)
        {
            TextPointer cursor = a_position;
            var segments = a_text.Split('\n');
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    var lb = new LineBreak(cursor);
                    cursor = lb.ElementEnd;
                }
                cursor = InsertPlainSegmentWithEscapeTagsAt(cursor, segments[i]);
            }
            return cursor;
        }

        /// <summary>AppendPlainSegmentWithEscapeTagsの位置指定版。</summary>
        /// <param name="a_position">挿入位置。</param>
        /// <param name="a_segment">1行分（改行を含まない）のテキスト。</param>
        /// <returns>挿入後の末尾位置。</returns>
        private TextPointer InsertPlainSegmentWithEscapeTagsAt(TextPointer a_position, string a_segment)
        {
            if (0 == a_segment.Length)
            {
                return a_position;
            }
            TextPointer cursor = a_position;
            var plain = new StringBuilder();
            foreach (char c in a_segment)
            {
                if (m_placeholderToChar.TryGetValue(c, out char real))
                {
                    if (plain.Length > 0)
                    {
                        var plainRun = new Run(plain.ToString(), cursor);
                        cursor = plainRun.ContentEnd;
                        plain.Clear();
                    }
                    var escRun = new Run(real.ToString(), cursor) { Tag = "escaped" };
                    cursor = escRun.ContentEnd;
                }
                else
                {
                    plain.Append(c);
                }
            }
            if (plain.Length > 0)
            {
                var plainRun = new Run(plain.ToString(), cursor);
                cursor = plainRun.ContentEnd;
            }
            return cursor;
        }
    }
}
