// SemanticUndoTracker.cs
//
// mde (Markdown インラインエディタ) の一部。
// 見出し・引用・コードブロック・箇条書き・太字・リンク等の「意味情報」（TextElement.Tag）を、WPF の Undo／Redo に
// 合わせて元に戻す。WPF の Undo が戻すのは文字と書式（FontWeight 等）だけで、Tag は戻らないため、そのままでは
// Undo の後に「見た目は普通の段落・太字でない文字なのに、保存すると見出し・太字になる」（またはその逆）ずれが起きる。

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace mde.common
{
    /// <summary>
    /// プログラムによる文書の変更（Run）を WPF の Undo 履歴の1操作にまとめ、その前後の全要素の Tag を覚えておき、
    /// その操作が Undo／Redo された時（NotifyTextChanged）に Tag も戻す。
    /// 「その操作が Undo されるのか」は、WPF の Undo 履歴の深さ（TextChanged の UndoAction から数える）で判定する。
    /// 文書の見た目の比較で判定すると、段落の文字・書式を変えない操作（太字にする等）の Undo を取り違えるため。
    /// </summary>
    public class SemanticUndoTracker
    {
        /// <summary>覚えておく操作の数の上限。</summary>
        private const int UNDO_LIMIT = 200;

        private readonly RichTextBox m_editor;
        private readonly Stack<Entry> m_undoEntries = new Stack<Entry>();
        private readonly Stack<Entry> m_redoEntries = new Stack<Entry>();
        /// <summary>WPF の Undo 履歴に積まれている操作の数（TextChanged の UndoAction から数える）。</summary>
        private int m_undoDepth;
        /// <summary>TextChanged が起きた回数（Run が、自分の操作の TextChanged が済んだかを調べる用）。</summary>
        private int m_textChangedCount;
        /// <summary>Undo 履歴の深さがまだ決まっていない（TextChanged を待っている）操作。</summary>
        private Entry m_entryAwaitingDepth;
        /// <summary>Run の入れ子の深さ（外側の Run だけが記録する）。</summary>
        private int m_nesting;

        private sealed class Entry
        {
            public Dictionary<TextElement, object> Before { get; set; }
            public Dictionary<TextElement, object> After { get; set; }
            /// <summary>この操作を積んだ直後の、WPF の Undo 履歴の深さ（未定なら -1）。</summary>
            public int Depth { get; set; } = -1;
        }

        /// <param name="a_editor">編集対象のRichTextBox。</param>
        public SemanticUndoTracker(RichTextBox a_editor)
        {
            m_editor = a_editor;
            // ファイルを開いた時等に Undo 履歴を消す（IsUndoEnabled をいったん false にする。FileOperationsManager.
            // ClearEditorUndoHistory）と、覚えている操作も消す
            DependencyPropertyDescriptor
                .FromProperty(TextBoxBase.IsUndoEnabledProperty, typeof(RichTextBox))
                .AddValueChanged(m_editor, (a_s, a_e) =>
                {
                    if (!m_editor.IsUndoEnabled)
                    {
                        Clear();
                    }
                });
        }

        /// <summary>
        /// 文書を変更する処理を、WPF の Undo 履歴の1操作として実行し、Tag の変化を覚えておく。入れ子で呼ばれた
        /// 場合は、外側の Run がまとめて記録する。
        /// </summary>
        /// <param name="a_action">文書を変更する処理。</param>
        public void Run(Action a_action)
        {
            if (0 < m_nesting)
            {
                a_action();
                return;
            }
            // 外側の変更ブロック（WPF 標準の処理等）の中で続けて呼ばれた場合は、WPF の Undo でも1操作にまとまるため、
            // 記録も1つにまとめる（最初の操作の前の Tag と、最後の操作の後の Tag を覚える）
            Entry awaiting = m_entryAwaitingDepth;
            Dictionary<TextElement, object> before = null == awaiting ? CaptureTags() : null;
            int textChangedCountBefore = m_textChangedCount;
            m_nesting++;
            m_editor.BeginChange();
            try
            {
                a_action();
            }
            finally
            {
                m_editor.EndChange();
                m_nesting--;
            }
            if (null != awaiting)
            {
                awaiting.After = CaptureTags();
                return;
            }
            Dictionary<TextElement, object> after = CaptureTags();
            if (SameTags(before, after))
            {
                return; // Tag は変わっていない：Undo で戻すものは無い
            }
            var entry = new Entry { Before = before, After = after };
            m_undoEntries.Push(entry);
            if (textChangedCountBefore != m_textChangedCount)
            {
                entry.Depth = m_undoDepth;
            }
            else
            {
                // 外側の変更ブロックの中から呼ばれた等で、TextChanged がまだ起きていない：起きた時に深さを決める。
                // 何も変わらず TextChanged が起きなかった場合は、記録を捨てる
                m_entryAwaitingDepth = entry;
                m_editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    if (ReferenceEquals(m_entryAwaitingDepth, entry))
                    {
                        m_entryAwaitingDepth = null;
                        if (0 < m_undoEntries.Count && ReferenceEquals(m_undoEntries.Peek(), entry))
                        {
                            m_undoEntries.Pop();
                        }
                    }
                }));
            }
            if (UNDO_LIMIT < m_undoEntries.Count)
            {
                // Stackの末尾（最も古い操作）を落とし、履歴を有限に保つ。
                Entry[] entries = m_undoEntries.ToArray();
                m_undoEntries.Clear();
                for (int i = UNDO_LIMIT - 1; 0 <= i; i--)
                {
                    m_undoEntries.Push(entries[i]);
                }
            }
            m_redoEntries.Clear();
        }

        /// <summary>
        /// RichTextBox の TextChanged：WPF の Undo 履歴の深さを数え、Tag を変えた操作が Undo／Redo された時に
        /// Tag も戻す（Ctrl+Z・Ctrl+Y 以外の方法で Undo／Redo された場合も含む）。MainWindow.EditorTextChanged が、
        /// 他の処理（入力中の自動変換等。その中でさらに文書が変わる）より先に呼ぶこと（先に呼ばないと、自動変換の
        /// 操作の方が先に数えられ、深さの順序が入れ替わる）。
        /// </summary>
        /// <param name="a_args">TextChanged の引数。</param>
        public void NotifyTextChanged(TextChangedEventArgs a_args)
        {
            m_textChangedCount++;
            switch (a_args.UndoAction)
            {
                case UndoAction.Create:
                    m_undoDepth++;
                    // 新しい操作が積まれた：Undo 済みで同じ深さ以上にあった古い記録は、もう使われない
                    while (0 < m_undoEntries.Count &&
                           !ReferenceEquals(m_undoEntries.Peek(), m_entryAwaitingDepth) &&
                           m_undoEntries.Peek().Depth >= m_undoDepth)
                    {
                        m_undoEntries.Pop();
                    }
                    m_redoEntries.Clear();
                    break;
                case UndoAction.Undo:
                    {
                        int undoneDepth = m_undoDepth;
                        m_undoDepth = Math.Max(0, m_undoDepth - 1);
                        if (0 < m_undoEntries.Count && undoneDepth == m_undoEntries.Peek().Depth)
                        {
                            Entry entry = m_undoEntries.Pop();
                            RestoreTags(entry.Before, entry.After);
                            m_redoEntries.Push(entry);
                        }
                        // 深さの合わなくなった古い記録は捨てる
                        while (0 < m_undoEntries.Count && m_undoEntries.Peek().Depth > m_undoDepth)
                        {
                            m_undoEntries.Pop();
                        }
                    }
                    break;
                case UndoAction.Redo:
                    m_undoDepth++;
                    if (0 < m_redoEntries.Count && m_undoDepth == m_redoEntries.Peek().Depth)
                    {
                        Entry entry = m_redoEntries.Pop();
                        RestoreTags(entry.After, entry.Before);
                        m_undoEntries.Push(entry);
                    }
                    break;
                case UndoAction.Clear:
                    Clear();
                    break;
            }
            if (null != m_entryAwaitingDepth &&
                (UndoAction.Create == a_args.UndoAction || UndoAction.Merge == a_args.UndoAction))
            {
                m_entryAwaitingDepth.Depth = m_undoDepth;
                m_entryAwaitingDepth = null;
            }
        }

        /// <summary>覚えている操作をすべて消す（Undo 履歴が消された時）。</summary>
        public void Clear()
        {
            m_undoEntries.Clear();
            m_redoEntries.Clear();
            m_entryAwaitingDepth = null;
            m_undoDepth = 0;
        }

        /// <summary>文書の中の、Tag を持つすべての要素（段落・リスト・表のセル・Run 等）と、その Tag。</summary>
        /// <returns>要素→Tag。</returns>
        private Dictionary<TextElement, object> CaptureTags()
        {
            var tags = new Dictionary<TextElement, object>();
            CaptureBlocks(m_editor.Document.Blocks, tags);
            return tags;
        }

        private static void CaptureBlocks(BlockCollection a_blocks, Dictionary<TextElement, object> a_tags)
        {
            foreach (Block block in a_blocks)
            {
                AddTag(block, a_tags);
                if (block is Paragraph paragraph)
                {
                    CaptureInlines(paragraph.Inlines, a_tags);
                }
                else if (block is List list)
                {
                    foreach (ListItem item in list.ListItems)
                    {
                        AddTag(item, a_tags);
                        CaptureBlocks(item.Blocks, a_tags);
                    }
                }
                else if (block is Table table)
                {
                    foreach (TableRowGroup group in table.RowGroups)
                    {
                        foreach (TableRow row in group.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                AddTag(cell, a_tags);
                                CaptureBlocks(cell.Blocks, a_tags);
                            }
                        }
                    }
                }
                else if (block is Section section)
                {
                    CaptureBlocks(section.Blocks, a_tags);
                }
            }
        }

        private static void CaptureInlines(InlineCollection a_inlines, Dictionary<TextElement, object> a_tags)
        {
            foreach (Inline inline in a_inlines)
            {
                AddTag(inline, a_tags);
                if (inline is Span span)
                {
                    CaptureInlines(span.Inlines, a_tags);
                }
            }
        }

        private static void AddTag(TextElement a_element, Dictionary<TextElement, object> a_tags)
        {
            if (null != a_element.Tag)
            {
                a_tags[a_element] = a_element.Tag;
            }
        }

        private static bool SameTags(Dictionary<TextElement, object> a_a, Dictionary<TextElement, object> a_b)
        {
            if (a_a.Count != a_b.Count)
            {
                return false;
            }
            foreach (KeyValuePair<TextElement, object> kv in a_a)
            {
                if (!a_b.TryGetValue(kv.Key, out object other) || !Equals(kv.Value, other))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// a_target の Tag に戻す。a_other にだけある（a_target の時点では Tag の無かった）要素の Tag は消す。
        /// Undo／Redo で文書から外れた要素は、もう表示も保存もされないため触らない。
        /// </summary>
        private void RestoreTags(Dictionary<TextElement, object> a_target, Dictionary<TextElement, object> a_other)
        {
            foreach (KeyValuePair<TextElement, object> kv in a_target)
            {
                if (IsInDocument(kv.Key) && !Equals(kv.Key.Tag, kv.Value))
                {
                    kv.Key.Tag = kv.Value;
                }
            }
            foreach (TextElement element in a_other.Keys)
            {
                if (!a_target.ContainsKey(element) && null != element.Tag && IsInDocument(element))
                {
                    element.Tag = null;
                }
            }
        }

        /// <summary>要素が、今の文書の中にあるか。</summary>
        private bool IsInDocument(TextElement a_element)
        {
            DependencyObject node = a_element;
            while (null != node)
            {
                if (ReferenceEquals(node, m_editor.Document))
                {
                    return true;
                }
                node = node is FrameworkContentElement fce ? fce.Parent : null;
            }
            return false;
        }
    }
}
