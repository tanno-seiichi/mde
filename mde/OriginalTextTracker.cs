// OriginalTextTracker.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 「編集していないブロック（見出し・段落・箇条書き・表・コードブロック）は、保存時に
// 元のテキストをそのまま書き戻す」仕組みを担当するクラス。読み込み時に各ブロックの
// 元のソーステキストを記憶しておき、実際に編集されたブロックだけを記憶から取り除くことで、
// 保存時にどちらを使うべきか判定できるようにする。
//
// MarkdownConverter・ListEditor・TableEditor・InlineStyleEditor など、文書構造を直接
// いじる複数のクラスから共有される協力オブジェクトとして使う（delegateではなく、
// 状態を持つ本物のオブジェクトとして渡す）。

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace mde
{
    /// <summary>
    /// 各トップレベルブロック（FlowDocument.Blocksの直接の子）について、読み込み時点の
    /// 元テキストを記憶し、そのブロックが編集されたら記憶を破棄する。ブロックのオブジェクト
    /// 参照をキーにした ConditionalWeakTable を使っているため、ブロックが不要になれば
    /// 自動的にエントリも解放される。
    /// </summary>
    public class OriginalTextTracker
    {
        private readonly RichTextBox editor;

        /// <summary>ブロックごとの元テキストを保持する値クラス（参照型で包むことで、
        /// ConditionalWeakTableに値を追加/更新できるようにしている）。</summary>
        private class Holder
        {
            public string Text;
        }

        private readonly ConditionalWeakTable<Block, Holder> table = new ConditionalWeakTable<Block, Holder>();

        /// <summary>
        /// OriginalTextTrackerを構築する。
        /// </summary>
        /// <param name="editor">対象のRichTextBox（GetTopLevelBlockでEditor.Documentと比較するために必要）。</param>
        public OriginalTextTracker(RichTextBox editor)
        {
            this.editor = editor;
        }

        /// <summary>
        /// 指定位置から上へたどり、FlowDocument.Blocksの直接の子であるトップレベルブロックを
        /// 見つける（すでにトップレベルの段落ならその段落自身、箇条書き項目や表のセルの中の
        /// 位置なら、それを含むList/Table）。
        /// </summary>
        /// <param name="position">起点となる位置。</param>
        /// <returns>見つかったトップレベルブロック。positionがnullなら null。</returns>
        public Block GetTopLevelBlock(TextPointer position)
        {
            var para = position?.Paragraph;
            if (para == null) return null;

            DependencyObject node = para;
            while (node != null)
            {
                if (node is Block block && ReferenceEquals(block.Parent, editor.Document))
                    return block;

                node = (node as TextElement)?.Parent;
            }
            return null;
        }

        /// <summary>
        /// 指定位置にあるブロック（またはそれを含むトップレベルブロック）を「編集済み」として
        /// 記憶から取り除く。保存時にはこのブロックは元テキストではなく、現在の構造から
        /// 新たに組み立て直したテキストが使われるようになる。
        /// </summary>
        /// <param name="position">編集が行われた位置。</param>
        public void Invalidate(TextPointer position)
        {
            var block = GetTopLevelBlock(position);
            if (block != null) table.Remove(block);
        }

        /// <summary>
        /// Invalidate(TextPointer)と同じことを、位置ではなくBlockの参照から直接行う。
        /// </summary>
        /// <param name="block">編集されたブロック（またはその子孫の要素）。</param>
        public void InvalidateForBlock(Block block)
        {
            DependencyObject node = block;
            while (node != null)
            {
                if (node is Block b && ReferenceEquals(b.Parent, editor.Document))
                {
                    table.Remove(b);
                    return;
                }
                node = (node as TextElement)?.Parent;
            }
        }

        /// <summary>
        /// 新しく解析されたブロックについて、それを生成した元のソース行をそのまま記憶する。
        /// 一度も編集されなければ、保存時にこのテキストがそのまま書き戻される。
        /// </summary>
        /// <param name="block">直前にドキュメントへ追加されたブロック。</param>
        /// <param name="lines">ファイル全体のソース行配列。</param>
        /// <param name="start">このブロックを生成した最初の行番号（含む）。</param>
        /// <param name="end">このブロックを生成した最後の行番号（含まない）。</param>
        public void Record(Block block, string[] lines, int start, int end)
        {
            table.AddOrUpdate(block, new Holder { Text = string.Join("\n", lines, start, end - start) });
        }

        /// <summary>
        /// ブロックの元テキストがまだ記憶されている（＝一度も編集されていない）かどうかを調べる。
        /// </summary>
        /// <param name="block">調べたいブロック。</param>
        /// <param name="text">記憶されていた元テキスト（見つかった場合）。</param>
        /// <returns>記憶が残っていれば true。</returns>
        public bool TryGetOriginal(Block block, out string text)
        {
            if (table.TryGetValue(block, out var holder))
            {
                text = holder.Text;
                return true;
            }
            text = null;
            return false;
        }

        /// <summary>ファイルを新規に読み込む際、それ以前の記憶をすべて破棄する。</summary>
        public void Clear()
        {
            table.Clear();
        }
    }
}
