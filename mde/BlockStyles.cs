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
        private static readonly Brush CellBorder = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush CodeBlockBackground = new SolidColorBrush(Color.FromRgb(0xEC, 0xE8, 0xDC));

        /// <summary>コードブロックの背景色（他クラスからも参照できるよう公開）。</summary>
        public static Brush CodeBlockBackgroundBrush => CodeBlockBackground;

        /// <summary>段落を、見出し・コードブロック等の特別な見た目が付く前の状態にリセットする。</summary>
        /// <param name="p">対象の段落。</param>
        public static void ClearSpecialStyling(Paragraph p)
        {
            p.Background = null;
            p.Padding = new Thickness(0);
            p.BorderThickness = new Thickness(0);
            p.BorderBrush = null;
            p.ClearValue(TextElement.FontFamilyProperty);
        }

        /// <summary>段落に見出しスタイルを適用する（level=0 なら本文スタイルに戻す）。</summary>
        /// <param name="p">対象の段落。</param>
        /// <param name="level">見出しレベル（0〜6。0は本文）。</param>
        public static void ApplyHeadingStyle(Paragraph p, int level)
        {
            ClearSpecialStyling(p);
            p.Tag = level == 0 ? null : (object)level;
            if (level == 0)
            {
                p.FontSize = 16;
                p.FontWeight = FontWeights.Normal;
                p.Margin = new Thickness(0, 0, 0, 14);
            }
            else
            {
                double[] sizes = { 0, 30, 24, 20, 18, 16.5, 15.5 };
                p.FontSize = sizes[level];
                p.FontWeight = FontWeights.Bold;
                p.Margin = new Thickness(0, level <= 2 ? 20 : 14, 0, 10);
                if (level <= 2)
                {
                    p.BorderBrush = CellBorder;
                    p.BorderThickness = new Thickness(0, 0, 0, level == 1 ? 0.75 : 0.5);
                    p.Padding = new Thickness(0, 0, 0, 4);
                }
                else
                {
                    p.BorderThickness = new Thickness(0);
                }
            }
        }

        /// <summary>段落にコードブロックスタイル（等幅フォント・背景色・枠線）を適用する。</summary>
        /// <param name="p">対象の段落。</param>
        /// <param name="language">```の直後の言語タグ（ツールチップに表示される）。</param>
        public static void ApplyCodeBlockStyle(Paragraph p, string language = "")
        {
            ClearSpecialStyling(p);
            p.Tag = new CodeBlockInfo { Language = language ?? "" };
            p.FontFamily = new FontFamily("Consolas");
            p.FontSize = 13.5;
            p.FontWeight = FontWeights.Normal;
            p.Background = CodeBlockBackground;
            p.Padding = new Thickness(14, 10, 14, 10);
            p.Margin = new Thickness(0, 4, 0, 14);
            p.BorderBrush = CellBorder;
            p.BorderThickness = new Thickness(1);
            ToolTipService.SetToolTip(p, string.IsNullOrEmpty(language) ? "コードブロック" : "コードブロック (" + language + ")");
        }
    }
}
