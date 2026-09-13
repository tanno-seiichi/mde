// BlockStyles.cs
//
// mde (Markdown インラインエディタ) の一部。
// 見出し・コードブロックの見た目（フォントサイズ・余白・枠線など）を適用する静的ヘルパー。
// Markdown解析（MarkdownConverter）と、右クリックでの段落種別変更（HeadingCodeBlockEditor）の
// 両方から共有で使われるため、状態を持たない静的メソッドとして独立させている。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace mde
{
    /// <summary>見出し・コードブロックの段落スタイルを適用する静的ヘルパー群。</summary>
    public static class BlockStyles
    {
        private static readonly Brush m_cellBorder = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4));
        private static readonly Brush m_codeBlockBackground = new SolidColorBrush(Color.FromRgb(0xEC, 0xE8, 0xDC));
        // 表のヘッダー行だけ、セル間の縦の区切り線（右辺）をこの太さにする。本文行の1に対して
        // わずかに太くすることで、印刷（PDF出力）時にヘッダー行の縦罫線だけが描画されずに
        // 消えてしまう現象を避ける（実機での複数バージョン比較検証により、色を変える案では
        // 効果が無く、太さを変えるこの案で解消することを確認済み）。
        private const double HEADER_VERTICAL_BORDER_THICKNESS = 1.75;
        // ハイライト（==text==）の背景色。
        private static readonly Brush m_highlightBackground = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0x8A));

        /// <summary>コードブロックの背景色（他クラスからも参照できるよう公開）。</summary>
        public static Brush CodeBlockBackgroundBrush => m_codeBlockBackground;

        /// <summary>ハイライト（==text==）の背景色（他クラスからも参照できるよう公開）。</summary>
        public static Brush HighlightBrush => m_highlightBackground;

        /// <summary>段落を、見出し・コードブロック等の特別な見た目が付く前の状態にリセットする。</summary>
        /// <param name="a_p">対象の段落。</param>
        public static void ClearSpecialStyling(Paragraph a_p)
        {
            a_p.Background = null;
            a_p.Padding = new Thickness(0);
            a_p.BorderThickness = new Thickness(0);
            a_p.BorderBrush = null;
            a_p.ClearValue(TextElement.FontFamilyProperty);
        }

        /// <summary>段落に見出しスタイルを適用する（a_level=0 なら本文スタイルに戻す）。</summary>
        /// <param name="a_p">対象の段落。</param>
        /// <param name="a_level">見出しレベル（0〜6。0は本文）。</param>
        public static void ApplyHeadingStyle(Paragraph a_p, int a_level)
        {
            ClearSpecialStyling(a_p);
            a_p.Tag = 0 == a_level ? null : (object)a_level;
            if (0 == a_level)
            {
                a_p.FontSize = 16;
                a_p.FontWeight = FontWeights.Normal;
                // Marginはここでは明示的に設定しない。RichTextBoxのStyle側でEditorBlockSpacing
                // （行間の値に関わらず常に固定値。MainWindow.xaml.csのApplyEditorLineHeight
                // 参照）が適用されることで、段落と段落の間の間隔が、行間の設定を変えても
                // 常に一定になるようにしている（ここでローカル値として設定すると、そちらを
                // 上書きしてしまい、連動しなくなる）。
                a_p.ClearValue(Paragraph.MarginProperty);
            }
            else
            {
                double[] sizes = { 0, 30, 24, 20, 18, 16.5, 15.5 };
                a_p.FontSize = sizes[a_level];
                a_p.FontWeight = FontWeights.Bold;
                a_p.Margin = new Thickness(0, a_level <= 2 ? 20 : 14, 0, 10);
                if (a_level <= 2)
                {
                    a_p.BorderBrush = m_cellBorder;
                    a_p.BorderThickness = new Thickness(0, 0, 0, 1 == a_level ? 0.75 : 0.5);
                    a_p.Padding = new Thickness(0, 0, 0, 4);
                }
                else
                {
                    a_p.BorderThickness = new Thickness(0);
                }
            }
        }

        /// <summary>段落にコードブロックスタイル（等幅フォント・背景色・枠線）を適用する。</summary>
        /// <param name="a_p">対象の段落。</param>
        /// <param name="a_language">```の直後の言語タグ（ツールチップに表示される）。</param>
        public static void ApplyCodeBlockStyle(Paragraph a_p, string a_language = "")
        {
            ClearSpecialStyling(a_p);
            a_p.Tag = new CodeBlockInfo { Language = a_language ?? "" };
            a_p.FontFamily = new FontFamily("Consolas");
            a_p.FontSize = 13.5;
            a_p.FontWeight = FontWeights.Normal;
            a_p.Background = m_codeBlockBackground;
            a_p.Padding = new Thickness(14, 10, 14, 10);
            a_p.Margin = new Thickness(0, 4, 0, 14);
            a_p.BorderBrush = m_cellBorder;
            a_p.BorderThickness = new Thickness(1);
            ToolTipService.SetToolTip(a_p, string.IsNullOrEmpty(a_language) ? "コードブロック" : "コードブロック (" + a_language + ")");
        }

        /// <summary>段落に水平線（&lt;hr&gt;相当）スタイルを適用する。</summary>
        /// <param name="a_p">対象の段落。中身（Inlines）は空のままにする想定。</param>
        public static void ApplyHorizontalRuleStyle(Paragraph a_p)
        {
            ClearSpecialStyling(a_p);
            a_p.Tag = new HorizontalRuleInfo();
            a_p.FontSize = 1;
            a_p.BorderBrush = m_cellBorder;
            a_p.BorderThickness = new Thickness(0, 1, 0, 0);
            a_p.Margin = new Thickness(0, 14, 0, 14);
            a_p.Padding = new Thickness(0);
        }

        /// <summary>タスクリスト用チェックボックスを生成する。
        /// MarkdownConverter（バッチ変換）とListEditor（ライブ入力変換）の両方から共有で使い、
        /// 見た目のプロパティ（IsChecked以外）が食い違わないようにするための共通ヘルパー。
        /// 幅・高さを明示的に固定している：既定のCheckBoxはOSのテーマ等によって実際の描画
        /// サイズがわずかに変わることがあり、これがRichTextBox内でタスク項目の行の高さだけ
        /// 不安定に見える一因になっていた（Paragraph.LineHeightは行の「最低限」の高さでしか
        /// なく、埋め込んだUIElement側がそれより大きいと行全体がその分だけ伸びてしまうため）。
        /// サイズを固定することで、チェックボックスを含む行の高さが常に一定になるようにする。
        /// Marginの上側にわずかな余白（4px）を付けているのは、行頭のマーカー（「・」）に対して
        /// チェックボックスの位置を見た目でもう少しだけ下げるための微調整（当初2pxだったが、
        /// 利用者からの追加要望により4pxに変更）。CheckBoxというUI部品自身の見た目上の余白で
        /// しかなく、段落の文字位置・TextPointer・IME関連の処理には一切関わらない。
        /// </summary>
        /// <param name="a_checked">チェック済み状態（[x]）かどうか。</param>
        public static CheckBox CreateTaskCheckbox(bool a_checked)
        {
            return new CheckBox
            {
                IsChecked = a_checked,
                Width = 15,
                Height = 15,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 4, 0),
                Tag = "task-checkbox"
            };
        }

        /// <summary>CreateTaskCheckboxで生成したチェックボックスを、行頭の箇条書きマーカー
        /// （「・」等）に対して縦方向中央に揃えた状態でInlineUIContainerに包んで返す。
        /// InlineUIContainer（Inlineの一種）の既定のBaselineAlignmentは"Baseline"であり、
        /// これは埋め込んだ要素の下端をテキストのベースラインに合わせる指定のため、正方形の
        /// チェックボックスの大部分が文字の上側にはみ出す形になり、行頭のマーカーよりも
        /// 高い位置にずれて表示されていた。BaselineAlignmentを"Center"にすることで、
        /// チェックボックス自体の縦方向中央を行の中央（マーカーがおおよそ描画される位置）へ
        /// 揃える。チェックボックスを段落へ挿入する箇所（ListEditor・MarkdownConverter）は
        /// すべてこのヘルパー経由にし、直接InlineUIContainerを組み立てることで見た目が
        /// 食い違うことのないようにする。</summary>
        /// <param name="a_checked">チェック済み状態（[x]）かどうか。</param>
        public static InlineUIContainer CreateTaskCheckboxContainer(bool a_checked)
        {
            return new InlineUIContainer(CreateTaskCheckbox(a_checked))
            {
                BaselineAlignment = BaselineAlignment.Center
            };
        }

        /// <summary>不順序リスト（箇条書き）のマーカー種別を、ネストの段数（1始まり）から決める。
        /// VSCode等と同様、1段目はDisc（・）、2段目はCircle（輪郭だけの丸）、3段目以降は
        /// Box（塗りつぶしの四角）にする。MarkdownConverter（バッチ変換）とListEditor
        /// （ライブ編集でのTab字下げ・字下げ解除）の両方から共有で使う。</summary>
        /// <param name="a_depth">ネストの段数（1始まり）。</param>
        /// <returns>対応するTextMarkerStyle。</returns>
        public static TextMarkerStyle UnorderedMarkerStyleForDepth(int a_depth)
        {
            if (a_depth <= 1)
            {
                return TextMarkerStyle.Disc;
            }
            if (2 == a_depth)
            {
                return TextMarkerStyle.Circle;
            }
            return TextMarkerStyle.Box;
        }

        // ======================================================================
        //  表の罫線（境界線の単線化）
        // ======================================================================

        /// <summary>
        /// 表の全セルに、隣接セルと境界線が二重に重ならないよう調整した罫線を設定する。
        /// WPFの<see cref="Table"/>/<see cref="TableCell"/>には、CSSの
        /// <c>border-collapse: collapse</c>に相当する「隣接セルの境界線を1本にまとめる」
        /// 機能が無い。単純に全セルへ4辺とも罫線を付けると、隣り合うセルの境界に2本の
        /// 罫線が重なって描画され、非整数DPI（125%/150%等の画面拡大率）や印刷（PDF出力）
        /// では2本の位置が微妙にずれてぼやけたり、太い二重線に見えたり、あるいは片方だけが
        /// 消えて見えたりする。
        /// 【設計（第2版）】当初は「最上段の行だけ上辺、最左列だけ左辺を追加で描く」という
        /// 行・列の位置によってセルごとにThicknessの形が異なる方式にしていたが、これは
        /// ヘッダー行と本文行とでセルのThicknessの形（上辺の有無）が異なることになり、
        /// 実機でのPDF出力テストで「ヘッダー行だけ縦の罫線が消える」という不具合が発生した
        /// （原因はWPFの印刷パイプライン側の挙動と見られ詳細は不明だが、セルごとに異なる
        /// Thicknessの組み合わせを使うこと自体を避けるのが確実な対策となる、という考え方
        /// だった）。そこで第2版では、<see cref="Table"/>自身が<see cref="Block"/>を継承
        /// しており、<see cref="TableCell"/>とは独立した自前のBorderBrush/BorderThickness
        /// を持てることを利用し、「全セルは例外なく同一のThickness（右辺・下辺のみ）を持ち、
        /// 表の外周の上辺・左辺は表自身が1回だけ描く」という形に変更した。
        /// 【第3版・太さ調整版（採用）】第2版を実機のPDF出力で確認したところ、Chromium
        /// あり版（HTML/ChromiumでPDF化する版）では罫線消失は発生しないが、Chromiumなし版
        /// （WPFの「Microsoft Print to PDF」印刷パイプラインでそのままPDF化する版）では、
        /// 依然としてヘッダー行の縦の罫線が印刷時に消えてしまう現象が残っていた。これは
        /// Thicknessの形の違いが原因ではなく、WPFの印刷パイプラインが極端に細い罫線（1px
        /// 相当）を特定の条件下でラスタライズし損ねる、という別の要因によるものと見られる。
        /// 対策として「ヘッダー行のセルの右辺（セル間の縦の区切り線）だけ本文行より少し
        /// 太くする」案と「ヘッダー行の罫線の色を見出しレベル1の下線と同じ色に変える」案の
        /// 2通りを試作し、実機で比較検証した結果、太くする案（本版）では罫線消失が解消し、
        /// 色を変える案では効果が見られなかったため、太さを変える案を正式に採用した。
        /// ヘッダー行（最上段の行）のセルの右辺だけ、本文行より少し太く
        /// （<see cref="HEADER_VERTICAL_BORDER_THICKNESS"/>）描くようにしている。WYSIWYG
        /// （Markdown）モードでの見た目にはほとんど影響しない程度の差にとどめてある。
        /// 呼び出し側は、行・列の挿入や削除など表の構造を変えた直後に、表全体に対して
        /// 毎回呼び直すこと（個々の操作ごとに罫線の付け外しを個別に追いかけるのではなく、
        /// 常にこの関数で表全体を作り直す方が、境界線が消える・重なるといった不具合を
        /// 防ぎやすい）。
        /// </summary>
        /// <param name="a_table">対象の表（RowGroups・Rows・Cellsまで構築済みであること）。</param>
        /// <param name="a_borderBrush">罫線の色（呼び出し側が使っているCELL_BORDER定数を渡す）。</param>
        public static void ApplyTableCellBorders(Table a_table, Brush a_borderBrush)
        {
            // 表自身の外周（上辺・左辺）を1回だけ描く。TableはBlockを継承しており、
            // TableCellとは独立したBorderBrush/BorderThicknessを持てる。
            a_table.BorderBrush = a_borderBrush;
            a_table.BorderThickness = new Thickness(1, 1, 0, 0);

            // 全セルの右辺・下辺に罫線を描く。ヘッダー行（最上段の行）の右辺（セル間の
            // 縦の区切り線）だけは、印刷時の消失対策としてわずかに太くする
            // （HEADER_VERTICAL_BORDER_THICKNESS。それ以外は本文行と完全に同じ太さ）。
            int rowIndex = 0;
            foreach (TableRowGroup rg in a_table.RowGroups)
            {
                foreach (TableRow row in rg.Rows)
                {
                    double right = (0 == rowIndex) ? HEADER_VERTICAL_BORDER_THICKNESS : 1;
                    foreach (TableCell cell in row.Cells)
                    {
                        cell.BorderBrush = a_borderBrush;
                        cell.BorderThickness = new Thickness(0, 0, right, 1);
                    }
                    rowIndex++;
                }
            }
        }

        // ======================================================================
        //  表の列幅（内容に合わせたコンパクトな列幅）
        // ======================================================================

        /// <summary>表のセルの計測に使うフォント（MainWindow.xamlのFlowDocumentの設定と
        /// 合わせてある）。</summary>
        private static readonly FontFamily m_tableMeasureFontFamily = new FontFamily("Yu Gothic UI, Segoe UI");
        private const double TABLE_MEASURE_FONT_SIZE = 16;

        /// <summary>この幅（px）を超える内容を持つ列は、固定幅にはせず、残りの幅を分け合って
        /// 折り返す列として扱う（ApplyContentBasedColumnWidths、現在は無効化）。
        /// MeasureNaturalColumnWidthsPxが返す「内容の自然な幅」の上限としても、同じ値を
        /// 流用している。長い説明文などが入る、そもそも複数行に折り返される前提の列の
        /// 幅が、この上限で頭打ちにならないと、列幅の自動計算（BuildColumnWidthDialogInfo・
        /// ApplyAutoCalculatedColumnWidths）でその列の比率が突出して大きくなり、他の列が
        /// 読めないほど狭く押しつぶされてしまう不具合が実機で確認されたため（詳細は
        /// MeasureNaturalColumnWidthsPxのコメント参照）。</summary>
        private const double TABLE_COLUMN_COMPACT_MAX_WIDTH = 300;

        /// <summary>TableCellのPadding（左右合計）。列幅を内容ぴったりにする際、この分だけ
        /// 上乗せする。</summary>
        private const double TABLE_CELL_HORIZONTAL_PADDING = 16;

        /// <summary>
        /// 自動計算した「内容にちょうど収まる幅」に、追加で確保しておく余裕（px）。
        /// MeasureNaturalColumnWidthsPxの測定（FormattedTextによる文字列幅の計測＋
        /// TABLE_CELL_HORIZONTAL_PADDING）には、セルの罫線の太さ（ApplyTableCellBordersで
        /// 各セルの右辺に加える罫線。本文行1px・ヘッダー行はHEADER_VERTICAL_BORDER_THICKNESS
        /// ＝1.75px）が含まれていない。また、FormattedTextによる測定値と、実際に
        /// RichTextBox上へ描画される幅との間には、環境の画面拡大率（DPI）等の要因により、
        /// ごくわずかな差が生じることがある。
        /// これまでは列幅が常にStar（比率）方式だったため、表全体が編集領域の横幅いっぱいに
        /// 間延びする形でこの差が吸収され、表面化しなかった。しかし、内容にちょうど収まる
        /// 固定幅（Pixel）で表示できる場合はそちらを使うよう変更した後（列幅を補正する機能の
        /// 改良を参照）、この差がわずかに足りないだけで、その列でいちばん長い文字列の
        /// 最後の1〜2文字だけが次の行へ意図せず送られてしまう不具合が実機で報告された
        /// （例：「1つ前に開いていたファイルを開く」の「く」だけ、「タスクリスト
        /// （チェックボックス）」の「ス）」だけが折り返される）。この余裕を上乗せすることで、
        /// このような境界ぎりぎりでの折り返しを防ぐ。
        /// </summary>
        private const double TABLE_COLUMN_WIDTH_SAFETY_MARGIN_PX = 8;

        /// <summary>
        /// 表の各列の幅を、実際のセル内容に合わせて設定する。WPFの既定のTable列幅
        /// アルゴリズムは、内容量に関わらず利用可能な幅いっぱいに広がってしまうため、
        /// GitHub等の一般的なMarkdownビューアのような「短い列は内容ぴったりにコンパクトに、
        /// 長い説明文などの列だけが残りの幅を使って折り返す」見た目にはならない
        /// （すべての列が均等に間延びして見える）。ここでは各列の実際のセル文字列を
        /// FormattedTextで測定し、TABLE_COLUMN_COMPACT_MAX_WIDTH以下に収まる列は
        /// 内容ぴったりの固定幅（Pixel）へ、収まらない列は残りの幅を分け合うStar幅へ、
        /// それぞれ明示的に上書きする。呼び出し側は、表の行・セルをすべて組み立てた
        /// 直後（内容が確定した後）に呼ぶこと。
        /// 注：System.Windows.Documents.Table（FrameworkContentElement）には
        /// HorizontalAlignmentプロパティが存在しない（FrameworkElement専用のプロパティの
        /// ため）。そのため、表全体を左揃えにする処理はここでは行っていない。
        /// </summary>
        /// <param name="a_table">対象の表（RowGroups・Rows・Cellsまで構築済みであること）。</param>
        public static void ApplyContentBasedColumnWidths(Table a_table)
        {
            int colCount = a_table.Columns.Count;
            if (0 == colCount)
            {
                return;
            }

            var maxWidths = new double[colCount];
            foreach (TableRowGroup rg in a_table.RowGroups)
            {
                foreach (TableRow row in rg.Rows)
                {
                    for (int c = 0; c < row.Cells.Count && c < colCount; c++)
                    {
                        TableCell cell = row.Cells[c];
                        string text = new TextRange(cell.ContentStart, cell.ContentEnd).Text?.Trim();
                        if (string.IsNullOrEmpty(text))
                        {
                            continue;
                        }
                        var typeface = new Typeface(
                            m_tableMeasureFontFamily, FontStyles.Normal, cell.FontWeight, FontStretches.Normal);
                        var formatted = new FormattedText(
                            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
                            TABLE_MEASURE_FONT_SIZE, Brushes.Black, 1.0);
                        if (formatted.Width > maxWidths[c])
                        {
                            maxWidths[c] = formatted.Width;
                        }
                    }
                }
            }

            for (int c = 0; c < colCount; c++)
            {
                if (maxWidths[c] <= 0)
                {
                    continue; // 空列は既定のまま（Autoに近い挙動）にしておく
                }
                double compactWidth = maxWidths[c] + TABLE_CELL_HORIZONTAL_PADDING;
                if (compactWidth <= TABLE_COLUMN_COMPACT_MAX_WIDTH)
                {
                    a_table.Columns[c].Width = new GridLength(compactWidth, GridUnitType.Pixel);
                }
                else
                {
                    a_table.Columns[c].Width = new GridLength(1, GridUnitType.Star);
                }
            }
        }

        // ======================================================================
        //  表の列幅（ダイアログでの手動調整・ステップ2）
        // ======================================================================

        /// <summary>列幅を「区切り行のダッシュ（-）の数」で表す際の既定値（未調整の列）。
        /// MarkdownConverterが生成する区切り行（"---"等）のダッシュ数と一致させてある。</summary>
        public const int TABLE_COLUMN_DEFAULT_DASH_COUNT = 3;

        /// <summary>列幅として指定できるダッシュ数の下限・上限。区切り行として意味を持つ
        /// 最小値（3）を下限とし、上限は極端に長い区切り行になり過ぎないよう抑える。</summary>
        public const int TABLE_COLUMN_MIN_DASH_COUNT = 3;
        public const int TABLE_COLUMN_MAX_DASH_COUNT = 60;

        /// <summary>列の内容が空の場合に使う、既定の列幅（px）。</summary>
        public const double DEFAULT_NATURAL_COLUMN_WIDTH_PX = 80;

        /// <summary>
        /// 「ダッシュ1個ぶんの幅」として使う基準値（px）。TABLE_MEASURE_FONT_FAMILY・
        /// TABLE_MEASURE_FONT_SIZEで半角文字1つ（"0"）を測定した幅を基準にしている。
        /// これにより、ダイアログでの「幅の数値」が、おおよそ「その文字数ぶんの半角文字が
        /// 入る幅」という感覚に近くなる。
        /// </summary>
        private static double DashUnitWidthPx()
        {
            var typeface = new Typeface(m_tableMeasureFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var formatted = new FormattedText(
                "0", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
                TABLE_MEASURE_FONT_SIZE, Brushes.Black, 1.0);
            return formatted.Width;
        }

        /// <summary>ダッシュ数（列幅調整ダイアログ・Markdownの区切り行の両方で使う単位）を、
        /// 有効な範囲（TABLE_COLUMN_MIN_DASH_COUNT〜TABLE_COLUMN_MAX_DASH_COUNT）に収める。</summary>
        /// <param name="a_count">ダッシュ数。</param>
        public static int ClampDashCount(int a_count)
        {
            if (a_count < TABLE_COLUMN_MIN_DASH_COUNT)
            {
                return TABLE_COLUMN_MIN_DASH_COUNT;
            }
            if (a_count > TABLE_COLUMN_MAX_DASH_COUNT)
            {
                return TABLE_COLUMN_MAX_DASH_COUNT;
            }
            return a_count;
        }

        /// <summary>ピクセル幅（実際のセル内容から測った、まだ調整されていない列の幅）を、
        /// ダッシュ数と同じ「単位」の数値に変換する（列幅調整ダイアログに現在値を表示する際、
        /// および調整済みの列と比率を揃える際に使う。DashUnitWidthPxを1単位とする）。</summary>
        /// <param name="a_pixelWidth">ピクセル幅。</param>
        public static int PixelWidthToDashCount(double a_pixelWidth)
        {
            double unit = DashUnitWidthPx();
            int count = (int)Math.Round((a_pixelWidth - TABLE_CELL_HORIZONTAL_PADDING) / unit, MidpointRounding.AwayFromZero);
            return ClampDashCount(count);
        }

        /// <summary>
        /// 1つのセルの内容を、実際に使われているRunごとのフォント（太字・インラインコード等の
        /// 装飾で明示的に指定されているフォントファミリー・サイズ・太さ）で個別に測定し、
        /// 合計した幅（px）を返す。
        /// 【なぜセル全体を1つのフォントで測定してはいけないか】以前は、セル全体のプレーン
        /// テキスト（TextRange.Text）を、常にTABLE_MEASURE_FONT_FAMILY・TABLE_MEASURE_FONT_SIZE
        /// という単一のフォントで一括測定していた。しかし、インラインコード（`Ctrl+N`等）は
        /// 実際にはConsolas・13.5pxという別のフォントで表示される（AppendStyledRunsWithLineBreaks
        /// 参照）ため、この一括測定では実際の表示幅より小さく見積もってしまうことがあった。
        /// この結果、README.mdの「基本操作」表（`Ctrl+N`等のインラインコードを含むセル）で、
        /// 列の右側に余白が残っているにも関わらず、その列の中でいちばん長いセルが折り返されて
        /// しまう、という不具合が実機で報告された（表全体の幅は列の合計から決まるため、個々の
        /// 列の測定が小さすぎると、表全体としては余白が残るのに、個々の列としては足りない、
        /// という状態になり得る）。
        /// このメソッドは、セルの各Run（AppendInlineMarkdownToParagraph等が組み立てる、太字・
        /// インラインコード等ごとに分かれた実際の描画単位）について、そのRunにローカルに設定
        /// されているFontFamily/FontSize/FontWeightがあればそれをそのまま使い（インラインコード
        /// のConsolas・13.5px、太字のFontWeights.Bold等）、無ければセルの既定
        /// （TABLE_MEASURE_FONT_FAMILY・TABLE_MEASURE_FONT_SIZE・セルのFontWeight。ヘッダー行は
        /// Bold）を使って個別に幅を測定し、合計する（1つの段落内で折り返さず1行に並ぶ前提の、
        /// 内容にちょうど収まる幅を求めるため）。ローカル値の有無で判定しているのは、Runが
        /// まだ実際の文書（FlowDocument）へ組み込まれる前に呼ばれる場合があり、通常の
        /// プロパティ値の継承（親からの自動解決）に頼ると、その時点でのRunの親子関係次第で
        /// 結果が変わってしまう可能性があるため（ローカルに明示設定された値は、親子関係に
        /// 関わらず常に同じ値を返す）。
        /// </summary>
        /// <param name="a_cell">対象のセル。</param>
        private static double MeasureCellContentWidthPx(TableCell a_cell)
        {
            double maxParaWidth = 0;
            foreach (Block block in a_cell.Blocks)
            {
                if (!(block is Paragraph p))
                {
                    continue;
                }
                double paraWidth = 0;
                foreach (Inline inline in p.Inlines)
                {
                    if (!(inline is Run run))
                    {
                        continue;
                    }
                    string text = run.Text;
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }
                    FontFamily fontFamily = DependencyProperty.UnsetValue != run.ReadLocalValue(TextElement.FontFamilyProperty)
                        ? run.FontFamily : m_tableMeasureFontFamily;
                    double fontSize = DependencyProperty.UnsetValue != run.ReadLocalValue(TextElement.FontSizeProperty)
                        ? run.FontSize : TABLE_MEASURE_FONT_SIZE;
                    FontWeight fontWeight = DependencyProperty.UnsetValue != run.ReadLocalValue(TextElement.FontWeightProperty)
                        ? run.FontWeight : a_cell.FontWeight;
                    var typeface = new Typeface(fontFamily, FontStyles.Normal, fontWeight, FontStretches.Normal);
                    var formatted = new FormattedText(
                        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
                        fontSize, Brushes.Black, 1.0);
                    paraWidth += formatted.Width;
                }
                if (paraWidth > maxParaWidth)
                {
                    maxParaWidth = paraWidth;
                }
            }
            return maxParaWidth;
        }

        /// <summary>各列の実際のセル内容から、「内容にちょうど収まる幅」を、頭打ち・余裕を
        /// 加える前の生の値（px。セルのPadding分は含む）として測る。MeasureNaturalColumnWidthsPx
        /// （Star比率で使う、頭打ち・余裕あり版）とMeasureUncappedColumnWidthsPx（Pixelで使う、
        /// 頭打ち無し・余裕あり版）の両方が、この共通の測定処理を経由する。</summary>
        /// <param name="a_table">対象の表。</param>
        private static double[] MeasureRawColumnContentWidthsPx(Table a_table)
        {
            int colCount = a_table.Columns.Count;
            var widths = new double[colCount];
            foreach (TableRowGroup rg in a_table.RowGroups)
            {
                foreach (TableRow row in rg.Rows)
                {
                    for (int c = 0; c < row.Cells.Count && c < colCount; c++)
                    {
                        double cellWidth = MeasureCellContentWidthPx(row.Cells[c]);
                        if (cellWidth > widths[c])
                        {
                            widths[c] = cellWidth;
                        }
                    }
                }
            }
            for (int c = 0; c < colCount; c++)
            {
                widths[c] = widths[c] <= 0 ? DEFAULT_NATURAL_COLUMN_WIDTH_PX : widths[c] + TABLE_CELL_HORIZONTAL_PADDING;
            }
            return widths;
        }

        /// <summary>各列の実際のセル内容から、内容にちょうど収まる幅（px）を測る
        /// （ApplyContentBasedColumnWidthsの測定部分と同じ考え方だが、Star幅の統一
        /// 方針で使うための独立した測定ヘルパー。ここで返す値は、実際に列へ適用する際に
        /// PixelWidthToDashCountでダッシュ数と同じ単位に変換した上でStarの比率として
        /// 使われる。詳細はApplyExplicitColumnWidthsのコメント参照）。
        /// 【上限（TABLE_COLUMN_COMPACT_MAX_WIDTH）について】長い説明文が入った列（1行の
        /// 文字数が多く、そもそも複数行に折り返される前提の列）を、そのまま測定結果通りの
        /// 幅にしてしまうと、その列だけ突出して幅の比率が大きくなり、他の列（短い見出し語
        /// など）が読めないほど狭く押しつぶされてしまう不具合が実機で確認された（doc/
        /// DESIGN.mdの「Tagの内容」「用途」列のような、説明文中心の表で顕著）。これを防ぐため、
        /// この幅はTABLE_COLUMN_COMPACT_MAX_WIDTHで頭打ちにしている。長い列どうしは
        /// おおむね同じ比率になり、それぞれの列の中で複数行に折り返して表示される
        /// （見切れるわけではない）。この頭打ちは、あくまでStar（比率）で複数列が幅を
        /// 奪い合う場合の「不公平さ」を防ぐためのものであり、Pixel（固定幅。他の列と
        /// 幅を奪い合わない）で使う場合には適用しない（MeasureUncappedColumnWidthsPx参照）。
        /// 【余裕（TABLE_COLUMN_WIDTH_SAFETY_MARGIN_PX）について】頭打ち後の幅に、さらに
        /// 小さな余裕を上乗せしている。これはセルの罫線の太さや、環境による測定と実際の
        /// 描画とのごくわずかな差を吸収するためのもの。詳細はTABLE_COLUMN_WIDTH_SAFETY_
        /// MARGIN_PXのコメント参照。</summary>
        /// <param name="a_table">対象の表。</param>
        private static double[] MeasureNaturalColumnWidthsPx(Table a_table)
        {
            var raw = MeasureRawColumnContentWidthsPx(a_table);
            var widths = new double[raw.Length];
            for (int c = 0; c < raw.Length; c++)
            {
                widths[c] = Math.Min(raw[c], TABLE_COLUMN_COMPACT_MAX_WIDTH) + TABLE_COLUMN_WIDTH_SAFETY_MARGIN_PX;
            }
            return widths;
        }

        /// <summary>
        /// 各列の実際のセル内容から、内容にちょうど収まる幅（px）を、TABLE_COLUMN_COMPACT_
        /// MAX_WIDTHでの頭打ちを行わずに測る（余裕（TABLE_COLUMN_WIDTH_SAFETY_MARGIN_PX）は
        /// 加える）。
        /// 【なぜPixelモードでは頭打ちをしてはいけないか】14.28で導入した300pxの頭打ちは、
        /// Star（比率）方式で複数列が「表全体の幅」を奪い合う場合に、1つの長い列が比率の
        /// 大半を占めて他の列を読めないほど狭く押しつぶしてしまう不具合を防ぐためのもの
        /// だった（Star特有の「他の列と幅を奪い合う」性質への対策）。しかしPixel（固定幅）
        /// 方式は、各列が他の列と幅を奪い合わない（表全体の幅に余裕がある場合にのみPixelを
        /// 使う設計のため）。そのため、Pixelで使う幅にまで一律300pxの頭打ちを適用すると、
        /// 「表全体には余裕があるのに、300pxで頭打ちにされた列だけが本来不要なはずの折り
        /// 返しをしてしまう」という不具合になる（実機で報告：README.mdの「基本操作」表で、
        /// `Shift+Ctrl+N`等のインラインコードを含むセルが、表の右側に余白が残っているのに
        /// 折り返される）。ApplyAutoCalculatedColumnWidthsは、この頭打ち無しの幅の合計が
        /// 表示可能幅に収まる場合にだけPixelを使うため、他の列を圧迫する心配が無い。
        /// </summary>
        /// <param name="a_table">対象の表。</param>
        private static double[] MeasureUncappedColumnWidthsPx(Table a_table)
        {
            var raw = MeasureRawColumnContentWidthsPx(a_table);
            var widths = new double[raw.Length];
            for (int c = 0; c < raw.Length; c++)
            {
                widths[c] = raw[c] + TABLE_COLUMN_WIDTH_SAFETY_MARGIN_PX;
            }
            return widths;
        }

        /// <summary>
        /// 表のインスタンスごとに、「区切り行のダッシュ数（＝Markdownソースへ実際に保存される
        /// べき値）」を覚えておくための、表示用の列幅（TableColumn.Width）とは独立した記録先。
        /// 表インスタンス（Tableオブジェクト）をキーにした
        /// <see cref="ConditionalWeakTable{TKey, TValue}"/>で、表が破棄されれば一緒にGCされる
        /// （明示的な削除処理は不要）。
        /// 【この記録先が必要な理由】メニュー「列幅を補正する」がオフの間は、表の見た目を
        /// 常に既定の均等幅（Auto）に戻す（<see cref="ApplyEffectiveColumnWidths"/>参照）。
        /// もしMarkdownへの書き出し（MarkdownConverter.TableToMarkdown）が表示用の
        /// TableColumn.Widthだけを見て区切り行のダッシュ数を判断していると、トグルがオフの間に
        /// 保存すると、実際には調整済みだった表の情報が失われ、既定値（|---|）で保存されてしまう
        /// （トグルは見た目だけのはずなのに、保存内容まで書き換えてしまう不具合）。
        /// そこで、区切り行のダッシュ数（保存されるべき「本当の値」）は、表示用の列幅とは
        /// 別にこの記録先で管理し、TableToMarkdownは常にここを参照する。表示用の列幅
        /// （TableColumn.Width）は、この値とメニューの状態から都度計算される、あくまで見た目
        /// だけの結果でしかない。
        /// </summary>
        private static readonly ConditionalWeakTable<Table, int[]> m_sourceDashCounts =
            new ConditionalWeakTable<Table, int[]>();

        /// <summary>表の、区切り行へ実際に保存されるべきダッシュ数を記録する（既存の記録が
        /// あれば置き換える）。MarkdownConverter.MarkdownToDocumentでの新規解析時（ソースから
        /// そのまま）と、TableEditor.ApplyColumnWidths（ダイアログでの変更確定時）の両方から
        /// 呼ばれる。</summary>
        /// <param name="a_table">対象の表。</param>
        /// <param name="a_dashCounts">列ごとのダッシュ数。</param>
        public static void SetSourceDashCounts(Table a_table, IReadOnlyList<int> a_dashCounts)
        {
            m_sourceDashCounts.Remove(a_table);
            m_sourceDashCounts.Add(a_table, a_dashCounts.ToArray());
        }

        /// <summary>表の、区切り行へ実際に保存されるべきダッシュ数を取得する。一度も
        /// SetSourceDashCountsが呼ばれていない表（挿入直後でまだ未調整の表など）はnullを返す
        /// （呼び出し側は、nullなら「すべて既定値」として扱うこと）。</summary>
        /// <param name="a_table">対象の表。</param>
        public static IReadOnlyList<int> GetSourceDashCounts(Table a_table)
        {
            return m_sourceDashCounts.TryGetValue(a_table, out var counts) ? counts : null;
        }

        /// <summary>列の挿入（TableEditor.InsertColumn）に合わせて、記録済みのダッシュ数一覧に
        /// 既定値の要素を1つ挿入する。まだ記録が無い表（一度も調整されていない表）には何もしない
        /// （記録が無いこと自体が「すべて既定値」を意味するため、そのままで整合する）。</summary>
        /// <param name="a_table">対象の表。</param>
        /// <param name="a_index">新しい列の挿入位置（0始まり）。</param>
        public static void InsertIntoSourceDashCounts(Table a_table, int a_index)
        {
            if (!m_sourceDashCounts.TryGetValue(a_table, out var counts))
            {
                return;
            }
            var list = counts.ToList();
            int idx = Math.Max(0, Math.Min(a_index, list.Count));
            list.Insert(idx, TABLE_COLUMN_DEFAULT_DASH_COUNT);
            m_sourceDashCounts.Remove(a_table);
            m_sourceDashCounts.Add(a_table, list.ToArray());
        }

        /// <summary>列の削除（TableEditor.DeleteColumn）に合わせて、記録済みのダッシュ数一覧から
        /// 該当する要素を1つ取り除く。まだ記録が無い表には何もしない（InsertIntoSourceDashCounts
        /// と同じ理由）。</summary>
        /// <param name="a_table">対象の表。</param>
        /// <param name="a_index">削除する列の位置（0始まり）。</param>
        public static void RemoveFromSourceDashCounts(Table a_table, int a_index)
        {
            if (!m_sourceDashCounts.TryGetValue(a_table, out var counts))
            {
                return;
            }
            var list = counts.ToList();
            if (a_index < 0 || a_index >= list.Count)
            {
                return;
            }
            list.RemoveAt(a_index);
            m_sourceDashCounts.Remove(a_table);
            m_sourceDashCounts.Add(a_table, list.ToArray());
        }

        /// <summary>
        /// 列幅調整ダイアログの初期表示用に、表の列ごとのラベル（見出しセルの文字列）と
        /// 現在の幅（ダッシュ数換算）、および「自動計算」チェックボックスの初期状態を組み立てる。
        /// 表がまだ調整されていない（GetSourceDashCountsがnull、またはすべて既定値）場合は、
        /// 実際の内容から測った幅をダッシュ数と同じ単位に変換した値と、IsAutoCalculated=true
        /// を返す。すでに明示的な幅（ダッシュ数）が記録されている場合は、その値と
        /// IsAutoCalculated=falseを返す（自動計算か否かは表全体で1つの状態であり、列ごとには
        /// 分けない。ApplyExplicitColumnWidthsが常に全列を同じ扱いにする方針と揃えてある）。
        /// </summary>
        /// <param name="a_table">対象の表。</param>
        public static (List<string> Labels, List<int> DashCounts, bool IsAutoCalculated) BuildColumnWidthDialogInfo(Table a_table)
        {
            var labels = new List<string>();
            var dashCounts = new List<int>();
            int colCount = a_table.Columns.Count;
            var naturalWidths = MeasureNaturalColumnWidthsPx(a_table);

            var sourceDashCounts = GetSourceDashCounts(a_table);
            bool isAutoCalculatedFlg = null == sourceDashCounts ||
                sourceDashCounts.All(d => TABLE_COLUMN_DEFAULT_DASH_COUNT == d);

            TableRow headerRow = null;
            foreach (TableRowGroup rg in a_table.RowGroups)
            {
                foreach (TableRow row in rg.Rows)
                {
                    headerRow = row;
                    break;
                }
                break;
            }

            for (int c = 0; c < colCount; c++)
            {
                string label = "";
                if (null != headerRow && c < headerRow.Cells.Count)
                {
                    label = new TextRange(headerRow.Cells[c].ContentStart, headerRow.Cells[c].ContentEnd).Text?.Trim() ?? "";
                }
                labels.Add(label);

                int dashCount;
                if (isAutoCalculatedFlg)
                {
                    // 自動計算の場合は、常にその場の内容から測り直した値を表示する（表示中に
                    // 内容が変わっていた場合も最新の内容に基づく比率になるようにするため）。
                    dashCount = PixelWidthToDashCount(c < naturalWidths.Length ? naturalWidths[c] : DEFAULT_NATURAL_COLUMN_WIDTH_PX);
                }
                else
                {
                    dashCount = ClampDashCount(c < sourceDashCounts.Count ? sourceDashCounts[c] : TABLE_COLUMN_DEFAULT_DASH_COUNT);
                }
                dashCounts.Add(dashCount);
            }
            return (labels, dashCounts, isAutoCalculatedFlg);
        }

        /// <summary>
        /// 表の各列に、明示的な比率幅（Star）を設定する。
        /// 【設計（第2版・比率方式への変更）】当初はPixel（絶対px）で幅を設定していたが、
        /// 表の合計幅が編集領域の横幅を超える場合、超えた分が右端で見切れて読めなくなる
        /// という報告があった。WPFのRichTextBoxは既定では表の右端を横スクロールで見られる
        /// ようにはならず、絶対px指定は「ウインドウを狭くすると必ず再発しうる」問題を
        /// 抱えていた。そこで、GridのColumnDefinitionと同じ「*（Star）」による比率指定に
        /// 変更した。Star幅の列は、表全体の横幅を、指定した比率で分け合う形になり、
        /// 編集領域の横幅を超えることが構造上あり得なくなる（ウインドウをリサイズしても
        /// 常にきれいに収まり直す）。ダッシュ数（区切り行の文字数・ダイアログの入力値）は、
        /// そのままStarの比率の値として使う（変換は不要。ピクセルへの変換が必要だったPixel
        /// 方式とは異なる）。
        /// なお、WPFの<see cref="Table"/>の列幅は、Pixel・Star・Auto（既定値）を混在させると、
        /// 意図と逆の見た目になる現象が実機で確認されている（ApplyContentBasedColumnWidths
        /// のコメント参照）。これは「Pixelと他の単位の混在」で確認された問題であり、今回の
        /// ようにStarだけで統一する場合は該当しないと考えられるが、念のため今回も「列幅を
        /// 1つでも明示的に調整する際は、対象の表の全列を例外なくStar幅にする」という、
        /// 単位を混在させない方針は維持している。
        /// <paramref name="a_dashCounts"/>で値が指定されている（nullでない）列は、その
        /// ダッシュ数をそのままStarの比率として使う。値が指定されていない（null）列は、
        /// その列の実際の内容から測った幅（MeasureNaturalColumnWidthsPx）をダッシュ数と
        /// 同じ単位に変換した上で比率として使う（ユーザーが調整していない列まで、
        /// 無関係に極端に狭くなってしまわないようにするため）。
        /// </summary>
        /// <param name="a_table">対象の表。</param>
        /// <param name="a_dashCounts">列ごとの新しい値（ダッシュ数＝Starの比率）。列の順序と
        /// 一致させること。nullの要素は「この列は調整しない（内容に合わせた比率のままにする）」
        /// を意味する。</param>
        public static void ApplyExplicitColumnWidths(Table a_table, IReadOnlyList<int?> a_dashCounts)
        {
            int colCount = a_table.Columns.Count;
            var naturalWidths = MeasureNaturalColumnWidthsPx(a_table);
            for (int c = 0; c < colCount; c++)
            {
                int? dashCount = c < a_dashCounts.Count ? a_dashCounts[c] : null;
                int starValue = dashCount.HasValue
                    ? ClampDashCount(dashCount.Value)
                    : PixelWidthToDashCount(c < naturalWidths.Length ? naturalWidths[c] : DEFAULT_NATURAL_COLUMN_WIDTH_PX);
                a_table.Columns[c].Width = new GridLength(starValue, GridUnitType.Star);
            }
        }

        /// <summary>
        /// まだ調整されていない表（区切り行がすべて既定値）に対して、列幅調整ダイアログの
        /// 「自動計算」欄と同じ計算方法（内容量から測った幅をダッシュ数と同じ単位に変換した
        /// 比率）で、表示上だけ列幅を適用する。あくまで表示（TableColumn.Width）だけの効果で
        /// あり、区切り行のダッシュ数（GetSourceDashCounts／SetSourceDashCounts）には一切
        /// 触れない。そのため、Markdownへの書き出し（MarkdownConverter.TableToMarkdown）は
        /// この比率を書き戻さず、区切り行は既定値（|---|）のまま保たれる。
        /// 【表示幅に収まる場合はコンパクトな固定幅（Pixel）を使う】大きなディスプレイ・
        /// 全画面表示など、内容量に対して表示領域が十分広い場合、これまでの実装（常にStar＝
        /// 比率で表の横幅いっぱいに広げる）では、短い見出し語だけの表でも編集領域の横幅
        /// いっぱいに間延びしてしまい、Typoraなど一般的なMarkdownビューアの「内容量ぴったりの
        /// コンパクトな表示」と見た目が食い違う、との報告が実機であった。これを防ぐため、
        /// <paramref name="a_availableWidthPx"/>が指定されており（nullでなく）、かつ各列の
        /// 自然な幅（測定値。長い説明文の列はTABLE_COLUMN_COMPACT_MAX_WIDTHで頭打ち済み）の
        /// 合計がその幅に収まる場合は、Starではなく、その自然な幅をそのままPixel（固定px）
        /// として設定する（表全体としては、常に同一の単位＝全列Pixelになり、単位の混在は
        /// 発生しない）。
        /// 合計が収まらない場合（表示領域が狭い場合）は、これまでと同じStar比率にする
        /// （表が編集領域の横幅を超えて右端が見切れることが構造上あり得ないようにするための、
        /// 既存の安全策。ApplyExplicitColumnWidthsのコメント参照）。<paramref name="a_availableWidthPx"/>
        /// がnull（呼び出し側が表示領域の幅を把握していない場合。例：保存専用の使い捨て文書や
        /// PDF書き出し用の一時文書に対する変換）の場合も、常にこれまでと同じStar比率にする
        /// （表示領域の実際の幅が分からない状況で誤った判断をしないよう、安全側に倒す）。
        /// </summary>
        /// <param name="a_table">対象の表。</param>
        /// <param name="a_availableWidthPx">表を表示できる実際の幅（px）。呼び出し側が
        /// 把握していない場合はnull（その場合は常にStar比率にする）。</param>
        public static void ApplyAutoCalculatedColumnWidths(Table a_table, double? a_availableWidthPx)
        {
            int colCount = a_table.Columns.Count;
            // Pixelで使う場合は、他の列と幅を奪い合わないため、TABLE_COLUMN_COMPACT_MAX_WIDTHの
            // 頭打ちを適用しない生の幅を使う（頭打ちを適用したままだと、表全体には余裕がある
            // のに、頭打ちで狭められた列だけが不要な折り返しをしてしまう。詳細は
            // MeasureUncappedColumnWidthsPxのコメント参照）。
            var uncappedWidths = MeasureUncappedColumnWidthsPx(a_table);

            if (a_availableWidthPx.HasValue && uncappedWidths.Sum() <= a_availableWidthPx.Value)
            {
                for (int c = 0; c < colCount; c++)
                {
                    a_table.Columns[c].Width = new GridLength(uncappedWidths[c], GridUnitType.Pixel);
                }
                return;
            }

            // 収まらない場合（Star比率へフォールバック）は、複数列が表全体の幅を奪い合う
            // ことになるため、これまで通り頭打ちした幅を使う（14.28の不具合の再発防止）。
            var naturalWidths = MeasureNaturalColumnWidthsPx(a_table);
            var dashCounts = new List<int?>();
            for (int c = 0; c < colCount; c++)
            {
                double px = c < naturalWidths.Length ? naturalWidths[c] : DEFAULT_NATURAL_COLUMN_WIDTH_PX;
                dashCounts.Add(PixelWidthToDashCount(px));
            }
            ApplyExplicitColumnWidths(a_table, dashCounts);
        }

        /// <summary>
        /// ウインドウ（エディタ）のリサイズ時に、未調整（自動計算）の表の列幅だけを、現在の
        /// 表示幅に合わせて再計算する。調整済み（区切り行が既定値以外）の表には何もしない
        /// （調整済みの表はStar比率のままであり、リサイズしても常に編集領域の横幅に収まるため、
        /// 再計算の必要が無い）。MainWindow.EditorSizeChanged（TableEditor.
        /// RefreshAutoCalculatedColumnWidthsForResize経由）から、表ごとに呼ばれる。
        /// </summary>
        /// <param name="a_table">対象の表。</param>
        /// <param name="a_availableWidthPx">現在、表を表示できる幅（px）。</param>
        public static void RefreshAutoCalculatedColumnWidths(Table a_table, double a_availableWidthPx)
        {
            var sourceDashCounts = GetSourceDashCounts(a_table);
            bool isAutoCalculatedFlg = null == sourceDashCounts ||
                sourceDashCounts.All(d => TABLE_COLUMN_DEFAULT_DASH_COUNT == d);
            if (isAutoCalculatedFlg)
            {
                ApplyAutoCalculatedColumnWidths(a_table, a_availableWidthPx);
            }
        }

        /// <summary>表の全列の幅を、既定のAuto幅（列幅調整の効果を一切適用しない、これまでの
        /// 既定の見た目）へ戻す。メニュー「列幅を補正する」がオフの場合に使う。区切り行の
        /// ダッシュ数（GetSourceDashCounts／SetSourceDashCounts）には一切触れない。あくまで
        /// 表示だけを既定に戻すものであり、すでに調整済みの表の情報を消してしまわないように
        /// するため（オンに戻せば元の比率がそのまま復元される）。</summary>
        /// <param name="a_table">対象の表。</param>
        private static void ResetColumnWidthsToAuto(Table a_table)
        {
            foreach (TableColumn col in a_table.Columns)
            {
                col.Width = new GridLength(0, GridUnitType.Auto);
            }
        }

        /// <summary>
        /// メニュー「列幅を補正する」の状態と、区切り行のダッシュ数（Markdownソースの値）から、
        /// 表の実際の列幅（表示のみ。TableColumn.Width）を決定して適用する。
        /// MarkdownConverter.MarkdownToDocumentでの新規解析時と、列幅調整ダイアログでOKが
        /// 押された時（TableEditor.ApplyColumnWidths）の両方から呼ばれる、共通の適用ロジック。
        /// いずれの呼び出し側も、このメソッドとは別に、区切り行のダッシュ数そのもの
        /// （保存されるべき「本当の値」）をSetSourceDashCountsで記録しておくこと（このメソッド
        /// 自身は表示だけを担当し、記録には関与しない）。
        /// 【挙動】
        /// ・「列幅を補正する」がオフ：ダッシュ数の内容に関わらず、常に既定のAuto幅（均等）に
        /// 　戻す（列幅機能全体のマスタースイッチ。すでにダイアログで明示的に調整済みの表も
        /// 　含めて、オフの間はすべて均等幅で表示される。ダッシュ数はSetSourceDashCountsの
        /// 　記録・Markdownソース側にそのまま残るため、オンに戻せば元の比率が復元される）。
        /// ・オン＋すべての列が既定値（|---|、未調整）：内容量から自動計算した比率を、表示上
        /// 　だけ適用する（ApplyAutoCalculatedColumnWidths）。
        /// ・オン＋1列でも既定値以外（調整済み）：ダッシュ数をそのまま比率として使う
        /// 　（ApplyExplicitColumnWidths。従来のステップ2の動作そのもの）。
        /// </summary>
        /// <param name="a_table">対象の表。</param>
        /// <param name="a_dashCounts">列ごとの区切り行のダッシュ数（Markdownソースの値そのもの）。
        /// 表の列数と件数が一致していること。</param>
        /// <param name="a_correctColumnWidthsFlg">メニュー「列幅を補正する」の現在の状態。</param>
        /// <param name="a_availableWidthPx">表を表示できる実際の幅（px）。未調整の表への
        /// 自動計算比率（ApplyAutoCalculatedColumnWidths）で、コンパクトな固定幅（Pixel）に
        /// できるかどうかの判断に使う。呼び出し側が把握していない場合はnull（その場合は
        /// 常にStar比率にする。詳細はApplyAutoCalculatedColumnWidthsのコメント参照）。</param>
        public static void ApplyEffectiveColumnWidths(
            Table a_table, IReadOnlyList<int> a_dashCounts, bool a_correctColumnWidthsFlg, double? a_availableWidthPx)
        {
            if (!a_correctColumnWidthsFlg)
            {
                ResetColumnWidthsToAuto(a_table);
                return;
            }
            if (a_dashCounts.All(d => TABLE_COLUMN_DEFAULT_DASH_COUNT == d))
            {
                ApplyAutoCalculatedColumnWidths(a_table, a_availableWidthPx);
            }
            else
            {
                var explicitDashCounts = a_dashCounts
                    .Select(d => TABLE_COLUMN_DEFAULT_DASH_COUNT == d ? (int?)null : d)
                    .ToList();
                ApplyExplicitColumnWidths(a_table, explicitDashCounts);
            }
        }
    }
}
