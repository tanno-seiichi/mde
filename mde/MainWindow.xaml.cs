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
        private bool isSourceMode = false;

        /// <summary>ユーザーの入力ではなく、コードによる変更を行っている間だけtrueにするガード
        /// フラグ。TextChangedハンドラの自動変換ロジックが、プログラムによる変更にまで
        /// 反応してしまわないようにするためのもの。</summary>
        private bool isProgrammaticChange = false;

        /// <summary>検索結果のハイライト（背景色）を適用/解除している間だけtrueにするガード
        /// フラグ。TextChangedがこれを見て、ダーティ扱いにしないようにする。</summary>
        private bool isApplyingHighlight = false;

        /// <summary>現在エディタに表示中のファイルの絶対パス。未保存なら null。</summary>
        private string currentFilePath = null;

        /// <summary>currentFilePathの保存先フォルダ（画像の相対パス解決で毎回計算し直さずに
        /// 済むようキャッシュしている）。</summary>
        private string currentFileDirectory = null;

        /// <summary>まだディスクに書き出されていない、メモリ上だけの編集内容（フォルダ全体の
        /// 置換、または編集中のファイルから離れた際に発生する）。キー=絶対パス、値=そのファイルの
        /// 現在のMarkDown内容。</summary>
        private readonly Dictionary<string, string> pendingFileEdits =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>現在のファイル（currentFilePath）に未保存の変更があるかどうか。</summary>
        private bool currentFileIsDirty = false;

        /// <summary>エディタに適用中のズーム倍率（1.0が100%）。</summary>
        private double zoomLevel = 1.0;

        /// <summary>このウィンドウ専用の一時フォルダ識別子（複数ウィンドウを同時に開いた際、
        /// ドラッグ挿入した画像のファイル名が衝突しないようにするため）。</summary>
        private readonly string instanceTempId = Guid.NewGuid().ToString("N");

        /// <summary>起動時に読み込んだ、前回終了時のウィンドウ状態設定。</summary>
        private AppSettings savedSettings;

        /// <summary>現在開いている検索と置換ウィンドウ（Ctrl+Fなどで、同時に2つ開かず既存の
        /// ものを前面に出すために使う）。開いていなければ null。</summary>
        private FindReplaceWindow openFindReplaceWindow;

        // フォルダ/アウトラインペインの表示・非表示状態
        private double lastFolderColumnWidth = 190;
        private double lastOutlineColumnWidth = 210;
        private bool folderPaneVisible = true;
        private bool outlinePaneVisible = true;

        // 右クリック時の対象（表・コードブロックまわりはTableEditor/InlineStyleEditorに
        // それぞれ専用のプロパティがあるので、ここではまだどのクラスにも属さないものだけを保持する）
        private Paragraph ctxParagraph;

        // ======================================================================
        //  各機能クラス（協力オブジェクト）
        // ======================================================================

        private readonly OriginalTextTracker originalTextTracker;
        private readonly LineEndingTracker lineEndingTracker;
        private readonly ImageManager imageManager;
        private readonly MarkdownConverter markdownConverter;
        private readonly ListEditor listEditor;
        private readonly HeadingCodeBlockEditor headingCodeBlockEditor;
        private readonly TableEditor tableEditor;
        private readonly InlineStyleEditor inlineStyleEditor;
        private readonly SearchReplaceService searchReplaceService;
        private readonly OutlineManager outlineManager;
        private readonly FolderTreeManager folderTreeManager;

        /// <summary>ウィンドウを初期化し、各機能クラスを構築・配線したうえで、初回起動時の
        /// 案内文書を読み込む。</summary>
        public MainWindow()
        {
            InitializeComponent();

            savedSettings = AppSettings.Load();
            if (!double.IsNaN(savedSettings.WindowLeft) && !double.IsNaN(savedSettings.WindowTop))
            {
                this.WindowStartupLocation = WindowStartupLocation.Manual;
                this.Left = savedSettings.WindowLeft;
                this.Top = savedSettings.WindowTop;
            }
            this.Width = savedSettings.WindowWidth;
            this.Height = savedSettings.WindowHeight;
            zoomLevel = savedSettings.ZoomLevel;
            folderPaneVisible = savedSettings.FolderPaneVisible;
            outlinePaneVisible = savedSettings.OutlinePaneVisible;

            originalTextTracker = new OriginalTextTracker(Editor);
            lineEndingTracker = new LineEndingTracker(PathsReferToSameFile);
            outlineManager = new OutlineManager(Editor);
            imageManager = new ImageManager(
                Editor, originalTextTracker, () => isSourceMode, () => currentFileDirectory,
                RunAsProgrammaticChange, outlineManager.Refresh, instanceTempId);
            markdownConverter = new MarkdownConverter(originalTextTracker, imageManager);
            listEditor = new ListEditor(Editor, originalTextTracker, RunAsProgrammaticChange);
            headingCodeBlockEditor = new HeadingCodeBlockEditor(Editor, originalTextTracker, RunAsProgrammaticChange);
            tableEditor = new TableEditor(
                Editor, originalTextTracker, MarkDirty, RunAsProgrammaticChange, () => isSourceMode,
                outlineManager.Refresh, InsertPlainTextWithLineBreaksForCodeBlock);
            folderTreeManager = new FolderTreeManager(
                LoadFile, () => currentFilePath, () => currentFileIsDirty,
                () => pendingFileEdits.Keys, PathsReferToSameFile);
            inlineStyleEditor = new InlineStyleEditor(
                Editor, originalTextTracker, RunAsProgrammaticChange, MarkDirty, outlineManager.Refresh,
                markdownConverter.BlockToMarkdown, () => currentFileDirectory, LoadFile,
                folderTreeManager.IsWithinLoadedFolder, OpenFileInNewWindow);
            searchReplaceService = new SearchReplaceService(
                Editor, SourceEditor, markdownConverter, originalTextTracker, lineEndingTracker,
                () => isSourceMode, RunAsProgrammaticChange, outlineManager.Refresh, p => OutlineManager.ScrollParagraphToTop(p, Editor),
                () => folderTreeManager.LoadedFolderRootPath, GetCurrentContentForFile,
                SetFileContentForReplaceImpl, LoadFile, RunWithoutDirtyMarking, outlineManager.MarkSearchMatches);

            this.Title = Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version;
            OutlineList.ItemsSource = outlineManager.Items;
            FolderTree.ItemsSource = folderTreeManager.Roots;
            DataObject.AddCopyingHandler(Editor, tableEditor.HandleCopying);
            DataObject.AddPastingHandler(Editor, tableEditor.HandlePasting);

            if (!string.IsNullOrEmpty(savedSettings.LastFilePath) && File.Exists(savedSettings.LastFilePath))
                LoadFile(savedSettings.LastFilePath);
            else
                LoadIntroContent();

            ApplyFolderPaneVisibility();
            ApplyOutlinePaneVisibility();
            SetZoom(zoomLevel);
            if (savedSettings.IsMaximized) this.WindowState = WindowState.Maximized;
        }

        /// <summary>ウィンドウを閉じる際、このウィンドウ専用の一時画像フォルダを削除し、
        /// 次回起動時に復元するウィンドウ状態を保存する。</summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "mde", instanceTempId);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // 削除できなくても致命的ではない（ベストエフォート）
            }

            bool maximized = this.WindowState == WindowState.Maximized;
            Rect bounds = maximized ? this.RestoreBounds : new Rect(this.Left, this.Top, this.Width, this.Height);
            var settings = new AppSettings
            {
                IsMaximized = maximized,
                WindowWidth = bounds.Width > 0 ? bounds.Width : savedSettings.WindowWidth,
                WindowHeight = bounds.Height > 0 ? bounds.Height : savedSettings.WindowHeight,
                WindowLeft = bounds.X,
                WindowTop = bounds.Y,
                FolderPaneVisible = folderPaneVisible,
                OutlinePaneVisible = outlinePaneVisible,
                ZoomLevel = zoomLevel,
                LastFilePath = currentFilePath
            };
            settings.Save();
        }

        // ======================================================================
        //  各クラスへ渡す共通delegateの実装
        // ======================================================================

        /// <summary>渡された処理を「プログラムによる変更」として実行する。実行中は
        /// Editor_TextChangedの自動変換ロジック（*や#での自動変換など）が働かない。</summary>
        /// <param name="action">実行する処理。</param>
        private void RunAsProgrammaticChange(Action action)
        {
            isProgrammaticChange = true;
            try { action(); }
            finally { isProgrammaticChange = false; }
        }

        /// <summary>渡された処理を「ハイライトの適用/解除のみ」として実行する。実行中は
        /// Editor_TextChangedがダーティ扱い・アウトライン再構築・元テキスト保持の破棄を
        /// 行わないようにする（検索結果の背景色変更は実際の編集ではないため）。</summary>
        /// <param name="action">実行する処理。</param>
        private void RunWithoutDirtyMarking(Action action)
        {
            isApplyingHighlight = true;
            try { action(); }
            finally { isApplyingHighlight = false; }
        }

        /// <summary>現在のファイルに未保存の変更があることを記録し、フォルダツリーの表示も
        /// 更新する。</summary>
        private void MarkDirty()
        {
            currentFileIsDirty = true;
            folderTreeManager.RefreshDirtyMarkers();
        }

        /// <summary>2つのパスが同一ファイルを指しているかどうかを調べる（大文字小文字を
        /// 区別せず、完全パスで比較する）。</summary>
        private bool PathsReferToSameFile(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>コードブロックへの貼り付け時に使う、改行を保ったプレーンテキスト挿入。
        /// TableEditorの貼り付け処理から呼ばれる。</summary>
        private void InsertPlainTextWithLineBreaksForCodeBlock(string text)
        {
            originalTextTracker.Invalidate(Editor.CaretPosition);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            RunAsProgrammaticChange(() =>
            {
                Editor.Selection.Text = lines[0];
                Editor.CaretPosition = Editor.Selection.End;
                for (int i = 1; i < lines.Length; i++)
                {
                    Editor.CaretPosition = Editor.CaretPosition.InsertLineBreak();
                    Editor.Selection.Select(Editor.CaretPosition, Editor.CaretPosition);
                    Editor.Selection.Text = lines[i];
                    Editor.CaretPosition = Editor.Selection.End;
                }
            });
        }

        /// <summary>
        /// フォルダペインに表示されていない範囲のファイルへのリンクをクリックした際、現在の
        /// ウィンドウの文書を置き換えるのではなく、新しいウィンドウでそのファイルを開く。
        /// </summary>
        /// <param name="path">開くファイルの絶対パス。</param>
        /// <param name="anchor">開いたあとにジャンプする見出し/アンカーのテキスト（無ければnull）。</param>
        private void OpenFileInNewWindow(string path, string anchor)
        {
            var newWindow = new MainWindow();
            newWindow.Show();
            newWindow.LoadFile(path);
            if (!string.IsNullOrEmpty(anchor))
            {
                newWindow.inlineStyleEditor.JumpToAnchor(anchor);
            }
        }

        /// <summary>
        /// ファイルの「今の内容」を解決する：現在開いているファイルならライブなエディタ内容、
        /// 保留中の編集があればそれ、どちらでもなければ null（呼び出し側がディスクから
        /// 読み込む）。
        /// </summary>
        private string GetCurrentContentForFile(string path)
        {
            if (!string.IsNullOrEmpty(currentFilePath) && PathsReferToSameFile(path, currentFilePath))
                return isSourceMode ? SourceEditor.Text : markdownConverter.DocumentToMarkdown(Editor.Document);

            foreach (var kv in pendingFileEdits)
                if (PathsReferToSameFile(kv.Key, path)) return kv.Value;

            return null;
        }

        /// <summary>検索・置換の結果をファイルへ反映する：現在開いているファイルならライブな
        /// エディタへ直接、そうでなければ保留中の編集として記憶する。</summary>
        private void SetFileContentForReplaceImpl(string path, string newContent)
        {
            if (!string.IsNullOrEmpty(currentFilePath) && PathsReferToSameFile(path, currentFilePath))
            {
                if (isSourceMode)
                {
                    SourceEditor.Text = newContent;
                }
                else
                {
                    RunAsProgrammaticChange(() => markdownConverter.MarkdownToDocument(newContent, Editor.Document));
                    outlineManager.Refresh();
                }
            }
            else
            {
                pendingFileEdits[path] = newContent;
                folderTreeManager.RefreshDirtyMarkers();
            }
        }

        // ======================================================================
        //  初期表示コンテンツ
        // ======================================================================

        /// <summary>初回起動時に表示する、組み込みの案内文書を読み込む。</summary>
        private void LoadIntroContent()
        {
            string intro = string.Join("\n", new[]
            {
                "# MarkDown インラインエディタ（デスクトップ版）",
                "",
                "このエディタは、編集ペインとプレビューペインが分かれていません。この画面に直接書き込むと、そのままMarkDownとして整形されます。",
                "",
                "* 「* 」と入力すると箇条書きに変わります",
                "* Enterキーで次の項目に進みます",
                "* Shift+Enterで項目内改行、Tabで字下げ（Shift+Tabで戻す）ができます",
                "* 空の項目でEnterキーを押すと箇条書きを抜けます",
                "",
                "「# 」〜「###### 」と入力すると見出しに変わります。右クリックでも見出しレベルを選べます。",
                "",
                "右クリックで表の挿入、行・列の削除、ソースモードへの切り替えもできます。",
                "",
                "「```」または「```言語名」と入力してEnterを押すとコードブロックになります。抜けるときはコードブロックの上下の行をクリックしてください。",
                "",
                "「開く…」でMarkDownファイルを選ぶと、同じフォルダにある画像も自動的に表示されます。"
            });
            markdownConverter.MarkdownToDocument(intro, Editor.Document);
            outlineManager.Refresh();
            currentFileIsDirty = false;
        }

        // ======================================================================
        //  入力中の自動変換の起点（Editor_TextChanged）
        // ======================================================================

        /// <summary>
        /// MarkDownモードのメイン変更ハンドラ：ファイルをダーティにし、アウトラインを再構築し、
        /// 触れたブロックの「元テキスト保持」の記憶を破棄する。実際のユーザー入力によるものだけ
        /// （プログラムによる変更でなければ）、インライン装飾・箇条書き/見出しへの自動変換の
        /// トリガーもチェックする。
        /// </summary>
        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            // RichTextBoxはInitializeComponent中に、既定の空文書を設定する際にTextChangedを
            // 発生させることがある。その時点ではコンストラクタでの各クラスの構築がまだ
            // 完了していない可能性があるため、念のためガードしておく。
            if (outlineManager == null || folderTreeManager == null || originalTextTracker == null) return;

            if (isSourceMode) return;

            // 検索結果のハイライト（背景色）の適用/解除も、WPFの仕様上TextChangedを発生させて
            // しまうが、これは実際の編集ではないため、ダーティ扱いにしたり元テキスト保持の
            // 記憶を破棄したりしてはいけない。
            if (isApplyingHighlight) return;

            outlineManager.Refresh();

            currentFileIsDirty = true;
            folderTreeManager.RefreshDirtyMarkers();
            originalTextTracker.Invalidate(Editor.CaretPosition);

            if (isProgrammaticChange) return;

            var para = Editor.CaretPosition?.Paragraph;
            if (para == null) return;
            if (para.Tag is CodeBlockInfo) return; // コードブロック内は自動整形しない

            if (inlineStyleEditor.CheckInlineFormatTrigger()) return;

            if (!(para.Parent is FlowDocument)) return; // 箇条書き/見出しへの自動変換はトップレベル段落のみ

            string text = new TextRange(para.ContentStart, para.ContentEnd).Text;
            text = text.TrimEnd('\r', '\n');

            var bulletMatch = Regex.Match(text, "^([*-])[ \u00A0]$");
            if (bulletMatch.Success)
            {
                listEditor.ConvertParagraphToListItem(para, bulletMatch.Groups[1].Value, false);
                return;
            }
            var orderedMatch = Regex.Match(text, "^\\d+\\.[ \u00A0]$");
            if (orderedMatch.Success)
            {
                listEditor.ConvertParagraphToListItem(para, null, true);
                return;
            }
            var m = Regex.Match(text, "^(#{1,6})[ \u00A0]$");
            if (m.Success)
            {
                headingCodeBlockEditor.ConvertParagraphToHeading(para, m.Groups[1].Value.Length);
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
        /// e.Handled=trueを設定するだけでは確実に文字の挿入を防げない。このイベントで直接
        /// 判定・処理することで、変換後に元のスペース文字が余分に残ってしまう不具合を防ぐ。
        /// </summary>
        private void Editor_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (isSourceMode || e.Text != " ") return;

            var para = Editor.CaretPosition?.Paragraph;
            if (para == null || !(para.Parent is FlowDocument) || para.Tag is CodeBlockInfo) return;
            if (Editor.CaretPosition.CompareTo(para.ContentEnd) != 0) return;

            string beforeSpace = new TextRange(para.ContentStart, para.ContentEnd).Text.TrimEnd('\r', '\n');

            var bulletKeyMatch = Regex.Match(beforeSpace, "^([*-])$");
            if (bulletKeyMatch.Success)
            {
                e.Handled = true;
                listEditor.ConvertParagraphToListItem(para, bulletKeyMatch.Groups[1].Value, false);
                return;
            }
            var orderedKeyMatch = Regex.Match(beforeSpace, "^\\d+\\.$");
            if (orderedKeyMatch.Success)
            {
                e.Handled = true;
                listEditor.ConvertParagraphToListItem(para, null, true);
            }
        }

        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (isSourceMode) return;
            var para = Editor.CaretPosition?.Paragraph;
            if (para == null) return;

            if (e.Key == Key.Enter)
            {
                if (listEditor.IsInListItem(para, out ListItem li, out List parentList))
                {
                    e.Handled = true;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        headingCodeBlockEditor.InsertLineBreakAtCaret();
                    else
                        listEditor.HandleListEnter(li, parentList);
                    return;
                }
                if (para.Tag is int level && level > 0)
                {
                    e.Handled = true;
                    headingCodeBlockEditor.HandleHeadingEnter(para);
                    return;
                }
                if (para.Tag is CodeBlockInfo)
                {
                    e.Handled = true;
                    headingCodeBlockEditor.InsertLineBreakAtCaret();
                    return;
                }
                if (para.Parent is TableCell)
                {
                    e.Handled = true;
                    headingCodeBlockEditor.InsertLineBreakAtCaret();
                    return;
                }
                if (para.Parent is FlowDocument)
                {
                    string plainText = new TextRange(para.ContentStart, para.ContentEnd).Text.TrimEnd('\r', '\n');
                    var fenceMatch = Regex.Match(plainText, "^```(\\S*)$");
                    if (fenceMatch.Success)
                    {
                        e.Handled = true;
                        headingCodeBlockEditor.ConvertParagraphToCodeBlock(para, fenceMatch.Groups[1].Value);
                        return;
                    }
                }
                return; // 通常の段落: WPF標準の動作（新しい段落の作成）に任せる
            }

            if (e.Key == Key.Tab && listEditor.IsInListItem(para, out ListItem tabLi, out List tabList))
            {
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    listEditor.OutdentListItem(tabLi, tabList);
                else
                    listEditor.IndentListItem(tabLi, tabList);
                return;
            }

            if (e.Key == Key.Tab && para.Tag is CodeBlockInfo)
            {
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    headingCodeBlockEditor.OutdentCodeLine(para);
                }
                else
                {
                    Editor.Selection.Text = "\t";
                    Editor.CaretPosition = Editor.Selection.End;
                    Editor.Selection.Select(Editor.CaretPosition, Editor.CaretPosition);
                }
                return;
            }

            if (para.Parent is TableCell cell)
            {
                if (e.Key == Key.Up || e.Key == Key.Down)
                {
                    e.Handled = true;
                    tableEditor.MoveVertical(cell, e.Key == Key.Up ? -1 : 1);
                }
                else if (e.Key == Key.Left && tableEditor.IsCaretAtStart(cell))
                {
                    e.Handled = true;
                    tableEditor.MoveHorizontal(cell, -1);
                }
                else if (e.Key == Key.Right && tableEditor.IsCaretAtEnd(cell))
                {
                    e.Handled = true;
                    tableEditor.MoveHorizontal(cell, 1);
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
        private void Editor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (isSourceMode) { e.Handled = true; return; }

            Point pos = Mouse.GetPosition(Editor);
            TextPointer tp = Editor.GetPositionFromPoint(pos, true);
            ctxParagraph = tp?.Paragraph;
            tableEditor.ContextCell = ctxParagraph?.Parent as TableCell;
            tableEditor.ContextParagraph = ctxParagraph;
            inlineStyleEditor.ContextParagraph = ctxParagraph;

            var hit = VisualTreeHelper.HitTest(Editor, pos);
            imageManager.ContextImage = FindVisualAncestorOrSelf<Image>(hit?.VisualHit);

            var linkRun = tp?.Parent as Run;
            inlineStyleEditor.ContextLinkRun = linkRun?.Tag is LinkInfo ? linkRun : null;

            bool inTable = tableEditor.ContextCell != null;
            bool inCodeBlock = ctxParagraph?.Tag is CodeBlockInfo;
            HeadingMenuItem.Visibility = inTable ? Visibility.Collapsed : Visibility.Visible;
            InsertTableMenuItem.Visibility = inTable ? Visibility.Collapsed : Visibility.Visible;
            InsertRowAboveMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            InsertRowBelowMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            InsertColumnLeftMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            InsertColumnRightMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            DeleteRowMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            DeleteColumnMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            CopyCodeBlockMenuItem.Visibility = inCodeBlock ? Visibility.Visible : Visibility.Collapsed;
            SaveImageMenuItem.Visibility = imageManager.ContextImage != null ? Visibility.Visible : Visibility.Collapsed;
            TextStyleMenuItem.Visibility = (!Editor.Selection.IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
            LinkMenuItem.Visibility = inlineStyleEditor.ContextLinkRun != null ? Visibility.Visible : Visibility.Collapsed;
            ToggleModeMenuItem.Header = isSourceMode ? "MarkDownモードに切り替え" : "ソースモードに切り替え";
        }

        /// <summary>ソースモードの右クリックメニュー。カット/コピー/貼り付けのみで、独自の
        /// 項目は追加しない。</summary>
        private void SourceEditor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
        }

        // ---- 見出し ----

        /// <summary>右クリックメニューから見出しレベルを変更する。</summary>
        private void HeadingItem_Click(object sender, RoutedEventArgs e)
        {
            if (ctxParagraph == null) return;
            int level = int.Parse((string)((MenuItem)sender).Tag);
            headingCodeBlockEditor.ChangeHeadingLevel(ctxParagraph, level);
            outlineManager.Refresh();
            MarkDirty();
        }

        // ---- 表 ----

        private void InsertTableItem_Click(object sender, RoutedEventArgs e)
        {
            tableEditor.ContextParagraph = ctxParagraph;
            var dlg = new TableSizeDialog { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                tableEditor.InsertTable(dlg.Rows, dlg.Columns);
            }
        }

        private void InsertRowAboveItem_Click(object sender, RoutedEventArgs e) => tableEditor.InsertRow(above: true);
        private void InsertRowBelowItem_Click(object sender, RoutedEventArgs e) => tableEditor.InsertRow(above: false);
        private void InsertColumnLeftItem_Click(object sender, RoutedEventArgs e) => tableEditor.InsertColumn(left: true);
        private void InsertColumnRightItem_Click(object sender, RoutedEventArgs e) => tableEditor.InsertColumn(left: false);
        private void DeleteRowItem_Click(object sender, RoutedEventArgs e) => tableEditor.DeleteRow();
        private void DeleteColumnItem_Click(object sender, RoutedEventArgs e) => tableEditor.DeleteColumn();

        // ---- 画像 ----

        private void SaveImageItem_Click(object sender, RoutedEventArgs e) => imageManager.SaveImageAs(this);

        // ---- コードブロック ----

        private void CopyCodeBlockItem_Click(object sender, RoutedEventArgs e) => inlineStyleEditor.CopyCodeBlockAsMarkdown();

        // ---- 文字装飾・リンク ----

        private void TextStyleItem_Click(object sender, RoutedEventArgs e)
        {
            string style = (string)((MenuItem)sender).Tag;
            inlineStyleEditor.ApplyTextStyleFromMenu(style, this);
        }

        private void LinkOpen_Click(object sender, RoutedEventArgs e) => inlineStyleEditor.OpenContextLink();
        private void LinkCopyUrl_Click(object sender, RoutedEventArgs e) => inlineStyleEditor.CopyContextLinkUrl();
        private void LinkEdit_Click(object sender, RoutedEventArgs e) => inlineStyleEditor.EditContextLink(this);
        private void LinkRemove_Click(object sender, RoutedEventArgs e) => inlineStyleEditor.RemoveContextLink();

        private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
            inlineStyleEditor.HandlePreviewMouseLeftButtonDown(sender, e);

        // ======================================================================
        //  視覚ツリーのヘルパー（Editor_ContextMenuOpeningでのみ使用）
        // ======================================================================

        /// <summary>指定した型の、最も近い視覚ツリーの祖先（またはその要素自身）を見つける。</summary>
        /// <param name="start">探索を始める要素。</param>
        /// <returns>見つかった要素。なければ null。</returns>
        private static T FindVisualAncestorOrSelf<T>(DependencyObject start) where T : DependencyObject
        {
            var current = start;
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
        private void ToggleModeBtn_Click(object sender, RoutedEventArgs e)
        {
            bool wasDirty = currentFileIsDirty;

            if (!isSourceMode)
            {
                SourceEditor.Text = markdownConverter.DocumentToMarkdown(Editor.Document);
                Editor.Visibility = Visibility.Collapsed;
                SourceEditor.Visibility = Visibility.Visible;
                isSourceMode = true;
                ModeIndicator.Text = "ソースモード";
                SourceEditor.Focus();
            }
            else
            {
                RunAsProgrammaticChange(() => markdownConverter.MarkdownToDocument(SourceEditor.Text, Editor.Document));
                SourceEditor.Visibility = Visibility.Collapsed;
                Editor.Visibility = Visibility.Visible;
                isSourceMode = false;
                ModeIndicator.Text = "MarkDownモード";
                outlineManager.Refresh();
                Editor.Focus();
            }

            // 表示モードの切り替えは、同じ内容を表示し直しているだけなので、それ自体で
            // ファイルが「未保存」扱いになってしまってはいけない。
            currentFileIsDirty = wasDirty;
            folderTreeManager.RefreshDirtyMarkers();
        }

        // ======================================================================
        //  新規作成 / 開く / 保存 / 名前を付けて保存
        // ======================================================================

        /// <summary>現在の内容を破棄して（未保存なら確認のうえ）新規文書を開始する。</summary>
        private void NewBtn_Click(object sender, RoutedEventArgs e)
        {
            if (currentFileIsDirty || pendingFileEdits.Count > 0)
            {
                var result = MessageBox.Show(
                    "現在の内容を破棄して新規作成します。保存されていない変更は失われますが、よろしいですか？",
                    "新規作成", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result != MessageBoxResult.OK) return;
            }

            DiscardCurrentDocumentSilently();
            Editor.Focus();
        }

        /// <summary>ファイルを開くダイアログを表示し、選択されたファイルを読み込む。</summary>
        private void OpenBtn_Click(object sender, RoutedEventArgs e)
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
        /// <param name="path">開くファイルの絶対パス。</param>
        private void LoadFile(string path)
        {
            // 特殊ケース：すでにアクティブなファイルを再度開こうとした場合。この判定がないと、
            // 下のGetCurrentContentForFileがライブな（編集中の）内容をそのまま返してしまい、
            // 「開く」操作が何もしていないように見えてしまう。
            if (!string.IsNullOrEmpty(currentFilePath) && PathsReferToSameFile(path, currentFilePath))
            {
                if (!currentFileIsDirty) return; // 読み込み・保存後に編集がなければ何もしない

                var result = MessageBox.Show(
                    "このファイルには保存されていない変更があります。破棄して、保存済みの内容で開き直しますか？",
                    "ファイルを開き直す", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (result != MessageBoxResult.OK) return;

                pendingFileEdits.Remove(path); // このファイルの保留中の編集も破棄する

                string onDiskContent = SafeReadFile(path);
                if (onDiskContent == null)
                {
                    MessageBox.Show("ファイルを開けませんでした。", "ファイルを開く", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (isSourceMode)
                {
                    SourceEditor.Text = onDiskContent;
                }
                else
                {
                    RunAsProgrammaticChange(() => markdownConverter.MarkdownToDocument(onDiskContent, Editor.Document));
                    outlineManager.Refresh();
                }
                searchReplaceService.OnDocumentReplaced();

                currentFileIsDirty = false;
                folderTreeManager.RefreshDirtyMarkers();
                return;
            }

            SnapshotCurrentFileIfDirty();

            string md = GetCurrentContentForFile(path);
            if (md == null)
            {
                md = SafeReadFile(path);
                if (md == null)
                {
                    MessageBox.Show("ファイルを開けませんでした。", "ファイルを開く",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            currentFilePath = path;
            currentFileDirectory = Path.GetDirectoryName(path);
            this.Title = Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version + " - " + Path.GetFileName(path);
            pendingFileEdits.Remove(path); // このファイルの内容は、以後エディタ自体が真実の情報源になる

            if (isSourceMode)
            {
                SourceEditor.Text = md;
            }
            else
            {
                RunAsProgrammaticChange(() => markdownConverter.MarkdownToDocument(md, Editor.Document));
                outlineManager.Refresh();
            }
            searchReplaceService.OnDocumentReplaced();

            currentFileIsDirty = false;

            if (!string.IsNullOrEmpty(currentFileDirectory) && !folderTreeManager.IsWithinLoadedFolder(currentFileDirectory))
                folderTreeManager.LoadFolderTree(currentFileDirectory);
            else
                folderTreeManager.RefreshDirtyMarkers();
        }

        /// <summary>
        /// 現在開いているファイルに未保存の変更があれば、別のファイルへ切り替える前に
        /// pendingFileEditsへ退避する（1つしかないエディタを共有しているため、切り替え時に
        /// 内容が失われないようにするため）。ソースモードでは単純化のためスキップする。
        /// </summary>
        private void SnapshotCurrentFileIfDirty()
        {
            if (string.IsNullOrEmpty(currentFilePath) || isSourceMode) return;

            if (!currentFileIsDirty)
            {
                pendingFileEdits.Remove(currentFilePath);
                return;
            }

            try
            {
                pendingFileEdits[currentFilePath] = markdownConverter.DocumentToMarkdown(Editor.Document);
            }
            catch
            {
                // ベストエフォートのみ。これが原因でファイル切り替えをブロックすることはない
            }
        }

        /// <summary>ファイルの内容を読み込み、改行コードを検出・記憶する。</summary>
        private string SafeReadFile(string path)
        {
            try
            {
                string content = File.ReadAllText(path, Encoding.UTF8);
                lineEndingTracker.DetectAndRemember(path, content);
                return content;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>現在のファイルを保存する（未保存の新規ファイルなら名前を付けて保存へ）。</summary>
        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentFilePath))
            {
                SaveAs();
                return;
            }
            if (!isSourceMode) imageManager.RelocatePendingTempImages(Editor.Document);
            string md = isSourceMode ? SourceEditor.Text : markdownConverter.DocumentToMarkdown(Editor.Document);
            File.WriteAllText(currentFilePath, lineEndingTracker.Apply(md, lineEndingTracker.GetFor(currentFilePath)), new UTF8Encoding(false));
            currentFileIsDirty = false;
            folderTreeManager.RefreshDirtyMarkers();
        }

        /// <summary>「名前を付けて保存」ダイアログを開く。</summary>
        private void SaveAsBtn_Click(object sender, RoutedEventArgs e)
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
                FileName = currentFilePath != null ? Path.GetFileName(currentFilePath) : "document.md"
            };
            if (currentFileDirectory != null) dlg.InitialDirectory = currentFileDirectory;

            if (dlg.ShowDialog() == true)
            {
                // 「名前を付けて保存」は、保存元ファイルの改行コードスタイルを引き継ぐ
                // （名前や場所が変わっただけで、その設定を失わせないため）。
                string lineEnding = !string.IsNullOrEmpty(currentFilePath) ? lineEndingTracker.GetFor(currentFilePath) : "\r\n";
                lineEndingTracker.SetFor(dlg.FileName, lineEnding);

                currentFilePath = dlg.FileName;
                currentFileDirectory = Path.GetDirectoryName(dlg.FileName);
                this.Title = Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version + " - " + Path.GetFileName(dlg.FileName);

                if (!isSourceMode) imageManager.RelocatePendingTempImages(Editor.Document);

                string md = isSourceMode ? SourceEditor.Text : markdownConverter.DocumentToMarkdown(Editor.Document);
                File.WriteAllText(dlg.FileName, lineEndingTracker.Apply(md, lineEnding), new UTF8Encoding(false));
                currentFileIsDirty = false;

                if (!string.IsNullOrEmpty(currentFileDirectory) && !folderTreeManager.IsWithinLoadedFolder(currentFileDirectory))
                    folderTreeManager.LoadFolderTree(currentFileDirectory);
                else
                    folderTreeManager.RefreshDirtyMarkers();
            }
        }

        /// <summary>
        /// 現在の文書をPDFへ書き出す。追加のライブラリを使わず、Windows標準の「Microsoft Print to
        /// PDF」仮想プリンタへ印刷する形で実現している（印刷ダイアログでこのプリンタを選ぶと、
        /// 保存先を聞かれてPDFファイルが作成される）。
        /// </summary>
        private void ExportPdfBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isSourceMode)
            {
                MessageBox.Show("PDFへの書き出しはMarkDownモードでのみ利用できます。", "PDFに書き出し",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new System.Windows.Controls.PrintDialog();
            if (dlg.ShowDialog() != true) return;

            try
            {
                var paginator = ((IDocumentPaginatorSource)Editor.Document).DocumentPaginator;
                paginator.PageSize = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
                string docName = "mde - " + (!string.IsNullOrEmpty(currentFilePath) ? Path.GetFileName(currentFilePath) : "無題");
                dlg.PrintDocument(paginator, docName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("書き出しに失敗しました: " + ex.Message, "PDFに書き出し",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>現在のファイルと、保留中の編集があるすべてのファイルを保存する。</summary>
        private void SaveAllBtn_Click(object sender, RoutedEventArgs e)
        {
            int savedCount = 0;
            var failures = new List<string>();

            if (!string.IsNullOrEmpty(currentFilePath))
            {
                try
                {
                    if (!isSourceMode) imageManager.RelocatePendingTempImages(Editor.Document);
                    string md = isSourceMode ? SourceEditor.Text : markdownConverter.DocumentToMarkdown(Editor.Document);
                    File.WriteAllText(currentFilePath, lineEndingTracker.Apply(md, lineEndingTracker.GetFor(currentFilePath)), new UTF8Encoding(false));
                    pendingFileEdits.Remove(currentFilePath);
                    currentFileIsDirty = false;
                    savedCount++;
                }
                catch (Exception ex)
                {
                    failures.Add(currentFilePath + " (" + ex.Message + ")");
                }
            }

            foreach (var kv in new List<KeyValuePair<string, string>>(pendingFileEdits))
            {
                try
                {
                    File.WriteAllText(kv.Key, lineEndingTracker.Apply(kv.Value, lineEndingTracker.GetFor(kv.Key)), new UTF8Encoding(false));
                    pendingFileEdits.Remove(kv.Key);
                    savedCount++;
                }
                catch (Exception ex)
                {
                    failures.Add(kv.Key + " (" + ex.Message + ")");
                }
            }

            folderTreeManager.RefreshDirtyMarkers();

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
            currentFilePath = null;
            currentFileDirectory = null;
            this.Title = Assembly.GetExecutingAssembly().GetName().Name;

            pendingFileEdits.Clear();
            currentFileIsDirty = false;

            RunAsProgrammaticChange(() =>
            {
                if (isSourceMode)
                {
                    SourceEditor.Text = "";
                }
                else
                {
                    Editor.Document.Blocks.Clear();
                    Editor.Document.Blocks.Add(new Paragraph());
                }
            });
            outlineManager.Refresh();
            folderTreeManager.RefreshDirtyMarkers();
        }

        // ======================================================================
        //  ズーム
        // ======================================================================

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(zoomLevel + 0.1);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(zoomLevel - 0.1);
        private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

        /// <summary>Ctrl+ホイールでエディタをズームする（スクロールの代わり）。</summary>
        private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                SetZoom(zoomLevel + (e.Delta > 0 ? 0.1 : -0.1));
            }
        }

        /// <summary>新しいズーム倍率を適用し、ツールバーのパーセント表示を更新する。</summary>
        /// <param name="value">新しいズーム倍率（1.0が100%）。妥当な範囲に丸められる。</param>
        private void SetZoom(double value)
        {
            zoomLevel = Math.Max(0.5, Math.Min(2.5, Math.Round(value, 2)));
            Editor.LayoutTransform = new ScaleTransform(zoomLevel, zoomLevel);
            SourceEditor.FontSize = 16 * zoomLevel;
            ZoomLabelBtn.Content = Math.Round(zoomLevel * 100) + "%";
        }

        /// <summary>ソースモード編集中、ファイルをダーティにする。</summary>
        private void SourceEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (folderTreeManager == null) return; // InitializeComponent中の発火に対するガード
            currentFileIsDirty = true;
            folderTreeManager.RefreshDirtyMarkers();
        }

        /// <summary>エディタの幅が変わったら、画像のサイズ調整をやり直す。</summary>
        private void Editor_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (imageManager == null) return; // InitializeComponent中の発火に対するガード
            if (isSourceMode) return;
            foreach (var img in imageManager.FindAllImages(Editor.Document))
            {
                imageManager.ApplyImageSizing(img);
            }
        }

        // ======================================================================
        //  ドラッグ&ドロップでの画像挿入（ImageManagerへそのまま橋渡し）
        // ======================================================================

        private void Editor_DragEnter(object sender, DragEventArgs e) => imageManager.HandleDragEnter(sender, e);
        private void Editor_DragOver(object sender, DragEventArgs e) => imageManager.HandleDragOver(sender, e);
        private void Editor_Drop(object sender, DragEventArgs e) => imageManager.HandleDrop(sender, e);

        // ======================================================================
        //  検索と置換（FindReplaceWindowを開く）
        // ======================================================================

        /// <summary>検索・置換の公開API。FindReplaceWindowから使う。</summary>
        public SearchReplaceService SearchReplace => searchReplaceService;

        /// <summary>現在開いているファイルの絶対パス（未保存なら null）。FindReplaceWindowが
        /// 「今開いているファイルから検索を始める」ために参照する。</summary>
        public string CurrentFilePath => currentFilePath;

        /// <summary>アウトラインペインの管理役。検索結果の反映などにFindReplaceWindowから使う。</summary>
        public OutlineManager OutlinePane => outlineManager;

        /// <summary>フォルダツリーペインの管理役。検索結果の反映などにFindReplaceWindowから使う。</summary>
        public FolderTreeManager FolderTreePane => folderTreeManager;

        private void FindReplaceBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenFindReplaceWindow();
        }

        /// <summary>ウィンドウ全体でCtrl+Fを検索と置換ダイアログのショートカットとして扱う。</summary>
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                OpenFindReplaceWindow();
            }
        }

        /// <summary>検索と置換ウィンドウを開く（すでに開いていれば、そちらを前面に出す）。</summary>
        private void OpenFindReplaceWindow()
        {
            if (openFindReplaceWindow != null)
            {
                openFindReplaceWindow.Activate();
                openFindReplaceWindow.Focus();
                return;
            }
            openFindReplaceWindow = new FindReplaceWindow(this) { Owner = this };
            openFindReplaceWindow.Closed += (s, e) => openFindReplaceWindow = null;
            openFindReplaceWindow.Show();
        }

        /// <summary>「バージョン情報」ボタン。アプリ名とバージョン番号を表示する。</summary>
        private void VersionInfoBtn_Click(object sender, RoutedEventArgs e)
        {
            //AboutWindowを表示する
            var aboutWindow = new AboutWindow();
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
        }

        // ======================================================================
        //  フォルダ / アウトラインペインの表示・非表示切り替え
        // ======================================================================

        /// <summary>フォルダツリーペインの表示/非表示を切り替える（幅は次に表示する時のために
        /// 記憶しておく）。</summary>
        private void ToggleFolderPaneBtn_Click(object sender, RoutedEventArgs e)
        {
            if (folderPaneVisible && FolderColumnDef.Width.Value > 0) lastFolderColumnWidth = FolderColumnDef.Width.Value;
            folderPaneVisible = !folderPaneVisible;
            ApplyFolderPaneVisibility();
        }

        /// <summary>folderPaneVisibleの現在値に従って、フォルダペインの表示状態をXAML側の
        /// コントロールへ反映する（ボタンクリック時、起動時の状態復元時の両方から呼ばれる）。</summary>
        private void ApplyFolderPaneVisibility()
        {
            if (folderPaneVisible)
            {
                FolderColumnDef.Width = new GridLength(lastFolderColumnWidth);
                FolderSplitterColumnDef.Width = new GridLength(2);
                FolderPaneBorder.Visibility = Visibility.Visible;
                FolderSplitter.Visibility = Visibility.Visible;
                ToggleFolderPaneBtn.Content = "フォルダを隠す";
            }
            else
            {
                FolderColumnDef.Width = new GridLength(0);
                FolderSplitterColumnDef.Width = new GridLength(0);
                FolderPaneBorder.Visibility = Visibility.Collapsed;
                FolderSplitter.Visibility = Visibility.Collapsed;
                ToggleFolderPaneBtn.Content = "フォルダを表示";
            }
        }

        /// <summary>アウトラインペインの表示/非表示を切り替える（幅は次に表示する時のために
        /// 記憶しておく）。</summary>
        private void ToggleOutlinePaneBtn_Click(object sender, RoutedEventArgs e)
        {
            if (outlinePaneVisible && OutlineColumnDef.Width.Value > 0) lastOutlineColumnWidth = OutlineColumnDef.Width.Value;
            outlinePaneVisible = !outlinePaneVisible;
            ApplyOutlinePaneVisibility();
        }

        /// <summary>outlinePaneVisibleの現在値に従って、アウトラインペインの表示状態をXAML側の
        /// コントロールへ反映する（ボタンクリック時、起動時の状態復元時の両方から呼ばれる）。</summary>
        private void ApplyOutlinePaneVisibility()
        {
            if (outlinePaneVisible)
            {
                OutlineColumnDef.Width = new GridLength(lastOutlineColumnWidth);
                OutlineSplitterColumnDef.Width = new GridLength(2);
                OutlinePaneBorder.Visibility = Visibility.Visible;
                OutlineSplitter.Visibility = Visibility.Visible;
                ToggleOutlinePaneBtn.Content = "アウトラインを隠す";
            }
            else
            {
                OutlineColumnDef.Width = new GridLength(0);
                OutlineSplitterColumnDef.Width = new GridLength(0);
                OutlinePaneBorder.Visibility = Visibility.Collapsed;
                OutlineSplitter.Visibility = Visibility.Collapsed;
                ToggleOutlinePaneBtn.Content = "アウトラインを表示";
            }
        }

        // ======================================================================
        //  フォルダペイン
        // ======================================================================

        /// <summary>フォルダピッカーを表示し、選択されたフォルダをフォルダツリーペインへ
        /// 読み込む（可能であれば同じ相対パスのファイルを開いたままにする）。</summary>
        private void OpenFolderTreeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (currentFileIsDirty || pendingFileEdits.Count > 0)
            {
                var confirmResult = MessageBox.Show(
                    "保存されていない変更があります。破棄して別のフォルダを開きますか？",
                    "フォルダを開く", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (confirmResult != MessageBoxResult.OK) return;
            }

            string previousRelativePath = folderTreeManager.GetCurrentFileRelativePath();

            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (dlg.ShowDialog() != true) return;

            DiscardCurrentDocumentSilently();
            folderTreeManager.LoadFolderTree(dlg.FolderName);
            folderTreeManager.OpenMatchingOrFirstFile(dlg.FolderName, previousRelativePath);
        }

        private void TreeViewItem_Expanded(object sender, RoutedEventArgs e) => folderTreeManager.HandleTreeViewItemExpanded(sender, e);

        /// <summary>
        /// フォルダツリーの選択項目が切り替わると、WPF標準の動作でその項目を横方向にも
        /// 完全に見えるようスクロールしてしまい、ファイル名が長い場合に横スクロールバーが
        /// 右へずれてしまう。これを防ぐため、内部のScrollViewerの横スクロール位置が0以外に
        /// 変化するたびに、強制的に0へ戻す。
        /// </summary>
        /// <summary>フォルダツリー内部のScrollViewerへの参照（選択項目切り替え後の横スクロール
        /// リセットに使う）。</summary>
        private ScrollViewer folderTreeScrollViewer;

        private void FolderTree_Loaded(object sender, RoutedEventArgs e)
        {
            folderTreeScrollViewer = FindVisualChild<ScrollViewer>(FolderTree);
        }

        private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            folderTreeManager.HandleSelectedItemChanged(sender, e);

            // 選択項目が切り替わると、WPF標準の動作でその項目を横方向にも完全に見えるよう
            // スクロールしてしまい、ファイル名が長い場合に横スクロールバーが右へずれてしまう。
            // このレイアウトパスが終わった直後（Loaded優先度）に横スクロールだけを0へ戻すことで、
            // それ以外のタイミングでのユーザーによる手動スクロールには一切影響しないようにする。
            if (folderTreeScrollViewer != null)
            {
                Dispatcher.BeginInvoke(new Action(() => folderTreeScrollViewer.ScrollToHorizontalOffset(0)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        // ======================================================================
        //  アウトラインペイン
        // ======================================================================

        private void OutlineList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            outlineManager.HandleSelectionChanged(sender, e);
    }
}
