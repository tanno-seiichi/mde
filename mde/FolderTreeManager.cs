// FolderTreeManager.cs
//
// mde (MarkDown インラインエディタ) の一部。
// フォルダツリーペインを担当するクラス。フォルダの読み込み、子ノードの遅延読み込み、
// 未保存マーカーの更新、ファイルノードのクリックでのファイルオープンを扱う。
// ペインの表示/非表示切り替えボタンはXAML上の特定コントロールを直接操作するため、
// MainWindow側に残している。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace mde
{
    /// <summary>フォルダツリーペインの読み込み・表示更新・ファイルオープンを担当する。</summary>
    public class FolderTreeManager
    {
        private readonly Action<string> m_loadFile;
        private readonly Func<string> m_getCurrentFilePath;
        private readonly Func<bool> m_getCurrentFileIsDirty;
        private readonly Func<IEnumerable<string>> m_getPendingEditPaths;
        private readonly Func<string, string, bool> m_pathsReferToSameFile;

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
        public FolderTreeManager(
            Action<string> a_loadFile,
            Func<string> a_getCurrentFilePath,
            Func<bool> a_getCurrentFileIsDirty,
            Func<IEnumerable<string>> a_getPendingEditPaths,
            Func<string, string, bool> a_pathsReferToSameFile)
        {
            this.m_loadFile = a_loadFile;
            this.m_getCurrentFilePath = a_getCurrentFilePath;
            this.m_getCurrentFileIsDirty = a_getCurrentFileIsDirty;
            this.m_getPendingEditPaths = a_getPendingEditPaths;
            this.m_pathsReferToSameFile = a_pathsReferToSameFile;
        }

        /// <summary>現在開いているファイルの、読み込んでいるフォルダからの相対パス
        /// （例: "sub\file.md"）を返す。ファイルが開かれていない、またはフォルダの外にある
        /// 場合は null。</summary>
        public string GetCurrentFileRelativePath()
        {
            string currentFilePath = m_getCurrentFilePath();
            if (string.IsNullOrEmpty(currentFilePath) || string.IsNullOrEmpty(LoadedFolderRootPath)) return null;
            try
            {
                string root = Path.GetFullPath(LoadedFolderRootPath).TrimEnd(Path.DirectorySeparatorChar);
                string file = Path.GetFullPath(currentFilePath);
                if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
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
            if (Roots.Count == 0) return;
            var firstFile = Roots[0].Children.FirstOrDefault(c => !c.IsDirectory);
            if (firstFile != null) m_loadFile(firstFile.FullPath);
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
        /// ファイルで未保存の変更があるもの、または検索・置換による保留中の編集があるものが対象。
        /// </summary>
        public void RefreshDirtyMarkers()
        {
            foreach (var root in Roots)
                RefreshDirtyMarkerRecursive(root);
        }

        private void RefreshDirtyMarkerRecursive(FileSystemItem a_node)
        {
            if (!a_node.IsDirectory && a_node.FullPath != null)
            {
                string currentFilePath = m_getCurrentFilePath();
                bool isCurrentFlg = !string.IsNullOrEmpty(currentFilePath) && m_pathsReferToSameFile(a_node.FullPath, currentFilePath);
                a_node.IsDirty = isCurrentFlg
                    ? m_getCurrentFileIsDirty()
                    : m_getPendingEditPaths().Any(k => m_pathsReferToSameFile(k, a_node.FullPath));
            }
            foreach (var child in a_node.Children)
                RefreshDirtyMarkerRecursive(child);
        }

        /// <summary>
        /// 「すべて検索」（フォルダ全体）で一致箇所が見つかったファイルの一覧を受け取り、
        /// そのファイル自身と、それを含むフォルダを強調表示する。呼び出し前の強調表示は
        /// クリアされる。
        /// </summary>
        /// <param name="a_matchedFilePaths">一致箇所があったファイルの絶対パス一覧。</param>
        public void MarkSearchMatches(IEnumerable<string> a_matchedFilePaths)
        {
            ClearSearchMatches();
            var matchedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in a_matchedFilePaths)
            {
                try { matchedSet.Add(Path.GetFullPath(p)); }
                catch { /* 無効なパスは無視する */ }
            }
            foreach (var root in Roots)
                MarkSearchMatchRecursive(root, matchedSet);
        }

        private bool MarkSearchMatchRecursive(FileSystemItem a_node, HashSet<string> a_matchedFullPaths)
        {
            bool anyMatchBelowFlg = false;
            if (!a_node.IsDirectory && a_node.FullPath != null)
            {
                try
                {
                    if (a_matchedFullPaths.Contains(Path.GetFullPath(a_node.FullPath)))
                    {
                        a_node.IsSearchMatch = true;
                        anyMatchBelowFlg = true;
                    }
                }
                catch { /* 無効なパスは無視する */ }
            }
            foreach (var child in a_node.Children)
            {
                if (MarkSearchMatchRecursive(child, a_matchedFullPaths)) anyMatchBelowFlg = true;
            }
            if (anyMatchBelowFlg) a_node.IsSearchMatch = true; // フォルダ自身も、含むファイルに一致があれば強調する
            return anyMatchBelowFlg;
        }

        /// <summary>検索結果の強調表示をすべて解除する。</summary>
        public void ClearSearchMatches()
        {
            foreach (var root in Roots)
                ClearSearchMatchRecursive(root);
        }

        private void ClearSearchMatchRecursive(FileSystemItem a_node)
        {
            a_node.IsSearchMatch = false;
            foreach (var child in a_node.Children)
                ClearSearchMatchRecursive(child);
        }

        /// <summary>
        /// 保存によって新しく作られたファイルを、既存のフォルダツリーへ追加する（ツリー全体を
        /// 読み込み直すのではなく、対応するフォルダノードの子として1件だけ挿入する）。
        /// 対象のフォルダがまだ子を読み込んでいない（プレースホルダのみ）場合や、そもそも
        /// ツリー内に見つからない場合は何もしない（展開時、または次回のフォルダ読み込み時に
        /// 自然に反映される）。すでに同じファイルのノードがあれば何もしない。
        /// </summary>
        /// <param name="a_filePath">追加するファイルの絶対パス。</param>
        public void AddFileNodeIfMissing(string a_filePath)
        {
            if (Roots.Count == 0 || string.IsNullOrEmpty(a_filePath)) return;

            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(a_filePath)); }
            catch { return; }
            if (string.IsNullOrEmpty(dir)) return;

            var folderNode = FindFolderNode(Roots[0], dir);
            if (folderNode == null) return;

            // まだ子が読み込まれていない（プレースホルダのみ）場合は、展開時に自然に反映される
            if (folderNode.Children.Count == 1 && folderNode.Children[0].FullPath == null) return;

            if (folderNode.Children.Any(c => !c.IsDirectory && PathsEqualLocal(c.FullPath, a_filePath))) return;

            string fileName = Path.GetFileName(a_filePath);
            var newItem = new FileSystemItem { Name = fileName, FullPath = a_filePath, IsDirectory = false };

            // 既存の並び（フォルダの後にファイル名順）に合わせて挿入位置を探す
            int insertIdx = folderNode.Children.Count;
            for (int i = 0; i < folderNode.Children.Count; i++)
            {
                var c = folderNode.Children[i];
                if (c.IsDirectory) continue;
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
                if (SelectFileNodeRecursive(root, a_filePath)) return;
        }

        private bool SelectFileNodeRecursive(FileSystemItem a_node, string a_filePath)
        {
            foreach (var child in a_node.Children)
            {
                if (!child.IsDirectory && PathsEqualLocal(child.FullPath, a_filePath))
                {
                    child.IsSelected = true;
                    a_node.IsExpanded = true;
                    return true;
                }
                if (child.IsDirectory && SelectFileNodeRecursive(child, a_filePath))
                {
                    a_node.IsExpanded = true;
                    return true;
                }
            }
            return false;
        }

        private FileSystemItem FindFolderNode(FileSystemItem a_node, string a_dir)
        {
            if (a_node.IsDirectory && PathsEqualLocal(a_node.FullPath, a_dir)) return a_node;
            foreach (var child in a_node.Children)
            {
                if (!child.IsDirectory) continue;
                var found = FindFolderNode(child, a_dir);
                if (found != null) return found;
            }
            return null;
        }

        private bool PathsEqualLocal(string a_a, string a_b)
        {
            if (string.IsNullOrEmpty(a_a) || string.IsNullOrEmpty(a_b)) return false;
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
        /// 'a_dir' が、現在フォルダペインに表示されているフォルダそのもの、またはそのサブフォルダで
        /// あれば true を返す。true の場合、同じ範囲のファイルを開いただけならツリーを
        /// 作り直す（ユーザーが展開していた状態を失わせる）必要はない。
        /// </summary>
        public bool IsWithinLoadedFolder(string a_dir)
        {
            if (string.IsNullOrEmpty(LoadedFolderRootPath) || string.IsNullOrEmpty(a_dir)) return false;
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

        private void PopulateChildren(FileSystemItem a_node)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(a_node.FullPath).OrderBy(d => d))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(".")) continue;
                    a_node.Children.Add(BuildFileSystemNode(dir, true));
                }
                foreach (var file in Directory.GetFiles(a_node.FullPath)
                             .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f))
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
        public void HandleTreeViewItemExpanded(object a_sender, RoutedEventArgs a_args)
        {
            if (a_sender is TreeViewItem tvi && tvi.DataContext is FileSystemItem node && node.IsDirectory)
            {
                if (node.Children.Count == 1 && node.Children[0].FullPath == null)
                {
                    node.Children.Clear();
                    PopulateChildren(node);
                    RefreshDirtyMarkers();
                }
            }
        }

        /// <summary>フォルダツリーでファイルノードがクリックされたら、そのファイルを開く。</summary>
        public void HandleSelectedItemChanged(object a_sender, RoutedPropertyChangedEventArgs<object> a_args)
        {
            if (a_args.NewValue is FileSystemItem item && !item.IsDirectory && item.FullPath != null)
            {
                m_loadFile(item.FullPath);
            }
        }
    }
}
