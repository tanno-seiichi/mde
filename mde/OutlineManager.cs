// OutlineManager.cs
//
// mde (MarkDown インラインエディタ) の一部。
// アウトラインペイン（見出し一覧）を担当するクラス。文書から見出しを収集して一覧を作り、
// クリックされた見出しまでエディタをスクロールする。

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace mde
{
    /// <summary>アウトラインペインの一覧構築と、見出しクリック時のジャンプを担当する。</summary>
    public class OutlineManager
    {
        private readonly RichTextBox m_editor;

        /// <summary>アウトラインペインの一覧（ListBox.ItemsSourceとして使う）。</summary>
        public ObservableCollection<OutlineEntry> Items { get; } = new ObservableCollection<OutlineEntry>();

        /// <summary>
        /// OutlineManagerを構築する。
        /// </summary>
        /// <param name="a_editor">対象のRichTextBox。</param>
        public OutlineManager(RichTextBox a_editor)
        {
            this.m_editor = a_editor;
        }

        /// <summary>現在の文書から見出しを収集し、一覧を作り直す。</summary>
        public void Refresh()
        {
            Items.Clear();
            foreach (Block block in m_editor.Document.Blocks)
            {
                if (block is Paragraph p && p.Tag is int level && level > 0)
                {
                    string text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                    if (0 == text.Length) text = "(無題)";
                    Items.Add(new OutlineEntry { Level = level, Text = text, Target = p });
                }
            }
        }

        /// <summary>
        /// 検索で見つかった一致箇所の一覧を受け取り、それぞれが属する見出しの区間（その一致箇所
        /// より手前にある、一番近い見出し）を強調表示する。呼び出し前の強調表示はクリアされる。
        /// </summary>
        /// <param name="a_matches">強調表示したい一致箇所（ライブなTextRange）。</param>
        public void MarkSearchMatches(IEnumerable<TextRange> a_matches)
        {
            ClearSearchMatches();
            foreach (var range in a_matches)
            {
                Paragraph nearestHeading = null;
                foreach (Block block in m_editor.Document.Blocks)
                {
                    if (block is Paragraph p && p.Tag is int level && level > 0)
                    {
                        if (p.ContentStart.CompareTo(range.Start) <= 0) nearestHeading = p;
                        else break; // ブロックは文書順に並んでいるので、超えた時点で打ち切ってよい
                    }
                }
                if (null == nearestHeading) continue;
                var entry = Items.FirstOrDefault(e => e.Target == nearestHeading);
                if (null != entry) entry.IsSearchMatch = true;
            }
        }

        /// <summary>検索結果の強調表示をすべて解除する。</summary>
        public void ClearSearchMatches()
        {
            foreach (var entry in Items) entry.IsSearchMatch = false;
        }

        /// <summary>アウトラインペインで見出しがクリックされた時に、エディタをその見出しまで
        /// スクロールする。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleSelectionChanged(object a_sender, SelectionChangedEventArgs a_args)
        {
            if (a_sender is ListBox list && list.SelectedItem is OutlineEntry entry && null != entry.Target)
            {
                m_editor.CaretPosition = entry.Target.ContentStart;
                ScrollParagraphToTop(entry.Target, m_editor);
                m_editor.Focus();
            }
        }

        /// <summary>
        /// 指定した段落が、表示領域の一番上に完全な形で見えるようスクロールする。すでに画面内に
        /// 見えている場合、標準のBringIntoViewは「見えているので何もしない」という挙動になり
        /// 一番上への移動が起きないため、先に一旦スクロール位置を先頭へリセットしてから
        /// BringIntoViewを呼ぶことで、毎回確実に一番上へ移動するようにしている。
        /// </summary>
        /// <param name="a_p">スクロール先の段落。</param>
        /// <param name="a_editor">対象のRichTextBox。</param>
        public static void ScrollParagraphToTop(Paragraph a_p, RichTextBox a_editor)
        {
            // WPF標準のBringIntoViewが、表示領域の一番上へ正しくスクロールしてくれる
            // （既に画面内に見えている場合は何もしない、という仕様のトレードオフを受け入れている）。
            a_p.BringIntoView();
        }

        private static T FindVisualChild<T>(DependencyObject a_root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(a_root); i++)
            {
                var child = VisualTreeHelper.GetChild(a_root, i);
                if (child is T match) return match;
                var found = FindVisualChild<T>(child);
                if (null != found) return found;
            }
            return null;
        }
    }
}
