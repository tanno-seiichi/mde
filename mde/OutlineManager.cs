// OutlineManager.cs
//
// mde (Markdown インラインエディタ) の一部。
// アウトラインペイン（見出し一覧）を担当するクラス。文書から見出しを収集して一覧を作り、
// クリックされた見出しまでエディタをスクロールする。

using System;
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

        /// <summary>現在の文書から見出しを収集し、一覧を作り直す。フォルダツリーペインと同じ
        /// 折りたたみ可能なツリー構造にするため、見出しレベルに応じた入れ子（親子関係）を
        /// 組み立てる（レベルが浅い見出しほど上位、直前に出てきたそれより浅いレベルの見出しの
        /// 子になる）。同じ見出し（Paragraphが変わっていないもの）については、作り直す前の
        /// 展開状態（IsExpanded）と検索一致の強調表示（IsSearchMatch）をそのまま引き継ぐ。
        ///
        /// IsSearchMatchも引き継ぐ必要があるのは、EditorTextChanged（ScheduleOutlineRefresh）が
        /// プログラム側の変更（LoadFileでのファイル読み込みなど）でも発生してしまい、「すべて
        /// 検索」等でIsSearchMatchを立てた直後に、それとは無関係な理由でRefresh()が再度呼ばれて
        /// 一覧を作り直してしまうことがあるため。以前はIsExpandedしか引き継いでいなかったため、
        /// このタイミングでIsSearchMatchが失われ、検索結果のアウトラインでの強調表示（黄色）が
        /// 表示されなくなる不具合があった。</summary>
        public void Refresh()
        {
            var expandedStateByTarget = new Dictionary<Paragraph, bool>();
            var searchMatchStateByTarget = new Dictionary<Paragraph, bool>();
            CollectPreservedState(Items, expandedStateByTarget, searchMatchStateByTarget);

            var flatEntries = new List<OutlineEntry>();
            foreach (Block block in m_editor.Document.Blocks)
            {
                if (block is Paragraph p && p.Tag is int level && level > 0)
                {
                    string text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                    if (0 == text.Length)
                    {
                        text = "(無題)";
                    }
                    var entry = new OutlineEntry { Level = level, Text = text, Target = p };
                    if (expandedStateByTarget.TryGetValue(p, out bool prevExpandedFlg))
                    {
                        entry.IsExpanded = prevExpandedFlg;
                    }
                    if (searchMatchStateByTarget.TryGetValue(p, out bool prevSearchMatchFlg))
                    {
                        entry.IsSearchMatch = prevSearchMatchFlg;
                    }
                    flatEntries.Add(entry);
                }
            }

            Items.Clear();
            // 直前までに見つかった、自分より浅いレベルの見出しを祖先の連なりとして保持し、
            // 自分と同じか浅いレベルの見出しが現れた時点でそこまでを祖先から外す（一般的な
            // 見出しレベルの入れ子ルール。例えばh1の次にh3が来た場合、間にh2が無くても
            // そのままh1の子になる）。
            var ancestorStack = new List<OutlineEntry>();
            foreach (var entry in flatEntries)
            {
                while (ancestorStack.Count > 0 && ancestorStack[ancestorStack.Count - 1].Level >= entry.Level)
                {
                    ancestorStack.RemoveAt(ancestorStack.Count - 1);
                }
                if (0 == ancestorStack.Count)
                {
                    Items.Add(entry);
                }
                else
                {
                    ancestorStack[ancestorStack.Count - 1].Children.Add(entry);
                }
                ancestorStack.Add(entry);
            }

            // 祖先への強調伝播（PropagateSearchMatchToAncestors）で立てたIsSearchMatchは、上の
            // 引き継ぎだけでは（祖先側のTargetにも別途記録されているため理屈上は引き継がれるが、
            // 念のため）再構築後の親子関係に対して確実に再計算しておく。
            PropagateSearchMatchToAncestors(Items);
        }

        /// <summary>ツリー全体を走査し、各見出しの展開状態（IsExpanded）・検索一致の強調表示
        /// （IsSearchMatch）を、対応する段落（Target）をキーにして集める。Refreshで一覧を
        /// 作り直す前に呼び、作り直した後の同じ見出しへ状態を引き継ぐために使う。</summary>
        private void CollectPreservedState(
            IEnumerable<OutlineEntry> a_entries,
            Dictionary<Paragraph, bool> a_expandedResult,
            Dictionary<Paragraph, bool> a_searchMatchResult)
        {
            foreach (var entry in a_entries)
            {
                if (null != entry.Target)
                {
                    a_expandedResult[entry.Target] = entry.IsExpanded;
                    a_searchMatchResult[entry.Target] = entry.IsSearchMatch;
                }
                CollectPreservedState(entry.Children, a_expandedResult, a_searchMatchResult);
            }
        }

        /// <summary>ツリー全体から、指定した段落（Target）に対応する項目を探す。</summary>
        /// <param name="a_entries">探索対象の一覧（このレベルの兄弟項目）。</param>
        /// <param name="a_target">探したい対応段落。</param>
        /// <returns>見つかった項目。見つからなければnull。</returns>
        private OutlineEntry FindEntryByTarget(IEnumerable<OutlineEntry> a_entries, Paragraph a_target)
        {
            foreach (var entry in a_entries)
            {
                if (entry.Target == a_target)
                {
                    return entry;
                }
                var found = FindEntryByTarget(entry.Children, a_target);
                if (null != found)
                {
                    return found;
                }
            }
            return null;
        }

        /// <summary>指定した文書内の位置より手前にある、一番近い見出しの段落を探す
        /// （SelectHeadingForPosition・MarkSearchMatchesで共通に使う）。</summary>
        /// <param name="a_position">対象の文書内の位置。</param>
        /// <returns>見つかった見出しの段落。見つからなければnull。</returns>
        private Paragraph FindNearestHeadingBefore(TextPointer a_position)
        {
            Paragraph nearestHeading = null;
            foreach (Block block in m_editor.Document.Blocks)
            {
                if (block is Paragraph p && p.Tag is int level && level > 0)
                {
                    if (p.ContentStart.CompareTo(a_position) <= 0)
                    {
                        nearestHeading = p;
                    }
                    else
                    {
                        break; // ブロックは文書順に並んでいるので、超えた時点で打ち切ってよい
                    }
                }
            }
            return nearestHeading;
        }

        /// <summary>
        /// 検索で見つかった一致箇所の一覧を受け取り、それぞれが属する見出しの区間（その一致箇所
        /// より手前にある、一番近い見出し）を強調表示する。呼び出し前の強調表示はクリアされる。
        /// </summary>
        /// <param name="a_matches">強調表示したい一致箇所（ライブなTextRange）。</param>
        /// <summary>ある段落の位置に対応する見出し項目が見つかり、選択すべき時に発火する。
        /// MainWindow側で、アウトラインペインの選択状態・スクロール位置に反映するために使う。</summary>
        public event Action<OutlineEntry> HeadingSelected;

        /// <summary>
        /// 指定した文書内の位置が属する見出し（その位置より手前にある、一番近い見出し）を探し、
        /// 見つかればHeadingSelectedイベントで通知する。検索結果へのジャンプなど、エディタ内の
        /// 特定の位置へ移動した時に、アウトラインペイン側の選択状態も追従させるために使う。
        /// </summary>
        /// <param name="a_position">対象の文書内の位置。</param>
        public void SelectHeadingForPosition(TextPointer a_position)
        {
            Paragraph nearestHeading = FindNearestHeadingBefore(a_position);
            if (null == nearestHeading)
            {
                return;
            }
            var entry = FindEntryByTarget(Items, nearestHeading);
            if (null != entry)
            {
                HeadingSelected?.Invoke(entry);
            }
        }

        /// <summary>検索で見つかった一致箇所の一覧を受け取り、それぞれが属する見出しの区間を
        /// 強調表示する。フォルダツリーペインと同じく、一致箇所を含む見出しが折りたたまれた
        /// 祖先見出しの中にある場合、その祖先見出しも強調する（展開しなくても、内部に一致箇所が
        /// あることが分かるようにするため）。</summary>
        /// <param name="a_matches">強調表示したい一致箇所（ライブなTextRange）。</param>
        public void MarkSearchMatches(IEnumerable<TextRange> a_matches)
        {
            ClearSearchMatches();
            foreach (var range in a_matches)
            {
                Paragraph nearestHeading = FindNearestHeadingBefore(range.Start);
                if (null == nearestHeading)
                {
                    continue;
                }
                var entry = FindEntryByTarget(Items, nearestHeading);
                if (null != entry)
                {
                    entry.IsSearchMatch = true;
                }
            }
            PropagateSearchMatchToAncestors(Items);
        }

        /// <summary>子孫のいずれかにIsSearchMatchが立っている見出しへ、その祖先見出しにも
        /// IsSearchMatchを伝播させる（フォルダツリーペインのMarkSearchMatchRecursiveと同じ
        /// 考え方）。</summary>
        /// <param name="a_entries">走査対象の一覧（このレベルの兄弟項目）。</param>
        /// <returns>この一覧の中に、自分自身または子孫にIsSearchMatchが立っている項目が
        /// 1つ以上あればtrue。</returns>
        private bool PropagateSearchMatchToAncestors(IEnumerable<OutlineEntry> a_entries)
        {
            bool anyMatchFlg = false;
            foreach (var entry in a_entries)
            {
                if (PropagateSearchMatchToAncestors(entry.Children))
                {
                    entry.IsSearchMatch = true;
                }
                if (entry.IsSearchMatch)
                {
                    anyMatchFlg = true;
                }
            }
            return anyMatchFlg;
        }

        /// <summary>検索結果の強調表示をすべて解除する。</summary>
        public void ClearSearchMatches()
        {
            ClearSearchMatchesRecursive(Items);
        }

        private void ClearSearchMatchesRecursive(IEnumerable<OutlineEntry> a_entries)
        {
            foreach (var entry in a_entries)
            {
                entry.IsSearchMatch = false;
                ClearSearchMatchesRecursive(entry.Children);
            }
        }

        /// <summary>
        /// プログラム側から（検索結果に合わせてなど）アウトラインペインのSelectedItemを
        /// 変更する時に立てるフラグ。SelectionChangedイベントは、ユーザーがクリックした時と
        /// プログラム側から変更した時の両方で発生するため、このフラグでユーザー操作かどうかを
        /// 区別し、プログラム側からの変更ではエディタのキャレット・スクロールを動かさない
        /// ようにする（動かしてしまうと、検索結果へジャンプした直後にキャレットが見出しの
        /// 先頭へ引き戻されてしまう）。
        /// </summary>
        private bool m_suppressSelectionNavigationFlg;

        /// <summary>
        /// 次に発生するSelectionChangedイベントで、エディタのキャレット・スクロールを
        /// 動かさないようにする。ScrollOutlineListToEntryなど、プログラム側からSelectedItemを
        /// 変更する直前に呼ぶこと。
        /// </summary>
        public void SuppressNextSelectionNavigation()
        {
            m_suppressSelectionNavigationFlg = true;
        }

        /// <summary>アウトラインペインで見出しがクリックされた時に、エディタをその見出しまで
        /// スクロールする。フォルダツリーペインのHandleSelectedItemChangedと同じく、TreeViewの
        /// SelectedItemChangedイベント（新しく選択された項目はa_args.NewValue）を受け取る。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleSelectionChanged(object a_sender, RoutedPropertyChangedEventArgs<object> a_args)
        {
            if (m_suppressSelectionNavigationFlg)
            {
                m_suppressSelectionNavigationFlg = false;
                return;
            }
            if (a_args.NewValue is OutlineEntry entry && null != entry.Target)
            {
                Paragraph target = entry.Target;
                // ここでm_editor.Focus()を同期的に（SelectedItemChangedの中で即座に）呼ぶと、
                // クリックされたTreeViewItem自身がまだ「選択状態にする・自分にフォーカスを
                // 当てる」という処理を完了しきっていない同じタイミングでキーボードフォーカスを
                // 強制的に奪ってしまい、TreeViewItem側の選択確定処理と競合する。この競合により、
                // 1回目のクリックでは項目が選択状態にならず、2回目のクリック（＝実質的に
                // ダブルクリック）でようやく選択できる、という不具合が発生する（フォルダペインの
                // LoadFileはこのFocus()呼び出しを行っていないため、この問題が起きない）。
                // クリックの入力処理が完全に終わった直後まで遅延させることで、この競合を避ける。
                m_editor.Dispatcher.BeginInvoke(new Action(() =>
                {
                    m_editor.CaretPosition = target.ContentStart;
                    ScrollParagraphToTop(target, m_editor);
                    m_editor.Focus();
                }), System.Windows.Threading.DispatcherPriority.Input);
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
                if (child is T match)
                {
                    return match;
                }
                var found = FindVisualChild<T>(child);
                if (null != found)
                {
                    return found;
                }
            }
            return null;
        }
    }
}
