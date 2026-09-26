// OutlineManager.cs
//
// mde (Markdown インラインエディタ) の一部。
// アウトラインペイン（見出し一覧）を担当するクラス。文書から見出しを収集して一覧を作り、
// クリックされた見出しまでエディタをスクロールする。あわせて、アウトラインペインの
// TreeView自体の上での選択項目の可視化・スクロール（横スクロール位置の補正を含む）も扱う。
// ペインの表示/非表示切り替え（幅の記憶・GridColumn操作）は、エディタペインのスクロール
// 位置保持処理と密接に絡んでいるため、引き続きMainWindow側に残している。

using mde.common;
using mde.logger;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace mde.manager
{
    /// <summary>アウトラインペインの一覧構築と、見出しクリック時のジャンプを担当する。</summary>
    public class OutlineManager
    {
        private readonly RichTextBox m_editor;
        private readonly TextBox m_sourceEditor;
        private readonly Func<bool> m_isSourceModeFunc;
        private readonly Func<FlowDocument, Block, int> m_getMarkdownOffsetForBlockFunc;
        private readonly TreeView m_outlineTree;

        /// <summary>アウトラインペイン内部のScrollViewerへの参照（選択項目切り替え後の横スクロール
        /// リセットに使う）。HandleOutlineTreeLoadedで取得する。</summary>
        private ScrollViewer m_outlineTreeScrollViewer;

        /// <summary>HandleOutlineTreeItemRequestBringIntoViewの再入防止フラグ。詳細は同メソッドの
        /// コメントを参照。</summary>
        private bool m_suppressOutlineBringIntoViewFixupFlg;

        /// <summary>アウトラインペインの一覧（ListBox.ItemsSourceとして使う）。</summary>
        public ObservableCollection<OutlineEntry> Items { get; } = new ObservableCollection<OutlineEntry>();

        /// <summary>
        /// OutlineManagerを構築する。
        /// </summary>
        /// <param name="a_editor">対象のRichTextBox（Markdownモード）。</param>
        /// <param name="a_sourceEditor">対象のTextBox（ソースモード）。ソースモード中に見出しを
        /// クリックした時、Markdown側ではなくこちらへジャンプするために使う。</param>
        /// <param name="a_isSourceModeFunc">現在ソースモードかどうかを返す関数。</param>
        /// <param name="a_getMarkdownOffsetForBlockFunc">MarkdownConverter.GetMarkdownOffset
        /// ForBlockへの参照。ソースモード中に見出しをクリックした時、その見出しに対応する
        /// ソース文字列中のオフセットを求めるために使う。</param>
        /// <param name="a_outlineTree">アウトラインペインのTreeViewコントロール（一度構築されたら
        /// 差し替わらない、XAML上のコントロールそのもの）。</param>
        public OutlineManager(RichTextBox a_editor, TextBox a_sourceEditor, Func<bool> a_isSourceModeFunc,
            Func<FlowDocument, Block, int> a_getMarkdownOffsetForBlockFunc, TreeView a_outlineTree)
        {
            this.m_editor = a_editor;
            this.m_sourceEditor = a_sourceEditor;
            this.m_isSourceModeFunc = a_isSourceModeFunc;
            this.m_getMarkdownOffsetForBlockFunc = a_getMarkdownOffsetForBlockFunc;
            this.m_outlineTree = a_outlineTree;
        }

        /// <summary>現在の文書から見出しを収集し、一覧を作り直す。フォルダツリーペインと同じ
        /// 折りたたみ可能なツリー構造にするため、見出しレベルに応じた入れ子（親子関係）を
        /// 組み立てる（レベルが浅い見出しほど上位）。同じ見出し（Paragraphが同一）については、
        /// 作り直す前の展開状態（IsExpanded）を引き継ぐ。</summary>
        public void Refresh()
        {
            var expandedStateByTarget = new Dictionary<Paragraph, bool>();
            CollectPreservedState(Items, expandedStateByTarget);

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
        }

        /// <summary>ツリー全体を走査し、各見出しの展開状態（IsExpanded）を、
        /// 対応する段落（Target）をキーにして集める。Refreshで一覧を
        /// 作り直す前に呼び、作り直した後の同じ見出しへ状態を引き継ぐために使う。</summary>
        private void CollectPreservedState(
            IEnumerable<OutlineEntry> a_entries,
            Dictionary<Paragraph, bool> a_expandedResult)
        {
            foreach (var entry in a_entries)
            {
                if (null != entry.Target)
                {
                    a_expandedResult[entry.Target] = entry.IsExpanded;
                }
                CollectPreservedState(entry.Children, a_expandedResult);
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

        /// <summary>
        /// プログラム側からアウトラインペインのSelectedItemを
        /// 変更する時に立てるフラグ。SelectionChangedはユーザークリックとプログラム変更の
        /// 両方で発生するため、このフラグで区別し、プログラム側からの変更ではエディタの
        /// キャレット・スクロールを動かさないようにする。
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
        /// SelectedItemChangedイベント（新しく選択された項目はa_args.NewValue）を受け取る。あわせて、
        /// 選択項目切り替え時にWPF標準の動作で横スクロール位置がずれてしまうのを毎回0へ戻す。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleSelectionChanged(object a_sender, RoutedPropertyChangedEventArgs<object> a_args)
        {
            if (m_suppressSelectionNavigationFlg)
            {
                m_suppressSelectionNavigationFlg = false;
            }
            else if (a_args.NewValue is OutlineEntry entry && null != entry.Target)
            {
                Paragraph target = entry.Target;
                // Focus()をSelectedItemChanged内で同期的に呼ぶと、TreeViewItem自身の選択確定
                // 処理とキーボードフォーカスの奪い合いが起き、1回目のクリックでは選択されず
                // 2回目（実質ダブルクリック）でようやく選択される不具合が起きる（フォルダ
                // ペインのLoadFileはFocus()を呼ばないためこの問題が起きない）。クリックの
                // 入力処理が完全に終わった直後まで遅延させることでこの競合を避ける。
                m_editor.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // ソースモード中はMarkdown側（非表示）のm_editorを操作しても見た目に
                    // 反映されないため、表示中のソースエディタ側で対応する行へジャンプする
                    // （モードの切り替えは行わない）。targetの対応関係はアウトライン一覧が
                    // m_editor.Documentから作られているため保たれているが、ソースモード中は
                    // アウトラインが自動更新されないため、その間の編集量によっては多少ずれる
                    // ことがある。
                    if (m_isSourceModeFunc())
                    {
                        int offset = m_getMarkdownOffsetForBlockFunc(m_editor.Document, target);
                        DebugLogger.Log($"OutlineManager.HandleSelectionChanged(source): offset={offset} textLen={m_sourceEditor.Text.Length}");
                        if (offset >= 0 && offset <= m_sourceEditor.Text.Length)
                        {
                            // CaretIndexを先に確定させてから行番号を計算する（ToggleModeBtnClick
                            // の→Source方向と同じ順序。詳しい理由はそちらのコメント参照）。
                            m_sourceEditor.CaretIndex = offset;
                            int line = m_sourceEditor.GetLineIndexFromCharacterIndex(offset);
                            DebugLogger.Log($"OutlineManager.HandleSelectionChanged(source): line={line} caretIndex={m_sourceEditor.CaretIndex}");
                            if (line >= 0)
                            {
                                // ScrollToLineは既に見えている行には最小限しか動かない仕様のため、
                                // 先に先頭へリセットしてから対象行へスクロールする（他のスクロール
                                // 処理と同じ対策）。間にUpdateLayout()を挟み、リセットを確実に
                                // 反映させてから判定させる（ToggleModeBtnClickと同じ対策）。
                                m_sourceEditor.ScrollToLine(0);
                                m_sourceEditor.UpdateLayout();
                                m_sourceEditor.ScrollToLine(line);
                            }
                        }
                        m_sourceEditor.Focus();
                        return;
                    }

                    m_editor.CaretPosition = target.ContentStart;
                    ScrollParagraphToTop(target, m_editor);
                    m_editor.Focus();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }

            // 選択項目が切り替わると、WPF標準の動作でその項目を横方向にも完全に見えるよう
            // スクロールしてしまい、見出しが長い場合に横スクロールバーが右へずれてしまう。
            // このレイアウトパスが終わった直後（Loaded優先度）に横スクロールだけを0へ戻すことで、
            // それ以外のタイミングでのユーザーによる手動スクロールには一切影響しないようにする。
            if (null != m_outlineTreeScrollViewer)
            {
                m_outlineTree.Dispatcher.BeginInvoke(new Action(() => m_outlineTreeScrollViewer.ScrollToHorizontalOffset(0)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>アウトラインペイン内部のScrollViewerへの参照を取得しておく（選択項目切り替え後の
        /// 横スクロールリセットに使う）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleOutlineTreeLoaded(object a_sender, RoutedEventArgs a_args)
        {
            m_outlineTreeScrollViewer = FindVisualChild<ScrollViewer>(m_outlineTree);
        }

        /// <summary>
        /// アウトラインのTreeViewItemは、選択・フォーカス取得のたびに既定の動作として自分自身を
        /// 横方向も含めて完全に見えるようスクロールしようとする（RequestBringIntoViewイベント）。
        /// 見出しは省略せず横スクロールで読む作りのため、長い見出しを選択するたびにWPFが横
        /// スクロールバーを右へ動かしてしまう。対策として、要求された範囲（TargetRect）を
        /// 「幅0（項目の左端）・高さは項目の高さのまま」の矩形に置き換えて改めてBringIntoView
        /// し直すことで、縦方向の可視化は保ちつつ横スクロール位置には触れさせないようにする。
        ///
        /// 再入判定にはTargetRect.Widthではなく専用のフラグを使う：WPF内部が呼ぶ既定の
        /// BringIntoView()（引数なし）はTargetRectとしてRect.Empty（Width/HeightがともにNegative
        /// Infinity）を渡してくるため、Width&lt;=0での判定だとこの既定呼び出し自体もスキップして
        /// しまい対策が効かない。
        /// </summary>
        /// <param name="a_sender">イベントの発生元（対象のTreeViewItem）。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleOutlineTreeItemRequestBringIntoView(object a_sender, RequestBringIntoViewEventArgs a_args)
        {
            if (m_suppressOutlineBringIntoViewFixupFlg)
            {
                return; // このメソッド自身が下で発行した再要求。無限ループを避けるため何もしない。
            }
            if (a_sender is FrameworkElement fe)
            {
                a_args.Handled = true;
                m_suppressOutlineBringIntoViewFixupFlg = true;
                try
                {
                    fe.BringIntoView(new Rect(0, 0, 0, fe.ActualHeight));
                }
                finally
                {
                    m_suppressOutlineBringIntoViewFixupFlg = false;
                }
            }
        }

        /// <summary>
        /// アウトラインペインで、指定した見出し項目を選択状態にし、見えていなければ見える位置まで
        /// スクロールする。フォルダペインのScrollFolderTreeToNodeと同じ考え方：TreeView.
        /// SelectedItemは読み取り専用のため、データ側のIsSelectedを立てたうえで、対応する
        /// TreeViewItem（表示上のコンテナ）をルートから順にたどって探し、BringIntoViewする。
        /// エディタ側の処理と干渉しないよう、アプリケーションが完全にアイドル状態
        /// （ApplicationIdle優先度）になってから実行する。
        /// </summary>
        /// <param name="a_entry">選択したい見出し項目。</param>
        private void ScrollOutlineTreeToEntry(OutlineEntry a_entry)
        {
            m_outlineTree.Dispatcher.BeginInvoke(new Action(() =>
            {
                var path = FindOutlinePathToItem(Items, a_entry);
                if (null == path)
                {
                    return;
                }
                // 対象の見出しが、折りたたまれた祖先見出しの中にある場合、隠れて見えなくならない
                // よう、経路上の祖先をすべて展開状態にする（フォルダペインのSelectFileNodeRecursive
                // が経路上のフォルダをIsExpanded=trueにするのと同じ考え方）。
                for (int i = 0; i < path.Count - 1; i++)
                {
                    path[i].IsExpanded = true;
                }
                // IsSelectedを立てると、ユーザーが手でクリックした時と同じSelectedItemChanged
                // イベントが発生し、エディタのキャレットが見出しの先頭へ動いてしまう。ここでは
                // あくまで「今どこにいるか」を表示に反映したいだけなので、そのナビゲーションを
                // 抑止しておく。
                SuppressNextSelectionNavigation();
                a_entry.IsSelected = true;
                NavigateToOutlineTreeViewItem(m_outlineTree, path, 0, 0);
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>アウトラインペインのTreeViewで、ルートから対象までの経路を1階層ずつたどりながら、
        /// 対応するTreeViewItem（表示上のコンテナ）を探す。フォルダペインのNavigateToTreeViewItemと
        /// 同じ考え方（展開直後はまだコンテナが生成されていないことがあるため、生成されるまで
        /// 待って再試行する）。</summary>
        /// <param name="a_current">現在の階層のItemsControl（TreeViewまたはTreeViewItem）。</param>
        /// <param name="a_path">ルートから対象までの経路。</param>
        /// <param name="a_index">現在探している経路上のインデックス。</param>
        /// <param name="a_retryCount">この階層での再試行回数（無限ループ防止用）。</param>
        private void NavigateToOutlineTreeViewItem(ItemsControl a_current, List<OutlineEntry> a_path, int a_index, int a_retryCount)
        {
            if (a_retryCount > 20)
            {
                return; // 想定外の状況が続く場合は諦める（無限ループ防止）
            }

            var container = a_current.ItemContainerGenerator.ContainerFromItem(a_path[a_index]) as TreeViewItem;
            if (null == container)
            {
                // まだこの階層のコンテナが生成されていない。少し待って再試行する。
                m_outlineTree.Dispatcher.BeginInvoke(new Action(() =>
                    NavigateToOutlineTreeViewItem(a_current, a_path, a_index, a_retryCount + 1)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                return;
            }

            if (a_index == a_path.Count - 1)
            {
                container.BringIntoView();
                // BringIntoViewが対象を横方向にも完全に見せようとして、横スクロールバーを
                // 右へずらしてしまうことがあるため、その後に横スクロールだけを0へ戻す
                // （フォルダペインのNavigateToTreeViewItemと同じ対策）。
                if (null != m_outlineTreeScrollViewer)
                {
                    m_outlineTree.Dispatcher.BeginInvoke(new Action(() =>
                        m_outlineTreeScrollViewer.ScrollToHorizontalOffset(0)),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                return;
            }

            // 次の階層（この項目の子）が展開・生成されるのを待ってから進む。
            m_outlineTree.Dispatcher.BeginInvoke(new Action(() =>
                NavigateToOutlineTreeViewItem(container, a_path, a_index + 1, 0)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>アウトラインのツリーで、ルートから対象のデータ項目までの経路（祖先を含む
        /// 一覧）を探す。フォルダペインのFindPathToItemと同じ考え方。</summary>
        /// <param name="a_items">探索対象の一覧（このレベルの兄弟項目）。</param>
        /// <param name="a_target">探したいデータ項目。</param>
        /// <returns>ルートから対象までの経路。見つからなければnull。</returns>
        private List<OutlineEntry> FindOutlinePathToItem(IEnumerable<OutlineEntry> a_items, OutlineEntry a_target)
        {
            foreach (var item in a_items)
            {
                if (item == a_target)
                {
                    return new List<OutlineEntry> { item };
                }
                var subPath = FindOutlinePathToItem(item.Children, a_target);
                if (null != subPath)
                {
                    subPath.Insert(0, item);
                    return subPath;
                }
            }
            return null;
        }

        /// <summary>
        /// 指定した段落が表示領域の一番上に見えるようスクロールする。標準のBringIntoViewは
        /// 既に見えている場合は何もしないため、常に一番上へ移動するとは限らない。
        /// </summary>
        /// <param name="a_p">スクロール先の段落。</param>
        /// <param name="a_editor">対象のRichTextBox。</param>
        public static void ScrollParagraphToTop(Paragraph a_p, RichTextBox a_editor)
        {
            // 既に見えている場合は移動しない、というBringIntoViewの仕様のトレードオフを受け入れている。
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
