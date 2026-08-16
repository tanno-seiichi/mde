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
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
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
            m_folderPaneVisibleFlg = m_savedSettings.FolderPaneVisible;
            m_outlinePaneVisibleFlg = m_savedSettings.OutlinePaneVisible;
            if (m_savedSettings.FolderPaneWidth > 0) m_lastFolderColumnWidth = m_savedSettings.FolderPaneWidth;
            if (m_savedSettings.OutlinePaneWidth > 0) m_lastOutlineColumnWidth = m_savedSettings.OutlinePaneWidth;

            m_originalTextTracker = new OriginalTextTracker(m_editor);
            m_lineEndingTracker = new LineEndingTracker(PathsReferToSameFile);
            m_outlineManager = new OutlineManager(m_editor);
            m_imageManager = new ImageManager(
                m_editor, m_originalTextTracker, () => m_isSourceModeFlg, () => m_currentFileDirectory,
                RunAsProgrammaticChange, m_outlineManager.Refresh, m_instanceTempId);
            m_markdownConverter = new MarkdownConverter(m_originalTextTracker, m_imageManager);
            m_listEditor = new ListEditor(m_editor, m_originalTextTracker, RunAsProgrammaticChange);
            m_headingCodeBlockEditor = new HeadingCodeBlockEditor(m_editor, m_originalTextTracker, RunAsProgrammaticChange);
            m_tableEditor = new TableEditor(
                m_editor, m_originalTextTracker, MarkDirty, RunAsProgrammaticChange, () => m_isSourceModeFlg,
                m_outlineManager.Refresh, InsertPlainTextWithLineBreaksForCodeBlock);
            m_folderTreeManager = new FolderTreeManager(
                LoadFile, () => m_currentFilePath, () => m_currentFileIsDirtyFlg,
                () => m_pendingFileEdits.Keys, PathsReferToSameFile);
            m_inlineStyleEditor = new InlineStyleEditor(
                m_editor, m_originalTextTracker, RunAsProgrammaticChange, MarkDirty, m_outlineManager.Refresh,
                m_markdownConverter.BlockToMarkdown, () => m_currentFileDirectory, LoadFile,
                m_folderTreeManager.IsWithinLoadedFolder, OpenFileInNewWindow);
            m_searchReplaceService = new SearchReplaceService(
                m_editor, m_sourceEditor, m_markdownConverter, m_originalTextTracker, m_lineEndingTracker,
                () => m_isSourceModeFlg, RunAsProgrammaticChange, m_outlineManager.Refresh, p => OutlineManager.ScrollParagraphToTop(p, m_editor),
                () => m_folderTreeManager.LoadedFolderRootPath, GetCurrentContentForFile,
                SetFileContentForReplaceImpl, LoadFile, RunWithoutDirtyMarking, m_outlineManager.MarkSearchMatches);

            this.Title = Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version;
            m_outlineList.ItemsSource = m_outlineManager.Items;
            m_folderTree.ItemsSource = m_folderTreeManager.Roots;
            DataObject.AddCopyingHandler(m_editor, m_tableEditor.HandleCopying);
            DataObject.AddPastingHandler(m_editor, m_tableEditor.HandlePasting);

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
            if (m_savedSettings.IsMaximized) this.WindowState = WindowState.Maximized;
        }

        /// <summary>ウィンドウを閉じようとした時、未保存の変更があれば確認する。</summary>
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
            if (!m_isFirstWindowInstanceFlg) return null;
            m_isFirstWindowInstanceFlg = false;

            try
            {
                string[] args = Environment.GetCommandLineArgs();
                // args[0]は実行ファイル自身のパスなので、実際の引数はargs[1]以降になる。
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1])) return null;
                return Path.GetFullPath(args[1]);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>ウィンドウを閉じる際、このウィンドウ専用の一時画像フォルダを削除し、
        /// 次回起動時に復元するウィンドウ状態を保存する。</summary>
        protected override void OnClosed(EventArgs a_args)
        {
            base.OnClosed(a_args);
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "mde", m_instanceTempId);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
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
                ZoomLevel = m_zoomLevel
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

        /// <summary>2つのパスが同一ファイルを指しているかどうかを調べる（大文字小文字を
        /// 区別せず、完全パスで比較する）。</summary>
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
        private string GetCurrentContentForFile(string a_path)
        {
            if (!string.IsNullOrEmpty(m_currentFilePath) && PathsReferToSameFile(a_path, m_currentFilePath))
                return m_isSourceModeFlg ? m_sourceEditor.Text : m_markdownConverter.DocumentToMarkdown(m_editor.Document);

            foreach (var kv in m_pendingFileEdits)
                if (PathsReferToSameFile(kv.Key, a_path)) return kv.Value;

            return null;
        }

        /// <summary>検索・置換の結果をファイルへ反映する：現在開いているファイルならライブな
        /// エディタへ直接、そうでなければ保留中の編集として記憶する。</summary>
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
        private void EditorTextChanged(object a_sender, TextChangedEventArgs a_args)
        {
            // RichTextBoxはInitializeComponent中に、既定の空文書を設定する際にTextChangedを
            // 発生させることがある。その時点ではコンストラクタでの各クラスの構築がまだ
            // 完了していない可能性があるため、念のためガードしておく。
            if (m_outlineManager == null || m_folderTreeManager == null || m_originalTextTracker == null) return;

            if (m_isSourceModeFlg) return;

            // 検索結果のハイライト（背景色）の適用/解除も、WPFの仕様上TextChangedを発生させて
            // しまうが、これは実際の編集ではないため、ダーティ扱いにしたり元テキスト保持の
            // 記憶を破棄したりしてはいけない。
            if (m_isApplyingHighlightFlg) return;

            m_outlineManager.Refresh();

            m_currentFileIsDirtyFlg = true;
            m_folderTreeManager.RefreshDirtyMarkers();
            m_originalTextTracker.Invalidate(m_editor.CaretPosition);

            if (m_isProgrammaticChangeFlg) return;

            var para = m_editor.CaretPosition?.Paragraph;
            if (para == null) return;
            if (para.Tag is CodeBlockInfo) return; // コードブロック内は自動整形しない

            if (m_inlineStyleEditor.CheckInlineFormatTrigger()) return;

            if (!(para.Parent is FlowDocument)) return; // 箇条書き/見出しへの自動変換はトップレベル段落のみ

            string text = new TextRange(para.ContentStart, para.ContentEnd).Text;
            text = text.TrimEnd('\r', '\n');

            var bulletMatch = Regex.Match(text, "^([*-])[ \u00A0]$");
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
            }
        }

        // ======================================================================
        //  キー入力の振り分け（Tab / Enter / 表内の矢印キー）
        // ======================================================================

        /// <summary>
        /// キー入力の中心的な振り分け役。箇条書き項目・見出し・コードブロック・表セルの
        /// いずれにキャレットがあるかに応じて、Tab/Shift+Tab/Enter/矢印キーの処理を
        /// 対応するクラスへ委譲する。
        /// </summary>
        /// <summary>
        /// 箇条書き/順序付きリストへの変換の入口（スペースキー）。印字可能な文字（スペースを含む）は
        /// WPFではPreviewKeyDownではなくPreviewTextInputを通じて挿入されるため、PreviewKeyDownで
        /// a_args.Handled=trueを設定するだけでは確実に文字の挿入を防げない。このイベントで直接
        /// 判定・処理することで、変換後に元のスペース文字が余分に残ってしまう不具合を防ぐ。
        /// </summary>
        private void EditorPreviewTextInput(object a_sender, TextCompositionEventArgs a_args)
        {
            if (m_isSourceModeFlg || a_args.Text != " ") return;

            var para = m_editor.CaretPosition?.Paragraph;
            if (para == null || !(para.Parent is FlowDocument) || para.Tag is CodeBlockInfo) return;
            if (m_editor.CaretPosition.CompareTo(para.ContentEnd) != 0) return;

            string beforeSpace = new TextRange(para.ContentStart, para.ContentEnd).Text.TrimEnd('\r', '\n');

            var bulletKeyMatch = Regex.Match(beforeSpace, "^([*-])$");
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

        private void EditorPreviewKeyDown(object a_sender, KeyEventArgs a_args)
        {
            if (m_isSourceModeFlg) return;
            var para = m_editor.CaretPosition?.Paragraph;
            if (para == null) return;

            if (a_args.Key == Key.Enter)
            {
                if (m_listEditor.IsInListItem(para, out ListItem li, out List parentList))
                {
                    a_args.Handled = true;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        m_headingCodeBlockEditor.InsertLineBreakAtCaret();
                    else
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
                    m_listEditor.OutdentListItem(tabLi, tabList);
                else
                    m_listEditor.IndentListItem(tabLi, tabList);
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
                if (a_args.Key == Key.Up || a_args.Key == Key.Down)
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

            bool inTableFlg = m_tableEditor.ContextCell != null;
            bool inCodeBlockFlg = m_ctxParagraph?.Tag is CodeBlockInfo;
            m_headingMenuItem.Visibility = inTableFlg ? Visibility.Collapsed : Visibility.Visible;
            m_insertTableMenuItem.Visibility = inTableFlg ? Visibility.Collapsed : Visibility.Visible;
            m_insertRowAboveMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_insertRowBelowMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_insertColumnLeftMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_insertColumnRightMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_deleteRowMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_deleteColumnMenuItem.Visibility = inTableFlg ? Visibility.Visible : Visibility.Collapsed;
            m_copyCodeBlockMenuItem.Visibility = inCodeBlockFlg ? Visibility.Visible : Visibility.Collapsed;
            m_saveImageMenuItem.Visibility = m_imageManager.ContextImage != null ? Visibility.Visible : Visibility.Collapsed;
            m_textStyleMenuItem.Visibility = (!m_editor.Selection.IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
            m_linkMenuItem.Visibility = m_inlineStyleEditor.ContextLinkRun != null ? Visibility.Visible : Visibility.Collapsed;
            m_toggleModeMenuItem.Header = m_isSourceModeFlg ? "MarkDownモードに切り替え" : "ソースモードに切り替え";
        }

        /// <summary>ソースモードの右クリックメニュー。カット/コピー/貼り付けのみで、独自の
        /// 項目は追加しない。</summary>
        private void SourceEditorContextMenuOpening(object a_sender, ContextMenuEventArgs a_args)
        {
        }

        // ---- 見出し ----

        /// <summary>右クリックメニューから見出しレベルを変更する。</summary>
        private void HeadingItemClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_ctxParagraph == null) return;
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
            if (dlg.ShowDialog() == true)
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

        // ---- 画像 ----

        private void SaveImageItemClick(object a_sender, RoutedEventArgs a_args) => m_imageManager.SaveImageAs(this);

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

        private void EditorPreviewMouseLeftButtonDown(object a_sender, MouseButtonEventArgs a_args) =>
            m_inlineStyleEditor.HandlePreviewMouseLeftButtonDown(a_sender, a_args);

        // ======================================================================
        //  視覚ツリーのヘルパー（Editor_ContextMenuOpeningでのみ使用）
        // ======================================================================

        /// <summary>指定した型の、最も近い視覚ツリーの祖先（またはその要素自身）を見つける。</summary>
        /// <param name="a_start">探索を始める要素。</param>
        /// <returns>見つかった要素。なければ null。</returns>
        private static T FindVisualAncestorOrSelf<T>(DependencyObject a_start) where T : DependencyObject
        {
            var current = a_start;
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ======================================================================
        //  モード切り替え（MarkDown ⇔ ソース）
        // ======================================================================

        /// <summary>MarkDownモード（WYSIWYG）とソースモード（生テキスト）を切り替える。</summary>
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
        private void NewBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            bool currentFileInFolderViewFlg = !string.IsNullOrEmpty(m_currentFileDirectory) &&
                m_folderTreeManager.IsWithinLoadedFolder(m_currentFileDirectory);

            if (!currentFileInFolderViewFlg && (m_currentFileIsDirtyFlg || m_pendingFileEdits.Count > 0))
            {
                var result = MessageBox.Show(
                    "現在の内容を破棄して新規作成します。保存されていない変更は失われますが、よろしいですか？",
                    "新規作成", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result != MessageBoxResult.OK) return;
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
        private void NewWindowClick(object a_sender, RoutedEventArgs a_args)
        {
            var newWindow = new MainWindow();
            newWindow.Show();
        }

        /// <summary>このウィンドウを閉じる（未保存の変更があればWindow_Closingで確認される）。</summary>
        private void CloseBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            Close();
        }

        /// <summary>ファイルを開くダイアログを表示し、選択されたファイルを読み込む。</summary>
        private void OpenBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Markdownファイル (*.md;*.markdown)|*.md;*.markdown|すべてのファイル (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                LoadFile(dlg.FileName);
            }
        }

        /// <summary>
        /// ファイルをエディタへ読み込む。すでに開いている同じファイルを再度開こうとした場合
        /// （未保存の変更を破棄してディスクの内容に戻すかどうかの確認）と、別のファイルへ
        /// 切り替える場合（まず今のファイルの未保存内容を退避する）の両方に対応する。
        /// </summary>
        /// <param name="a_path">開くファイルの絶対パス。</param>
        private void LoadFile(string a_path)
        {
            // 特殊ケース：すでにアクティブなファイルを再度開こうとした場合。この判定がないと、
            // 下のGetCurrentContentForFileがライブな（編集中の）内容をそのまま返してしまい、
            // 「開く」操作が何もしていないように見えてしまう。
            if (!string.IsNullOrEmpty(m_currentFilePath) && PathsReferToSameFile(a_path, m_currentFilePath))
            {
                if (!m_currentFileIsDirtyFlg) return; // 読み込み・保存後に編集がなければ何もしない

                var result = MessageBox.Show(
                    "このファイルには保存されていない変更があります。破棄して、保存済みの内容で開き直しますか？",
                    "ファイルを開き直す", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (result != MessageBoxResult.OK) return;

                m_pendingFileEdits.Remove(a_path); // このファイルの保留中の編集も破棄する

                string onDiskContent = SafeReadFile(a_path);
                if (onDiskContent == null)
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
                }
                m_searchReplaceService.OnDocumentReplaced();

                m_currentFileIsDirtyFlg = false;
                m_folderTreeManager.RefreshDirtyMarkers();
                return;
            }

            SnapshotCurrentFileIfDirty();

            string md = GetCurrentContentForFile(a_path);
            if (md == null)
            {
                md = SafeReadFile(a_path);
                if (md == null)
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

            if (m_isSourceModeFlg)
            {
                m_sourceEditor.Text = md;
            }
            else
            {
                RunAsProgrammaticChange(() => m_markdownConverter.MarkdownToDocument(md, m_editor.Document));
                m_outlineManager.Refresh();
            }
            m_searchReplaceService.OnDocumentReplaced();

            m_currentFileIsDirtyFlg = false;

            if (!string.IsNullOrEmpty(m_currentFileDirectory) && !m_folderTreeManager.IsWithinLoadedFolder(m_currentFileDirectory))
                m_folderTreeManager.LoadFolderTree(m_currentFileDirectory);
            else
                m_folderTreeManager.RefreshDirtyMarkers();
        }

        /// <summary>
        /// 現在開いているファイルに未保存の変更があれば、別のファイルへ切り替える前に
        /// pendingFileEditsへ退避する（1つしかないエディタを共有しているため、切り替え時に
        /// 内容が失われないようにするため）。ソースモードでは単純化のためスキップする。
        /// </summary>
        private void SnapshotCurrentFileIfDirty()
        {
            if (string.IsNullOrEmpty(m_currentFilePath) || m_isSourceModeFlg) return;

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

        /// <summary>現在のファイルを保存する（未保存の新規ファイルなら名前を付けて保存へ）。</summary>
        private void SaveBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (string.IsNullOrEmpty(m_currentFilePath))
            {
                SaveAs();
                return;
            }
            if (!m_isSourceModeFlg) m_imageManager.RelocatePendingTempImages(m_editor.Document);
            string md = m_isSourceModeFlg ? m_sourceEditor.Text : m_markdownConverter.DocumentToMarkdown(m_editor.Document);
            File.WriteAllText(m_currentFilePath, m_lineEndingTracker.Apply(md, m_lineEndingTracker.GetFor(m_currentFilePath)), new UTF8Encoding(false));
            m_currentFileIsDirtyFlg = false;
            m_folderTreeManager.AddFileNodeIfMissing(m_currentFilePath);
            m_folderTreeManager.RefreshDirtyMarkers();
            m_folderTreeManager.SelectFileNode(m_currentFilePath);
        }

        /// <summary>「名前を付けて保存」ダイアログを開く。</summary>
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
                FileName = m_currentFilePath != null ? Path.GetFileName(m_currentFilePath) : "document.md"
            };
            // 現在の文書自体には保存先フォルダがまだ無くても（新規作成直後など）、フォルダビューに
            // 何かフォルダが表示されていれば、そちらを初期フォルダとして使う。
            string initialDirectory = m_currentFileDirectory ?? m_folderTreeManager.LoadedFolderRootPath;
            if (!string.IsNullOrEmpty(initialDirectory)) dlg.InitialDirectory = initialDirectory;

            if (dlg.ShowDialog() != true) return;

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
                m_currentFileDirectory = newFileDirectory; // 画像パスの解決に一時的に必要
                if (!m_isSourceModeFlg) m_imageManager.RelocatePendingTempImages(m_editor.Document);

                string outsideMd = m_isSourceModeFlg ? m_sourceEditor.Text : m_markdownConverter.DocumentToMarkdown(m_editor.Document);
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

            if (!m_isSourceModeFlg) m_imageManager.RelocatePendingTempImages(m_editor.Document);

            string md = m_isSourceModeFlg ? m_sourceEditor.Text : m_markdownConverter.DocumentToMarkdown(m_editor.Document);
            File.WriteAllText(newFilePath, m_lineEndingTracker.Apply(md, lineEnding), new UTF8Encoding(false));
            m_currentFileIsDirtyFlg = false;

            if (!folderIsLoadedFlg)
                m_folderTreeManager.LoadFolderTree(m_currentFileDirectory);
            else
                m_folderTreeManager.AddFileNodeIfMissing(newFilePath);
            m_folderTreeManager.RefreshDirtyMarkers();
            m_folderTreeManager.SelectFileNode(newFilePath);
        }

        /// <summary>
        /// 現在の文書をPDFへ書き出す。追加のライブラリを使わず、Windows標準の「Microsoft Print to
        /// PDF」仮想プリンタへ印刷する形で実現している（印刷ダイアログでこのプリンタを選ぶと、
        /// 保存先を聞かれてPDFファイルが作成される）。
        /// </summary>
        private void ExportPdfBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_isSourceModeFlg)
            {
                MessageBox.Show("PDFへの書き出しはMarkDownモードでのみ利用できます。", "PDFに書き出し",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new System.Windows.Controls.PrintDialog();
            if (dlg.ShowDialog() != true) return;

            try
            {
                var paginator = ((IDocumentPaginatorSource)m_editor.Document).DocumentPaginator;
                paginator.PageSize = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
                string docName = "mde - " + (!string.IsNullOrEmpty(m_currentFilePath) ? Path.GetFileName(m_currentFilePath) : "無題");
                dlg.PrintDocument(paginator, docName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("書き出しに失敗しました: " + ex.Message, "PDFに書き出し",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>現在のファイルと、保留中の編集があるすべてのファイルを保存する。</summary>
        private void SaveAllBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_currentFileIsDirtyFlg || m_pendingFileEdits.Count > 0)
            {
                var confirmResult = MessageBox.Show(
                    "編集中のすべてのファイルを保存します。よろしいですか？",
                    "すべて保存", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (confirmResult != MessageBoxResult.OK) return;
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
                    if (!m_isSourceModeFlg) m_imageManager.RelocatePendingTempImages(m_editor.Document);
                    string md = m_isSourceModeFlg ? m_sourceEditor.Text : m_markdownConverter.DocumentToMarkdown(m_editor.Document);
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
                message += "\n\n保存に失敗したファイル:\n" + string.Join("\n", failures);

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
        private void SourceEditorTextChanged(object a_sender, TextChangedEventArgs a_args)
        {
            if (m_folderTreeManager == null) return; // InitializeComponent中の発火に対するガード
            m_currentFileIsDirtyFlg = true;
            m_folderTreeManager.RefreshDirtyMarkers();
        }

        /// <summary>エディタの幅が変わったら、画像のサイズ調整をやり直す。</summary>
        private void EditorSizeChanged(object a_sender, SizeChangedEventArgs a_args)
        {
            if (m_imageManager == null) return; // InitializeComponent中の発火に対するガード
            if (m_isSourceModeFlg) return;
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
        private void MainWindowPreviewKeyDown(object a_sender, KeyEventArgs a_args)
        {
            if (a_args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                OpenFindReplaceWindow();
            }
            // Ctrl+Nは「新規作成」のショートカットとして扱う（Windows標準のCtrl+Nは「新しいウィンドウ」なので、そちらは無効化する）。
            else if (a_args.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                NewBtnClick(a_sender, null);
            }
            // Shift+Ctrl+Nは「新しいウィンドウ」のショートカットとして扱う。
            else if (a_args.Key == Key.N && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                a_args.Handled = true;
                NewWindowClick(a_sender, null);
            }
            // Ctrl+Oは「開く」のショートカットとして扱う。
            else if (a_args.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                OpenBtnClick(a_sender, null);
            }
            // Ctrl+Sは「保存」のショートカットとして扱う（Windows標準のCtrl+Sは「すべて保存」なので、そちらは無効化する）。
            else if (a_args.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                SaveBtnClick(a_sender, null);
            }
            // Shift+Ctrl+Sは「名前を付けて保存...」のショートカットとして扱う。
            else if (a_args.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                a_args.Handled = true;
                SaveAs();
            }
            // Ctrl+Pは「PDFに書き出し」のショートカットとして扱う（Windows標準のCtrl+Pは「印刷」なので、そちらは無効化する）。
            else if (a_args.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                ExportPdfBtnClick(a_sender, null);
            }
            // Ctrl+Wは「ウィンドウを閉じる」のショートカットとして扱う。
            else if (a_args.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                a_args.Handled = true;
                Close();
            }
        }

        /// <summary>検索と置換ウィンドウを開く（すでに開いていれば、そちらを前面に出す）。</summary>
        private void OpenFindReplaceWindow()
        {
            if (m_openFindReplaceWindow != null)
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
        private void VersionInfoBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            var aboutWindow = new AboutWindow { Owner = this };
            aboutWindow.ShowDialog();
        }

        // ======================================================================
        //  フォルダ / アウトラインペインの表示・非表示切り替え
        // ======================================================================

        /// <summary>フォルダツリーペインの表示/非表示を切り替える（幅は次に表示する時のために
        /// 記憶しておく）。</summary>
        private void ToggleFolderPaneBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_folderPaneVisibleFlg && m_folderColumnDef.Width.Value > 0) m_lastFolderColumnWidth = m_folderColumnDef.Width.Value;
            m_folderPaneVisibleFlg = !m_folderPaneVisibleFlg;
            ApplyFolderPaneVisibility();
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
        /// 記憶しておく）。</summary>
        private void ToggleOutlinePaneBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_outlinePaneVisibleFlg && m_outlineColumnDef.Width.Value > 0) m_lastOutlineColumnWidth = m_outlineColumnDef.Width.Value;
            m_outlinePaneVisibleFlg = !m_outlinePaneVisibleFlg;
            ApplyOutlinePaneVisibility();
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

        // ======================================================================
        //  フォルダペイン
        // ======================================================================

        /// <summary>フォルダピッカーを表示し、選択されたフォルダをフォルダツリーペインへ
        /// 読み込む（可能であれば同じ相対パスのファイルを開いたままにする）。</summary>
        private void OpenFolderTreeBtnClick(object a_sender, RoutedEventArgs a_args)
        {
            if (m_currentFileIsDirtyFlg || m_pendingFileEdits.Count > 0)
            {
                var confirmResult = MessageBox.Show(
                    "保存されていない変更があります。破棄して別のフォルダを開きますか？",
                    "フォルダを開く", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (confirmResult != MessageBoxResult.OK) return;
            }

            string previousRelativePath = m_folderTreeManager.GetCurrentFileRelativePath();

            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (dlg.ShowDialog() != true) return;

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

        private static T FindVisualChild<T>(DependencyObject a_root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(a_root); i++)
            {
                var child = VisualTreeHelper.GetChild(a_root, i);
                if (child is T match) return match;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
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
            if (m_folderTreeScrollViewer != null)
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
