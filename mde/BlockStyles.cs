// BlockStyles.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 見出し・コードブロックの見た目（フォントサイズ・余白・枠線など）を適用する静的ヘルパー。
// MarkDown解析（MarkdownConverter）と、右クリックでの段落種別変更（HeadingCodeBlockEditor）の
// 両方から共有で使われるため、状態を持たない静的メソッドとして独立させている。

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

        /// <summary>コードブロックの背景色（他クラスからも参照できるよう公開）。</summary>
        public static Brush CodeBlockBackgroundBrush => CODE_BLOCK_BACKGROUND;

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
                // Marginはここでは明示的に設定しない。RichTextBoxのStyle側で
                // EditorBlockSpacing（行間と連動する動的リソース）による値が適用される
                // ことで、段落と段落の間の間隔が、段落内で折り返された行同士の間隔と
                // 揃うようにしている（ここでローカル値として設定すると、そちらを
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
    }
}
