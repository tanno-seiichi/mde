// ListEditor.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 箇条書き・順序付きリストの編集を担当するクラス。段落からリスト項目への変換、
// Enterキーでの項目追加/リスト脱出、Tab/Shift+Tabでの字下げ・字下げ解除を扱う。

using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace mde
{
    /// <summary>
    /// 箇条書き・順序付きリストの編集機能一式。MainWindowとは疎結合で、Editor本体・
    /// 「元テキスト保持」の追跡役・プログラム的変更ラップ用のdelegateだけを受け取って動作する。
    /// </summary>
    public class ListEditor
    {
        private readonly RichTextBox editor;
        private readonly OriginalTextTracker originalTextTracker;
        private readonly Action<Action> runAsProgrammaticChange;

        /// <summary>
        /// ListEditorを構築する。
        /// </summary>
        /// <param name="editor">編集対象のRichTextBox。</param>
        /// <param name="originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        public ListEditor(RichTextBox editor, OriginalTextTracker originalTextTracker, Action<Action> runAsProgrammaticChange)
        {
            this.editor = editor;
            this.originalTextTracker = originalTextTracker;
            this.runAsProgrammaticChange = runAsProgrammaticChange;
        }

        /// <summary>段落がリスト項目に含まれているかどうかを調べる。</summary>
        /// <param name="para">調べる段落。</param>
        /// <param name="li">含まれているListItem（見つかった場合）。</param>
        /// <param name="parentList">そのListItemを含むList（見つかった場合）。</param>
        /// <returns>リスト項目の段落であれば true。</returns>
        public bool IsInListItem(Paragraph para, out ListItem li, out List parentList)
        {
            li = para.Parent as ListItem;
            if (li != null)
            {
                parentList = li.Parent as List;
                return parentList != null;
            }
            parentList = null;
            return false;
        }

        /// <summary>段落をリスト項目（新規または既存リストへの追加）に変換する。</summary>
        /// <param name="p">変換する段落（そのテキストが最初の項目のテキストになる）。</param>
        /// <param name="marker">箇条書き記号（"*"または"-"）。順序付きリストの場合はnull。</param>
        /// <param name="ordered">順序付きリストなら true、箇条書きなら false。</param>
        public void ConvertParagraphToListItem(Paragraph p, string marker, bool ordered)
        {
            runAsProgrammaticChange(() =>
            {
                Block prev = p.PreviousBlock;
                var newLiPara = new Paragraph();
                var newLi = new ListItem(newLiPara);

                if (prev is List prevList && (prevList.MarkerStyle == TextMarkerStyle.Decimal) == ordered)
                {
                    prevList.ListItems.Add(newLi);
                    editor.Document.Blocks.Remove(p);
                }
                else
                {
                    var list = new List
                    {
                        MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Tag = ordered ? null : marker
                    };
                    list.ListItems.Add(newLi);
                    editor.Document.Blocks.InsertBefore(p, list);
                    editor.Document.Blocks.Remove(p);
                }
                editor.CaretPosition = newLiPara.ContentStart;
            });
            editor.Focus();
        }

        /// <summary>リスト項目自身のテキストを取得する（入れ子のサブリストは含まない）。
        /// マーカー記号を拾ってしまうTextRangeではなく、Inlinesを直接読むため安全。</summary>
        /// <param name="li">調べる項目。</param>
        /// <returns>トリム済みのテキスト。画像だけがあり文字がない場合はゼロ幅スペース1文字。</returns>
        public string GetOwnListItemText(ListItem li)
        {
            var ownPara = li.Blocks.FirstBlock as Paragraph;
            if (ownPara == null) return "";
            var sb = new StringBuilder();
            AppendPlainInlineText(ownPara.Inlines, sb);
            string t = sb.ToString().Trim();
            if (t.Length == 0 && HasDescendantImage(ownPara)) return "\u200B";
            return t;
        }

        /// <summary>Inlinesコレクションの中の、Runのリテラルテキストだけを連結する
        /// （Spanは再帰的にたどり、LineBreakは改行として扱う）。TextRange.Textと違い、
        /// リスト項目のマーカー記号を文字として拾ってしまうことがない。</summary>
        private void AppendPlainInlineText(InlineCollection inlines, StringBuilder sb)
        {
            foreach (Inline inline in inlines)
            {
                if (inline is Run run) sb.Append(run.Text);
                else if (inline is Span span) AppendPlainInlineText(span.Inlines, sb);
                else if (inline is LineBreak) sb.Append('\n');
            }
        }

        private bool HasDescendantImage(Paragraph p)
        {
            foreach (Inline inline in p.Inlines)
                if (InlineContainsImage(inline)) return true;
            return false;
        }

        private bool InlineContainsImage(Inline inline)
        {
            if (inline is InlineUIContainer iuc && iuc.Child is Image) return true;
            if (inline is Span span)
                foreach (Inline child in span.Inlines)
                    if (InlineContainsImage(child)) return true;
            return false;
        }

        /// <summary>リスト項目内でのEnterキー処理。項目が空ならリストを抜けて新しい通常の段落を
        /// 作り、空でなければ新しい項目を後ろに追加する。</summary>
        /// <param name="li">現在の項目。</param>
        /// <param name="parentList">現在の項目を含むList。</param>
        public void HandleListEnter(ListItem li, List parentList)
        {
            originalTextTracker.InvalidateForBlock(parentList);
            runAsProgrammaticChange(() =>
            {
                bool hasNestedList = li.Blocks.Count > 1;
                bool isEmpty = !hasNestedList && GetOwnListItemText(li).Length == 0;

                if (isEmpty)
                {
                    List topList = parentList;
                    DependencyObject cursor = parentList.Parent;
                    while (cursor is ListItem ownerLi)
                    {
                        topList = ownerLi.Parent as List;
                        cursor = topList?.Parent;
                    }

                    var newPara = new Paragraph();
                    editor.Document.Blocks.InsertAfter(topList, newPara);

                    parentList.ListItems.Remove(li);
                    if (parentList.ListItems.Count == 0)
                    {
                        RemoveEmptyList(parentList);
                    }
                    editor.CaretPosition = newPara.ContentStart;
                }
                else
                {
                    var newLi = new ListItem(new Paragraph());
                    var items = parentList.ListItems.Cast<ListItem>().ToList();
                    int idx = items.IndexOf(li);
                    parentList.ListItems.Clear();
                    for (int k = 0; k < items.Count; k++)
                    {
                        parentList.ListItems.Add(items[k]);
                        if (k == idx) parentList.ListItems.Add(newLi);
                    }
                    editor.CaretPosition = ((Paragraph)newLi.Blocks.FirstBlock).ContentStart;
                }
            });
            editor.Focus();
        }

        private void RemoveEmptyList(List list)
        {
            if (list.Parent is ListItem ownerLi)
                ownerLi.Blocks.Remove(list);
            else if (list.Parent is FlowDocument doc)
                doc.Blocks.Remove(list);
        }

        /// <summary>Tabキーでのリスト項目の字下げ。前の兄弟項目の下に新しい入れ子リストを作り、
        /// そこへ移す。</summary>
        /// <param name="li">字下げする項目。</param>
        /// <param name="parentList">現在の（字下げ前の）親List。</param>
        public void IndentListItem(ListItem li, List parentList)
        {
            originalTextTracker.InvalidateForBlock(parentList);
            ListItem prevLi = null;
            foreach (ListItem item in parentList.ListItems)
            {
                if (item == li) break;
                prevLi = item;
            }
            if (prevLi == null) return; // 先頭の項目は字下げできない

            runAsProgrammaticChange(() =>
            {
                List nestedList = prevLi.Blocks.Count > 1 ? prevLi.Blocks.LastBlock as List : null;
                if (nestedList == null)
                {
                    bool parentOrdered = parentList.MarkerStyle == TextMarkerStyle.Decimal;
                    nestedList = new List
                    {
                        MarkerStyle = parentOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Circle,
                        Tag = parentOrdered ? null : ((parentList.Tag as string) ?? "*")
                    };
                    prevLi.Blocks.Add(nestedList);
                }
                parentList.ListItems.Remove(li);
                nestedList.ListItems.Add(li);

                if (li.Blocks.FirstBlock is Paragraph fp) editor.CaretPosition = fp.ContentEnd;
            });
            editor.Focus();
        }

        /// <summary>Shift+Tabキーでのリスト項目の字下げ解除。自分より後ろの兄弟項目も一緒に
        /// 自分の下の入れ子リストへ移してから、1段上の親リストへ昇格させる。</summary>
        /// <param name="li">字下げ解除する項目。</param>
        /// <param name="parentList">現在の（解除前の）親List。</param>
        public void OutdentListItem(ListItem li, List parentList)
        {
            originalTextTracker.InvalidateForBlock(parentList);
            if (!(parentList.Parent is ListItem parentLi)) return; // すでにトップレベル
            if (!(parentLi.Parent is List grandList)) return;

            runAsProgrammaticChange(() =>
            {
                var siblings = parentList.ListItems.Cast<ListItem>().ToList();
                int idx = siblings.IndexOf(li);
                var before = siblings.Take(idx).ToList();
                var after = siblings.Skip(idx + 1).ToList();

                parentList.ListItems.Clear();
                foreach (var b in before) parentList.ListItems.Add(b);

                if (after.Count > 0)
                {
                    bool parentOrdered = parentList.MarkerStyle == TextMarkerStyle.Decimal;
                    List ownNested = new List
                    {
                        MarkerStyle = parentOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Circle,
                        Tag = parentOrdered ? null : ((parentList.Tag as string) ?? "*")
                    };
                    foreach (var a in after) ownNested.ListItems.Add(a);
                    li.Blocks.Add(ownNested);
                }

                var grandItems = grandList.ListItems.Cast<ListItem>().ToList();
                int gIdx = grandItems.IndexOf(parentLi);
                grandList.ListItems.Clear();
                for (int k = 0; k < grandItems.Count; k++)
                {
                    grandList.ListItems.Add(grandItems[k]);
                    if (k == gIdx) grandList.ListItems.Add(li);
                }

                if (parentList.ListItems.Count == 0)
                {
                    parentLi.Blocks.Remove(parentList);
                }

                if (li.Blocks.FirstBlock is Paragraph fp) editor.CaretPosition = fp.ContentEnd;
            });
            editor.Focus();
        }
    }
}
