// OutlineManager.cs
//
// mde (MarkDown インラインエディタ) の一部。
// アウトラインペイン（見出し一覧）を担当するクラス。文書から見出しを収集して一覧を作り、
// クリックされた見出しまでエディタをスクロールする。

using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Documents;

namespace mde
{
    /// <summary>アウトラインペインの一覧構築と、見出しクリック時のジャンプを担当する。</summary>
    public class OutlineManager
    {
        private readonly RichTextBox editor;

        /// <summary>アウトラインペインの一覧（ListBox.ItemsSourceとして使う）。</summary>
        public ObservableCollection<OutlineEntry> Items { get; } = new ObservableCollection<OutlineEntry>();

        /// <summary>
        /// OutlineManagerを構築する。
        /// </summary>
        /// <param name="editor">対象のRichTextBox。</param>
        public OutlineManager(RichTextBox editor)
        {
            this.editor = editor;
        }

        /// <summary>現在の文書から見出しを収集し、一覧を作り直す。</summary>
        public void Refresh()
        {
            Items.Clear();
            foreach (Block block in editor.Document.Blocks)
            {
                if (block is Paragraph p && p.Tag is int level && level > 0)
                {
                    string text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                    if (text.Length == 0) text = "(無題)";
                    Items.Add(new OutlineEntry { Level = level, Text = text, Target = p });
                }
            }
        }

        /// <summary>アウトラインペインで見出しがクリックされた時に、エディタをその見出しまで
        /// スクロールする。</summary>
        public void HandleSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox list && list.SelectedItem is OutlineEntry entry && entry.Target != null)
            {
                editor.CaretPosition = entry.Target.ContentStart;
                ScrollParagraphToTop(entry.Target);
                editor.Focus();
            }
        }

        /// <summary>指定した段落が、表示領域の一番上に完全な形で見えるようスクロールする。</summary>
        /// <param name="p">スクロール先の段落。</param>
        public static void ScrollParagraphToTop(Paragraph p)
        {
            // WPF標準のBringIntoViewが、表示領域の一番上へ正しくスクロールしてくれる。
            // 以前試した独自のScrollViewer/GetCharacterRectによるオフセット計算は、
            // 理屈の上では正しく見えても実際には見出しが欠けて表示される結果になったため、
            // よりシンプルで動作確認済みのBringIntoViewを採用している。
            p.BringIntoView();
        }
    }
}
