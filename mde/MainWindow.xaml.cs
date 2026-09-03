// MainWindow.xaml.cs
//
// mde (MarkDown インラインエディタ) の一部。
// アプリのメインウィンドウ。ここでは各機能クラス（MarkdownConverter、TableEditor、
// ListEditor、HeadingCodeBlockEditor、InlineStyleEditor、ImageManager、SearchReplaceService、
// OutlineManager、FolderTreeManager）をすべて構築し、コンストラクタで必要なdelegateを配線する。
// XAML側から呼ばれるイベントハンドラの多くは、実際の処理を各クラスへそのまま橋渡しする
// 薄いラッパーになっている。ウィンドウ全体・現在のファイル・キーボード入力の振り分けといった
// 「どのクラスにも属さない」調整の役割もここが担う。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace mde
{
    /// <summary>
    /// mdeの唯一のメインウィンドウ。1つのRichTextBox（MarkDownモード）と1つのプレーンな
    /// TextBox（ソースモード）を中心に、フォルダペイン・アウトラインペインを備えたWYSIWYG
    /// MarkDownエディタ。実際の編集ロジックは役割ごとに独立したクラスへ委譲している。
    /// </summary>
    public partial class MainWindow : Window
    {
        // ======================================================================
        //  状態（フィールド）
        // ======================================================================

        /// <summary>ソースモード（生テキスト表示）中はtrue。</summary>
        private bool m_isSourceModeFlg = false;

        /// <summary>ユーザーの入力ではなく、コードによる変更を行っている間だけtrueにするガード
        /// フラグ。TextChangedハンドラの自動変換ロジックが、プログラムによる変更にまで
        /// 反応してしまわないようにするためのもの。</summary>
        private bool m_isProgrammaticChangeFlg = false;

        /// <summary>IME固まり不具合調査用の心拍ログタイマー。ガベージコレクションで消えないよう、
        /// フィールドとして保持しておく。</summary>
        private System.Windows.Threading.DispatcherTimer m_imeDebugHeartbeatTimer;

        /// <summary>検索結果のハイライト（背景色）を適用/解除している間だけtrueにするガード
        /// フラグ。TextChangedがこれを見て、ダーティ扱いにしないようにする。</summary>
        private bool m_isApplyingHighlightFlg = false;

        /// <summary>現在エディタに表示中のファイルの絶対パス。未保存なら null。</summary>
        private string m_currentFilePath = null;

        /// <summary>currentFilePathの保存先フォルダ（画像の相対パス解決で毎回計算し直さずに
        /// 済むようキャッシュしている）。</summary>
        private string m_currentFileDirectory = null;

        /// <summary>まだディスクに書き出されていない、メモリ上だけの編集内容（フォルダ全体の
        /// 置換、または編集中のファイルから離れた際に発生する）。キー=絶対パス、値=そのファイルの
        /// 現在のMarkDown内容。</summary>
        private readonly Dictionary<string, string> m_pendingFileEdits =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>現在のファイル（m_currentFilePath）に未保存の変更があるかどうか。</summary>
        private bool m_currentFileIsDirtyFlg = false;

        /// <summary>エディタに適用中のズーム倍率（1.0が100%）。</summary>
        private double m_zoomLevel = 1.0;
        private double m_editorLineHeight = 26;
        private bool m_requireCtrlForLinkClickFlg = true;

        /// <summary>段落中の途中改行（空行を伴わない、ソース上の単純な改行）を、そのまま
        /// 見た目の改行として表示するか（true。既定値。mde/Typoraの従来動作）、それとも
        /// 空行が入るまでは改行しない、CommonMark/VSCodeのMarkDownプレビュー標準の表示に
        /// するか（false）。メニュー「表示」→「段落中の改行」で切り替えられる。</summary>
        private bool m_preserveSourceLineBreaksFlg = true;

        /// <summary>PDF書き出し時の、上下左右の余白（px）。メニュー「PDFの余白を設定…」で
        /// 変更でき、次回起動時にも復元される。既定値はAppSettingsのものと揃えてある。</summary>
        private double m_pdfMarginTop = 64;
        private double m_pdfMarginBottom = 64;
        private double m_pdfMarginLeft = 80;
        private double m_pdfMarginRight = 80;

        /// <summary>メニュー「ファイル」→「最近使ったファイル」に表示する、最近開いた/保存した
        /// ファイルの絶対パス一覧（先頭が最新）。次回起動時にも復元される。</summary>
        private List<string> m_recentFiles = new List<string>();
        private const int MAX_RECENT_FILES = 10;

        /// <summary>このウィンドウ専用の一時フォルダ識別子（複数ウィンドウを同時に開いた際、
        /// ドラッグ挿入した画像のファイル名が衝突しないようにするため）。</summary>
        private readonly string m_instanceTempId = Guid.NewGuid().ToString("N");

        /// <summary>起動時に読み込んだ、前回終了時のウィンドウ状態設定。</summary>
        private AppSettings m_savedSettings;

        /// <summary>アプリ起動時のコマンドライン引数を使ってよいのは最初の1つのウィンドウだけ、
        /// という制御のための静的フラグ（プロセス全体で共有される）。</summary>
        private static bool m_isFirstWindowInstanceFlg = true;

        /// <summary>現在開いている検索と置換ウィンドウ（Ctrl+Fなどで、同時に2つ開かず既存の
        /// ものを前面に出すために使う）。開いていなければ null。</summary>
        private FindReplaceWindow m_openFindReplaceWindow;

        // フォルダ/アウトラインペインの表示・非表示状態
        private double m_lastFolderColumnWidth = 190;
        private double m_lastOutlineColumnWidth = 210;
        private bool m_folderPaneVisibleFlg = false;
        private bool m_outlinePaneVisibleFlg = false;

        // 右クリック時の対象（表・コードブロックまわりはTableEditor/InlineStyleEditorに
        // それぞれ専用のプロパティがあるので、ここではまだどのクラスにも属さないものだけを保持する）
        private Paragraph m_ctxParagraph;

        // ======================================================================
        //  各機能クラス（協力オブジェクト）
        // ======================================================================

        private readonly OriginalTextTracker m_originalTextTracker;
        private readonly LineEndingTracker m_lineEndingTracker;
        private readonly ImageManager m_imageManager;
        private readonly MarkdownConverter m_markdownConverter;
        private readonly ListEditor m_listEditor;
        private readonly HeadingCodeBlockEditor m_headingCodeBlockEditor;
        private readonly TableEditor m_tableEditor;
        private readonly InlineStyleEditor m_inlineStyleEditor;
        private readonly SearchReplaceService m_searchReplaceService;
        private readonly OutlineManager m_outlineManager;
        private readonly FolderTreeManager m_folderTreeManager;

        /// <summary>ウィンドウを初期化し、各機能クラスを構築・配線したうえで、初回起動時の
        /// 案内文書を読み込む。</summary>
        public MainWindow()
        {
            InitializeComponent();

            m_savedSettings = AppSettings.Load();
            if (!double.IsNaN(m_savedSettings.WindowLeft) && !double.IsNaN(m_savedSettings.WindowTop))
            {
                this.WindowStartupLocation = WindowStartupLocation.Manual;
                this.Left = m_savedSettings.WindowLeft;
                this.Top = m_savedSettings.WindowTop;
            }
            this.Width = m_savedSettings.WindowWidth;
            this.Height = m_savedSettings.WindowHeight;
            m_zoomLevel = m_savedSettings.ZoomLevel;
            m_editorLineHeight = m_savedSettings.EditorLineHeight > 0 ? m_savedSettings.EditorLineHeight : 26;
            m_requireCtrlForLinkClickFlg = m_savedSettings.RequireCtrlForLinkClickFlg;
            m_preserveSourceLineBreaksFlg = m_savedSettings.PreserveSourceLineBreaksFlg;
            m_pdfMarginTop = m_savedSettings.PdfMarginTop > 0 ? m_savedSettings.PdfMarginTop : 64;
            m_pdfMarginBottom = m_savedSettings.PdfMarginBottom > 0 ? m_savedSettings.PdfMarginBottom : 64;
            m_pdfMarginLeft = m_savedSettings.PdfMarginLeft > 0 ? m_savedSettings.PdfMarginLeft : 80;
            m_pdfMarginRight = m_savedSettings.PdfMarginRight > 0 ? m_savedSettings.PdfMarginRight : 80;
            m_recentFiles = null != m_savedSettings.RecentFiles ? new List<string>(m_savedSettings.RecentFiles) : new List<string>();
            RebuildRecentFilesMenu();
            ApplyEditorLineHeight(m_editorLineHeight);
            UpdateLinkModeMenuChecks();
            UpdateLineBreakModeMenuChecks();
            m_folderPaneVisibleFlg = m_savedSettings.FolderPaneVisible;
            m_outlinePaneVisibleFlg = m_savedSettings.OutlinePaneVisible;
            if (m_savedSettings.FolderPaneWidth > 0)
            {
                m_lastFolderColumnWidth = m_savedSettings.FolderPaneWidth;
            }
            if (m_savedSettings.OutlinePaneWidth > 0)
            {
                m_lastOutlineColumnWidth = m_savedSettings.OutlinePaneWidth;
            }

            m_originalTextTracker = new OriginalTextTracker(m_editor);
            m_lineEndingTracker = new LineEndingTracker(PathsReferToSameFile);
            m_outlineManager = new OutlineManager(m_editor);
            m_outlineManager.HeadingSelected += ScrollOutlineListToEntry;
            m_imageManager = new ImageManager(
                m_editor, m_originalTextTracker, () => m_isSourceModeFlg, () => m_currentFileDirectory,
                () => m_currentFilePath,
                RunAsProgrammaticChange, m_outlineManager.Refresh, m_instanceTempId);
            m_markdownConverter = new MarkdownConverter(m_originalTextTracker, m_imageManager, () => m_preserveSourceLineBreaksFlg);
            m_listEditor = new ListEditor(m_editor, m_originalTextTracker, RunAsProgrammaticChange);
            m_headingCodeBlockEditor = new HeadingCodeBlockEditor(m_editor, m_originalTextTracker, RunAsProgrammaticChange);
            m_tableEditor = new TableEditor(
                m_editor, m_originalTextTracker, MarkDirty, RunAsProgrammaticChange, () => m_isSourceModeFlg,
                m_outlineManager.Refresh, InsertPlainTextWithLineBreaksForCodeBlock);
            m_folderTreeManager = new FolderTreeManager(
                LoadFile, () => m_currentFilePath, () => m_currentFileIsDirtyFlg,
                () => m_pendingFileEdits.Keys, PathsReferToSameFile);
            m_folderTreeManager.NodeSelected += ScrollFolderTreeToNode;
            m_inlineStyleEditor = new InlineStyleEditor(
                m_editor, m_originalTextTracker, RunAsProgrammaticChange, MarkDirty, m_outlineManager.Refresh,
                m_markdownConverter.BlockToMarkdown, () => m_currentFileDirectory, LoadFile,
                m_folderTreeManager.IsWithinLoadedFolder, OpenFileInNewWindow, () => m_requireCtrlForLinkClickFlg);
            m_searchReplaceService = new SearchReplaceService(
                m_editor, m_sourceEditor, m_markdownConverter, m_originalTextTracker, m_lineEndingTracker,
                () => m_isSourceModeFlg, RunAsProgrammaticChange, m_outlineManager.Refresh, p => OutlineManager.ScrollParagraphToTop(p, m_editor),
                () => m_folderTreeManager.LoadedFolderRootPath, GetCurrentContentForFile,
                SetFileContentForReplaceImpl, LoadFile, RunWithoutDirtyMarking, m_outlineManager.MarkSearchMatches,
                m_outlineManager.SelectHeadingForPosition);

            this.Title = Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version;
            m_outlineList.ItemsSource = m_outlineManager.Items;
            m_folderTree.ItemsSource = m_folderTreeManager.Roots;
            DataObject.AddCopyingHandler(m_editor, m_tableEditor.HandleCopying);
            DataObject.AddPastingHandler(m_editor, m_tableEditor.HandlePasting);
            DataObject.AddPastingHandler(m_editor, EditorHandleInlineMarkdownPasting);

            // タスクリストのチェックボックス（[ ]/[x]）のトグルを検知する。埋め込まれた各
            // CheckBoxに個別のハンドラを付けるのではなく、ToggleButton.CheckedEvent/
            // UncheckedEventがルーティングイベントとしてm_editorまでバブリングしてくることを
            // 利用し、m_editor自身に登録した単一のハンドラでまとめて受け取る。
            m_editor.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(TaskCheckboxToggled));
            m_editor.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(TaskCheckboxToggled));

            // IME固まり不具合調査用のデバッグログ。フォーカスの実際の移り変わりと、実際に
            // 入力されてくる文字（IME変換中のものも含む）の流れを時系列で記録しておくことで、
            // 症状発生時にどちらが先に・どういう順序で起きているのかを、実機の動画だけに
            // 頼らず後から追えるようにする。
            m_editor.GotKeyboardFocus += (a_s, a_e) =>
                DebugLogger.Log($"Editor.GotKeyboardFocus: Old={a_e.OldFocus} New={a_e.NewFocus}");
            m_editor.LostKeyboardFocus += (a_s, a_e) =>
                DebugLogger.Log($"Editor.LostKeyboardFocus: Old={a_e.OldFocus} New={a_e.NewFocus} " +
                    $"ForegroundWindow={DebugLogger.DescribeForegroundWindow()}");
            m_editor.PreviewTextInput += (a_s, a_e) =>
                DebugLogger.Log(
                    $"Editor.PreviewTextInput: Text=\"{a_e.Text}\" " +
                    $"CompositionText=\"{a_e.TextComposition?.CompositionText}\" " +
                    $"SystemCompositionText=\"{a_e.TextComposition?.SystemCompositionText}\" " +
                    $"ControlText=\"{a_e.TextComposition?.ControlText}\"");
            m_editor.TextInput += (a_s, a_e) =>
                DebugLogger.Log(
                    $"Editor.TextInput: Text=\"{a_e.Text}\" " +
                    $"CompositionText=\"{a_e.TextComposition?.CompositionText}\"");

            // IME固まり調査用：症状発生時、何もイベントが起きない「無音」の期間が観測される
            // ことがあるため、イベント任せではなく、一定間隔で強制的に現在の状態（WPFの論理
            // フォーカスと、実際のOSレベルのフォアグラウンドウィンドウの両方）を記録する
            // 「心拍」ログも合わせて出す。
            m_imeDebugHeartbeatTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            m_imeDebugHeartbeatTimer.Tick += (a_s, a_e) =>
                DebugLogger.Log(
                    $"heartbeat: EditorIsFocused={m_editor.IsFocused} " +
                    $"EditorIsKeyboardFocused={m_editor.IsKeyboardFocused} " +
                    $"Keyboard.FocusedElement={Keyboard.FocusedElement} " +
                    $"WindowIsActive={this.IsActive} " +
                    $"ForegroundWindow={DebugLogger.DescribeForegroundWindow()}");
            m_imeDebugHeartbeatTimer.Start();

            // 起動時引数でMarkDownファイルのパスを受け取っていれば、そちらを開く
            // （ファイルの関連付けからのダブルクリック起動などに対応するため）。
            string startupFilePath = ResolveStartupFilePath();
            if (!string.IsNullOrEmpty(startupFilePath) && File.Exists(startupFilePath))
            {
                LoadFile(startupFilePath);
                m_folderTreeManager.SelectFileNode(startupFilePath);
            }

            ApplyFolderPaneVisibility();
            ApplyOutlinePaneVisibility();
            SetZoom(m_zoomLevel);
            if (m_savedSettings.IsMaximized)
            {
                this.WindowState = WindowState.Maximized;
            }
        }

        /// <summary>ウィンドウを閉じようとした時、未保存の変更があれば確認する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void WindowClosing(object a_sender, System.ComponentModel.CancelEventArgs a_args)
        {
            if (m_currentFileIsDirtyFlg || m_pendingFileEdits.Count > 0)
            {
                var result = MessageBox.Show(
                    "保存されていない変更があります。破棄して閉じますか？",
                    "閉じる", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (result != MessageBoxResult.OK)
                {
                    a_args.Cancel = true;
                }
            }
        }

        /// <summary>
        /// 起動時のコマンドライン引数からMarkDownファイルのパスを取り出す。ファイルの関連付けで
        /// 「プログラムから開く」を使った場合や、コマンドプロンプトから直接パスを指定して
        /// 起動した場合などに対応するためのもの。引数が無ければnullを返す。
        /// </summary>
        /// <returns>解決できた絶対パス。引数が無い、または解決に失敗した場合はnull。</returns>
        private string ResolveStartupFilePath()
        {
            // 起動時引数は、プロセス全体で共通の情報であり、ウィンドウ単位のものではない。
            // 「新しいウィンドウ」やファイルリンクからの別ウィンドウ起動でも同じ引数が
            // 見えてしまうため、最初の1つのウィンドウでだけ使うようにする。
            if (!m_isFirstWindowInstanceFlg)
            {
                return null;
            }
            m_isFirstWindowInstanceFlg = false;

            try
            {
                string[] args = Environment.GetCommandLineArgs();
                // args[0]は実行ファイル自身のパスなので、実際の引数はargs[1]以降になる。
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                {
                    return null;
                }
                return Path.GetFullPath(args[1]);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>ウィンドウを閉じる際、このウィンドウ専用の一時画像フォルダを削除し、
        /// 次回起動時に復元するウィンドウ状態を保存する。</summary>
        /// <param name="a_args">イベントの引数。</param>
        protected override void OnClosed(EventArgs a_args)
        {
            base.OnClosed(a_args);
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "mde", m_instanceTempId);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // 削除できなくても致命的ではない（ベストエフォート）
            }

            bool maximizedFlg = this.WindowState == WindowState.Maximized;
            Rect bounds = maximizedFlg ? this.RestoreBounds : new Rect(this.Left, this.Top, this.Width, this.Height);
            // 表示中のペインについては、その時点の実際の幅（スプリッタでドラッグ調整された
            // 最新の値）を保存する。非表示のペインについては、直前に表示していた時の幅を使う。
            double folderWidthToSave = (m_folderPaneVisibleFlg && m_folderColumnDef.Width.Value > 0)
                ? m_folderColumnDef.Width.Value : m_lastFolderColumnWidth;
            double outlineWidthToSave = (m_outlinePaneVisibleFlg && m_outlineColumnDef.Width.Value > 0)
                ? m_outlineColumnDef.Width.Value : m_lastOutlineColumnWidth;

            var settings = new AppSettings
            {
                IsMaximized = maximizedFlg,
                WindowWidth = bounds.Width > 0 ? bounds.Width : m_savedSettings.WindowWidth,
                WindowHeight = bounds.Height > 0 ? bounds.Height : m_savedSettings.WindowHeight,
                WindowLeft = bounds.X,
                WindowTop = bounds.Y,
                FolderPaneVisible = m_folderPaneVisibleFlg,
                OutlinePaneVisible = m_outlinePaneVisibleFlg,
                FolderPaneWidth = folderWidthToSave,
                OutlinePaneWidth = outlineWidthToSave,
                ZoomLevel = m_zoomLevel,
                EditorLineHeight = m_editorLineHeight,
                RequireCtrlForLinkClickFlg = m_requireCtrlForLinkClickFlg,
                PreserveSourceLineBreaksFlg = m_preserveSourceLineBreaksFlg,
                PdfMarginTop = m_pdfMarginTop,
                PdfMarginBottom = m_pdfMarginBottom,
                PdfMarginLeft = m_pdfMarginLeft,
                PdfMarginRight = m_pdfMarginRight,
                RecentFiles = m_recentFiles
            };
            settings.Save();
        }

        // ======================================================================
        //  各クラスへ渡す共通delegateの実装
        // ======================================================================

        /// <summary>渡された処理を「プログラムによる変更」として実行する。実行中は
        /// Editor_TextChangedの自動変換ロジック（*や#での自動変換など）が働かない。</summary>
        /// <param name="a_action">実行する処理。</param>
        private void RunAsProgrammaticChange(Action a_action)
        {
            m_isProgrammaticChangeFlg = true;
            try { a_action(); }
            finally { m_isProgrammaticChangeFlg = false; }
        }

        /// <summary>渡された処理を「ハイライトの適用/解除のみ」として実行する。実行中は
        /// Editor_TextChangedがダーティ扱い・アウトライン再構築・元テキスト保持の破棄を
        /// 行わないようにする（検索結果の背景色変更は実際の編集ではないため）。</summary>
        /// <param name="a_action">実行する処理。</param>
        private void RunWithoutDirtyMarking(Action a_action)
        {
            m_isApplyingHighlightFlg = true;
            try { a_action(); }
            finally { m_isApplyingHighlightFlg = false; }
        }

        /// <summary>現在のファイルに未保存の変更があることを記録し、フォルダツリーの表示も
        /// 更新する。</summary>
        private void MarkDirty()
        {
            m_currentFileIsDirtyFlg = true;
            m_folderTreeManager.RefreshDirtyMarkers();
        }

        /// <summary>
        /// タスクリストのチェックボックス（[ ]/[x]）が変更された段落を「編集済み」として記録し
        /// （元テキストのままではチェック状態の変化が保存に反映されないため）、ファイルを
        /// 未保存扱いにする。CheckBox（FrameworkElementの階層）→ InlineUIContainer
        /// （TextElementの階層）→ Paragraph、と異なるクラス階層をまたいで親をたどる必要がある
        /// （WPFの制約）。
        /// </summary>
        /// <param name="a_checkBoxOrDescendant">トグルされたCheckBox（またはその子孫の要素）。</param>
        private void InvalidateTaskCheckboxBlock(DependencyObject a_checkBoxOrDescendant)
        {
            DependencyObject node = a_checkBoxOrDescendant;
            while (null != node && !(node is Paragraph))
            {
                node = (node is FrameworkElement fe) ? fe.Parent : (node as TextElement)?.Parent;
            }
            if (node is Paragraph para)
            {
                m_originalTextTracker.InvalidateForBlock(para);
                MarkDirty();
            }
        }

        /// <summary>
        /// タスクリストのチェックボックス（[ ]/[x]）がクリックされてチェック状態が変わった時の
        /// 処理。埋め込まれた各CheckBoxに個別のハンドラを付けるのではなく、ToggleButton.
        /// CheckedEvent/UncheckedEventがルーティングイベントとしてm_editorまでバブリング
        /// してくることを利用し、コンストラクタでm_editor自身に登録した単一のハンドラで
        /// まとめて受け取る。
        /// 実際の「元テキスト保持」の記憶破棄（InvalidateTaskCheckboxBlock）は、クリックを
        /// 検出する`EditorPreviewMouseLeftButtonDown`側で既に同期的に呼んでいるため、この
        /// ハンドラは保険（キーボード操作など、将来他の経路でIsCheckedが変わった場合の
        /// 受け皿）として残してある。
        /// </summary>
        /// <param name="a_sender">イベントの発生元（トグルされたCheckBox）。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void TaskCheckboxToggled(object a_sender, RoutedEventArgs a_args)
        {
            if (m_isProgrammaticChangeFlg)
            {
                return;
            }
            if (!(a_sender is CheckBox cb) || "task-checkbox" != (cb.Tag as string))
            {
                return;
            }
            InvalidateTaskCheckboxBlock(cb);
        }

        /// <summary>2つのパスが同一ファイルを指しているかどうかを調べる（大文字小文字を
        /// 区別せず、完全パスで比較する）。</summary>
        /// <param name="a_a">比較対象の1つ目。</param>
        /// <param name="a_b">比較対象の2つ目。</param>
        /// <returns>同一ファイルを指していればtrue。</returns>
        private bool PathsReferToSameFile(string a_a, string a_b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a_a), Path.GetFullPath(a_b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>コードブロックへの貼り付け時に使う、改行を保ったプレーンテキスト挿入。
        /// TableEditorの貼り付け処理から呼ばれる。</summary>
        /// <param name="a_text">対象の文字列。</param>
        private void InsertPlainTextWithLineBreaksForCodeBlock(string a_text)
        {
            m_originalTextTracker.Invalidate(m_editor.CaretPosition);
            var lines = a_text.Replace("\r\n", "\n").Split('\n');
            RunAsProgrammaticChange(() =>
            {
                m_editor.Selection.Text = lines[0];
                m_editor.CaretPosition = m_editor.Selection.End;
                for (int i = 1; i < lines.Length; i++)
                {
                    m_editor.CaretPosition = m_editor.CaretPosition.InsertLineBreak();
                    m_editor.Selection.Select(m_editor.CaretPosition, m_editor.CaretPosition);
                    m_editor.Selection.Text = lines[i];
                    m_editor.CaretPosition = m_editor.Selection.End;
                }
            });
        }

        /// <summary>
        /// フォルダペインに表示されていない範囲のファイルへのリンクをクリックした際、現在の
        /// ウィンドウの文書を置き換えるのではなく、新しいウィンドウでそのファイルを開く。
        /// </summary>
        /// <param name="a_path">開くファイルの絶対パス。</param>
        /// <param name="a_anchor">開いたあとにジャンプする見出し/アンカーのテキスト（無ければnull）。</param>
        private void OpenFileInNewWindow(string a_path, string a_anchor)
        {
            var newWindow = new MainWindow();
            newWindow.Show();
            newWindow.LoadFile(a_path);
            if (!string.IsNullOrEmpty(a_anchor))
            {
                newWindow.m_inlineStyleEditor.JumpToAnchor(a_anchor);
            }
        }

        /// <summary>
        /// ファイルの「今の内容」を解決する：現在開いているファイルならライブなエディタ内容、
        /// 保留中の編集があればそれ、どちらでもなければ null（呼び出し側がディスクから
        /// 読み込む）。
        /// </summary>
        /// <param name="a_path">対象のファイルパス。</param>
        /// <returns>そのファイルの現在の内容。該当がなければnull。</returns>
        private string GetCurrentContentForFile(string a_path)
        {
            if (!string.IsNullOrEmpty(m_currentFilePath) && PathsReferToSameFile(a_path, m_currentFilePath))
            {
                return m_isSourceModeFlg ? m_sourceEditor.Text : m_markdownConverter.DocumentToMarkdown(m_editor.Document);
            }

            foreach (var kv in m_pendingFileEdits)
            {
                if (PathsReferToSameFile(kv.Key, a_path))
                {
                    return kv.Value;
                }
            }

            return null;
        }

        /// <summary>検索・置換の結果をファイルへ反映する：現在開いているファイルならライブな
        /// エディタへ直接、そうでなければ保留中の編集として記憶する。</summary>
        /// <param name="a_path">対象のファイルパス。</param>
        /// <param name="a_newContent">新しい内容。</param>
        private void SetFileContentForReplaceImpl(string a_path, string a_newContent)
        {
            if (!string.IsNullOrEmpty(m_currentFilePath) && PathsReferToSameFile(a_path, m_currentFilePath))
            {
                if (m_isSourceModeFlg)
                {
                    m_sourceEditor.Text = a_newContent;
                }
                else
                {
                    RunAsProgrammaticChange(() => m_markdownConverter.MarkdownToDocument(a_newContent, m_editor.Document));
                    m_outlineManager.Refresh();
                }
            }
            else
            {
                m_pendingFileEdits[a_path] = a_newContent;
                m_folderTreeManager.RefreshDirtyMarkers();
            }
        }

        // ======================================================================
        //  入力中の自動変換の起点（EditorTextChanged）
        // ======================================================================

        /// <summary>
        /// MarkDownモードのメイン変更ハンドラ：ファイルをダーティにし、アウトラインを再構築し、
        /// 触れたブロックの「元テキスト保持」の記憶を破棄する。実際のユーザー入力によるものだけ
        /// （プログラムによる変更でなければ）、インライン装飾・箇条書き/見出しへの自動変換の
        /// トリガーもチェックする。
        /// </summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void EditorTextChanged(object a_sender, TextChangedEventArgs a_args)
        {
            DebugLogger.Log(
                $"EditorTextChanged: m_isProgrammaticChangeFlg={m_isProgrammaticChangeFlg} " +
                $"IsFocused={m_editor.IsFocused} Changes={a_args.Changes.Count}");
            // RichTextBoxはInitializeComponent中に、既定の空文書を設定する際にTextChangedを
            // 発生させることがある。その時点ではコンストラクタでの各クラスの構築がまだ
            // 完了していない可能性があるため、念のためガードしておく。
            if (null == m_outlineManager ||
                null == m_folderTreeManager ||
                null == m_originalTextTracker) return;

            if (m_isSourceModeFlg)
            {
                return;
            }

            // 検索結果のハイライト（背景色）の適用/解除も、WPFの仕様上TextChangedを発生させて
            // しまうが、これは実際の編集ではないため、ダーティ扱いにしたり元テキスト保持の
            // 記憶を破棄したりしてはいけない。
            if (m_isApplyingHighlightFlg)
            {
                return;
            }

            m_outlineManager.Refresh();

            m_currentFileIsDirtyFlg = true;
            m_folderTreeManager.RefreshDirtyMarkers();
            m_originalTextTracker.Invalidate(m_editor.CaretPosition);

            if (m_isProgrammaticChangeFlg)
            {
                return;
            }

            var para = m_editor.CaretPosition?.Paragraph;
            if (null == para)
            {
                return;
            }
            if (para.Tag is CodeBlockInfo)
            {
                return; // コードブロック内は自動整形しない
            }

            if (m_inlineStyleEditor.CheckInlineFormatTrigger())
            {
                return;
            }

            // タスクリスト（"[ ] "/"[x] "）のライブ入力変換。タスクチェックボックスへの変換は、
            // リスト項目自身の段落（トップレベルではない）に対して行う必要があるため、直後の
            // トップレベル限定ガードより前でチェックする。
            if (para.Parent is ListItem && null == para.Tag)
            {
                // TextRange(para.ContentStart, para.ContentEnd).Textは、リスト項目内では行頭の
                // マーカー記号（「•」等）まで文字列に含んでしまうことがある
                // （InlineStyleEditor.GetSafeRangeTextの説明を参照）ため、マーカー記号を
                // 拾わない安全な取得方法を使う。
                string liText = m_listEditor.GetParagraphPlainText(para);
                var taskMatch = Regex.Match(liText, "^\\[([ xX])\\][ \u00A0]$");
                if (taskMatch.Success)
                {
                    m_listEditor.ConvertListItemTextToTaskCheckbox(para, " " != taskMatch.Groups[1].Value);
                    return;
                }
            }

            if (!(para.Parent is FlowDocument))
            {
                return; // 箇条書き/見出しへの自動変換はトップレベル段落のみ
            }

            string text = new TextRange(para.ContentStart, para.ContentEnd).Text;
            text = text.TrimEnd('\r', '\n');

            var bulletMatch = Regex.Match(text, "^([*+-])[ \u00A0]$");
            if (bulletMatch.Success)
            {
                m_listEditor.ConvertParagraphToListItem(para, bulletMatch.Groups[1].Value, false);
                return;
            }
            var orderedMatch = Regex.Match(text, "^\\d+\\.[ \u00A0]$");
            if (orderedMatch.Success)
            {
                m_listEditor.ConvertParagraphToListItem(para, null, true);
                return;
            }
            var m = Regex.Match(text, "^(#{1,6})[ \u00A0]$");
            if (m.Success)
            {
                m_headingCodeBlockEditor.ConvertParagraphToHeading(para, m.Groups[1].Value.Length);
                return;
            }
            // 水平線（---/***/___）のライブ入力変換。バッチ変換（MarkdownConverter）と同じ
            // 正規表現を使い、両者の判定基準を一致させている。見出しと異なりトリガーとなる
            // 区切り文字（スペース等）が存在しないため、段落のテキストがパターンに完全一致
            // した時点（マーカーが3文字揃った時点）で直ちに変換する。
            var hrMatch = Regex.Match(text, "^ {0,3}([-*_])\\1{2,}\\s*$");
            if (hrMatch.Success)
            {
                m_headingCodeBlockEditor.ConvertParagraphToHorizontalRule(para);
            }
        }

        // ======================================================================
        //  キー入力の振り分け（Tab / Enter / 表内の矢印キー）
        // ======================================================================

        /// <summary>
        /// 箇条書き/順序付きリストへの変換の入口（スペースキー）。印字可能な文字（スペースを含む）は
        /// WPFではPreviewKeyDownではなくPreviewTextInputを通じて挿入されるため、PreviewKeyDownで
        /// a_args.Handled=trueを設定するだけでは確実に文字の挿入を防げない。このイベントで直接
        /// 判定・処理することで、変換後に元のスペース文字が余分に残ってしまう不具合を防ぐ。
        /// </summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void EditorPreviewTextInput(object a_sender, TextCompositionEventArgs a_args)
        {
            if (m_isSourceModeFlg || " " != a_args.Text)
            {
                return;
            }

            var para = m_editor.CaretPosition?.Paragraph;
            if (null == para || !(para.Parent is FlowDocument) || para.Tag is CodeBlockInfo)
            {
                return;
            }
            if (0 != m_editor.CaretPosition.CompareTo(para.ContentEnd))
            {
                return;
            }

            string beforeSpace = new TextRange(para.ContentStart, para.ContentEnd).Text.TrimEnd('\r', '\n');

            var bulletKeyMatch = Regex.Match(beforeSpace, "^([*+-])$");
            if (bulletKeyMatch.Success)
            {
                a_args.Handled = true;
                m_listEditor.ConvertParagraphToListItem(para, bulletKeyMatch.Groups[1].Value, false);
                return;
            }
            var orderedKeyMatch = Regex.Match(beforeSpace, "^\\d+\\.$");
            if (orderedKeyMatch.Success)
            {
                a_args.Handled = true;
                m_listEditor.ConvertParagraphToListItem(para, null, true);
            }
        }

        /// <summary>`TextPointer.Paragraph`を段落解決の一次手段として使い、nullが返った場合は
        /// `TextPointer.Parent`から上へ辿って囲んでいるParagraphを探すフォールバックを行う。
        /// 表のセルなど入れ子になった構造の中では`.Paragraph`が期待通りに解決しないことがある
        /// ため、その対策。</summary>
        /// <param name="a_position">解決したい位置。</param>
        /// <returns>囲んでいるParagraph（見つからなければnull）。</returns>
        private static Paragraph ResolveParagraph(TextPointer a_position)
        {
            if (a_position?.Paragraph is Paragraph direct)
            {
                return direct;
            }
            DependencyObject node = a_position?.Parent;
            while (null != node)
            {
                if (node is Paragraph p)
                {
                    return p;
                }
                node = (node as TextElement)?.Parent;
            }
            return null;
        }

        /// <summary>
        /// 他アプリ（他のMarkDownアプリ含む）や、Xaml/Rtf形式を伴わない生のテキストが
        /// クリップボードから貼り付けられた時、そのテキストをMarkDownのインライン記法
        /// （**太字**・~~取り消し線~~・&lt;u&gt;下線&lt;/u&gt;・`コード`・[リンク](url)等）として
        /// 解釈しながら挿入する。mde自身や他のリッチテキストアプリ（Xaml/Rtf形式を伴う）からの
        /// 貼り付けは、WPF標準のリッチテキスト貼り付けにそのまま任せる（何もしない）。
        /// コードブロックへの貼り付けは`TableEditor.HandlePasting`が先にリテラル挿入として
        /// 処理し`CancelCommand()`するため、ここには来ない。表としての貼り付け
        /// （Excel等からのHTML/TSV）も同様に`TableEditor.HandlePasting`が先に処理する
        /// （`DataObject.AddPastingHandler`は登録順にすべてのハンドラを呼ぶため、
        /// `a_args.CommandCancelled`を確認してから処理する）。
        /// </summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">貼り付けイベントの引数。</param>
        private void EditorHandleInlineMarkdownPasting(object a_sender, DataObjectPastingEventArgs a_args)
        {
            if (m_isSourceModeFlg ||
                a_args.CommandCancelled)
            {
                return;
            }
            if (a_args.SourceDataObject.GetDataPresent(DataFormats.Xaml) ||
                a_args.SourceDataObject.GetDataPresent(DataFormats.Rtf))
            {
                return; // リッチな形式が付いている場合はWPF標準の貼り付けに任せる
            }
            var para = ResolveParagraph(m_editor.CaretPosition);
            if (null != para && para.Tag is CodeBlockInfo)
            {
                return; // コードブロックへの貼り付けはTableEditor.HandlePastingが処理する
            }
            if (!a_args.SourceDataObject.GetDataPresent(DataFormats.Text))
            {
                return;
            }
            string text = (string)a_args.SourceDataObject.GetData(DataFormats.Text);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            a_args.CancelCommand();
            m_originalTextTracker.Invalidate(m_editor.CaretPosition);
            RunAsProgrammaticChange(() =>
            {
                if (!m_editor.Selection.IsEmpty)
                {
                    m_editor.Selection.Text = "";
                }
                TextPointer end = m_markdownConverter.InsertInlineMarkdownAtPosition(m_editor.CaretPosition, text);
                m_editor.CaretPosition = end;
                m_editor.Selection.Select(end, end);
            });
            m_outlineManager.Refresh();
            MarkDirty();
        }

        /// <summary>
        /// キー入力の中心的な振り分け役。箇条書き項目・見出し・コードブロック・表セルの
        /// いずれにキャレットがあるかに応じて、Tab/Shift+Tab/Enter/矢印キーの処理を
        /// 対応するクラスへ委譲する。
        /// </summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void EditorPreviewKeyDown(object a_sender, KeyEventArgs a_args)
        {
            if (m_isSourceModeFlg)
            {
                return;
            }
            var para = ResolveParagraph(m_editor.CaretPosition);
            if (null == para)
            {
                return;
            }

            if (a_args.Key == Key.Enter)
            {
                if (m_listEditor.IsInListItem(para, out ListItem li, out List parentList))
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        a_args.Handled = true;
                        m_headingCodeBlockEditor.InsertLineBreakAtCaret();
                        return;
                    }

                    a_args.Handled = true;
                    m_listEditor.HandleListEnter(li, parentList);
                    return;
                }
                if (para.Tag is int level && level > 0)
                {
                    a_args.Handled = true;
                    m_headingCodeBlockEditor.HandleHeadingEnter(para);
                    return;
                }
                if (para.Tag is CodeBlockInfo)
                {
                    a_args.Handled = true;
                    m_headingCodeBlockEditor.InsertLineBreakAtCaret();
                    return;
                }
                if (para.Parent is TableCell)
                {
                    a_args.Handled = true;
                    m_headingCodeBlockEditor.InsertLineBreakAtCaret();
                    return;
                }
                if (para.Parent is FlowDocument)
                {
                    string plainText = new TextRange(para.ContentStart, para.ContentEnd).Text.TrimEnd('\r', '\n');
                    var fenceMatch = Regex.Match(plainText, "^```(\\S*)$");
                    if (fenceMatch.Success)
                    {
                        a_args.Handled = true;
                        m_headingCodeBlockEditor.ConvertParagraphToCodeBlock(para, fenceMatch.Groups[1].Value);
                        return;
                    }
                }
                return; // 通常の段落: WPF標準の動作（新しい段落の作成）に任せる
            }

            if (a_args.Key == Key.Tab && m_listEditor.IsInListItem(para, out ListItem tabLi, out List tabList))
            {
                a_args.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    m_listEditor.OutdentListItem(tabLi, tabList);
                }
                else
                {
                    m_listEditor.IndentListItem(tabLi, tabList);
                }
                return;
            }

            if (a_args.Key == Key.Tab && para.Tag is CodeBlockInfo)
            {
                a_args.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    m_headingCodeBlockEditor.OutdentCodeLine(para);
                }
                else
                {
                    m_editor.Selection.Text = "\t";
                    m_editor.CaretPosition = m_editor.Selection.End;
                    m_editor.Selection.Select(m_editor.CaretPosition, m_editor.CaretPosition);
                }
                return;
            }

            if (para.Parent is TableCell cell)
            {
                if (a_args.Key == Key.Up ||
                    a_args.Key == Key.Down)
                {
                    a_args.Handled = true;
                    m_tableEditor.MoveVertical(cell, a_args.Key == Key.Up ? -1 : 1);
                }
                else if (a_args.Key == Key.Left && m_tableEditor.IsCaretAtStart(cell))
                {
                    a_args.Handled = true;
                    m_tableEditor.MoveHorizontal(cell, -1);
                }
                else if (a_args.Key == Key.Right && m_tableEditor.IsCaretAtEnd(cell))
                {
                    a_args.Handled = true;
                    m_tableEditor.MoveHorizontal(cell, 1);
                }
            }
        }

        // ======================================================================
        //  右クリックメニュー
        // ======================================================================

        /// <summary>
        /// 右クリック位置の下に何があるか（段落・表セル・画像・リンク）を判定し、対応する
        /// メニュー項目の表示/非表示を切り替える。判定結果は各クラスの専用プロパティ
        /// （TableEditor.ContextCellなど）へ格納し、以後のメニュー項目クリックから
        /// 参照できるようにする。
        /// </summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void EditorContextMenuOpening(object a_sender, ContextMenuEventArgs a_args)
        {
            if (m_isSourceModeFlg) { a_args.Handled = true; return; }

            Point pos = Mouse.GetPosition(m_editor);
            TextPointer tp = m_editor.GetPositionFromPoint(pos, true);
            m_ctxParagraph = tp?.Paragraph;
            m_tableEditor.ContextCell = m_ctxParagraph?.Parent as TableCell;
            m_tableEditor.ContextParagraph = m_ctxParagraph;
            m_inlineStyleEditor.ContextParagraph = m_ctxParagraph;

            var hit = VisualTreeHelper.HitTest(m_editor, pos);
            m_imageManager.ContextImage = FindVisualAncestorOrSelf<Image>(hit?.VisualHit);

            var linkRun = tp?.Parent as Run;
            m_inlineStyleEditor.ContextLinkRun = linkRun?.Tag is LinkInfo ? linkRun : null;

            bool inTableFlg = null != m_tableEditor.ContextCell;
            bool inCodeBlockFlg = m_ctxParagraph?.Tag is CodeBlockInfo;
            m_headingMenuItem.Visibility = inTableFlg ? Visibility.Collapsed : Visibility.Visible;
            m_insertTableMenuItem.Visibility = inTableFlg ? Visibility.Collapsed : Visibility.Visible;
            m_insertRowAboveMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_insertRowBelowMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_insertColumnLeftMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_insertColumnRightMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_deleteRowMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_deleteColumnMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_deleteTableMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_copyCodeBlockMenuItem.Visibility = inCodeBlockFlg ? Visibility.Visible : Visibility.Collapsed;
            m_openImageMenuItem.Visibility = null != m_imageManager.ContextImage ? Visibility.Visible : Visibility.Collapsed;
            m_saveImageMenuItem.Visibility = null != m_imageManager.ContextImage ? Visibility.Visible : Visibility.Collapsed;
            m_deleteImageMenuItem.Visibility = null != m_imageManager.ContextImage ? Visibility.Visible : Visibility.Collapsed;
            m_textStyleMenuItem.Visibility = (!m_editor.Selection.IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
            m_linkMenuItem.Visibility = null != m_inlineStyleEditor.ContextLinkRun ? Visibility.Visible : Visibility.Collapsed;
            m_toggleModeMenuItem.Header = m_isSourceModeFlg ? "MarkDownモードに切り替え" : "ソースモードに切り替え";
        }

        /// <summary>ソースモードの右クリックメニュー。カット/コピー/貼り付けのみで、独自の
        /// 項目は追加しない。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void SourceEditorContextMenuOpening(object a_sender, ContextMenuEventArgs a_args)
        {
        }

        // ---- 見出し ----

        /// <summary>右クリックメニューから見出しレベルを変更する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void HeadingItemClick(object a_sender, RoutedEventArgs a_args)
        {
            if (null == m_ctxParagraph)
            {
                return;
            }
            int level = int.Parse((string)((MenuItem)a_sender).Tag);
            m_headingCodeBlockEditor.ChangeHeadingLevel(m_ctxParagraph, level);
            m_outlineManager.Refresh();
            MarkDirty();
        }

        // ---- 表 ----

        private void InsertTableItemClick(object a_sender, RoutedEventArgs a_args)
        {
            m_tableEditor.ContextParagraph = m_ctxParagraph;
            var dlg = new TableSizeDialog { Owner = this };
            if (true == dlg.ShowDialog())
            {
                m_tableEditor.InsertTable(dlg.Rows, dlg.Columns);
            }
        }

        private void InsertRowAboveItemClick(object a_sender, RoutedEventArgs a_args) => m_tableEditor.InsertRow(a_aboveFlg: true);
        private void InsertRowBelowItemClick(object a_sender, RoutedEventArgs a_args) => m_tableEditor.InsertRow(a_aboveFlg: false);
        private void InsertColumnLeftItemClick(object a_sender, RoutedEventArgs a_args) => m_tableEditor.InsertColumn(a_leftFlg: true);
        private void InsertColumnRightItemClick(object a_sender, RoutedEventArgs a_args) => m_tableEditor.InsertColumn(a_leftFlg: false);
        private void DeleteRowItemClick(object a_sender, RoutedEventArgs a_args) => m_tableEditor.DeleteRow();
        private void DeleteColumnItemClick(object a_sender, RoutedEventArgs a_args) => m_tableEditor.DeleteColumn();
        private void DeleteTableItemClick(object a_sender, RoutedEventArgs a_args) => m_tableEditor.DeleteTable();

        // ---- 画像 ----

        /// <summary>右クリックメニュー「画像を開く」。ダブルクリック時と同じ
        /// ImageManager.OpenImageFileを、右クリック位置の画像（ContextImage）に対して呼ぶ。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void OpenImageItemClick(object a_sender, RoutedEventArgs a_args)
        {
            if (null != m_imageManager.ContextImage)
            {
                m_imageManager.OpenImageFile(m_imageManager.ContextImage);
            }
        }

        private void SaveImageItemClick(object a_sender, RoutedEventArgs a_args) => m_imageManager.SaveImageAs(this);

        /// <summary>右クリックメニュー「画像を削除」。右クリック位置の画像（ContextImage）を
        /// ImageManager.DeleteImageで削除する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void DeleteImageItemClick(object a_sender, RoutedEventArgs a_args)
        {
            if (null != m_imageManager.ContextImage)
            {
                m_imageManager.DeleteImage(m_imageManager.ContextImage);
            }
        }

        // ---- コードブロック ----

        private void CopyCodeBlockItemClick(object a_sender, RoutedEventArgs a_args) => m_inlineStyleEditor.CopyCodeBlockAsMarkdown();

        // ---- 文字装飾・リンク ----

        private void TextStyleItemClick(object a_sender, RoutedEventArgs a_args)
        {
            string style = (string)((MenuItem)a_sender).Tag;
            m_inlineStyleEditor.ApplyTextStyleFromMenu(style, this);
        }

        private void LinkOpenClick(object a_sender, RoutedEventArgs a_args) => m_inlineStyleEditor.OpenContextLink();
        private void LinkCopyUrlClick(object a_sender, RoutedEventArgs a_args) => m_inlineStyleEditor.CopyContextLinkUrl();
        private void LinkEditClick(object a_sender, RoutedEventArgs a_args) => m_inlineStyleEditor.EditContextLink(this);
        private void LinkRemoveClick(object a_sender, RoutedEventArgs a_args) => m_inlineStyleEditor.RemoveContextLink();

        /// <summary>エディタ上でのマウス左ボタン押下を処理する。ダブルクリック位置が画像の上
        /// だった場合は、リンク先の画像ファイルを開くことを優先する（Ctrl+クリックでの
        /// リンク開きより先に判定する）。それ以外は通常通りInlineStyleEditorのリンククリック
        /// 処理へ委譲する。画像の判定は、右クリックメニュー（EditorContextMenuOpening）の
        /// 「画像を保存」判定と同じ、RichTextBox側からのVisualTreeHelper.HitTestを使う方式。
        /// Image要素自身にPreviewMouseLeftButtonDownを直接持たせる方式（ドラッグ書き出し用に
        /// 元々ある仕組み）はダブルクリック検出には使わない。RichTextBox内に埋め込まれた
        /// InlineUIContainerの子要素は、マウスイベントの到達やカーソル表示がRichTextBox内部の
        /// テキスト編集処理と絡んで不安定になることがあるため、代わりに常に確実に動作する
        /// RichTextBox側でのヒットテストに統一している。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void EditorPreviewMouseLeftButtonDown(object a_sender, MouseButtonEventArgs a_args)
        {
            Point pos = a_args.GetPosition(m_editor);
            var hit = VisualTreeHelper.HitTest(m_editor, pos);

            // タスクリストのチェックボックス（[ ]/[x]）のクリックによるチェック⇔アンチェック
            // 切替。このメソッドのドキュメントコメントにある通り、RichTextBoxに埋め込まれた
            // InlineUIContainerの子要素（CheckBox自身）の内部クリック処理に頼るとイベントが
            // 不安定になるため、画像と同じくRichTextBox側でのヒットテストで検出し、IsChecked
            // を手動で反転させたうえで、「元テキスト保持」の記憶破棄もここで直接・同期的に
            // 呼ぶ（IsChecked変更にRunAsProgrammaticChangeを使わないのは、実際のユーザー
            // 操作として扱うために重要）。
            CheckBox cb = FindVisualAncestorOrSelf<CheckBox>(hit?.VisualHit);
            if (null != cb && "task-checkbox" == (cb.Tag as string))
            {
                a_args.Handled = true;
                cb.IsChecked = !(true == cb.IsChecked);
                InvalidateTaskCheckboxBlock(cb);
                return;
            }

            if (a_args.ClickCount >= 2)
            {
                Image img = FindVisualAncestorOrSelf<Image>(hit?.VisualHit);
                if (null != img)
                {
                    a_args.Handled = true;
                    m_imageManager.OpenImageFile(img);
                    return;
                }
            }
            m_inlineStyleEditor.HandlePreviewMouseLeftButtonDown(a_sender, a_args);
        }

        // ======================================================================
        //  視覚ツリーのヘルパー（EditorContextMenuOpening・EditorPreviewMouseLeftButtonDownで使用）
        // ======================================================================

        /// <summary>指定した型の、最も近い視覚ツリーの祖先（またはその要素自身）を見つける。</summary>
        /// <param name="a_start">探索を始める要素。</param>
        /// <returns>見つかった要素。なければ null。</returns>
        private static T FindVisualAncestorOrSelf<T>(DependencyObject a_start) where T : DependencyObject
        {
            var current = a_start;
            while (null != current)
            {
                if (current is T match)
                {
                    return match;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ======================================================================
        //  モード切り替え（MarkDown ⇔ ソース）
        // ======================================================================

        /// <summary>MarkDownモード（WYSIWYG）とソースモード（生テキスト）を切り替える。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void ToggleModeBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            bool wasDirtyFlg = m_currentFileIsDirtyFlg;

            if (!m_isSourceModeFlg)
            {
                m_sourceEditor.Text = m_markdownConverter.DocumentToMarkdown(m_editor.Document);
                m_editor.Visibility = Visibility.Collapsed;
                m_sourceEditor.Visibility = Visibility.Visible;
                m_isSourceModeFlg = true;
                m_modeIndicator.Text = "ソースモード";
                m_sourceEditor.Focus();
            }
            else
            {
                RunAsProgrammaticChange(() => m_markdownConverter.MarkdownToDocument(m_sourceEditor.Text, m_editor.Document));
                m_sourceEditor.Visibility = Visibility.Collapsed;
                m_editor.Visibility = Visibility.Visible;
                m_isSourceModeFlg = false;
                m_modeIndicator.Text = "MarkDownモード";
                m_outlineManager.Refresh();
                m_editor.Focus();
            }

            // 表示モードの切り替えは、同じ内容を表示し直しているだけなので、それ自体で
            // ファイルが「未保存」扱いになってしまってはいけない。
            m_currentFileIsDirtyFlg = wasDirtyFlg;
            m_folderTreeManager.RefreshDirtyMarkers();
        }

        // ======================================================================
        //  新規作成 / 開く / 保存 / 名前を付けて保存
        // ======================================================================

        /// <summary>現在の内容を破棄して新規文書を開始する。現在のファイルがフォルダビューに
        /// 表示されている場合は、破棄前に編集内容を保留中の編集として退避したうえで、確認
        /// ダイアログなしで新規作成する（あとでフォルダビューから開き直して保存できるため）。
        /// フォルダビューに表示されていないファイル（またはそもそも未保存の新規文書）の場合は、
        /// 内容が失われる可能性があるため従来通り確認する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void NewBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            bool currentFileInFolderViewFlg = !string.IsNullOrEmpty(m_currentFileDirectory) &&
                m_folderTreeManager.IsWithinLoadedFolder(m_currentFileDirectory);

            if (!currentFileInFolderViewFlg && (m_currentFileIsDirtyFlg || m_pendingFileEdits.Count > 0))
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
        private void NewWindowClick(object a_sender, RoutedEventArgs a_args)
        {
            var newWindow = new MainWindow();
            newWindow.Show();
        }

        /// <summary>このウィンドウを閉じる（未保存の変更があればWindow_Closingで確認される）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void CloseBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            Close();
        }

        /// <summary>ファイルを開くダイアログを表示し、選択されたファイルを読み込む。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void OpenBtnClick(object a_sender, RoutedEventArgs a_args)
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

        private void LoadFile(string a_path)
        {
            // 特殊ケース：すでにアクティブなファイルを再度開こうとした場合。この判定がないと、
            // 下のGetCurrentContentForFileがライブな（編集中の）内容をそのまま返してしまい、
            // 「開く」操作が何もしていないように見えてしまう。
            if (!string.IsNullOrEmpty(m_currentFilePath) && PathsReferToSameFile(a_path, m_currentFilePath))
            {
                if (!m_currentFileIsDirtyFlg)
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

                if (m_isSourceModeFlg)
                {
                    m_sourceEditor.Text = onDiskContent;
                }
                else
                {
                    RunAsProgrammaticChange(() => m_markdownConverter.MarkdownToDocument(onDiskContent, m_editor.Document));
                    m_outlineManager.Refresh();
                    // 文書を丸ごと差し替えた直後は、キャレット位置が未確定・不定なままになる
                    // ことがある（古い位置を指したままになるなど）。ここで明示的に文書の先頭へ
                    // 設定しておくことで、その直後に検索の「次を検索」等がCaretPositionを基準に
                    // 開始位置を計算する際、信頼できる値になるようにする。
                    m_editor.CaretPosition = m_editor.Document.ContentStart;
                    ClearEditorUndoHistory();
                }
                m_searchReplaceService.OnDocumentReplaced();
                m_openFindReplaceWindow?.ReapplyHighlightForCurrentFile();

                m_currentFileIsDirtyFlg = false;
                m_folderTreeManager.RefreshDirtyMarkers();
                return;
            }

            SnapshotCurrentFileIfDirty();

            string md = GetCurrentContentForFile(a_path);
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

            m_currentFilePath = a_path;
            m_currentFileDirectory = Path.GetDirectoryName(a_path);
            this.Title = Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version + " - " + Path.GetFileName(a_path);
            m_pendingFileEdits.Remove(a_path); // このファイルの内容は、以後エディタ自体が真実の情報源になる
            RecordFileHistory(a_path);
            AddToRecentFiles(a_path);

            if (m_isSourceModeFlg)
            {
                m_sourceEditor.Text = md;
            }
            else
            {
                RunAsProgrammaticChange(() => m_markdownConverter.MarkdownToDocument(md, m_editor.Document));
                m_outlineManager.Refresh();
                // 文書を丸ごと差し替えた直後は、キャレット位置が未確定・不定なままになる
                // ことがある（古い位置を指したままになるなど）。ここで明示的に文書の先頭へ
                // 設定しておくことで、その直後に検索の「次を検索」等がCaretPositionを基準に
                // 開始位置を計算する際、信頼できる値になるようにする。
                m_editor.CaretPosition = m_editor.Document.ContentStart;
                ClearEditorUndoHistory();
            }
            m_searchReplaceService.OnDocumentReplaced();
            m_openFindReplaceWindow?.ReapplyHighlightForCurrentFile();

            m_currentFileIsDirtyFlg = false;

            if (!string.IsNullOrEmpty(m_currentFileDirectory) && !m_folderTreeManager.IsWithinLoadedFolder(m_currentFileDirectory))
            {
                m_folderTreeManager.LoadFolderTree(m_currentFileDirectory);
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

        /// <summary>Ctrl+左右カーソルキーで前後にたどれる、開いたファイルの履歴（ブラウザの
        /// 「戻る/進む」と同様の仕組み）。</summary>
        private readonly List<string> m_fileHistory = new List<string>();
        private int m_fileHistoryIndex = -1;
        /// <summary>Ctrl+左右による履歴移動でLoadFileを呼んでいる最中は、それ自体を新しい
        /// 履歴として記録しないようにするためのフラグ。</summary>
        private bool m_isNavigatingFileHistoryFlg;

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
            if (0 == m_fileHistory.Count || !PathsReferToSameFile(m_fileHistory[m_fileHistory.Count - 1], a_path))
            {
                m_fileHistory.Add(a_path);
                m_fileHistoryIndex = m_fileHistory.Count - 1;
            }
        }

        /// <summary>Ctrl+左カーソルキー：1つ前に開いていたファイルを開く。</summary>
        private void NavigateFileHistoryBack()
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
        private void NavigateFileHistoryForward()
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
            m_recentFiles.RemoveAll(p => PathsReferToSameFile(p, a_path));
            m_recentFiles.Insert(0, a_path);
            if (m_recentFiles.Count > MAX_RECENT_FILES)
            {
                m_recentFiles.RemoveRange(MAX_RECENT_FILES, m_recentFiles.Count - MAX_RECENT_FILES);
            }
            RebuildRecentFilesMenu();
        }

        /// <summary>「最近使ったファイル」サブメニューの中身を、現在のm_recentFilesの内容から
        /// 組み立て直す。</summary>
        private void RebuildRecentFilesMenu()
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
            if (string.IsNullOrEmpty(m_currentFilePath) || m_isSourceModeFlg)
            {
                return;
            }

            if (!m_currentFileIsDirtyFlg)
            {
                m_pendingFileEdits.Remove(m_currentFilePath);
                return;
            }

            try
            {
                m_pendingFileEdits[m_currentFilePath] = m_markdownConverter.DocumentToMarkdown(m_editor.Document);
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
        /// 保存時に書き出すMarkDownテキストを、一時フォルダに残ったままの画像（WYSIWYGモードで
        /// ドラッグ&amp;ドロップ挿入した直後、まだ一度も保存していない画像）を
        /// "&lt;ファイル名&gt;.images"フォルダへ退避・パス書き換えした上で返す。WYSIWYGモードで
        /// 画像を挿入した直後にソースモードへ切り替えてそのまま保存した場合も退避が必要なため、
        /// モードを問わず常にこの退避処理を行う。ソースモード中の保存では、ライブな
        /// m_editor.Document・m_sourceEditor.Text のどちらも直接いじらずに済むよう、
        /// ExportPdfBtnClickと同じ「使い捨てのスクラッチ文書＋専用OriginalTextTracker」
        /// パターンで処理し、書き換え後のテキストを m_sourceEditor.Text へも反映しておく
        /// （同じ一時ファイルを次回保存時に再び探しに行って失敗することがないように）。
        /// </summary>
        /// <returns>保存すべきMarkDownテキスト。</returns>
        private string GetMarkdownForSaveWithImageRelocation()
        {
            if (!m_isSourceModeFlg)
            {
                m_imageManager.RelocatePendingTempImages(m_editor.Document);
                return m_markdownConverter.DocumentToMarkdown(m_editor.Document);
            }

            var tempTracker = new OriginalTextTracker(m_editor);
            var tempConverter = new MarkdownConverter(tempTracker, m_imageManager, () => m_preserveSourceLineBreaksFlg);
            var tempDoc = new FlowDocument();
            tempConverter.MarkdownToDocument(m_sourceEditor.Text, tempDoc);
            m_imageManager.RelocatePendingTempImages(tempDoc);
            string md = tempConverter.DocumentToMarkdown(tempDoc);
            m_sourceEditor.Text = md;
            return md;
        }

        /// <summary>現在のファイルを保存する（未保存の新規ファイルなら名前を付けて保存へ）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void SaveBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (string.IsNullOrEmpty(m_currentFilePath))
            {
                SaveAs();
                return;
            }
            string md = GetMarkdownForSaveWithImageRelocation();
            File.WriteAllText(m_currentFilePath, m_lineEndingTracker.Apply(md, m_lineEndingTracker.GetFor(m_currentFilePath)), new UTF8Encoding(false));
            m_currentFileIsDirtyFlg = false;
            m_folderTreeManager.AddFileNodeIfMissing(m_currentFilePath);
            m_folderTreeManager.RefreshDirtyMarkers();
            m_folderTreeManager.SelectFileNode(m_currentFilePath);
        }

        /// <summary>「名前を付けて保存」ダイアログを開く。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void SaveAsBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            SaveAs();
        }

        /// <summary>新しいファイル名/保存先を尋ね、そこへ保存する（元ファイルの改行コード
        /// スタイルは引き継ぐ）。</summary>
        private void SaveAs()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdownファイル (*.md)|*.md|すべてのファイル (*.*)|*.*",
                FileName = null != m_currentFilePath ? Path.GetFileName(m_currentFilePath) : "document.md"
            };
            // 現在の文書自体には保存先フォルダがまだ無くても（新規作成直後など）、フォルダビューに
            // 何かフォルダが表示されていれば、そちらを初期フォルダとして使う。
            string initialDirectory = m_currentFileDirectory ?? m_folderTreeManager.LoadedFolderRootPath;
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
            string lineEnding = !string.IsNullOrEmpty(m_currentFilePath) ? m_lineEndingTracker.GetFor(m_currentFilePath) : "\r\n";
            m_lineEndingTracker.SetFor(newFilePath, lineEnding);

            bool folderIsLoadedFlg = !string.IsNullOrEmpty(m_folderTreeManager.LoadedFolderRootPath);
            bool isWithinCurrentFolderFlg = folderIsLoadedFlg && m_folderTreeManager.IsWithinLoadedFolder(newFileDirectory);

            if (folderIsLoadedFlg && !isWithinCurrentFolderFlg)
            {
                // 現在表示中のフォルダの外に保存する場合：このウィンドウでの編集内容は
                // 保存先ファイルへ引き継がれる（新しいウィンドウで開く）ため、このウィンドウ
                // 自体は表示中のフォルダの表示を維持したまま、その先頭のファイルへ切り替える。
                string savedDirectoryBackup = m_currentFileDirectory;
                string savedFilePathBackup = m_currentFilePath;
                m_currentFileDirectory = newFileDirectory; // 画像パスの解決に一時的に必要
                m_currentFilePath = newFilePath; // 画像退避フォルダ名（<ファイル名>.images）の決定に一時的に必要
                string outsideMd = GetMarkdownForSaveWithImageRelocation();
                File.WriteAllText(newFilePath, m_lineEndingTracker.Apply(outsideMd, lineEnding), new UTF8Encoding(false));

                // 古いパスの情報を破棄する（他のファイルの保留中の編集には触れない）。
                m_currentFilePath = null;
                m_currentFileDirectory = null;
                m_currentFileIsDirtyFlg = false;
                m_folderTreeManager.OpenFirstFileInLoadedFolder();

                OpenFileInNewWindow(newFilePath, null);
                return;
            }

            m_currentFilePath = newFilePath;
            m_currentFileDirectory = newFileDirectory;
            this.Title = Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version + " - " + Path.GetFileName(newFilePath);
            AddToRecentFiles(newFilePath);

            string md = GetMarkdownForSaveWithImageRelocation();
            File.WriteAllText(newFilePath, m_lineEndingTracker.Apply(md, lineEnding), new UTF8Encoding(false));
            m_currentFileIsDirtyFlg = false;

            if (!folderIsLoadedFlg)
            {
                m_folderTreeManager.LoadFolderTree(m_currentFileDirectory);
            }
            else
            {
                m_folderTreeManager.AddFileNodeIfMissing(newFilePath);
            }
            m_folderTreeManager.RefreshDirtyMarkers();
            m_folderTreeManager.SelectFileNode(newFilePath);
        }

        /// <summary>
        /// 現在の文書をPDFへ書き出す。headless Chromium（PuppeteerSharp、MITライセンス）で
        /// 文書をHTMLとして印刷する形でPDF化している。この方式なら、保存先ダイアログの既定
        /// ファイル名（元のMarkDownファイル名から拡張子を変えたもの）も、書き出し完了後の
        /// 自動オープンも問題なく行える上、画面表示と同じフォント（游ゴシック UI等）が
        /// そのまま使え、見出しへのジャンプリンクや取り消し線も本物の見た目で表現できる。
        /// 初回実行時のみ、Chromium本体（数百MB）のダウンロードが発生するため、
        /// インターネット接続が必要で、多少時間がかかる。
        /// </summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private async void ExportPdfBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            // 現在の内容をMarkDownテキストとして取得する（MarkDownモード・ソースモードの
            // どちらでも動作する。以前あった「MarkDownモードでのみ利用可能」という制限は、
            // このMarkDownテキスト経由の方式にしたことで不要になった）。
            string md = m_isSourceModeFlg ? m_sourceEditor.Text : m_markdownConverter.DocumentToMarkdown(m_editor.Document);

            // 編集中のMarkDownファイル名から拡張子を除いたものを、書き出すPDFの既定の
            // ファイル名にする（例：「README.md」→「README.pdf」）。
            string docName = !string.IsNullOrEmpty(m_currentFilePath)
                ? Path.GetFileNameWithoutExtension(m_currentFilePath) : "無題";

            var saveDlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDFファイル (*.pdf)|*.pdf|すべてのファイル (*.*)|*.*",
                FileName = docName + ".pdf"
            };
            string initialDirectory = m_currentFileDirectory ?? m_folderTreeManager.LoadedFolderRootPath;
            if (!string.IsNullOrEmpty(initialDirectory))
            {
                saveDlg.InitialDirectory = initialDirectory;
            }
            if (true != saveDlg.ShowDialog())
            {
                return;
            }

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                // MarkDownテキストを、書き出し専用の使い捨てFlowDocumentへ変換する。
                // m_markdownConverter（m_originalTextTrackerを共有している）をそのまま使うと、
                // MarkdownToDocument内部のCleanupでライブなエディタ側の「未編集ブロックは
                // 元テキストのまま保存する」という記憶まで失われてしまうため、この書き出し専用の
                // 変換だけは独立したOriginalTextTrackerを使う一時的なMarkdownConverterで行う。
                var tempTracker = new OriginalTextTracker(m_editor);
                var tempConverter = new MarkdownConverter(tempTracker, m_imageManager, () => m_preserveSourceLineBreaksFlg);
                var tempDoc = new FlowDocument();
                tempConverter.MarkdownToDocument(md, tempDoc);

                await new ChromiumPdfExporter(m_imageManager).ExportAsync(tempDoc, saveDlg.FileName,
                    m_pdfMarginTop, m_pdfMarginBottom, m_pdfMarginLeft, m_pdfMarginRight);

                // 書き出したPDFを、既定の関連付けアプリで自動的に開く。
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(saveDlg.FileName)
                    {
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // 自動で開けなくても、PDFの書き出し自体は完了しているため無視する
                    // （関連付けアプリが無い環境などが考えられる）。
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("書き出しに失敗しました: " + ex.Message, "PDFに書き出し",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        /// <summary>現在のファイルと、保留中の編集があるすべてのファイルを保存する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void SaveAllBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_currentFileIsDirtyFlg || m_pendingFileEdits.Count > 0)
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

            if (!string.IsNullOrEmpty(m_currentFilePath))
            {
                try
                {
                    string md = GetMarkdownForSaveWithImageRelocation();
                    File.WriteAllText(m_currentFilePath, m_lineEndingTracker.Apply(md, m_lineEndingTracker.GetFor(m_currentFilePath)), new UTF8Encoding(false));
                    m_pendingFileEdits.Remove(m_currentFilePath);
                    m_currentFileIsDirtyFlg = false;
                    savedCount++;
                }
                catch (Exception ex)
                {
                    failures.Add(m_currentFilePath + " (" + ex.Message + ")");
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
        private void DiscardCurrentDocumentSilently()
        {
            m_currentFilePath = null;
            m_currentFileDirectory = null;
            this.Title = Assembly.GetExecutingAssembly().GetName().Name;

            m_pendingFileEdits.Clear();
            m_currentFileIsDirtyFlg = false;

            RunAsProgrammaticChange(() =>
            {
                if (m_isSourceModeFlg)
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
            m_currentFilePath = null;
            m_currentFileDirectory = null;
            this.Title = Assembly.GetExecutingAssembly().GetName().Name;
            m_currentFileIsDirtyFlg = false;

            RunAsProgrammaticChange(() =>
            {
                if (m_isSourceModeFlg)
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

        // ======================================================================
        //  ズーム
        // ======================================================================

        private void ZoomInClick(object a_sender, RoutedEventArgs a_args) => SetZoom(m_zoomLevel + 0.1);
        private void ZoomOutClick(object a_sender, RoutedEventArgs a_args) => SetZoom(m_zoomLevel - 0.1);
        private void ZoomResetClick(object a_sender, RoutedEventArgs a_args) => SetZoom(1.0);

        /// <summary>Ctrl+ホイールでエディタをズームする（スクロールの代わり）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void EditorPreviewMouseWheel(object a_sender, MouseWheelEventArgs a_args)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                SetZoom(m_zoomLevel + (a_args.Delta > 0 ? 0.1 : -0.1));
            }
        }

        /// <summary>新しいズーム倍率を適用し、ツールバーのパーセント表示を更新する。</summary>
        /// <param name="a_value">新しいズーム倍率（1.0が100%）。妥当な範囲に丸められる。</param>
        private void SetZoom(double a_value)
        {
            m_zoomLevel = Math.Max(0.5, Math.Min(2.5, Math.Round(a_value, 2)));
            m_editor.LayoutTransform = new ScaleTransform(m_zoomLevel, m_zoomLevel);
            m_sourceEditor.FontSize = 16 * m_zoomLevel;
            m_zoomLabelBtn.Content = Math.Round(m_zoomLevel * 100) + "%";
        }

        /// <summary>ソースモード編集中、ファイルをダーティにする。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void SourceEditorTextChanged(object a_sender, TextChangedEventArgs a_args)
        {
            if (null == m_folderTreeManager)
            {
                return; // InitializeComponent中の発火に対するガード
            }
            m_currentFileIsDirtyFlg = true;
            m_folderTreeManager.RefreshDirtyMarkers();
        }

        /// <summary>エディタの幅が変わったら、画像のサイズ調整をやり直す。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void EditorSizeChanged(object a_sender, SizeChangedEventArgs a_args)
        {
            if (null == m_imageManager)
            {
                return; // InitializeComponent中の発火に対するガード
            }
            if (m_isSourceModeFlg)
            {
                return;
            }
            foreach (var img in m_imageManager.FindAllImages(m_editor.Document))
            {
                m_imageManager.ApplyImageSizing(img);
            }
        }

        // ======================================================================
        //  ドラッグ&ドロップでの画像挿入（ImageManagerへそのまま橋渡し）
        // ======================================================================

        private void EditorDragEnter(object a_sender, DragEventArgs a_args) => m_imageManager.HandleDragEnter(a_sender, a_args);
        private void EditorDragOver(object a_sender, DragEventArgs a_args) => m_imageManager.HandleDragOver(a_sender, a_args);
        private void EditorDrop(object a_sender, DragEventArgs a_args) => m_imageManager.HandleDrop(a_sender, a_args);

        // ======================================================================
        //  検索と置換（FindReplaceWindowを開く）
        // ======================================================================

        /// <summary>検索・置換の公開API。FindReplaceWindowから使う。</summary>
        public SearchReplaceService SearchReplace => m_searchReplaceService;

        /// <summary>現在開いているファイルの絶対パス（未保存なら null）。FindReplaceWindowが
        /// 「今開いているファイルから検索を始める」ために参照する。</summary>
        public string CurrentFilePath => m_currentFilePath;

        /// <summary>アウトラインペインの管理役。検索結果の反映などにFindReplaceWindowから使う。</summary>
        public OutlineManager OutlinePane => m_outlineManager;

        /// <summary>フォルダツリーペインの管理役。検索結果の反映などにFindReplaceWindowから使う。</summary>
        public FolderTreeManager FolderTreePane => m_folderTreeManager;

        private void FindReplaceBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            OpenFindReplaceWindow();
        }

        /// <summary>メインウインドウのキーボードショートカットの実装。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void MainWindowPreviewKeyDown(object a_sender, KeyEventArgs a_args)
        {
            if (a_args.Key == Key.F &&
                Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                OpenFindReplaceWindow();
            }
            // Ctrl+Nは「新規作成」のショートカットとして扱う（Windows標準のCtrl+Nは「新しいウィンドウ」なので、そちらは無効化する）。
            else if (a_args.Key == Key.N &&
                     Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                NewBtnClick(a_sender, null);
            }
            // Shift+Ctrl+Nは「新しいウィンドウ」のショートカットとして扱う。
            else if (a_args.Key == Key.N &&
                     Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                a_args.Handled = true;
                NewWindowClick(a_sender, null);
            }
            // Ctrl+Oは「開く」のショートカットとして扱う。
            else if (a_args.Key == Key.O &&
                     Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                OpenBtnClick(a_sender, null);
            }
            // Ctrl+Sは「保存」のショートカットとして扱う（Windows標準のCtrl+Sは「すべて保存」なので、そちらは無効化する）。
            else if (a_args.Key == Key.S &&
                     Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                SaveBtnClick(a_sender, null);
            }
            // Shift+Ctrl+Sは「名前を付けて保存...」のショートカットとして扱う。
            else if (a_args.Key == Key.S &&
                     Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                a_args.Handled = true;
                SaveAs();
            }
            // Ctrl+Pは「PDFに書き出し」のショートカットとして扱う（Windows標準のCtrl+Pは「印刷」なので、そちらは無効化する）。
            else if (a_args.Key == Key.P &&
                     Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                ExportPdfBtnClick(a_sender, null);
            }
            // Ctrl+Wは「ウィンドウを閉じる」のショートカットとして扱う。
            else if (a_args.Key == Key.W &&
                     Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                Close();
            }
            // Ctrl+Zは「元に戻す」のショートカットとして扱う（ソースモードでは、TextBox標準の
            // Undoにそのまま任せる）。
            else if (a_args.Key == Key.Z &&
                     Keyboard.Modifiers == ModifierKeys.Control &&
                     !m_isSourceModeFlg)
            {
                a_args.Handled = true;
                if (m_editor.CanUndo)
                {
                    m_editor.Undo();
                }
            }
            // Ctrl+Yは「やり直し」のショートカットとして扱う（ソースモードでは、TextBox標準の
            // Redoにそのまま任せる）。
            else if (a_args.Key == Key.Y &&
                     Keyboard.Modifiers == ModifierKeys.Control &&
                     !m_isSourceModeFlg)
            {
                a_args.Handled = true;
                if (m_editor.CanRedo)
                {
                    m_editor.Redo();
                }
            }
            // Ctrl+左カーソルキーは「1つ前に開いていたファイルを開く」のショートカットとして扱う。
            else if (a_args.Key == Key.Left &&
                     Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                NavigateFileHistoryBack();
            }
            // Ctrl+右カーソルキーは「1つ後に開いていたファイルを開く」のショートカットとして扱う。
            else if (a_args.Key == Key.Right &&
                     Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                NavigateFileHistoryForward();
            }
        }

        /// <summary>検索と置換ウィンドウを開く（すでに開いていれば、そちらを前面に出す）。</summary>
        private void OpenFindReplaceWindow()
        {
            if (null != m_openFindReplaceWindow)
            {
                m_openFindReplaceWindow.Activate();
                m_openFindReplaceWindow.Focus();
                return;
            }
            m_openFindReplaceWindow = new FindReplaceWindow(this) { Owner = this };
            m_openFindReplaceWindow.Closed += (s, e) => m_openFindReplaceWindow = null;
            m_openFindReplaceWindow.Show();
        }

        /// <summary>「バージョン情報」ボタン。アプリ名とバージョン番号を表示する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void VersionInfoBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            var aboutWindow = new AboutWindow { Owner = this };
            aboutWindow.ShowDialog();
        }

        /// <summary>メニュー「ファイル」→「PDFの余白を設定…」。ダイアログを開き、PDF書き出し時の
        /// 上下左右の余白（px）を設定する。OKで確定した値を記憶し、次回起動時にも復元される
        /// （実際にPDFへ反映されるのは、次に「PDFに書き出し」を実行した時点）。</summary>
        /// <param name="a_sender">「PDFの余白を設定…」メニュー項目。</param>
        /// <param name="a_args">Click event.</param>
        private void PdfMarginBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            var dialog = new PdfMarginDialog(m_pdfMarginTop, m_pdfMarginBottom, m_pdfMarginLeft, m_pdfMarginRight)
                { Owner = this };
            if (true == dialog.ShowDialog())
            {
                m_pdfMarginTop = dialog.MarginTop;
                m_pdfMarginBottom = dialog.MarginBottom;
                m_pdfMarginLeft = dialog.MarginLeft;
                m_pdfMarginRight = dialog.MarginRight;
            }
        }

        /// <summary>メニュー「表示」→「行間を設定…」。ダイアログを開き、ライブプレビュー
        /// しながら行間を調整できるようにする。OKで確定した値を記憶し、次回起動時にも
        /// 復元されるようにする。</summary>
        /// <param name="a_sender">「行間を設定…」メニュー項目。</param>
        /// <param name="a_args">Click event.</param>
        private void LineHeightBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            var dialog = new LineHeightDialog(m_editorLineHeight, ApplyEditorLineHeight) { Owner = this };
            if (true == dialog.ShowDialog())
            {
                m_editorLineHeight = dialog.LineHeightValue;
            }
            ApplyEditorLineHeight(m_editorLineHeight);
        }

        /// <summary>メニュー「表示」→「リンクの開き方」→「Ctrl+クリックで開く」。</summary>
        /// <param name="a_sender">メニュー項目。</param>
        /// <param name="a_args">Click event.</param>
        private void LinkModeCtrlClickChecked(object a_sender, RoutedEventArgs a_args)
        {
            m_requireCtrlForLinkClickFlg = true;
            UpdateLinkModeMenuChecks();
        }

        /// <summary>メニュー「表示」→「リンクの開き方」→「クリックのみで開く」。</summary>
        /// <param name="a_sender">メニュー項目。</param>
        /// <param name="a_args">Click event.</param>
        private void LinkModeClickOnlyChecked(object a_sender, RoutedEventArgs a_args)
        {
            m_requireCtrlForLinkClickFlg = false;
            UpdateLinkModeMenuChecks();
        }

        /// <summary>「リンクの開き方」メニューの2項目を、現在の設定に合わせてラジオボタンのように
        /// 片方だけチェック状態にする。</summary>
        private void UpdateLinkModeMenuChecks()
        {
            m_linkModeCtrlClickMenuItem.IsChecked = m_requireCtrlForLinkClickFlg;
            m_linkModeClickOnlyMenuItem.IsChecked = !m_requireCtrlForLinkClickFlg;
        }

        /// <summary>メニュー「表示」→「段落中の改行」→「ソースの通りに改行する」（mde/Typora
        /// 従来の表示。既定値）。</summary>
        /// <param name="a_sender">メニュー項目。</param>
        /// <param name="a_args">Click event.</param>
        private void LineBreakModeSourceChecked(object a_sender, RoutedEventArgs a_args)
        {
            SetPreserveSourceLineBreaksFlg(true);
        }

        /// <summary>メニュー「表示」→「段落中の改行」→「空行が入るまで改行しない」
        /// （CommonMark/VSCodeのMarkDownプレビュー標準の表示）。</summary>
        /// <param name="a_sender">メニュー項目。</param>
        /// <param name="a_args">Click event.</param>
        private void LineBreakModeBlankOnlyChecked(object a_sender, RoutedEventArgs a_args)
        {
            SetPreserveSourceLineBreaksFlg(false);
        }

        /// <summary>段落中の改行の扱いの設定を変更する。MarkDownモードで文書を表示中であれば、
        /// 現在の内容を一旦MarkDownテキストへ書き出してから読み直すことで、新しい設定を
        /// その場で反映させる（LoadFile等、文書を丸ごと差し替える他の処理と同じパターン）。
        /// 文書再構築直後のキャレット位置は不定・不確実になりうるため、LoadFile等の既存箇所に
        /// ならい、独自のキャレット位置の保持・復元は一切試みず、
        /// 常に文書の先頭へ明示的に設定する。ソースモード中は、次にMarkDownモードへ切り替え
        /// られた際の変換に反映されるよう、設定値を変更するだけにとどめる。</summary>
        /// <param name="a_value">true＝ソースの通りに改行する、false＝空行が入るまで改行しない。</param>
        private void SetPreserveSourceLineBreaksFlg(bool a_value)
        {
            if (m_preserveSourceLineBreaksFlg != a_value)
            {
                if (m_isSourceModeFlg)
                {
                    m_preserveSourceLineBreaksFlg = a_value;
                }
                else
                {
                    string md = m_markdownConverter.DocumentToMarkdown(m_editor.Document);
                    m_preserveSourceLineBreaksFlg = a_value;
                    RunAsProgrammaticChange(() => m_markdownConverter.MarkdownToDocument(md, m_editor.Document));
                    m_outlineManager.Refresh();
                    m_editor.CaretPosition = m_editor.Document.ContentStart;
                }
            }
            UpdateLineBreakModeMenuChecks();
        }

        /// <summary>「段落中の改行」メニューの2項目を、現在の設定に合わせてラジオボタンのように
        /// 片方だけチェック状態にする。</summary>
        private void UpdateLineBreakModeMenuChecks()
        {
            m_lineBreakModeSourceMenuItem.IsChecked = m_preserveSourceLineBreaksFlg;
            m_lineBreakModeBlankOnlyMenuItem.IsChecked = !m_preserveSourceLineBreaksFlg;
        }

        /// <summary>エディタの行間（Paragraphの行の高さ）を、指定した値へ反映する。
        /// 段落内で行が折り返された時の間隔は、その段落のLineHeightそのもので決まる。もし
        /// 段落・リスト・表どうしの間の余白（Margin）にもLineHeightと同じ値を足してしまうと、
        /// 「折り返された行の間隔」の2倍近くになってしまい、段落間だけ不自然に間隔が
        /// 空いて見える（実際にこの不具合が起きていたため、箇条書きの項目内の段落
        /// （BuildNestedList参照）にならい、段落・リスト・表どうしの余白はLineHeightの値に
        /// 関わらず常にこの固定値のままにしている）。ただし固定値をゼロにはしない：見出し・
        /// コードブロック・水平線はそれぞれ固有のMarginを明示的に持っているのに対し、通常の
        /// 段落・リスト・表はこの共有の値だけを頼りにしているため、ここがゼロだと、空行
        /// 区切りのMarkDown段落どうしが単純な行の折り返しと見分けが付かなくなってしまう。</summary>
        /// <param name="a_value">設定したい行間の値。</param>
        private void ApplyEditorLineHeight(double a_value)
        {
            Resources["EditorLineHeight"] = a_value;
            Resources["EditorBlockSpacing"] = new Thickness(0, 0, 0, 14);
        }

        // ======================================================================
        //  フォルダ / アウトラインペインの表示・非表示切り替え
        // ======================================================================

        /// <summary>フォルダツリーペインの表示/非表示を切り替える（幅は次に表示する時のために
        /// 記憶しておく）。切り替えでエディタの表示幅が変わり文章の折り返し位置が変わっても、
        /// エディタペインの見た目上のスクロール位置が動かないよう、切り替えの前後でスクロール
        /// 位置を保存・復元する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void ToggleFolderPaneBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_folderPaneVisibleFlg && m_folderColumnDef.Width.Value > 0)
            {
                m_lastFolderColumnWidth = m_folderColumnDef.Width.Value;
            }
            m_folderPaneVisibleFlg = !m_folderPaneVisibleFlg;

            object scrollAnchor = CaptureEditorScrollAnchor();
            ApplyFolderPaneVisibility();
            UpdateLayout();
            RestoreEditorScrollAnchor(scrollAnchor);
        }

        /// <summary>folderPaneVisibleの現在値に従って、フォルダペインの表示状態をXAML側の
        /// コントロールへ反映する（ボタンクリック時、起動時の状態復元時の両方から呼ばれる）。</summary>
        private void ApplyFolderPaneVisibility()
        {
            if (m_folderPaneVisibleFlg)
            {
                m_folderColumnDef.Width = new GridLength(m_lastFolderColumnWidth);
                m_folderSplitterColumnDef.Width = new GridLength(2);
                m_folderPaneBorder.Visibility = Visibility.Visible;
                m_folderSplitter.Visibility = Visibility.Visible;
                m_toggleFolderPaneBtn.Content = "フォルダを隠す";
            }
            else
            {
                m_folderColumnDef.Width = new GridLength(0);
                m_folderSplitterColumnDef.Width = new GridLength(0);
                m_folderPaneBorder.Visibility = Visibility.Collapsed;
                m_folderSplitter.Visibility = Visibility.Collapsed;
                m_toggleFolderPaneBtn.Content = "フォルダを表示";
            }
        }

        /// <summary>アウトラインペインの表示/非表示を切り替える（幅は次に表示する時のために
        /// 記憶しておく）。切り替えでエディタの表示幅が変わり文章の折り返し位置が変わっても、
        /// エディタペインの見た目上のスクロール位置が動かないよう、切り替えの前後でスクロール
        /// 位置を保存・復元する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void ToggleOutlinePaneBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_outlinePaneVisibleFlg && m_outlineColumnDef.Width.Value > 0)
            {
                m_lastOutlineColumnWidth = m_outlineColumnDef.Width.Value;
            }
            m_outlinePaneVisibleFlg = !m_outlinePaneVisibleFlg;

            object scrollAnchor = CaptureEditorScrollAnchor();
            ApplyOutlinePaneVisibility();
            UpdateLayout();
            RestoreEditorScrollAnchor(scrollAnchor);
        }

        /// <summary>outlinePaneVisibleの現在値に従って、アウトラインペインの表示状態をXAML側の
        /// コントロールへ反映する（ボタンクリック時、起動時の状態復元時の両方から呼ばれる）。</summary>
        private void ApplyOutlinePaneVisibility()
        {
            if (m_outlinePaneVisibleFlg)
            {
                m_outlineColumnDef.Width = new GridLength(m_lastOutlineColumnWidth);
                m_outlineSplitterColumnDef.Width = new GridLength(2);
                m_outlinePaneBorder.Visibility = Visibility.Visible;
                m_outlineSplitter.Visibility = Visibility.Visible;
                m_toggleOutlinePaneBtn.Content = "アウトラインを隠す";
            }
            else
            {
                m_outlineColumnDef.Width = new GridLength(0);
                m_outlineSplitterColumnDef.Width = new GridLength(0);
                m_outlinePaneBorder.Visibility = Visibility.Collapsed;
                m_outlineSplitter.Visibility = Visibility.Collapsed;
                m_toggleOutlinePaneBtn.Content = "アウトラインを表示";
            }
        }

        /// <summary>エディタ内部のScrollViewerへの参照（フォルダ/アウトラインペイン切り替え時の
        /// スクロール位置保持に使う）。RestoreEditorScrollAnchorで初回使用時に取得する。</summary>
        private ScrollViewer m_editorScrollViewer;

        /// <summary>CaptureEditorScrollAnchorが覚えておく、MarkDownモード時のスクロール位置。
        /// 「左上端に見えている段落」そのものと、その段落のどのくらいの割合が上端より上に
        /// スクロールされているか（0=段落の先頭が見えている、1に近いほど段落の末尾近くまで
        /// スクロール済み）を保持する。ピクセル量ではなく段落に対する割合で覚えておくのは、
        /// 見出しの図解画像のように1段落がエディタの表示領域よりずっと大きいコンテンツでは、
        /// ペイン切り替えによる幅変更で画像自体のサイズも変わるため（ImageManager.
        /// ApplyImageSizing参照）、ピクセル量では復元後に全く違う場所を指してしまうため。</summary>
        private class EditorScrollAnchor
        {
            public Paragraph Paragraph;
            public double VerticalFraction;
            public double HorizontalFraction;
        }

        /// <summary>フォルダ/アウトラインペインの表示・非表示を切り替える直前に呼び出し、今
        /// エディタペインの左上端に見えている位置を覚えておく。ペインの表示・非表示で
        /// エディタの表示幅が変わると、文章の折り返し位置や画像の縮小率が変わってしまい、
        /// スクロール量（ピクセル位置）をそのまま維持するだけでは全く違う場所が表示されて
        /// しまう。そこで、幅変更後にRestoreEditorScrollAnchorで同じ場所へスクロールし直す
        /// ための情報をここで集めておく。</summary>
        /// <returns>MarkDownモードならEditorScrollAnchor、ソースモードなら一番上に見えている
        /// 行の先頭文字インデックス（int）。復元できる情報が無ければnull。</returns>
        private object CaptureEditorScrollAnchor()
        {
            if (m_isSourceModeFlg)
            {
                if (!m_sourceEditor.IsLoaded)
                {
                    return null;
                }
                int firstVisibleLine = m_sourceEditor.GetFirstVisibleLineIndex();
                if (firstVisibleLine < 0)
                {
                    return null;
                }
                return m_sourceEditor.GetCharacterIndexFromLineIndex(firstVisibleLine);
            }

            if (!m_editor.IsLoaded)
            {
                return null;
            }

            TextPointer nearPointer = m_editor.GetPositionFromPoint(new Point(0, 0), true);
            Paragraph para = nearPointer?.Paragraph;
            if (null == para)
            {
                return null;
            }

            Rect startRect = para.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            Rect endRect = para.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            double height = endRect.Bottom - startRect.Top;
            double width = endRect.Right - startRect.Left;
            return new EditorScrollAnchor
            {
                Paragraph = para,
                // GetPositionFromPointは、その段落が図解画像1枚だけで構成されているような
                // 場合（＝段落の途中を指す文字位置が存在しない場合）、段落の先頭か末尾の
                // どちらか近い方へ吸着してしまい、実際にはその画像の途中までスクロールして
                // いたという情報が失われる。そのため個々の文字位置ではなく、段落全体の
                // 縦横のRect（先頭〜末尾）を基準に「左上端が段落の何%の位置にあるか」を
                // 割合として覚えておくことで、画像の途中までスクロールしていた場合にも
                // 対応できるようにしている。
                VerticalFraction = (height > 0) ? Clamp01((0 - startRect.Top) / height) : 0,
                HorizontalFraction = (width > 0) ? Clamp01((0 - startRect.Left) / width) : 0,
            };
        }

        /// <summary>0〜1の範囲に収める。</summary>
        private static double Clamp01(double a_value) => a_value < 0 ? 0 : (a_value > 1 ? 1 : a_value);

        /// <summary>CaptureEditorScrollAnchorで覚えておいた位置が、再びエディタペインの左上端に
        /// 来るようスクロール位置を復元する。ペインの表示・非表示によるレイアウト変更
        /// （折り返し位置・画像サイズの再計算）が完了した後でないと正しい位置を計算できない
        /// ため、呼び出し側で幅の変更後にUpdateLayout()を挟んでから呼び出すこと。</summary>
        /// <param name="a_anchor">CaptureEditorScrollAnchorの戻り値。</param>
        private void RestoreEditorScrollAnchor(object a_anchor)
        {
            if (null == a_anchor)
            {
                return;
            }

            if (m_isSourceModeFlg)
            {
                if (a_anchor is int charIndex)
                {
                    int newLine = m_sourceEditor.GetLineIndexFromCharacterIndex(charIndex);
                    if (newLine >= 0)
                    {
                        // ScrollToLineは「見えていなければ最小限だけスクロールする」仕様のため、
                        // 既に見えている行だと一番上への移動が起きないことがある。OutlineManager.
                        // ScrollParagraphToTopと同じ理由・同じ対策で、先に一旦先頭へスクロール位置を
                        // リセットしてから改めて対象行へスクロールし、毎回確実に一番上へ来るようにする。
                        m_sourceEditor.ScrollToLine(0);
                        m_sourceEditor.ScrollToLine(newLine);
                    }
                }
                return;
            }

            if (a_anchor is EditorScrollAnchor anchor &&
                null != anchor.Paragraph?.Parent) // 段落がまだ文書ツリーに残っていることの確認
            {
                if (null == m_editorScrollViewer)
                {
                    m_editorScrollViewer = FindVisualChild<ScrollViewer>(m_editor);
                }
                if (null == m_editorScrollViewer)
                {
                    return;
                }

                // GetCharacterRectは、現在のビューポート（見えている範囲）を基準にした相対座標を
                // 返す。段落の（レイアウト変更後の、新しいサイズでの）先頭〜末尾のRectを改めて
                // 取得し、記憶しておいた割合を掛けることで、「左上端にあったのと同じ場所」の
                // 現在のビューポート内での位置を求める。それに変更前のスクロール位置を足すことで、
                // 文書全体を基準にした「復元すべきスクロール位置」になる。
                Rect startRect = anchor.Paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                Rect endRect = anchor.Paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                double height = endRect.Bottom - startRect.Top;
                double width = endRect.Right - startRect.Left;
                double targetTop = startRect.Top + anchor.VerticalFraction * height;
                double targetLeft = startRect.Left + anchor.HorizontalFraction * width;

                m_editorScrollViewer.ScrollToVerticalOffset(m_editorScrollViewer.VerticalOffset + targetTop);
                m_editorScrollViewer.ScrollToHorizontalOffset(m_editorScrollViewer.HorizontalOffset + targetLeft);
            }
        }

        // ======================================================================
        //  フォルダペイン
        // ======================================================================

        /// <summary>フォルダピッカーを表示し、選択されたフォルダをフォルダツリーペインへ
        /// 読み込む（可能であれば同じ相対パスのファイルを開いたままにする）。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void OpenFolderTreeBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_currentFileIsDirtyFlg || m_pendingFileEdits.Count > 0)
            {
                var confirmResult = MessageBox.Show(
                    "保存されていない変更があります。破棄して別のフォルダを開きますか？",
                    "フォルダを開く", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (confirmResult != MessageBoxResult.OK)
                {
                    return;
                }
            }

            string previousRelativePath = m_folderTreeManager.GetCurrentFileRelativePath();

            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (true != dlg.ShowDialog())
            {
                return;
            }

            DiscardCurrentDocumentSilently();
            m_folderTreeManager.LoadFolderTree(dlg.FolderName);
            m_folderTreeManager.OpenMatchingOrFirstFile(dlg.FolderName, previousRelativePath);
        }

        private void TreeViewItemExpanded(object a_sender, RoutedEventArgs a_args) => m_folderTreeManager.HandleTreeViewItemExpanded(a_sender, a_args);

        /// <summary>
        /// フォルダツリーの選択項目が切り替わると、WPF標準の動作でその項目を横方向にも
        /// 完全に見えるようスクロールしてしまい、ファイル名が長い場合に横スクロールバーが
        /// 右へずれてしまう。これを防ぐため、内部のScrollViewerの横スクロール位置が0以外に
        /// 変化するたびに、強制的に0へ戻す。
        /// </summary>
        /// <summary>フォルダツリー内部のScrollViewerへの参照（選択項目切り替え後の横スクロール
        /// リセットに使う）。</summary>
        private ScrollViewer m_folderTreeScrollViewer;

        private void FolderTreeLoaded(object a_sender, RoutedEventArgs a_args)
        {
            m_folderTreeScrollViewer = FindVisualChild<ScrollViewer>(m_folderTree);
        }

        /// <summary>
        /// アウトラインペインで、指定した見出し項目を選択状態にし、見えていなければ見える位置まで
        /// スクロールする。ListBoxは標準でScrollIntoViewを持っているため、フォルダペインの
        /// TreeViewのような複雑なコンテナ探索は不要。エディタ側の処理と干渉しないよう、
        /// アプリケーションが完全にアイドル状態（ApplicationIdle優先度）になってから実行する。
        /// </summary>
        /// <param name="a_entry">選択したい見出し項目。</param>
        private void ScrollOutlineListToEntry(OutlineEntry a_entry)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // SelectedItemを変更すると、ユーザーが手でクリックした時と同じ
                // SelectionChangedイベントが発生し、エディタのキャレットが見出しの先頭へ
                // 動いてしまう。ここではあくまで「今どこにいるか」を表示に反映したいだけ
                // なので、そのナビゲーションを抑止しておく。
                m_outlineManager.SuppressNextSelectionNavigation();
                m_outlineList.SelectedItem = a_entry;
                m_outlineList.ScrollIntoView(a_entry);
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// 指定したファイルノードが、フォルダペイン上で見える位置になるようスクロールする
        /// （すでに見えていれば何もしない）。すべて検索の結果一覧・次を検索/前を検索から
        /// ファイルを開いた時など、フォルダペインで選択状態にしたファイルへ、必要な祖先フォルダの
        /// 展開・コンテナ生成が完了してからスクロールするために、アプリケーションが完全に
        /// アイドル状態になってから処理を始める（エディタ側の移動処理と干渉しないようにするため）。
        /// </summary>
        /// <param name="a_node">選択したファイルノード。</param>
        private void ScrollFolderTreeToNode(FileSystemItem a_node)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var path = FindPathToItem(m_folderTreeManager.Roots, a_node);
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
                Dispatcher.BeginInvoke(new Action(() =>
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
                    Dispatcher.BeginInvoke(new Action(() =>
                        m_folderTreeScrollViewer.ScrollToHorizontalOffset(0)),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                return;
            }

            // 次の階層（この項目の子）が展開・生成されるのを待ってから進む。
            Dispatcher.BeginInvoke(new Action(() =>
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

        private void FolderTreeSelectedItemChanged(object a_sender, RoutedPropertyChangedEventArgs<object> a_args)
        {
            m_folderTreeManager.HandleSelectedItemChanged(a_sender, a_args);

            // 選択項目が切り替わると、WPF標準の動作でその項目を横方向にも完全に見えるよう
            // スクロールしてしまい、ファイル名が長い場合に横スクロールバーが右へずれてしまう。
            // このレイアウトパスが終わった直後（Loaded優先度）に横スクロールだけを0へ戻すことで、
            // それ以外のタイミングでのユーザーによる手動スクロールには一切影響しないようにする。
            if (null != m_folderTreeScrollViewer)
            {
                Dispatcher.BeginInvoke(new Action(() => m_folderTreeScrollViewer.ScrollToHorizontalOffset(0)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        // ======================================================================
        //  アウトラインペイン
        // ======================================================================

        private void OutlineListSelectionChanged(object a_sender, SelectionChangedEventArgs a_args) =>
            m_outlineManager.HandleSelectionChanged(a_sender, a_args);
    }
}
