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
        // ヘッダー行のセル間縦罫線（右辺）だけこの太さにする。本文行の1pxのままだと、印刷
        // （PDF出力）時にヘッダー行の縦罫線だけラスタライズされず消えることがあるため
        // （色を変える対策は効果なし、太さを変える対策のみ有効と確認済み）。
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

        /// <summary>タスクリスト用チェックボックスを生成する。MarkdownConverter（バッチ変換）と
        /// ListEditor（ライブ入力変換）の両方から共有で使い、見た目が食い違わないようにする
        /// 共通ヘルパー。幅・高さを固定しているのは、既定のCheckBoxはOSテーマ等で描画サイズが
        /// わずかに変わり、行の高さが不安定に見えるため（Paragraph.LineHeightは最低高さでしか
        /// なく、埋め込みUIElementが大きいと行全体が伸びる）。Marginの上側4pxは、行頭マーカー
        /// （「・」）に対してチェックボックスの位置を下げる見た目上の微調整で、段落の文字位置・
        /// TextPointer・IME関連の処理には関わらない。
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
        /// InlineUIContainerの既定のBaselineAlignmentは"Baseline"（埋め込み要素の下端を
        /// テキストのベースラインに合わせる）のため、正方形のチェックボックスが文字の上側に
        /// はみ出してマーカーより高い位置にずれる。BaselineAlignment="Center"にすることで
        /// チェックボックスの縦方向中央を行の中央へ揃える。段落への挿入箇所（ListEditor・
        /// MarkdownConverter）はすべてこのヘルパー経由にし、見た目の食い違いを防ぐ。</summary>
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
        /// WPFの<see cref="Table"/>/<see cref="TableCell"/>にはCSSの
        /// <c>border-collapse: collapse</c>に相当する機能が無く、全セルへ4辺とも罫線を
        /// 付けると隣接セルの境界に2本の罫線が重なり、非整数DPIや印刷（PDF出力）では
        /// 位置がずれてぼやけたり片方が消えたりする。そこで、全セルは例外なく同一の
        /// Thickness（右辺・下辺のみ）を持ち、表の外周の上辺・左辺は<see cref="Table"/>
        /// 自身（<see cref="TableCell"/>とは独立したBorderBrush/BorderThicknessを持てる）
        /// が1回だけ描く。
        /// ヘッダー行（最上段）のセル間の縦の区切り線（行内で最後ではないセルの右辺）だけは
        /// 本文行より少し太く（<see cref="HEADER_VERTICAL_BORDER_THICKNESS"/>）描く。これは
        /// WPFの印刷パイプラインが極端に細い罫線（1px相当）を特定条件下でラスタライズし
        /// 損ね、ヘッダー行の縦罫線だけ印刷時に消えることがあるための対策（太さを変える
        /// 対策のみ有効、色を変える対策は無効と確認済み）。WYSIWYGモードの見た目には
        /// ほぼ影響しない。この消失は隣接セル間の内部の区切り線でのみ発生し、表の外周
        /// （各行最後のセルの右辺）では発生しないため、最後のセルの右辺は太さを変えず
        /// 本文行と同じにする。
        /// 呼び出し側は、行・列の挿入や削除など表の構造を変えた直後に、表全体に対して
        /// 毎回呼び直すこと（個々の操作ごとに追いかけるより、常に表全体を作り直す方が
        /// 境界線の消失・重複を防ぎやすい）。
        /// </summary>
        /// <param name="a_table">対象の表（RowGroups・Rows・Cellsまで構築済みであること）。</param>
        /// <param name="a_borderBrush">罫線の色（呼び出し側が使っているm_cellBorderを渡す）。</param>
        public static void ApplyTableCellBorders(Table a_table, Brush a_borderBrush)
        {
            // 表自身の外周（上辺・左辺）を1回だけ描く。
            a_table.BorderBrush = a_borderBrush;
            a_table.BorderThickness = new Thickness(1, 1, 0, 0);

            // 全セルの右辺・下辺に罫線を描く。ヘッダー行の内部区切り線だけ太くする
            // （HEADER_VERTICAL_BORDER_THICKNESS）。行内最後のセルの右辺（表の外周）は
            // 本文行と同じ太さ（1）のまま。
            int rowIndex = 0;
            foreach (TableRowGroup rg in a_table.RowGroups)
            {
                foreach (TableRow row in rg.Rows)
                {
                    int cellCount = row.Cells.Count;
                    int cellIndex = 0;
                    foreach (TableCell cell in row.Cells)
                    {
                        bool isLastCellInRow = (cellIndex == cellCount - 1);
                        double right = (0 == rowIndex && !isLastCellInRow) ? HEADER_VERTICAL_BORDER_THICKNESS : 1;
                        cell.BorderBrush = a_borderBrush;
                        cell.BorderThickness = new Thickness(0, 0, right, 1);
                        cellIndex++;
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

        /// <summary>この幅（px）を超える内容を持つ列は固定幅にせず、残りの幅を分け合って
        /// 折り返す列として扱う（ApplyContentBasedColumnWidths、現在は無効化）。
        /// MeasureNaturalColumnWidthsPxが返す「内容の自然な幅」の上限としても流用している。
        /// 長い説明文の列をこの上限で頭打ちにしないと、列幅の自動計算（BuildColumnWidthDialogInfo・
        /// ApplyAutoCalculatedColumnWidths）でその列の比率が突出し、他の列が読めないほど
        /// 狭く押しつぶされる（詳細はMeasureNaturalColumnWidthsPxのコメント参照）。</summary>
        private const double TABLE_COLUMN_COMPACT_MAX_WIDTH = 300;

        /// <summary>TableCellのPadding（左右合計）。列幅を内容ぴったりにする際、この分だけ
        /// 上乗せする。</summary>
        private const double TABLE_CELL_HORIZONTAL_PADDING = 16;

        /// <summary>
        /// 自動計算した「内容にちょうど収まる幅」に、追加で確保しておく余裕（px）。
        /// MeasureNaturalColumnWidthsPxの測定（FormattedTextによる文字列幅の計測＋
        /// TABLE_CELL_HORIZONTAL_PADDING）には、セルの罫線の太さ（ApplyTableCellBordersで
        /// 各セルの右辺に加える罫線。本文行1px・ヘッダー行はHEADER_VERTICAL_BORDER_THICKNESS
        /// ＝1.75px）が含まれておらず、また環境のDPI等により実際の描画幅と測定値にごく
        /// わずかな差が生じることがある。固定幅（Pixel）表示でこの差が足りないと、いちばん
        /// 長い文字列の最後の1〜2文字だけ次の行へ折り返される不具合が実機で確認されたため、
        /// この余裕を上乗せして防いでいる。
        /// </summary>
        private const double TABLE_COLUMN_WIDTH_SAFETY_MARGIN_PX = 8;

        /// <summary>
        /// 表の各列の幅を、実際のセル内容に合わせて設定する。WPFの既定のTable列幅
        /// アルゴリズムは内容量に関わらず利用可能な幅いっぱいに広がるため、GitHub等の
        /// 一般的なMarkdownビューアのような「短い列はコンパクトに、長い列だけ折り返す」
        /// 見た目にはならない。各列の実際のセル文字列をFormattedTextで測定し、
        /// TABLE_COLUMN_COMPACT_MAX_WIDTH以下に収まる列は内容ぴったりの固定幅（Pixel）へ、
        /// 収まらない列は残りの幅を分け合うStar幅へ、それぞれ上書きする。呼び出し側は、
        /// 表の行・セルをすべて組み立てた直後（内容が確定した後）に呼ぶこと。
        /// 注：System.Windows.Documents.Table（FrameworkContentElement）には
        /// HorizontalAlignmentプロパティが無いため、表全体を左揃えにする処理はここでは
        /// 行っていない。
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
        /// 「ダッシュ1個ぶんの幅」として使う基準値（px）。m_tableMeasureFontFamily・
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
        /// 合計した幅（px）を返す。セル全体を単一フォント（m_tableMeasureFontFamily・
        /// TABLE_MEASURE_FONT_SIZE）で一括測定すると、インラインコード（Consolas・13.5px。
        /// AppendStyledRunsWithLineBreaks参照）等は実際より小さく見積もられ、列全体としては
        /// 余白が残るのに該当セルだけ折り返される不具合が起きるため、Run単位で測る。
        /// 各Runは、ローカルに設定されたFontFamily/FontSize/FontWeightがあればそれを使い、
        /// 無ければセルの既定（m_tableMeasureFontFamily・TABLE_MEASURE_FONT_SIZE・セルの
        /// FontWeight）を使う（ローカル値の有無で判定するのは、まだFlowDocumentに組み込まれる
        /// 前のRunでも、親子関係に左右されず同じ結果を得るため）。
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
                    // セルに画像（InlineUIContainerで包まれたImage）がある場合、その表示幅
                    // （Image.Width。ImageManager.ApplyImageSizingで設定済み）も内容幅に含める。
                    // Runのテキストだけを見ると、画像中心のセルの列が実際の画像幅より狭く
                    // 測定され、「列幅を補正する」オン時に画像の右端が見切れてしまうため。
                    if (inline is InlineUIContainer iuc && iuc.Child is Image img)
                    {
                        if (!double.IsNaN(img.Width) && img.Width > 0)
                        {
                            paraWidth += img.Width;
                        }
                        continue;
                    }
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
        /// （ApplyContentBasedColumnWidthsの測定部分と同じ考え方だが、Star幅の統一方針で
        /// 使うための独立した測定ヘルパー。詳細はApplyExplicitColumnWidthsのコメント参照）。
        /// 長い説明文の列をそのままの幅にすると、Star（比率）で他の列を圧迫して読めない
        /// ほど狭く押しつぶす不具合があったため、TABLE_COLUMN_COMPACT_MAX_WIDTHで頭打ちに
        /// している（長い列同士はおおむね同じ比率になり、それぞれの中で複数行に折り返す）。
        /// Pixel（固定幅。他の列と幅を奪い合わない）で使う場合はこの頭打ちを適用しない
        /// （MeasureUncappedColumnWidthsPx参照）。頭打ち後の幅には、罫線の太さやDPI差を
        /// 吸収するための余裕（TABLE_COLUMN_WIDTH_SAFETY_MARGIN_PX）も上乗せする。</summary>
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
        /// 加える）。300pxの頭打ちは、Star（比率）方式で複数列が幅を奪い合う場合に1つの
        /// 長い列が比率の大半を占めて他の列を圧迫するのを防ぐためのものであり、Pixel
        /// （固定幅、他の列と幅を奪い合わない）方式にまで適用すると、表全体には余裕が
        /// あるのに頭打ちされた列だけが不要な折り返しをする不具合になる。
        /// ApplyAutoCalculatedColumnWidthsは、この頭打ち無しの幅の合計が表示可能幅に
        /// 収まる場合にだけPixelを使うため、他の列を圧迫する心配が無い。
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
        /// 表インスタンスをキーにした<see cref="ConditionalWeakTable{TKey, TValue}"/>で、
        /// 表が破棄されれば一緒にGCされる（明示的な削除処理は不要）。
        /// メニュー「列幅を補正する」がオフの間は、表の見た目を既定の均等幅（Auto）に戻す
        /// （<see cref="ApplyEffectiveColumnWidths"/>参照）。もし保存処理（MarkdownConverter.
        /// TableToMarkdown）が表示用のTableColumn.Widthだけを見てダッシュ数を判断すると、
        /// トグルがオフの間に保存した際、調整済みだった表の情報が既定値（|---|）で失われて
        /// しまう。そこで、保存されるべき「本当の値」はこの記録先で表示用の列幅とは別に
        /// 管理し、TableToMarkdownは常にここを参照する。
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
        /// 表の各列に、明示的な比率幅（Star）を設定する。列幅はPixelではなくStar（Gridの
        /// ColumnDefinitionと同様の比率指定）を使う。表の合計幅が編集領域の横幅を超えると
        /// 右端が見切れて読めなくなり、WPFのRichTextBoxは既定で横スクロールできないため、
        /// 絶対px指定はウインドウを狭くすると再発する問題を抱えていた。Star幅なら表全体の
        /// 横幅を比率で分け合うため、編集領域を超えることが構造上あり得ない。ダッシュ数
        /// （区切り行の文字数・ダイアログの入力値）はそのままStarの比率として使う。
        /// なお、WPFの<see cref="Table"/>の列幅はPixel・Star・Auto（既定値）を混在させると
        /// 意図と逆の見た目になることが確認されているため（ApplyContentBasedColumnWidths
        /// のコメント参照）、列幅を1つでも明示的に調整する際は対象の表の全列を例外なく
        /// Star幅にする方針を維持している。
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
        /// 比率）で、表示上だけ列幅を適用する。あくまで表示（TableColumn.Width）だけの効果
        /// であり、区切り行のダッシュ数（GetSourceDashCounts／SetSourceDashCounts）には
        /// 一切触れない。そのため、Markdownへの書き出し（MarkdownConverter.TableToMarkdown）
        /// はこの比率を書き戻さず、区切り行は既定値（|---|）のまま保たれる。
        /// <paramref name="a_availableWidthPx"/>が指定されており、かつ各列の自然な幅
        /// （測定値。長い説明文の列はTABLE_COLUMN_COMPACT_MAX_WIDTHで頭打ち済み）の合計が
        /// その幅に収まる場合は、Starではなく自然な幅をそのままPixel（固定px）として設定する
        /// （常に同一の単位＝全列Pixelになり、単位の混在は発生しない）。表示領域に余裕が
        /// ある場合でも常にStarで横幅いっぱいに広げると、短い見出し語だけの表まで間延びして
        /// 見えてしまうため、それを防ぐ措置。
        /// 合計が収まらない場合（表示領域が狭い場合）や<paramref name="a_availableWidthPx"/>
        /// がnull（呼び出し側が表示領域の幅を把握していない場合。保存専用の使い捨て文書や
        /// PDF書き出し用の一時文書など）は、常にStar比率にする（表が編集領域の横幅を超えて
        /// 右端が見切れることが構造上あり得ないようにするための安全策）。
        /// </summary>
        /// <param name="a_table">対象の表。</param>
        /// <param name="a_availableWidthPx">表を表示できる実際の幅（px）。呼び出し側が
        /// 把握していない場合はnull（その場合は常にStar比率にする）。</param>
        public static void ApplyAutoCalculatedColumnWidths(Table a_table, double? a_availableWidthPx)
        {
            int colCount = a_table.Columns.Count;
            // Pixelは他の列と幅を奪い合わないため、頭打ち無しの生の幅を使う
            // （詳細はMeasureUncappedColumnWidthsPxのコメント参照）。
            var uncappedWidths = MeasureUncappedColumnWidthsPx(a_table);

            if (a_availableWidthPx.HasValue && uncappedWidths.Sum() <= a_availableWidthPx.Value)
            {
                for (int c = 0; c < colCount; c++)
                {
                    a_table.Columns[c].Width = new GridLength(uncappedWidths[c], GridUnitType.Pixel);
                }
                return;
            }

            // Star比率へフォールバックする場合は、複数列が幅を奪い合うため頭打ちした幅を使う。
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
