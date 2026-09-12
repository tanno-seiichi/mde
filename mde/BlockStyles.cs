// BlockStyles.cs
//
// mde (Markdown インラインエディタ) の一部。
// 見出し・コードブロックの見た目（フォントサイズ・余白・枠線など）を適用する静的ヘルパー。
// Markdown解析（MarkdownConverter）と、右クリックでの段落種別変更（HeadingCodeBlockEditor）の
// 両方から共有で使われるため、状態を持たない静的メソッドとして独立させている。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace mde
{
    /// <summary>見出し・コードブロックの段落スタイルを適用する静的ヘルパー群。</summary>
    public static class BlockStyles
    {
        private static readonly Brush CELL_BORDER = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4));
        private static readonly Brush CODE_BLOCK_BACKGROUND = new SolidColorBrush(Color.FromRgb(0xEC, 0xE8, 0xDC));
        // 表のヘッダー行だけ、セル間の縦の区切り線（右辺）をこの太さにする。本文行の1に対して
        // わずかに太くすることで、印刷（PDF出力）時にヘッダー行の縦罫線だけが描画されずに
        // 消えてしまう現象を避ける（実機での複数バージョン比較検証により、色を変える案では
        // 効果が無く、太さを変えるこの案で解消することを確認済み）。
        private const double HEADER_VERTICAL_BORDER_THICKNESS = 1.75;
        // ハイライト（==text==）の背景色。
        private static readonly Brush HIGHLIGHT_BACKGROUND = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0x8A));

        /// <summary>コードブロックの背景色（他クラスからも参照できるよう公開）。</summary>
        public static Brush CodeBlockBackgroundBrush => CODE_BLOCK_BACKGROUND;

        /// <summary>ハイライト（==text==）の背景色（他クラスからも参照できるよう公開）。</summary>
        public static Brush HighlightBrush => HIGHLIGHT_BACKGROUND;

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
                    a_p.BorderBrush = CELL_BORDER;
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
            a_p.Tag = new CodeBlockInfo { m_language = a_language ?? "" };
            a_p.FontFamily = new FontFamily("Consolas");
            a_p.FontSize = 13.5;
            a_p.FontWeight = FontWeights.Normal;
            a_p.Background = CODE_BLOCK_BACKGROUND;
            a_p.Padding = new Thickness(14, 10, 14, 10);
            a_p.Margin = new Thickness(0, 4, 0, 14);
            a_p.BorderBrush = CELL_BORDER;
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
            a_p.BorderBrush = CELL_BORDER;
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
        private static readonly FontFamily TABLE_MEASURE_FONT_FAMILY = new FontFamily("Yu Gothic UI, Segoe UI");
        private const double TABLE_MEASURE_FONT_SIZE = 16;

        /// <summary>この幅（px）を超える内容を持つ列は、固定幅にはせず、残りの幅を分け合って
        /// 折り返す列として扱う。</summary>
        private const double TABLE_COLUMN_COMPACT_MAX_WIDTH = 300;

        /// <summary>TableCellのPadding（左右合計）。列幅を内容ぴったりにする際、この分だけ
        /// 上乗せする。</summary>
        private const double TABLE_CELL_HORIZONTAL_PADDING = 16;

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
                            TABLE_MEASURE_FONT_FAMILY, FontStyles.Normal, cell.FontWeight, FontStretches.Normal);
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
    }
}
