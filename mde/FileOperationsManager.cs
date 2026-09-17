// FileOperationsManager.cs
//
// mde (Markdown インラインエディタ) の一部。
// 「新規作成・開く・保存・名前を付けて保存・すべて保存・閉じる」および、それに付随する
// 最近使ったファイル一覧・Ctrl+左右キーでのファイル履歴・エディタのUndo履歴クリア・
// 一時画像の退避を伴う保存前処理を担当するクラス。
// MainWindow.xaml.csの「新規作成 / 開く / 保存 / 名前を付けて保存」区画（PDF書き出しを除く）
// をそのまま抽出したもの。XAMLのClick=は元のメソッド名（MainWindow側の薄いラッパー）を
// そのまま参照し続けるため、このクラス自身はXAMLから直接参照されない。
// MainWindow本体への参照は持たず、必要な操作・状態はすべてコンストラクタで渡された
// delegateまたは協力オブジェクト（他の各機能クラス）経由で行う。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace mde
{
    /// <summary>
    /// ファイルの新規作成・開く・保存・名前を付けて保存・すべて保存・履歴・最近使った
    /// ファイル一覧を担当する。MainWindowから、現在のファイルパス・ディレクトリ・
    /// ダーティフラグの読み書きや、他の各機能クラスへの参照をdelegate経由で受け取る。
    /// </summary>
    public class FileOperationsManager
    {
        private readonly RichTextBox m_editor;
        private readonly TextBox m_sourceEditor;
        private readonly MarkdownConverter m_markdownConverter;
        private readonly ImageManager m_imageManager;
        private readonly LineEndingTracker m_lineEndingTracker;
        private readonly OutlineManager m_outlineManager;
        private readonly FolderTreeManager m_folderTreeManager;
        private readonly SearchReplaceService m_searchReplaceService;
        private readonly MenuItem m_recentFilesMenu;
        private readonly List<string> m_recentFiles;
        private readonly Dictionary<string, string> m_pendingFileEdits;

        private readonly Func<bool> m_isSourceMode;
        private readonly Func<bool> m_getPreserveSourceLineBreaks;
        private readonly Func<bool> m_getCorrectColumnWidths;
        private readonly Action<Action> m_runAsProgrammaticChange;
        private readonly Func<string, string> m_getCurrentContentForFile;
        private readonly Func<string, string, bool> m_pathsReferToSameFile;
        private readonly Action<string> m_rememberCurrentScrollPosition;
        private readonly Action<string> m_restoreOrResetScrollPosition;
        private readonly Action<string, string> m_openFileInNewWindow;
        private readonly Action m_openNewWindow;
        private readonly Action m_closeWindow;
        private readonly Action<string> m_setWindowTitle;
        private readonly Func<FindReplaceWindow> m_getOpenFindReplaceWindow;

        private readonly Func<string> m_getCurrentFilePath;
        private readonly Action<string> m_setCurrentFilePath;
        private readonly Func<string> m_getCurrentFileDirectory;
        private readonly Action<string> m_setCurrentFileDirectory;
        private readonly Func<bool> m_getCurrentFileIsDirty;
        private readonly Action<bool> m_setCurrentFileIsDirty;

        /// <summary>メニュー「ファイル」→「最近使ったファイル」に表示する、最近開いた/保存した
        /// ファイルの最大件数。</summary>
        private const int MAX_RECENT_FILES = 10;

        /// <summary>Ctrl+左右カーソルキーで前後にたどれる、開いたファイルの履歴（ブラウザの
        /// 「戻る/進む」と同様の仕組み）。</summary>
        private readonly List<string> m_fileHistory = new List<string>();
        private int m_fileHistoryIndex = -1;
        /// <summary>Ctrl+左右による履歴移動でLoadFileを呼んでいる最中は、それ自体を新しい
        /// 履歴として記録しないようにするためのフラグ。</summary>
        private bool m_isNavigatingFileHistoryFlg;

        /// <summary>
        /// FileOperationsManagerを構築する。
        /// </summary>
        /// <param name="a_editor">Markdownモードのエディタ。</param>
        /// <param name="a_sourceEditor">ソースモードのエディタ。</param>
        /// <param name="a_markdownConverter">FlowDocument⇔Markdown変換。</param>
        /// <param name="a_imageManager">画像管理（保存前の一時画像退避に使う）。</param>
        /// <param name="a_lineEndingTracker">改行コードの検出・記憶・適用。</param>
        /// <param name="a_outlineManager">アウトラインペイン（文書差し替え後の再構築に使う）。</param>
        /// <param name="a_folderTreeManager">フォルダペイン。</param>
        /// <param name="a_searchReplaceService">検索と置換（文書差し替え通知に使う）。</param>
        /// <param name="a_recentFilesMenu">「最近使ったファイル」サブメニュー。</param>
        /// <param name="a_recentFiles">最近使ったファイル一覧（MainWindow.OnClosedでの設定保存と
        /// 同じリストを共有する）。</param>
        /// <param name="a_pendingFileEdits">まだディスクに書き出されていない編集内容
        /// （MainWindowの他の箇所と同じ辞書を共有する）。</param>
        /// <param name="a_isSourceMode">現在ソースモードかどうかを返すdelegate。</param>
        /// <param name="a_getPreserveSourceLineBreaks">段落中の改行の扱い設定を返すdelegate
        /// （保存前処理で使い捨てのMarkdownConverterを組み立てる際に使う）。</param>
        /// <param name="a_getCorrectColumnWidths">列幅補正設定を返すdelegate（同上）。</param>
        /// <param name="a_runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        /// <param name="a_getCurrentContentForFile">ファイルの「今の内容」を解決するdelegate。</param>
        /// <param name="a_pathsReferToSameFile">2つのパスが同一ファイルを指すか調べるdelegate。</param>
        /// <param name="a_rememberCurrentScrollPosition">ファイル切り替え前に、現在のスクロール
        /// 位置を覚えておくdelegate。</param>
        /// <param name="a_restoreOrResetScrollPosition">ファイルを開いた直後に、覚えている
        /// スクロール位置を復元する（無ければ先頭へ戻す）delegate。</param>
        /// <param name="a_openFileInNewWindow">別ウィンドウで指定したファイルを開くdelegate。</param>
        /// <param name="a_openNewWindow">空の新しいウィンドウを開くdelegate。</param>
        /// <param name="a_closeWindow">このウィンドウを閉じるdelegate。</param>
        /// <param name="a_setWindowTitle">ウィンドウタイトルを設定するdelegate。</param>
        /// <param name="a_getOpenFindReplaceWindow">現在開いている検索と置換ウィンドウ（無ければ
        /// null）を返すdelegate。呼ばれるたびに最新の値を取得できるよう、スナップショットでは
        /// なくdelegateとして受け取る。</param>
        /// <param name="a_getCurrentFilePath">現在のファイルパスを返すdelegate。</param>
        /// <param name="a_setCurrentFilePath">現在のファイルパスを設定するdelegate。</param>
        /// <param name="a_getCurrentFileDirectory">現在のファイルの保存先フォルダを返すdelegate。</param>
        /// <param name="a_setCurrentFileDirectory">現在のファイルの保存先フォルダを設定するdelegate。</param>
        /// <param name="a_getCurrentFileIsDirty">現在のファイルに未保存の変更があるかを返すdelegate。</param>
        /// <param name="a_setCurrentFileIsDirty">現在のファイルの未保存フラグを設定するdelegate。</param>
        public FileOperationsManager(
            RichTextBox a_editor,
            TextBox a_sourceEditor,
            MarkdownConverter a_markdownConverter,
            ImageManager a_imageManager,
            LineEndingTracker a_lineEndingTracker,
            OutlineManager a_outlineManager,
            FolderTreeManager a_folderTreeManager,
            SearchReplaceService a_searchReplaceService,
            MenuItem a_recentFilesMenu,
            List<string> a_recentFiles,
            Dictionary<string, string> a_pendingFileEdits,
            Func<bool> a_isSourceMode,
            Func<bool> a_getPreserveSourceLineBreaks,
            Func<bool> a_getCorrectColumnWidths,
            Action<Action> a_runAsProgrammaticChange,
            Func<string, string> a_getCurrentContentForFile,
            Func<string, string, bool> a_pathsReferToSameFile,
            Action<string> a_rememberCurrentScrollPosition,
            Action<string> a_restoreOrResetScrollPosition,
            Action<string, string> a_openFileInNewWindow,
            Action a_openNewWindow,
            Action a_closeWindow,
            Action<string> a_setWindowTitle,
            Func<FindReplaceWindow> a_getOpenFindReplaceWindow,
            Func<string> a_getCurrentFilePath,
            Action<string> a_setCurrentFilePath,
            Func<string> a_getCurrentFileDirectory,
            Action<string> a_setCurrentFileDirectory,
            Func<bool> a_getCurrentFileIsDirty,
            Action<bool> a_setCurrentFileIsDirty)
        {
            this.m_editor = a_editor;
            this.m_sourceEditor = a_sourceEditor;
            this.m_markdownConverter = a_markdownConverter;
            this.m_imageManager = a_imageManager;
            this.m_lineEndingTracker = a_lineEndingTracker;
            this.m_outlineManager = a_outlineManager;
            this.m_folderTreeManager = a_folderTreeManager;
            this.m_searchReplaceService = a_searchReplaceService;
            this.m_recentFilesMenu = a_recentFilesMenu;
            this.m_recentFiles = a_recentFiles;
            this.m_pendingFileEdits = a_pendingFileEdits;
            this.m_isSourceMode = a_isSourceMode;
            this.m_getPreserveSourceLineBreaks = a_getPreserveSourceLineBreaks;
            this.m_getCorrectColumnWidths = a_getCorrectColumnWidths;
            this.m_runAsProgrammaticChange = a_runAsProgrammaticChange;
            this.m_getCurrentContentForFile = a_getCurrentContentForFile;
            this.m_pathsReferToSameFile = a_pathsReferToSameFile;
            this.m_rememberCurrentScrollPosition = a_rememberCurrentScrollPosition;
            this.m_restoreOrResetScrollPosition = a_restoreOrResetScrollPosition;
            this.m_openFileInNewWindow = a_openFileInNewWindow;
            this.m_openNewWindow = a_openNewWindow;
            this.m_closeWindow = a_closeWindow;
            this.m_setWindowTitle = a_setWindowTitle;
            this.m_getOpenFindReplaceWindow = a_getOpenFindReplaceWindow;
            this.m_getCurrentFilePath = a_getCurrentFilePath;
            this.m_setCurrentFilePath = a_setCurrentFilePath;
            this.m_getCurrentFileDirectory = a_getCurrentFileDirectory;
            this.m_setCurrentFileDirectory = a_setCurrentFileDirectory;
            this.m_getCurrentFileIsDirty = a_getCurrentFileIsDirty;
            this.m_setCurrentFileIsDirty = a_setCurrentFileIsDirty;
        }

        /// <summary>ウィンドウタイトルを、現在のファイル名（無ければアプリ名のみ）から
        /// 組み立てて設定する。</summary>
        /// <param name="a_fileNameOrNull">タイトルに含めるファイル名。ファイルが無い場合はnull。</param>
        private void SetWindowTitleForFile(string a_fileNameOrNull)
        {
            string title = string.IsNullOrEmpty(a_fileNameOrNull)
                ? Assembly.GetExecutingAssembly().GetName().Name
                : Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version +
                    " - " + a_fileNameOrNull;
            m_setWindowTitle(title);
        }

        /// <summary>現在の内容を破棄して新規文書を開始する。現在のファイルがフォルダビューに
        /// 表示されている場合は、破棄前に編集内容を保留中の編集として退避したうえで、確認
        /// ダイアログなしで新規作成する（あとでフォルダビューから開き直して保存できるため）。
        /// フォルダビューに表示されていないファイル（またはそもそも未保存の新規文書）の場合は、
        /// 内容が失われる可能性があるため従来通り確認する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void NewBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            bool currentFileInFolderViewFlg = !string.IsNullOrEmpty(m_getCurrentFileDirectory()) &&
                m_folderTreeManager.IsWithinLoadedFolder(m_getCurrentFileDirectory());

            if (!currentFileInFolderViewFlg && (m_getCurrentFileIsDirty() || m_pendingFileEdits.Count > 0))
            {
                var result = MessageBox.Show(
                    "現在の内容を破棄して新規作成します。保存されていない変更は失われますが、よろしいですか？",
                    "新規作成", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result != MessageBoxResult.OK)
                {
                    return;
                }
            }

            if (currentFileInFolderViewFlg)
            {
                SnapshotCurrentFileIfDirty();
                StartNewDocumentKeepingPendingEdits();
            }
            else
            {
                DiscardCurrentDocumentSilently();
            }
            m_editor.Focus();
        }

        /// <summary>新しいウィンドウを開く（現在のウィンドウの内容には触れない）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void NewWindowClick(object a_sender, RoutedEventArgs a_args)
        {
            m_openNewWindow();
        }

        /// <summary>このウィンドウを閉じる（未保存の変更があればWindow_Closingで確認される）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void CloseBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            m_closeWindow();
        }

        /// <summary>ファイルを開くダイアログを表示し、選択されたファイルを読み込む。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void OpenBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Markdownファイル (*.md;*.markdown)|*.md;*.markdown|すべてのファイル (*.*)|*.*"
            };
            if (true == dlg.ShowDialog())
            {
                LoadFile(dlg.FileName);
            }
        }

        public void LoadFile(string a_path)
        {
            string currentFilePath = m_getCurrentFilePath();

            // 特殊ケース：すでにアクティブなファイルを再度開こうとした場合。この判定がないと、
            // 下のGetCurrentContentForFileがライブな（編集中の）内容をそのまま返してしまい、
            // 「開く」操作が何もしていないように見えてしまう。
            if (!string.IsNullOrEmpty(currentFilePath) && m_pathsReferToSameFile(a_path, currentFilePath))
            {
                if (!m_getCurrentFileIsDirty())
                {
                    return; // 読み込み・保存後に編集がなければ何もしない
                }

                var result = MessageBox.Show(
                    "このファイルには保存されていない変更があります。破棄して、保存済みの内容で開き直しますか？",
                    "ファイルを開き直す", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (result != MessageBoxResult.OK)
                {
                    return;
                }

                m_pendingFileEdits.Remove(a_path); // このファイルの保留中の編集も破棄する

                string onDiskContent = SafeReadFile(a_path);
                if (null == onDiskContent)
                {
                    MessageBox.Show("ファイルを開けませんでした。", "ファイルを開く", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (m_isSourceMode())
                {
                    m_sourceEditor.Text = onDiskContent;
                }
                else
                {
                    m_runAsProgrammaticChange(() => m_markdownConverter.MarkdownToDocument(onDiskContent, m_editor.Document));
                    m_outlineManager.Refresh();
                    // 文書を丸ごと差し替えた直後は、キャレット位置が未確定・不定なままになる
                    // ことがある（古い位置を指したままになるなど）。ここで明示的に文書の先頭へ
                    // 設定しておくことで、その直後に検索の「次を検索」等がCaretPositionを基準に
                    // 開始位置を計算する際、信頼できる値になるようにする。
                    m_editor.CaretPosition = m_editor.Document.ContentStart;
                    ClearEditorUndoHistory();
                }
                // このブランチは「同じファイルを、保存済みの内容で開き直す」場合であり、別の
                // ファイルへの切り替えではないため、スクロール位置には触れない（今どこを見て
                // いたかがそのまま保たれる方が、保存し忘れた変更を破棄して開き直すという
                // 操作の性質上、自然な挙動になる）。
                m_searchReplaceService.OnDocumentReplaced();
                m_getOpenFindReplaceWindow()?.ReapplyHighlightForCurrentFile();

                m_setCurrentFileIsDirty(false);
                m_folderTreeManager.RefreshDirtyMarkers();
                return;
            }

            // ファイルを切り替える前に、今表示している内容のスクロール位置を、あとで
            // このファイルへ戻ってきた時のために覚えておく（起動直後などm_currentFilePathが
            // まだ無い場合は、覚えておく対象が無いため何もしない）。
            if (!string.IsNullOrEmpty(currentFilePath))
            {
                m_rememberCurrentScrollPosition(currentFilePath);
            }

            SnapshotCurrentFileIfDirty();

            string md = m_getCurrentContentForFile(a_path);
            if (null == md)
            {
                md = SafeReadFile(a_path);
                if (null == md)
                {
                    MessageBox.Show("ファイルを開けませんでした。", "ファイルを開く",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            m_setCurrentFilePath(a_path);
            m_setCurrentFileDirectory(Path.GetDirectoryName(a_path));
            SetWindowTitleForFile(Path.GetFileName(a_path));
            m_pendingFileEdits.Remove(a_path); // このファイルの内容は、以後エディタ自体が真実の情報源になる
            RecordFileHistory(a_path);
            AddToRecentFiles(a_path);

            if (m_isSourceMode())
            {
                m_sourceEditor.Text = md;
            }
            else
            {
                m_runAsProgrammaticChange(() => m_markdownConverter.MarkdownToDocument(md, m_editor.Document));
                m_outlineManager.Refresh();
                // 文書を丸ごと差し替えた直後は、キャレット位置が未確定・不定なままになる
                // ことがある（古い位置を指したままになるなど）。ここで明示的に文書の先頭へ
                // 設定しておくことで、その直後に検索の「次を検索」等がCaretPositionを基準に
                // 開始位置を計算する際、信頼できる値になるようにする。
                m_editor.CaretPosition = m_editor.Document.ContentStart;
                ClearEditorUndoHistory();
            }
            // このファイルを以前この session 内で開いたことがあり、スクロール位置を覚えて
            // いれば、そこへ戻す（無ければ先頭へ）。
            m_restoreOrResetScrollPosition(a_path);
            m_searchReplaceService.OnDocumentReplaced();
            m_getOpenFindReplaceWindow()?.ReapplyHighlightForCurrentFile();

            m_setCurrentFileIsDirty(false);

            string currentFileDirectory = m_getCurrentFileDirectory();
            if (!string.IsNullOrEmpty(currentFileDirectory) && !m_folderTreeManager.IsWithinLoadedFolder(currentFileDirectory))
            {
                m_folderTreeManager.LoadFolderTree(currentFileDirectory);
            }
            else
            {
                m_folderTreeManager.RefreshDirtyMarkers();
            }
        }

        /// <summary>
        /// 現在開いているファイルに未保存の変更があれば、別のファイルへ切り替える前に
        /// pendingFileEditsへ退避する（1つしかないエディタを共有しているため、切り替え時に
        /// 内容が失われないようにするため）。ソースモードでは単純化のためスキップする。
        /// </summary>
        /// <summary>
        /// エディタのUndo（元に戻す）履歴をクリアする。IsUndoEnabledをいったんfalseにしてから
        /// trueに戻すと、WPF標準の仕組みでUndo履歴が破棄される。ファイルを新しく読み込んで
        /// 文書を丸ごと差し替えた直後に呼ぶことで、「Ctrl+Zを押したら別のファイルの内容に
        /// 戻ってしまう」という混乱を防ぐ（Undo/Redoは、あくまで今のファイルの編集内容の
        /// 範囲だけで完結させる）。
        /// </summary>
        private void ClearEditorUndoHistory()
        {
            m_editor.IsUndoEnabled = false;
            m_editor.IsUndoEnabled = true;
        }

        /// <summary>
        /// 新しくファイルを開いた時に、Ctrl+左右で戻れる履歴へ記録する。履歴の途中（戻った
        /// 状態）から別のファイルを開いた場合は、それより先の「進む」履歴をブラウザと同様に
        /// 破棄する。
        /// </summary>
        /// <param name="a_path">開いたファイルの絶対パス。</param>
        private void RecordFileHistory(string a_path)
        {
            if (m_isNavigatingFileHistoryFlg)
            {
                return;
            }
            if (m_fileHistoryIndex < m_fileHistory.Count - 1)
            {
                m_fileHistory.RemoveRange(m_fileHistoryIndex + 1, m_fileHistory.Count - m_fileHistoryIndex - 1);
            }
            if (0 == m_fileHistory.Count || !m_pathsReferToSameFile(m_fileHistory[m_fileHistory.Count - 1], a_path))
            {
                m_fileHistory.Add(a_path);
                m_fileHistoryIndex = m_fileHistory.Count - 1;
            }
        }

        /// <summary>Ctrl+左カーソルキー：1つ前に開いていたファイルを開く。</summary>
        public void NavigateFileHistoryBack()
        {
            if (m_fileHistoryIndex <= 0)
            {
                return;
            }
            m_fileHistoryIndex--;
            m_isNavigatingFileHistoryFlg = true;
            try
            {
                LoadFile(m_fileHistory[m_fileHistoryIndex]);
            }
            finally
            {
                m_isNavigatingFileHistoryFlg = false;
            }
        }

        /// <summary>Ctrl+右カーソルキー：1つ後に開いていたファイルを開く。</summary>
        public void NavigateFileHistoryForward()
        {
            if (m_fileHistoryIndex < 0 || m_fileHistoryIndex >= m_fileHistory.Count - 1)
            {
                return;
            }
            m_fileHistoryIndex++;
            m_isNavigatingFileHistoryFlg = true;
            try
            {
                LoadFile(m_fileHistory[m_fileHistoryIndex]);
            }
            finally
            {
                m_isNavigatingFileHistoryFlg = false;
            }
        }

        /// <summary>
        /// メニュー「ファイル」→「最近使ったファイル」に表示する一覧へ、指定したファイルを
        /// 先頭に追加する（既に一覧にあれば、いったん外してから先頭へ入れ直す）。件数が
        /// MAX_RECENT_FILESを超えたら、古いものから切り捨てる。次回起動時にも復元される。
        /// </summary>
        /// <param name="a_path">開いた・保存したファイルの絶対パス。</param>
        private void AddToRecentFiles(string a_path)
        {
            if (string.IsNullOrEmpty(a_path))
            {
                return;
            }
            m_recentFiles.RemoveAll(p => m_pathsReferToSameFile(p, a_path));
            m_recentFiles.Insert(0, a_path);
            if (m_recentFiles.Count > MAX_RECENT_FILES)
            {
                m_recentFiles.RemoveRange(MAX_RECENT_FILES, m_recentFiles.Count - MAX_RECENT_FILES);
            }
            RebuildRecentFilesMenu();
        }

        /// <summary>「最近使ったファイル」サブメニューの中身を、現在のm_recentFilesの内容から
        /// 組み立て直す。</summary>
        public void RebuildRecentFilesMenu()
        {
            m_recentFilesMenu.Items.Clear();
            if (0 == m_recentFiles.Count)
            {
                m_recentFilesMenu.Items.Add(new MenuItem { Header = "（履歴なし）", IsEnabled = false });
                return;
            }
            foreach (string path in m_recentFiles)
            {
                var item = new MenuItem
                {
                    Header = Path.GetFileName(path),
                    ToolTip = path,
                    Tag = path
                };
                item.Click += RecentFileMenuItemClick;
                m_recentFilesMenu.Items.Add(item);
            }
            m_recentFilesMenu.Items.Add(new Separator());
            var clearItem = new MenuItem { Header = "履歴をクリア(_C)" };
            clearItem.Click += ClearRecentFilesClick;
            m_recentFilesMenu.Items.Add(clearItem);
        }

        /// <summary>「最近使ったファイル」の項目をクリックした時、そのファイルを開く。
        /// ファイルが見つからない（移動・削除済みなど）場合は、その旨を伝えて一覧からも外す。</summary>
        /// <param name="a_sender">クリックされたメニュー項目（Tagにファイルの絶対パスを持つ）。</param>
        /// <param name="a_args">Click event.</param>
        private void RecentFileMenuItemClick(object a_sender, RoutedEventArgs a_args)
        {
            string path = (string)((MenuItem)a_sender).Tag;
            if (!File.Exists(path))
            {
                MessageBox.Show("ファイルが見つかりませんでした：" + path, "最近使ったファイル",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                m_recentFiles.Remove(path);
                RebuildRecentFilesMenu();
                return;
            }
            LoadFile(path);
        }

        /// <summary>「履歴をクリア」：最近使ったファイルの一覧を空にする。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">Click event.</param>
        private void ClearRecentFilesClick(object a_sender, RoutedEventArgs a_args)
        {
            m_recentFiles.Clear();
            RebuildRecentFilesMenu();
        }

        private void SnapshotCurrentFileIfDirty()
        {
            string currentFilePath = m_getCurrentFilePath();
            if (string.IsNullOrEmpty(currentFilePath) || m_isSourceMode())
            {
                return;
            }

            if (!m_getCurrentFileIsDirty())
            {
                m_pendingFileEdits.Remove(currentFilePath);
                return;
            }

            try
            {
                m_pendingFileEdits[currentFilePath] = m_markdownConverter.DocumentToMarkdown(m_editor.Document);
            }
            catch
            {
                // ベストエフォートのみ。これが原因でファイル切り替えをブロックすることはない
            }
        }

        /// <summary>ファイルの内容を読み込み、改行コードを検出・記憶する。</summary>
        /// <param name="a_path">対象のファイルパス。</param>
        /// <returns>読み込んだファイルの内容。失敗した場合はnull。</returns>
        private string SafeReadFile(string a_path)
        {
            try
            {
                string content = File.ReadAllText(a_path, Encoding.UTF8);
                m_lineEndingTracker.DetectAndRemember(a_path, content);
                return content;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 保存時に書き出すMarkdownテキストを、一時フォルダに残ったままの画像（WYSIWYGモードで
        /// ドラッグ&amp;ドロップ挿入した直後、まだ一度も保存していない画像）を
        /// "&lt;ファイル名&gt;.images"フォルダへ退避・パス書き換えした上で返す。WYSIWYGモードで
        /// 画像を挿入した直後にソースモードへ切り替えてそのまま保存した場合も退避が必要なため、
        /// モードを問わず常にこの退避処理を行う。ソースモード中の保存では、ライブな
        /// m_editor.Document・m_sourceEditor.Text のどちらも直接いじらずに済むよう、
        /// 「使い捨てのスクラッチ文書＋専用OriginalTextTracker」パターンで処理し、
        /// 書き換え後のテキストを m_sourceEditor.Text へも反映しておく（同じ一時ファイルを
        /// 次回保存時に再び探しに行って失敗することがないように）。
        ///
        /// 「名前を付けて保存」で保存先フォルダやファイル名が変わる場合は、<paramref
        /// name="a_oldFileDirectoryForRelocation"/>・<paramref name="a_oldFilePathForRelocation"/>
        /// に変更前のフォルダ・パスを渡すことで、元のファイル専用の画像フォルダ
        /// （"&lt;元のファイル名&gt;.images"）内の画像も新しい保存先へ一緒にコピーする
        /// （ImageManager.RelocateImagesFromOldFileImagesFolder参照）。通常の保存（上書き保存・
        /// すべて保存）ではフォルダもファイル名も変わらないため、両方nullのままでよい。
        /// </summary>
        /// <param name="a_oldFileDirectoryForRelocation">「名前を付けて保存」で、変更前のファイルの
        /// 保存先フォルダ。通常の保存ではnull。</param>
        /// <param name="a_oldFilePathForRelocation">「名前を付けて保存」で、変更前のファイルのパス
        /// （画像フォルダ名の決定に使う）。通常の保存ではnull。</param>
        /// <returns>保存すべきMarkdownテキスト。</returns>
        private string GetMarkdownForSaveWithImageRelocation(
            string a_oldFileDirectoryForRelocation = null, string a_oldFilePathForRelocation = null)
        {
            if (!m_isSourceMode())
            {
                m_imageManager.RelocatePendingTempImages(m_editor.Document);
                m_imageManager.RelocateImagesFromOldFileImagesFolder(
                    m_editor.Document, a_oldFileDirectoryForRelocation, a_oldFilePathForRelocation);
                return m_markdownConverter.DocumentToMarkdown(m_editor.Document);
            }

            var tempTracker = new OriginalTextTracker(m_editor);
            var tempConverter = new MarkdownConverter(tempTracker, m_imageManager, m_getPreserveSourceLineBreaks, m_getCorrectColumnWidths);
            var tempDoc = new FlowDocument();
            tempConverter.MarkdownToDocument(m_sourceEditor.Text, tempDoc);
            m_imageManager.RelocatePendingTempImages(tempDoc);
            m_imageManager.RelocateImagesFromOldFileImagesFolder(
                tempDoc, a_oldFileDirectoryForRelocation, a_oldFilePathForRelocation);
            string md = tempConverter.DocumentToMarkdown(tempDoc);
            m_sourceEditor.Text = md;
            return md;
        }

        /// <summary>現在のファイルを保存する（未保存の新規ファイルなら名前を付けて保存へ）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void SaveBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            string currentFilePath = m_getCurrentFilePath();
            if (string.IsNullOrEmpty(currentFilePath))
            {
                SaveAs();
                return;
            }
            string md = GetMarkdownForSaveWithImageRelocation();
            File.WriteAllText(currentFilePath, m_lineEndingTracker.Apply(md, m_lineEndingTracker.GetFor(currentFilePath)), new UTF8Encoding(false));
            m_setCurrentFileIsDirty(false);
            m_folderTreeManager.AddFileNodeIfMissing(currentFilePath);
            m_folderTreeManager.RefreshDirtyMarkers();
            m_folderTreeManager.SelectFileNode(currentFilePath);
        }

        /// <summary>「名前を付けて保存」ダイアログを開く。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void SaveAsBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            SaveAs();
        }

        /// <summary>新しいファイル名/保存先を尋ね、そこへ保存する（元ファイルの改行コード
        /// スタイルは引き継ぐ）。</summary>
        public void SaveAs()
        {
            string currentFilePath = m_getCurrentFilePath();
            // 画像専用フォルダ（"<ファイル名>.images"）内の画像を新しい保存先へも一緒にコピー
            // できるよう、m_setCurrentFileDirectory等で上書きされる前の値を覚えておく
            // （ImageManager.RelocateImagesFromOldFileImagesFolder参照）。
            string oldFileDirectory = m_getCurrentFileDirectory();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdownファイル (*.md)|*.md|すべてのファイル (*.*)|*.*",
                FileName = null != currentFilePath ? Path.GetFileName(currentFilePath) : "document.md"
            };
            // 現在の文書自体には保存先フォルダがまだ無くても（新規作成直後など）、フォルダビューに
            // 何かフォルダが表示されていれば、そちらを初期フォルダとして使う。
            string initialDirectory = m_getCurrentFileDirectory() ?? m_folderTreeManager.LoadedFolderRootPath;
            if (!string.IsNullOrEmpty(initialDirectory))
            {
                dlg.InitialDirectory = initialDirectory;
            }

            if (true != dlg.ShowDialog())
            {
                return;
            }

            string newFilePath = dlg.FileName;
            string newFileDirectory = Path.GetDirectoryName(dlg.FileName);

            // 「名前を付けて保存」は、保存元ファイルの改行コードスタイルを引き継ぐ
            // （名前や場所が変わっただけで、その設定を失わせないため）。
            string lineEnding = !string.IsNullOrEmpty(currentFilePath) ? m_lineEndingTracker.GetFor(currentFilePath) : "\r\n";
            m_lineEndingTracker.SetFor(newFilePath, lineEnding);

            bool folderIsLoadedFlg = !string.IsNullOrEmpty(m_folderTreeManager.LoadedFolderRootPath);
            bool isWithinCurrentFolderFlg = folderIsLoadedFlg && m_folderTreeManager.IsWithinLoadedFolder(newFileDirectory);

            if (folderIsLoadedFlg && !isWithinCurrentFolderFlg)
            {
                // 現在表示中のフォルダの外に保存する場合：このウィンドウでの編集内容は
                // 保存先ファイルへ引き継がれる（新しいウィンドウで開く）ため、このウィンドウ
                // 自体は表示中のフォルダの表示を維持したまま、その先頭のファイルへ切り替える。
                m_setCurrentFileDirectory(newFileDirectory); // 画像パスの解決に一時的に必要
                m_setCurrentFilePath(newFilePath); // 画像退避フォルダ名（<ファイル名>.images）の決定に一時的に必要
                string outsideMd = GetMarkdownForSaveWithImageRelocation(oldFileDirectory, currentFilePath);
                File.WriteAllText(newFilePath, m_lineEndingTracker.Apply(outsideMd, lineEnding), new UTF8Encoding(false));

                // 古いパスの情報を破棄する（他のファイルの保留中の編集には触れない）。
                m_setCurrentFilePath(null);
                m_setCurrentFileDirectory(null);
                m_setCurrentFileIsDirty(false);
                m_folderTreeManager.OpenFirstFileInLoadedFolder();

                m_openFileInNewWindow(newFilePath, null);
                return;
            }

            m_setCurrentFilePath(newFilePath);
            m_setCurrentFileDirectory(newFileDirectory);
            SetWindowTitleForFile(Path.GetFileName(newFilePath));
            AddToRecentFiles(newFilePath);

            string md = GetMarkdownForSaveWithImageRelocation(oldFileDirectory, currentFilePath);
            File.WriteAllText(newFilePath, m_lineEndingTracker.Apply(md, lineEnding), new UTF8Encoding(false));
            m_setCurrentFileIsDirty(false);

            if (!folderIsLoadedFlg)
            {
                m_folderTreeManager.LoadFolderTree(m_getCurrentFileDirectory());
            }
            else
            {
                m_folderTreeManager.AddFileNodeIfMissing(newFilePath);
            }
            m_folderTreeManager.RefreshDirtyMarkers();
            m_folderTreeManager.SelectFileNode(newFilePath);
        }

        /// <summary>編集中のすべてのファイル（現在のファイル＋保留中の編集があるすべてのファイル）
        /// をまとめて保存する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void SaveAllBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_getCurrentFileIsDirty() || m_pendingFileEdits.Count > 0)
            {
                var confirmResult = MessageBox.Show(
                    "編集中のすべてのファイルを保存します。よろしいですか？",
                    "すべて保存", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (confirmResult != MessageBoxResult.OK)
                {
                    return;
                }
            }
            else
            {
                return;
            }

            int savedCount = 0;
            var failures = new List<string>();

            string currentFilePath = m_getCurrentFilePath();
            if (!string.IsNullOrEmpty(currentFilePath))
            {
                try
                {
                    string md = GetMarkdownForSaveWithImageRelocation();
                    File.WriteAllText(currentFilePath, m_lineEndingTracker.Apply(md, m_lineEndingTracker.GetFor(currentFilePath)), new UTF8Encoding(false));
                    m_pendingFileEdits.Remove(currentFilePath);
                    m_setCurrentFileIsDirty(false);
                    savedCount++;
                }
                catch (Exception ex)
                {
                    failures.Add(currentFilePath + " (" + ex.Message + ")");
                }
            }

            foreach (var kv in new List<KeyValuePair<string, string>>(m_pendingFileEdits))
            {
                try
                {
                    File.WriteAllText(kv.Key, m_lineEndingTracker.Apply(kv.Value, m_lineEndingTracker.GetFor(kv.Key)), new UTF8Encoding(false));
                    m_pendingFileEdits.Remove(kv.Key);
                    savedCount++;
                }
                catch (Exception ex)
                {
                    failures.Add(kv.Key + " (" + ex.Message + ")");
                }
            }

            m_folderTreeManager.RefreshDirtyMarkers();

            string message = savedCount + " 個のファイルを保存しました。";
            if (failures.Count > 0)
            {
                message += "\n\n保存に失敗したファイル:\n" + string.Join("\n", failures);
            }

            MessageBox.Show(message, "すべて保存", MessageBoxButton.OK,
                failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        /// <summary>エディタを空の無題文書にリセットし、未保存変更の追跡もすべてクリアする
        /// （確認は行わない。呼び出し側が必要に応じて事前にユーザーへ確認済みであることを
        /// 前提とする）。</summary>
        public void DiscardCurrentDocumentSilently()
        {
            m_setCurrentFilePath(null);
            m_setCurrentFileDirectory(null);
            SetWindowTitleForFile(null);

            m_pendingFileEdits.Clear();
            m_setCurrentFileIsDirty(false);

            m_runAsProgrammaticChange(() =>
            {
                if (m_isSourceMode())
                {
                    m_sourceEditor.Text = "";
                }
                else
                {
                    m_editor.Document.Blocks.Clear();
                    m_editor.Document.Blocks.Add(new Paragraph());
                }
            });
            m_outlineManager.Refresh();
            m_folderTreeManager.RefreshDirtyMarkers();
        }

        /// <summary>
        /// DiscardCurrentDocumentSilentlyと同様に、エディタを空の無題文書へリセットするが、
        /// 他のファイルの保留中の編集（m_pendingFileEdits）はそのまま残す。フォルダビューに
        /// 表示されているファイルから新規作成する際、そのファイル自身の編集内容は事前に
        /// SnapshotCurrentFileIfDirtyで退避済みであることを前提とする。
        /// </summary>
        private void StartNewDocumentKeepingPendingEdits()
        {
            m_setCurrentFilePath(null);
            m_setCurrentFileDirectory(null);
            SetWindowTitleForFile(null);
            m_setCurrentFileIsDirty(false);

            m_runAsProgrammaticChange(() =>
            {
                if (m_isSourceMode())
                {
                    m_sourceEditor.Text = "";
                }
                else
                {
                    m_editor.Document.Blocks.Clear();
                    m_editor.Document.Blocks.Add(new Paragraph());
                }
            });
            m_outlineManager.Refresh();
            m_folderTreeManager.RefreshDirtyMarkers();
        }
    }
}
