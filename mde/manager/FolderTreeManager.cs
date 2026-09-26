// FolderTreeManager.cs
//
// mde (Markdown インラインエディタ) の一部。
// フォルダツリーペインを担当するクラス。フォルダの読み込み、子ノードの遅延読み込み、
// 未保存マーカーの更新、ファイルノードのクリックでのファイルオープン、選択項目の
// TreeView上でのスクロール・可視化（横スクロール位置の補正を含む）を扱う。
// ペインの表示/非表示切り替え（幅の記憶・GridColumn操作）は、エディタペインの
// スクロール位置保持処理と密接に絡んでいるため、引き続きMainWindow側に残している。

using mde.common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace mde.manager
{
    /// <summary>フォルダツリーペインの読み込み・表示更新・ファイルオープンを担当する。</summary>
    public class FolderTreeManager
    {
        private readonly Action<string> m_loadFile;
        private readonly Func<string> m_getCurrentFilePath;
        private readonly Func<bool> m_getCurrentFileIsDirty;
        private readonly Func<IEnumerable<string>> m_getPendingEditPaths;
        private readonly Func<string, string, bool> m_pathsReferToSameFile;
        private readonly TreeView m_folderTree;
        private readonly Action m_discardCurrentDocumentSilently;

        /// <summary>フォルダツリー内部のScrollViewerへの参照（選択項目切り替え後の横スクロール
        /// リセットに使う）。HandleFolderTreeLoadedで取得する。</summary>
        private ScrollViewer m_folderTreeScrollViewer;

        /// <summary>FolderTreeItemRequestBringIntoViewの再入防止フラグ。詳細は同メソッドの
        /// コメントを参照。</summary>
        private bool m_suppressFolderBringIntoViewFixupFlg;

        /// <summary>現在読み込んでいるフォルダのルートパス。未読み込みなら null。</summary>
        public string LoadedFolderRootPath { get; private set; }

        /// <summary>フォルダツリーペインのルートノード一覧（TreeView.ItemsSourceとして使う）。</summary>
        public ObservableCollection<FileSystemItem> Roots { get; } = new ObservableCollection<FileSystemItem>();

        /// <summary>
        /// FolderTreeManagerを構築する。
        /// </summary>
        /// <param name="a_loadFile">ファイルを開くdelegate。</param>
        /// <param name="a_getCurrentFilePath">現在開いているファイルのパスを返すdelegate。</param>
        /// <param name="a_getCurrentFileIsDirty">現在のファイルに未保存の変更があるかを返すdelegate。</param>
        /// <param name="a_getPendingEditPaths">保留中の編集があるファイルパス一覧を返すdelegate。</param>
        /// <param name="a_pathsReferToSameFile">2つのパスが同一ファイルを指すかを判定するdelegate。</param>
        /// <param name="a_folderTree">フォルダツリーペインのTreeViewコントロール（一度構築されたら
        /// 差し替わらない、XAML上のコントロールそのもの）。</param>
        /// <param name="a_discardCurrentDocumentSilently">別のフォルダを開く前に、現在の文書・
        /// 保留中の編集を静かに破棄するdelegate。</param>
        public FolderTreeManager(
            Action<string> a_loadFile,
            Func<string> a_getCurrentFilePath,
            Func<bool> a_getCurrentFileIsDirty,
            Func<IEnumerable<string>> a_getPendingEditPaths,
            Func<string, string, bool> a_pathsReferToSameFile,
            TreeView a_folderTree,
            Action a_discardCurrentDocumentSilently)
        {
            this.m_loadFile = a_loadFile;
            this.m_getCurrentFilePath = a_getCurrentFilePath;
            this.m_getCurrentFileIsDirty = a_getCurrentFileIsDirty;
            this.m_getPendingEditPaths = a_getPendingEditPaths;
            this.m_pathsReferToSameFile = a_pathsReferToSameFile;
            this.m_folderTree = a_folderTree;
            this.m_discardCurrentDocumentSilently = a_discardCurrentDocumentSilently;
        }

        /// <summary>現在開いているファイルの、読み込んでいるフォルダからの相対パス
        /// （例: "sub\file.md"）を返す。ファイルが開かれていない、またはフォルダの外にある
        /// 場合は null。</summary>
        /// <returns>現在のファイルの、読み込んでいるフォルダからの相対パス。フォルダの外にある場合はnull。</returns>
        public string GetCurrentFileRelativePath()
        {
            string currentFilePath = m_getCurrentFilePath();
            if (string.IsNullOrEmpty(currentFilePath) || string.IsNullOrEmpty(LoadedFolderRootPath))
            {
                return null;
            }
            try
            {
                string root = Path.GetFullPath(LoadedFolderRootPath).TrimEnd(Path.DirectorySeparatorChar);
                string file = Path.GetFullPath(currentFilePath);
                if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return file.Substring(root.Length + 1);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>新しく読み込んだフォルダの中に、同じ相対パスのファイルがあればそれを開き、
        /// なければフォルダの最初のファイルを開く。</summary>
        /// <param name="a_newFolderPath">新しく読み込むフォルダ。</param>
        /// <param name="a_relativePath">以前開いていたファイルの相対パス。</param>
        public void OpenMatchingOrFirstFile(string a_newFolderPath, string a_relativePath)
        {
            if (!string.IsNullOrEmpty(a_relativePath))
            {
                try
                {
                    string candidate = Path.Combine(a_newFolderPath, a_relativePath);
                    if (File.Exists(candidate))
                    {
                        m_loadFile(candidate);
                        return;
                    }
                }
                catch
                {
                    // 見つからなければ最初のファイルを開く処理へフォールバックする
                }
            }
            OpenFirstFileInLoadedFolder();
        }

        /// <summary>現在読み込んでいるフォルダツリーのルート直下にある最初のファイル
        /// （サブフォルダは除く）を開く。</summary>
        public void OpenFirstFileInLoadedFolder()
        {
            if (0 == Roots.Count)
            {
                return;
            }
            var firstFile = Roots[0].Children.FirstOrDefault(c => !c.IsDirectory);
            if (null != firstFile)
            {
                m_loadFile(firstFile.FullPath);
            }
        }

        /// <summary>ルートフォルダを指定して、フォルダツリーペインの内容を読み込む。</summary>
        /// <param name="a_folderPath">読み込むフォルダ。</param>
        public void LoadFolderTree(string a_folderPath)
        {
            LoadedFolderRootPath = a_folderPath;
            Roots.Clear();
            try
            {
                var root = BuildFileSystemNode(a_folderPath, true);
                root.Children.Clear();
                PopulateChildren(root);
                root.IsExpanded = true;
                Roots.Add(root);
                RefreshDirtyMarkers();
            }
            catch
            {
                // フォルダにアクセスできない場合は、ツリーを空のままにしておく
            }
        }

        /// <summary>
        /// フォルダツリー全体を走査し、各ファイルノードに未保存マーカーを付ける。現在開いている
        /// ファイルで未保存の変更があるものが対象。
        /// </summary>
        public void RefreshDirtyMarkers()
        {
            foreach (var root in Roots)
            {
                RefreshDirtyMarkerRecursive(root);
            }
        }

        private void RefreshDirtyMarkerRecursive(FileSystemItem a_node)
        {
            if (!a_node.IsDirectory && null != a_node.FullPath)
            {
                string currentFilePath = m_getCurrentFilePath();
                bool isCurrentFlg = !string.IsNullOrEmpty(currentFilePath) && m_pathsReferToSameFile(a_node.FullPath, currentFilePath);
                a_node.IsDirty = isCurrentFlg
                    ? m_getCurrentFileIsDirty()
                    : m_getPendingEditPaths().Any(k => m_pathsReferToSameFile(k, a_node.FullPath));
            }
            foreach (var child in a_node.Children)
            {
                RefreshDirtyMarkerRecursive(child);
            }
        }

        /// <summary>子がまだ遅延読み込みされていない（「読み込み中…」の仮ノード1件だけの）
        /// フォルダかどうか。HandleTreeViewItemExpandedの判定条件と同じ。</summary>
        private static bool IsUnloadedPlaceholder(FileSystemItem a_node) =>
            1 == a_node.Children.Count && null == a_node.Children[0].FullPath;

        /// <summary>
        /// 保存によって新しく作られたファイルを、既存のフォルダツリーへ追加する（ツリー全体の
        /// 再読み込みではなく、対応するフォルダノードへ1件だけ挿入する）。対象フォルダが
        /// 未読み込み（プレースホルダのみ）、ツリー内に見つからない、または既に同じノードが
        /// あれば何もしない（未読み込みの場合は展開時に自然に反映される）。
        /// </summary>
        /// <param name="a_filePath">追加するファイルの絶対パス。</param>
        public void AddFileNodeIfMissing(string a_filePath)
        {
            if (0 == Roots.Count || string.IsNullOrEmpty(a_filePath))
            {
                return;
            }

            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(a_filePath)); }
            catch { return; }
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            var folderNode = FindFolderNode(Roots[0], dir);
            if (null == folderNode)
            {
                return;
            }

            // まだ子が読み込まれていない（プレースホルダのみ）場合は、展開時に自然に反映される
            if (1 == folderNode.Children.Count &&
                null == folderNode.Children[0].FullPath) return;

            if (folderNode.Children.Any(c => !c.IsDirectory && PathsEqualLocal(c.FullPath, a_filePath)))
            {
                return;
            }

            string fileName = Path.GetFileName(a_filePath);
            var newItem = new FileSystemItem { Name = fileName, FullPath = a_filePath, IsDirectory = false };

            // 既存の並び（フォルダの後にファイル名順）に合わせて挿入位置を探す
            int insertIdx = folderNode.Children.Count;
            for (int i = 0; i < folderNode.Children.Count; i++)
            {
                var c = folderNode.Children[i];
                if (c.IsDirectory)
                {
                    continue;
                }
                if (string.Compare(fileName, c.Name, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    insertIdx = i;
                    break;
                }
            }
            folderNode.Children.Insert(insertIdx, newItem);
        }

        /// <summary>
        /// 指定したファイルに対応するノードを、フォルダツリーペインで選択状態にする
        /// （見つからなければ何もしない）。そのノードまでの経路にあるフォルダは、
        /// 隠れて見えなくならないよう展開状態にする。
        /// </summary>
        /// <param name="a_filePath">選択したいファイルの絶対パス。</param>
        public void SelectFileNode(string a_filePath)
        {
            foreach (var root in Roots)
            {
                var node = SelectFileNodeRecursive(root, a_filePath);
                if (null != node)
                {
                    ScrollFolderTreeToNode(node);
                    return;
                }
            }
        }

        private FileSystemItem SelectFileNodeRecursive(FileSystemItem a_node, string a_filePath)
        {
            foreach (var child in a_node.Children)
            {
                if (!child.IsDirectory && PathsEqualLocal(child.FullPath, a_filePath))
                {
                    child.IsSelected = true;
                    a_node.IsExpanded = true;
                    return child;
                }
                if (child.IsDirectory)
                {
                    var found = SelectFileNodeRecursive(child, a_filePath);
                    if (null != found)
                    {
                        a_node.IsExpanded = true;
                        return found;
                    }
                }
            }
            return null;
        }

        private FileSystemItem FindFolderNode(FileSystemItem a_node, string a_dir)
        {
            if (a_node.IsDirectory && PathsEqualLocal(a_node.FullPath, a_dir))
            {
                return a_node;
            }
            foreach (var child in a_node.Children)
            {
                if (!child.IsDirectory)
                {
                    continue;
                }
                var found = FindFolderNode(child, a_dir);
                if (null != found)
                {
                    return found;
                }
            }
            return null;
        }

        private bool PathsEqualLocal(string a_a, string a_b)
        {
            if (string.IsNullOrEmpty(a_a) || string.IsNullOrEmpty(a_b))
            {
                return false;
            }
            try
            {
                return string.Equals(
                    Path.GetFullPath(a_a).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(a_b).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 'a_dir' が現在フォルダペインに表示されているフォルダ自身、またはそのサブフォルダで
        /// あれば true を返す。true なら、同じ範囲内のファイルを開いただけではツリーを
        /// 作り直す（展開状態を失わせる）必要はない。
        /// </summary>
        /// <param name="a_dir">判定対象のディレクトリパス。</param>
        /// <returns>指定フォルダが読み込み済みフォルダの範囲内であればtrue。</returns>
        public bool IsWithinLoadedFolder(string a_dir)
        {
            if (string.IsNullOrEmpty(LoadedFolderRootPath) || string.IsNullOrEmpty(a_dir))
            {
                return false;
            }
            try
            {
                string root = Path.GetFullPath(LoadedFolderRootPath).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
                string target = Path.GetFullPath(a_dir).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
                return target == root || target.StartsWith(root + Path.DirectorySeparatorChar);
            }
            catch
            {
                return false;
            }
        }

        private FileSystemItem BuildFileSystemNode(string a_path, bool a_isDirectoryFlg)
        {
            var item = new FileSystemItem
            {
                Name = string.IsNullOrEmpty(Path.GetFileName(a_path)) ? a_path : Path.GetFileName(a_path),
                FullPath = a_path,
                IsDirectory = a_isDirectoryFlg
            };
            if (a_isDirectoryFlg)
            {
                // 遅延読み込みする前から展開矢印が表示されるよう、仮の子ノードを1つ入れておく
                item.Children.Add(new FileSystemItem { Name = "読み込み中…", IsDirectory = false, FullPath = null });
            }
            return item;
        }

        /// <summary>
        /// Windowsエクスプローラーの「自然順ソート」（"2" が "10" より前に来る、数字部分を
        /// 数値として比較する並び順）と完全に一致させるため、エクスプローラーが内部で使う
        /// shlwapi.dll の StrCmpLogicalW をそのまま利用する。
        /// </summary>
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a_x, string a_y);

        /// <summary>ファイル名・フォルダ名を、エクスプローラーと同じ自然順で比較するコンパレータ。</summary>
        private static readonly Comparison<string> m_naturalCompare = (a_x, a_y) => StrCmpLogicalW(a_x, a_y);

        private void PopulateChildren(FileSystemItem a_node)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(a_node.FullPath).OrderBy(d => d, Comparer<string>.Create(m_naturalCompare)))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith("."))
                    {
                        continue;
                    }
                    a_node.Children.Add(BuildFileSystemNode(dir, true));
                }
                foreach (var file in Directory.GetFiles(a_node.FullPath)
                             .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f, Comparer<string>.Create(m_naturalCompare)))
                {
                    a_node.Children.Add(BuildFileSystemNode(file, false));
                }
            }
            catch
            {
                // アクセス拒否等はそのまま無視し、それまでに追加できた分だけを残す
            }
        }

        /// <summary>フォルダツリーのノードが初めて展開された時に、子ノードの遅延読み込みを行う。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleTreeViewItemExpanded(object a_sender, RoutedEventArgs a_args)
        {
            if (a_sender is TreeViewItem tvi && tvi.DataContext is FileSystemItem node && node.IsDirectory)
            {
                if (IsUnloadedPlaceholder(node))
                {
                    node.Children.Clear();
                    PopulateChildren(node);
                    RefreshDirtyMarkers();
                }
            }
        }

        /// <summary>フォルダツリーでファイルノードがクリックされたら、そのファイルを開く。あわせて、
        /// 選択項目切り替え時にWPF標準の動作で横スクロール位置がずれてしまうのを毎回0へ戻す。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleSelectedItemChanged(object a_sender, RoutedPropertyChangedEventArgs<object> a_args)
        {
            if (a_args.NewValue is FileSystemItem item && !item.IsDirectory && null != item.FullPath)
            {
                // SelectFileNodeでプログラム側からIsSelectedを立てた場合もこのイベントは
                // 発生する。既に開いているファイルなら再読み込みしない（再読み込みすると
                // エディタのスクロール・キャレット位置が先頭にリセットされてしまうため）。
                if (!m_pathsReferToSameFile(item.FullPath, m_getCurrentFilePath()))
                {
                    m_loadFile(item.FullPath);
                }
            }

            // 選択項目が切り替わると、WPF標準の動作でその項目を横方向にも完全に見えるよう
            // スクロールしてしまい、ファイル名が長い場合に横スクロールバーが右へずれてしまう。
            // このレイアウトパスが終わった直後（Loaded優先度）に横スクロールだけを0へ戻すことで、
            // それ以外のタイミングでのユーザーによる手動スクロールには一切影響しないようにする。
            if (null != m_folderTreeScrollViewer)
            {
                m_folderTree.Dispatcher.BeginInvoke(new Action(() => m_folderTreeScrollViewer.ScrollToHorizontalOffset(0)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>フォルダピッカーを表示し、選択されたフォルダをフォルダツリーペインへ
        /// 読み込む（可能であれば同じ相対パスのファイルを開いたままにする）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleOpenFolderTreeBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_getCurrentFileIsDirty() || m_getPendingEditPaths().Any())
            {
                var confirmResult = MessageBox.Show(
                    "保存されていない変更があります。破棄して別のフォルダを開きますか？",
                    "フォルダを開く", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (confirmResult != MessageBoxResult.OK)
                {
                    return;
                }
            }

            string previousRelativePath = GetCurrentFileRelativePath();

            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (true != dlg.ShowDialog())
            {
                return;
            }

            m_discardCurrentDocumentSilently();
            LoadFolderTree(dlg.FolderName);
            OpenMatchingOrFirstFile(dlg.FolderName, previousRelativePath);
        }

        /// <summary>フォルダツリー内部のScrollViewerへの参照を取得しておく（選択項目切り替え後の
        /// 横スクロールリセットに使う）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleFolderTreeLoaded(object a_sender, RoutedEventArgs a_args)
        {
            m_folderTreeScrollViewer = FindVisualChild<ScrollViewer>(m_folderTree);
        }

        /// <summary>
        /// フォルダのTreeViewItemは、選択・フォーカス取得のたびに既定の動作として自分自身を
        /// 横方向も含めて完全に見えるようスクロールしようとする（RequestBringIntoViewイベント）。
        /// ファイル名・フォルダ名は省略せず横スクロールで読む作りのため、長い名前を選択する
        /// たびにWPFが横スクロールバーを右へ動かしてしまう。対策として、要求された範囲
        /// （TargetRect）を「幅0（項目の左端）・高さは項目の高さのまま」の矩形に置き換えて
        /// 改めてBringIntoViewし直すことで、縦方向の可視化は保ちつつ横スクロール位置には
        /// 触れさせないようにする。
        ///
        /// 再入判定にはTargetRect.Widthではなく専用のフラグを使う：WPF内部が呼ぶ既定の
        /// BringIntoView()（引数なし）はTargetRectとしてRect.Empty（Width/HeightがともにNegative
        /// Infinity）を渡してくるため、Width&lt;=0での判定だとこの既定呼び出し自体もスキップして
        /// しまい対策が効かない。
        /// </summary>
        /// <param name="a_sender">イベントの発生元（対象のTreeViewItem）。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleFolderTreeItemRequestBringIntoView(object a_sender, RequestBringIntoViewEventArgs a_args)
        {
            if (m_suppressFolderBringIntoViewFixupFlg)
            {
                return; // このメソッド自身が下で発行した再要求。無限ループを避けるため何もしない。
            }
            if (a_sender is FrameworkElement fe)
            {
                a_args.Handled = true;
                m_suppressFolderBringIntoViewFixupFlg = true;
                try
                {
                    fe.BringIntoView(new Rect(0, 0, 0, fe.ActualHeight));
                }
                finally
                {
                    m_suppressFolderBringIntoViewFixupFlg = false;
                }
            }
        }

        /// <summary>
        /// 指定したファイルノードが、フォルダペイン上で見える位置になるようスクロールする
        /// （すでに見えていれば何もしない）。
        /// フォルダペインで選択状態にしたファイルへ、必要な祖先フォルダの
        /// 展開・コンテナ生成が完了してからスクロールするために、アプリケーションが完全に
        /// アイドル状態になってから処理を始める（エディタ側の移動処理と干渉しないようにするため）。
        /// </summary>
        /// <param name="a_node">選択したファイルノード。</param>
        private void ScrollFolderTreeToNode(FileSystemItem a_node)
        {
            m_folderTree.Dispatcher.BeginInvoke(new Action(() =>
            {
                var path = FindPathToItem(Roots, a_node);
                if (null == path)
                {
                    return;
                }
                NavigateToTreeViewItem(m_folderTree, path, 0, 0);
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>フォルダペインのTreeViewで、ルートから対象までの経路を1階層ずつたどりながら、
        /// 対応するTreeViewItem（表示上のコンテナ）を探す。展開直後はまだコンテナが
        /// 生成されていないことがあるため、生成されるまで待って再試行する。</summary>
        /// <param name="a_current">現在の階層のItemsControl（TreeViewまたはTreeViewItem）。</param>
        /// <param name="a_path">ルートから対象までの経路。</param>
        /// <param name="a_index">現在探している経路上のインデックス。</param>
        /// <param name="a_retryCount">この階層での再試行回数（無限ループ防止用）。</param>
        private void NavigateToTreeViewItem(ItemsControl a_current, List<FileSystemItem> a_path, int a_index, int a_retryCount)
        {
            if (a_retryCount > 20)
            {
                return; // 想定外の状況が続く場合は諦める（無限ループ防止）
            }

            var container = a_current.ItemContainerGenerator.ContainerFromItem(a_path[a_index]) as TreeViewItem;
            if (null == container)
            {
                // まだこの階層のコンテナが生成されていない。少し待って再試行する。
                m_folderTree.Dispatcher.BeginInvoke(new Action(() =>
                    NavigateToTreeViewItem(a_current, a_path, a_index, a_retryCount + 1)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                return;
            }

            if (a_index == a_path.Count - 1)
            {
                container.BringIntoView();
                // BringIntoViewが対象を横方向にも完全に見せようとして、横スクロールバーを
                // 右へずらしてしまうことがあるため、その後に横スクロールだけを0へ戻す。
                if (null != m_folderTreeScrollViewer)
                {
                    m_folderTree.Dispatcher.BeginInvoke(new Action(() =>
                        m_folderTreeScrollViewer.ScrollToHorizontalOffset(0)),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                return;
            }

            // 次の階層（この項目の子）が展開・生成されるのを待ってから進む。
            m_folderTree.Dispatcher.BeginInvoke(new Action(() =>
                NavigateToTreeViewItem(container, a_path, a_index + 1, 0)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>ツリーのルートから対象のデータ項目までの経路（祖先を含む一覧）を探す。</summary>
        /// <param name="a_items">探索対象の一覧（このレベルの兄弟項目）。</param>
        /// <param name="a_target">探したいデータ項目。</param>
        /// <returns>ルートから対象までの経路。見つからなければnull。</returns>
        private List<FileSystemItem> FindPathToItem(IEnumerable<FileSystemItem> a_items, FileSystemItem a_target)
        {
            foreach (var item in a_items)
            {
                if (item == a_target)
                {
                    return new List<FileSystemItem> { item };
                }
                var subPath = FindPathToItem(item.Children, a_target);
                if (null != subPath)
                {
                    subPath.Insert(0, item);
                    return subPath;
                }
            }
            return null;
        }

        /// <summary>指定した型の子孫要素をビジュアルツリーから探す（OutlineManagerにも同じ実装が
        /// 存在する。両者は依存関係を持たない小さな自己完結の補助関数のため、共通化せずそれぞれに
        /// 持たせている）。</summary>
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
