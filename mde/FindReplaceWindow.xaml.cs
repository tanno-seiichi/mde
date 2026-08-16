// FindReplaceWindow.xaml.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 検索と置換ウィンドウ。対象範囲（現在のファイル／読み込んでいるフォルダ全体）と、
// 動作モード（一度にすべて検索・置換する／1件ずつ確認しながら進めるセッション）の
// 2軸に対応する。実際の検索・置換処理はすべて owner.SearchReplace（SearchReplaceService）
// に委譲しており、このウィンドウ自身はUIの状態と、フォルダ範囲での1件ずつセッションの
// ファイル/一致箇所間の移動だけを管理する。

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Documents;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace mde
{
    /// <summary>
    /// 検索と置換ダイアログ。現在ファイル範囲の検索処理そのものはこのクラスは持たず
    /// （MainWindow側にあり、エディタ上でライブにハイライトする）、フォルダ範囲の場合のみ、
    /// このウィンドウが1件ずつセッションの「今どのファイルの何件目か」を管理する。
    /// </summary>
    public partial class FindReplaceWindow : Window
    {
        /// <summary>検索・置換の対象となるメインウィンドウ。</summary>
        private readonly MainWindow owner;

        /// <summary>現在表示中の「すべて検索/すべて置換」結果一覧の背後にあるファイルパス。</summary>
        private List<string> resultsPaths = new List<string>();

        /// <summary>「すべて検索」（現在のファイル範囲）で見つかった、ライブなTextRangeの一覧。
        /// ResultsListの各項目と対応しており、ダブルクリックでその位置へジャンプするために使う。
        /// フォルダ範囲の結果を表示している間はnull。</summary>
        private List<TextRange> currentFileMatchRanges;

        /// <summary>直前に「すべて検索」を実行した際の検索語。次を検索/前を検索が同じ検索語で
        /// 呼ばれた場合、結果一覧を消さずに残しておくために使う。</summary>
        private string lastFindAllTerm;

        // ---- 1件ずつ確認するセッションの状態（フォルダ範囲の場合のみ使用。現在ファイル範囲は
        // エディタ自身の選択状態を直接使って進める。owner.SearchReplace.StepFindNext等を参照） ----

        /// <summary>1件ずつ確認するセッションが進行中かどうか。</summary>
        private bool sessionActive = false;

        /// <summary>セッションの対象がフォルダ全体（true）か現在のファイルのみ（false）か。</summary>
        private bool sessionFolderScope;

        /// <summary>フォルダ範囲セッションで巡回するすべてのファイル。</summary>
        private List<string> sessionFiles;

        /// <summary>sessionFiles中、現在検索対象になっているファイルのインデックス。</summary>
        private int sessionFileIndex;

        /// <summary>sessionFileIndexのファイルの現在の内容（一致箇所が置換されるたびその場で
        /// 書き換えられ、その後owner.SearchReplace.SetFileContentForReplace経由で反映される）。</summary>
        private string sessionContent;

        /// <summary>sessionContentに、まだownerへ反映していない変更があるかどうか。</summary>
        private bool sessionFileChanged;

        /// <summary>sessionContent内で、検索を再開する位置。</summary>
        private int sessionSearchPos;

        /// <summary>現在ユーザーに提示中で、置換/スキップの判断待ちの一致箇所。</summary>
        private (int index, int length)? sessionPendingMatch;

        /// <summary>現在のセッションでこれまでに置換した件数の合計（結果サマリー表示用）。</summary>
        private int sessionTotalReplaced;

        /// <summary>フォルダ範囲セッションで、これまでに変更されたファイルの数。</summary>
        private int sessionFilesChanged;

        /// <summary>ダイアログを作成し、検索ボックスへフォーカスする。</summary>
        /// <param name="owner">検索・置換対象のメインウィンドウ。</param>
        public FindReplaceWindow(MainWindow owner)
        {
            InitializeComponent();
            this.owner = owner;
            SearchBox.Focus();
            Closed += (s, e) =>
            {
                owner.SearchReplace.ClearHighlight();
                owner.OutlinePane.ClearSearchMatches();
                owner.FolderTreePane.ClearSearchMatches();
            };
        }

        private string Term => SearchBox.Text;
        private string Replacement => ReplaceBox.Text ?? "";
        private bool CaseSensitive => CaseSensitiveBox.IsChecked == true;
        private bool UseRegex => UseRegexBox.IsChecked == true;

        // ---------------- 一括検索・一括置換 ----------------

        /// <summary>「次を検索」ボタン。現在ファイル範囲なら次の一致箇所を探して強調表示し、
        /// フォルダ範囲なら一致箇所を含むすべてのファイルを一覧表示する。</summary>
        /// <param name="sender">ボタン。</param>
        /// <param name="e">クリックイベント。</param>
        // ---- フォルダ全体スコープでの「次を検索」「前を検索」共通の位置情報。
        // 「今どのファイルの何件目の一致箇所を見ているか」という1つの状態を、次を検索・前を検索の
        // 両方で共有して使う（別々の状態にしていると、方向を切り替えた時に「今どこにいるか」が
        // 引き継がれず、そのファイルの先頭または末尾からやり直してしまう不具合があったため）。 ----
        private List<string> folderFindFileList;
        private int folderFindCurrentFileIdx = -1;
        private List<TextRange> folderFindCurrentFileMatches;
        private int folderFindCurrentMatchIdx = -1;

        private void FindNext_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Term)) return;
            if (Term != lastFindAllTerm)
            {
                currentFileMatchRanges = null;
                ResultsList.Visibility = Visibility.Collapsed;
            }

            if (ScopeCurrentFile.IsChecked == true)
            {
                bool found = owner.SearchReplace.FindNextInCurrentFile(Term, CaseSensitive, UseRegex);
                StatusText.Text = found ? "見つかりました（エディタ内で強調表示しています）。" : "見つかりませんでした。";
            }
            else
            {
                FindNextInFolder();
            }
        }

        private bool PathsEqual(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// フォルダ全体スコープでの位置情報を、まだ無ければ初期化する。現在開いているファイルが
        /// このフォルダ内にあれば、そのファイルを先頭に来るよう並び順を回転させ、位置情報の
        /// 「今のファイル」として扱う（ただし一致箇所の一覧はまだ設定しない。呼び出し側が
        /// 必要に応じてSeedFolderWalkOrHighlightCurrentFileで補う）。
        /// </summary>
        private void EnsureFolderWalkInitialized()
        {
            if (folderFindFileList != null) return;

            folderFindFileList = owner.SearchReplace.GetFolderFiles();
            folderFindCurrentFileIdx = -1;
            folderFindCurrentFileMatches = null;
            folderFindCurrentMatchIdx = -1;

            string currentPath = owner.CurrentFilePath;
            if (!string.IsNullOrEmpty(currentPath))
            {
                int idx = folderFindFileList.FindIndex(f => PathsEqual(f, currentPath));
                if (idx >= 0)
                {
                    if (idx > 0)
                        folderFindFileList = folderFindFileList.Skip(idx).Concat(folderFindFileList.Take(idx)).ToList();
                    folderFindCurrentFileIdx = 0;
                }
            }
        }

        /// <summary>
        /// 「すべて検索」の結果一覧からファイルをダブルクリックで開いた直後など、すでに
        /// ある特定のファイル・一致箇所を表示している状態から、次を検索/前を検索がそこから
        /// 正しく続けられるよう位置情報を作り直す（合わせておかないと、直後の次を検索が
        /// 「まだこのファイルの1件目を見ていない」と誤解して同じ箇所を再度表示してしまう）。
        /// </summary>
        /// <param name="path">今表示しているファイルの絶対パス。</param>
        /// <param name="matches">そのファイル内のすべての一致箇所。</param>
        /// <param name="shownMatchIndex">その中で、今まさに表示している一致箇所のインデックス。</param>
        private void SeedFolderWalkFromCurrentFile(string path, List<TextRange> matches, int shownMatchIndex)
        {
            folderFindFileList = owner.SearchReplace.GetFolderFiles();
            int idx = folderFindFileList.FindIndex(f => PathsEqual(f, path));
            if (idx > 0)
                folderFindFileList = folderFindFileList.Skip(idx).Concat(folderFindFileList.Take(idx)).ToList();
            folderFindCurrentFileIdx = idx >= 0 ? 0 : -1;
            folderFindCurrentFileMatches = matches;
            folderFindCurrentMatchIdx = shownMatchIndex;
        }

        /// <summary>
        /// フォルダ全体スコープでの「次を検索」。今のファイルの一致箇所一覧の中でまだ次に
        /// 進めるならそちらへ、進めなければ次のファイルへ切り替えて、そのファイルのすべての
        /// 一致箇所を改めて強調表示してから先頭の一致箇所へ移動する。
        /// </summary>
        private void FindNextInFolder()
        {
            EnsureFolderWalkInitialized();
            if (folderFindFileList.Count == 0) { StatusText.Text = "見つかりませんでした。"; return; }

            // 今のファイルの一致箇所一覧の中で、まだ次に進める一致箇所があるか。
            if (folderFindCurrentFileMatches != null && folderFindCurrentMatchIdx + 1 < folderFindCurrentFileMatches.Count)
            {
                folderFindCurrentMatchIdx++;
                owner.SearchReplace.SelectAndScrollTo(folderFindCurrentFileMatches[folderFindCurrentMatchIdx]);
                StatusText.Text = "見つかりました：" + System.IO.Path.GetFileName(folderFindFileList[folderFindCurrentFileIdx]) +
                    "（" + (folderFindCurrentMatchIdx + 1) + " / " + folderFindCurrentFileMatches.Count + "件目）";
                return;
            }

            // 今のファイルでは尽きたので、次に一致箇所があるファイルへ進む（末尾まで来たら
            // 先頭へ折り返し、フォルダ内を何周でも回り続ける）。
            int filesChecked = 0;
            while (filesChecked <= folderFindFileList.Count)
            {
                folderFindCurrentFileIdx++;
                if (folderFindCurrentFileIdx >= folderFindFileList.Count) folderFindCurrentFileIdx = 0;
                filesChecked++;

                string path = folderFindFileList[folderFindCurrentFileIdx];
                if (!PathsEqual(path, owner.CurrentFilePath))
                    owner.SearchReplace.OpenFileForFindReplace(path);
                SyncResultsListSelection(path);

                folderFindCurrentFileMatches = owner.SearchReplace.HighlightAllMatchesInCurrentFile(Term, CaseSensitive, UseRegex);
                if (folderFindCurrentFileMatches.Count > 0)
                {
                    folderFindCurrentMatchIdx = 0;
                    owner.SearchReplace.SelectAndScrollTo(folderFindCurrentFileMatches[0]);
                    StatusText.Text = "見つかりました：" + System.IO.Path.GetFileName(path) +
                        "（1 / " + folderFindCurrentFileMatches.Count + "件目）";
                    return;
                }
            }
            // フォルダを一周しても一致箇所が1つも見つからなかった場合のみ、ここに到達する。
            StatusText.Text = "見つかりませんでした。";
        }

        /// <summary>「すべて検索」（フォルダ全体）の結果一覧の選択項目を、今表示しているファイルに
        /// 合わせて切り替える。一覧にそのファイルが無ければ何もしない。</summary>
        /// <param name="path">今表示しているファイルの絶対パス。</param>
        private void SyncResultsListSelection(string path)
        {
            if (resultsPaths == null || resultsPaths.Count == 0) return;
            int idx = resultsPaths.FindIndex(p => PathsEqual(p, path));
            if (idx >= 0 && idx < ResultsList.Items.Count)
                ResultsList.SelectedIndex = idx;
        }

        /// <summary>「前を検索」ボタン。</summary>
        /// <param name="sender">ボタン。</param>
        /// <param name="e">クリックイベント。</param>
        private void FindPrevious_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Term)) return;
            if (Term != lastFindAllTerm)
            {
                currentFileMatchRanges = null;
                ResultsList.Visibility = Visibility.Collapsed;
            }

            if (ScopeCurrentFile.IsChecked == true)
            {
                bool found = owner.SearchReplace.FindPreviousInCurrentFile(Term, CaseSensitive, UseRegex);
                StatusText.Text = found ? "見つかりました（エディタ内で強調表示しています）。" : "見つかりませんでした。";
            }
            else
            {
                FindPreviousInFolder();
            }
        }

        /// <summary>
        /// フォルダ全体スコープでの「前を検索」。FindNextInFolderと同じ位置情報を共有しており、
        /// 今のファイルの一致箇所一覧の中でまだ前に戻れるならそちらへ、戻れなければ前のファイルへ
        /// 切り替えて、そのファイルのすべての一致箇所を改めて強調表示してから末尾の一致箇所へ
        /// 移動する。
        /// </summary>
        private void FindPreviousInFolder()
        {
            EnsureFolderWalkInitialized();
            if (folderFindFileList.Count == 0) { StatusText.Text = "見つかりませんでした。"; return; }

            if (folderFindCurrentFileMatches != null && folderFindCurrentMatchIdx - 1 >= 0)
            {
                folderFindCurrentMatchIdx--;
                owner.SearchReplace.SelectAndScrollTo(folderFindCurrentFileMatches[folderFindCurrentMatchIdx]);
                StatusText.Text = "見つかりました：" + System.IO.Path.GetFileName(folderFindFileList[folderFindCurrentFileIdx]) +
                    "（" + (folderFindCurrentMatchIdx + 1) + " / " + folderFindCurrentFileMatches.Count + "件目）";
                return;
            }

            int filesChecked = 0;
            while (filesChecked <= folderFindFileList.Count)
            {
                folderFindCurrentFileIdx--;
                if (folderFindCurrentFileIdx < 0) folderFindCurrentFileIdx = folderFindFileList.Count - 1;
                filesChecked++;

                string path = folderFindFileList[folderFindCurrentFileIdx];
                if (!PathsEqual(path, owner.CurrentFilePath))
                    owner.SearchReplace.OpenFileForFindReplace(path);
                SyncResultsListSelection(path);

                folderFindCurrentFileMatches = owner.SearchReplace.HighlightAllMatchesInCurrentFile(Term, CaseSensitive, UseRegex);
                if (folderFindCurrentFileMatches.Count > 0)
                {
                    folderFindCurrentMatchIdx = folderFindCurrentFileMatches.Count - 1;
                    owner.SearchReplace.SelectAndScrollTo(folderFindCurrentFileMatches[folderFindCurrentMatchIdx]);
                    StatusText.Text = "見つかりました：" + System.IO.Path.GetFileName(path) +
                        "（" + folderFindCurrentFileMatches.Count + " / " + folderFindCurrentFileMatches.Count + "件目）";
                    return;
                }
            }
            StatusText.Text = "見つかりませんでした。";
        }

        /// <summary>「すべて検索」ボタン：選択されている範囲内の一致箇所をすべて一覧表示する
        /// （現在のファイル範囲では、各一致箇所を前後の文脈付きで一覧表示する）。</summary>
        /// <param name="sender">ボタン。</param>
        /// <param name="e">クリックイベント。</param>
        private void FindAll_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Term)) return;
            folderFindFileList = null; // 次を検索/前を検索の位置情報が古いまま残らないようリセット
            lastFindAllTerm = Term;

            if (ScopeCurrentFile.IsChecked == true)
            {
                var matches = owner.SearchReplace.HighlightAllMatchesInCurrentFile(Term, CaseSensitive, UseRegex);
                currentFileMatchRanges = matches;
                owner.FolderTreePane.ClearSearchMatches();

                var snippets = new List<string>();
                foreach (var range in matches)
                {
                    string context = "";
                    try
                    {
                        var para = range.Start.Paragraph;
                        if (para != null) context = new TextRange(para.ContentStart, para.ContentEnd).Text.Trim();
                    }
                    catch { /* 取得できなければ空のまま表示する */ }
                    if (context.Length > 70) context = context.Substring(0, 70) + "…";
                    snippets.Add(context.Length > 0 ? context : range.Text);
                }

                resultsPaths = new List<string>(); // ファイル単位の結果ではないので、こちらは使わない
                ResultsList.ItemsSource = snippets;
                ResultsList.Visibility = snippets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                StatusText.Text = snippets.Count > 0
                    ? snippets.Count + " 件見つかりました（すべてエディタ内・アウトラインで強調表示しています。ダブルクリックでその箇所へ移動します）。"
                    : "見つかりませんでした。";
            }
            else
            {
                currentFileMatchRanges = null;
                owner.OutlinePane.ClearSearchMatches();
                var results = owner.SearchReplace.FindAllInFolder(Term, CaseSensitive, UseRegex);
                resultsPaths = results.Select(r => r.Item1).ToList();
                owner.FolderTreePane.MarkSearchMatches(resultsPaths);
                ResultsList.ItemsSource = results.Select(r => r.Item1 + "　（" + r.Item2 + "件）").ToList();
                ResultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                StatusText.Text = results.Count > 0
                    ? results.Count + " 個のファイルで見つかりました（フォルダビューでも強調表示しています）。ダブルクリックで開きます。"
                    : "見つかりませんでした。";
            }
        }

        /// <summary>フォルダ範囲の検索結果一覧でダブルクリックされたファイルを開き、
        /// その最初の一致箇所へジャンプする。</summary>
        /// <param name="sender">結果一覧。</param>
        /// <param name="e">マウスイベント。</param>
        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            int idx = ResultsList.SelectedIndex;
            if (idx < 0) return;

            if (currentFileMatchRanges != null)
            {
                if (idx < currentFileMatchRanges.Count)
                    owner.SearchReplace.SelectAndScrollTo(currentFileMatchRanges[idx]);
                return;
            }

            if (idx >= resultsPaths.Count) return;
            owner.SearchReplace.OpenFileForFindReplace(resultsPaths[idx]);
            var matches = owner.SearchReplace.HighlightAllMatchesInCurrentFile(Term, CaseSensitive, UseRegex);
            if (matches.Count > 0)
            {
                owner.SearchReplace.SelectAndScrollTo(matches[0]);
                SeedFolderWalkFromCurrentFile(resultsPaths[idx], matches, 0);
            }
        }

        /// <summary>「すべて置換」ボタン。選択されている範囲のすべての一致箇所を一度に置換する。</summary>
        /// <param name="sender">ボタン。</param>
        /// <param name="e">クリックイベント。</param>
        private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Term)) return;
            folderFindFileList = null;

            if (ScopeCurrentFile.IsChecked == true)
            {
                int count = owner.SearchReplace.ReplaceAllInCurrentFile(Term, Replacement, CaseSensitive, UseRegex);
                StatusText.Text = count + " 件を置換しました（保存するまでファイルには反映されません）。";
                ResultsList.Visibility = Visibility.Collapsed;
            }
            else
            {
                var results = owner.SearchReplace.ReplaceAllInFolder(Term, Replacement, CaseSensitive, UseRegex);
                int totalCount = results.Sum(r => r.Item2);
                StatusText.Text = results.Count + " 個のファイルで合計 " + totalCount +
                    " 件を置換しました（保存するまでファイルには書き出されません。ツールバーの「すべて保存」で反映してください）。";
                resultsPaths = results.Select(r => r.Item1).ToList();
                ResultsList.ItemsSource = results.Select(r => r.Item1 + "　（" + r.Item2 + "件置換）").ToList();
                ResultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ---------------- 1件ずつ確認するセッション ----------------

        /// <summary>「1件ずつ置換」ボタン。選択されている範囲を対象に、1件ずつ確認しながら
        /// 進めるセッションを開始する。</summary>
        /// <param name="sender">ボタン。</param>
        /// <param name="e">クリックイベント。</param>
        private void StepReplace_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Term)) return;
            folderFindFileList = null;

            sessionActive = true;
            sessionFolderScope = ScopeFolder.IsChecked == true;
            sessionTotalReplaced = 0;
            sessionFilesChanged = 0;
            ResultsList.Visibility = Visibility.Collapsed;

            if (sessionFolderScope)
            {
                sessionFiles = owner.SearchReplace.GetFolderFiles();
                sessionFileIndex = -1;
                AdvanceToNextSessionFile();
                AdvanceToNextSessionMatch();
            }
            else
            {
                bool found = owner.SearchReplace.StepFindNext(Term, CaseSensitive, UseRegex, fromSelectionEnd: false);
                if (found) ShowLiveStepPanel();
                else EndSession();
            }
        }

        /// <summary>現在ファイル範囲でのセッションパネル表示。一致箇所はテキスト断片ではなく、
        /// エディタ内で直接強調表示される。</summary>
        private void ShowLiveStepPanel()
        {
            StepFileText.Text = "現在のファイル";
            StepContextText.Text = "エディタ内で強調表示された箇所をご確認ください。";
            StepPanel.Visibility = Visibility.Visible;
            StatusText.Text = "";
        }

        /// <summary>現在のセッションファイルへの未反映の変更があれば書き込み、sessionFiles内の
        /// 次のファイルへ進む（一覧を使い切ったらsessionContentをnullにする）。</summary>
        private void AdvanceToNextSessionFile()
        {
            CommitSessionFileIfChanged();

            sessionFileIndex++;
            if (sessionFiles == null || sessionFileIndex >= sessionFiles.Count)
            {
                sessionContent = null;
                return;
            }

            sessionContent = owner.SearchReplace.GetFileContentForReplace(sessionFiles[sessionFileIndex]);
            sessionSearchPos = 0;
            sessionFileChanged = false;
        }

        /// <summary>現在のセッションファイルに未反映の変更があれば、owner（ライブエディタまたは
        /// 保留中の編集）へ書き戻す。</summary>
        private void CommitSessionFileIfChanged()
        {
            if (!sessionFileChanged || sessionContent == null) return;
            if (sessionFiles == null || sessionFileIndex < 0 || sessionFileIndex >= sessionFiles.Count) return;

            owner.SearchReplace.SetFileContentForReplace(sessionFiles[sessionFileIndex], sessionContent);
            sessionFilesChanged++;
            sessionFileChanged = false;
        }

        /// <summary>現在のセッションファイル内のsessionSearchPos以降で次の一致箇所を探す。
        /// 現在のファイルに一致箇所が残っていなければ、後続のファイルへ順に進む。
        /// どのファイルにも一致箇所が残っていなければセッションを終了する。</summary>
        private void AdvanceToNextSessionMatch()
        {
            while (sessionContent != null)
            {
                var match = owner.SearchReplace.FindNextMatchInText(sessionContent, Term, CaseSensitive, UseRegex, sessionSearchPos);
                if (match != null)
                {
                    sessionPendingMatch = match;
                    ShowSessionMatch();
                    return;
                }
                AdvanceToNextSessionFile();
            }
            EndSession();
        }

        /// <summary>フォルダ範囲セッションの現在の一致箇所を、前後約30文字分の文脈付きの
        /// テキスト断片として表示する（そのファイルはエディタで開いていないため、直接
        /// 強調表示することはできない）。</summary>
        private void ShowSessionMatch()
        {
            var (idx, len) = sessionPendingMatch.Value;
            int ctxStart = Math.Max(0, idx - 30);
            int ctxEnd = Math.Min(sessionContent.Length, idx + len + 30);
            string before = sessionContent.Substring(ctxStart, idx - ctxStart).Replace("\r", "").Replace("\n", "⏎");
            string matched = sessionContent.Substring(idx, len).Replace("\r", "").Replace("\n", "⏎");
            string after = sessionContent.Substring(idx + len, ctxEnd - (idx + len)).Replace("\r", "").Replace("\n", "⏎");

            StepFileText.Text = Path.GetFileName(sessionFiles[sessionFileIndex]);
            StepContextText.Text = "…" + before + "【" + matched + "】" + after + "…";
            StepPanel.Visibility = Visibility.Visible;
            StatusText.Text = "";
        }

        /// <summary>セッションパネルの「置換」ボタン。現在の一致箇所を置換し、次の一致箇所へ進む。</summary>
        /// <param name="sender">ボタン。</param>
        /// <param name="e">クリックイベント。</param>
        private void StepReplaceOne_Click(object sender, RoutedEventArgs e)
        {
            if (!sessionActive) return;

            if (!sessionFolderScope)
            {
                sessionTotalReplaced++;
                bool found = owner.SearchReplace.StepReplaceAndFindNext(Term, Replacement, CaseSensitive, UseRegex);
                if (found) ShowLiveStepPanel();
                else EndSession();
                return;
            }

            if (sessionPendingMatch == null) return;
            var (idx, len) = sessionPendingMatch.Value;
            sessionContent = owner.SearchReplace.ReplaceOneMatch(sessionContent, Term, Replacement, CaseSensitive, UseRegex, idx, len);
            sessionFileChanged = true;
            sessionTotalReplaced++;
            sessionSearchPos = idx + Replacement.Length;
            AdvanceToNextSessionMatch();
        }

        /// <summary>セッションパネルの「スキップ」ボタン。現在の一致箇所には触れず、
        /// 次の一致箇所へ進む。</summary>
        /// <param name="sender">ボタン。</param>
        /// <param name="e">クリックイベント。</param>
        private void StepSkip_Click(object sender, RoutedEventArgs e)
        {
            if (!sessionActive) return;

            if (!sessionFolderScope)
            {
                bool found = owner.SearchReplace.StepSkipAndFindNext(Term, CaseSensitive, UseRegex);
                if (found) ShowLiveStepPanel();
                else EndSession();
                return;
            }

            if (sessionPendingMatch == null) return;
            var (idx, len) = sessionPendingMatch.Value;
            sessionSearchPos = idx + len;
            AdvanceToNextSessionMatch();
        }

        /// <summary>セッションパネルの「残りをすべて置換」ボタン。現在の一致箇所と、
        /// 残りすべての一致箇所を確認なしで置換し、セッションを終了する。</summary>
        /// <param name="sender">ボタン。</param>
        /// <param name="e">クリックイベント。</param>
        private void StepReplaceRemaining_Click(object sender, RoutedEventArgs e)
        {
            if (!sessionActive) return;

            if (!sessionFolderScope)
            {
                sessionTotalReplaced += owner.SearchReplace.StepReplaceAllRemaining(Term, Replacement, CaseSensitive, UseRegex);
                EndSession();
                return;
            }

            while (sessionContent != null)
            {
                var match = owner.SearchReplace.FindNextMatchInText(sessionContent, Term, CaseSensitive, UseRegex, sessionSearchPos);
                if (match == null)
                {
                    AdvanceToNextSessionFile();
                    continue;
                }
                var (idx, len) = match.Value;
                sessionContent = owner.SearchReplace.ReplaceOneMatch(sessionContent, Term, Replacement, CaseSensitive, UseRegex, idx, len);
                sessionFileChanged = true;
                sessionTotalReplaced++;
                sessionSearchPos = idx + Replacement.Length;
            }
            EndSession();
        }

        /// <summary>セッションパネルの「終了」ボタン。セッションを途中で終了する。</summary>
        /// <param name="sender">ボタン。</param>
        /// <param name="e">クリックイベント。</param>
        private void StepEnd_Click(object sender, RoutedEventArgs e)
        {
            EndSession();
        }

        /// <summary>未反映のファイル変更を書き戻し、セッションパネルを閉じ、置換件数の
        /// サマリーを表示する。</summary>
        private void EndSession()
        {
            if (sessionFolderScope) CommitSessionFileIfChanged();
            sessionActive = false;
            sessionPendingMatch = null;
            StepPanel.Visibility = Visibility.Collapsed;

            StatusText.Text = sessionTotalReplaced + " 件を置換しました" +
                (sessionFolderScope
                    ? "（" + sessionFilesChanged + " 個のファイル、保存するまでファイルには反映されません）。"
                    : "（現在のファイルに直接反映されました。保存するまでファイルには書き出されません）。");
        }

        /// <summary>
        /// 「置換後の文字列」エキスパンダーの開閉に合わせて、置換系のボタン（1件ずつ置換…・
        /// すべて置換）の表示/非表示を切り替える。初期状態（閉じている）では検索系の
        /// ボタンだけを表示し、開くと置換操作もできるようになる。
        /// </summary>
        private void ReplaceExpander_Toggled(object sender, RoutedEventArgs e)
        {
            var visibility = ReplaceExpander.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            StepReplaceButton.Visibility = visibility;
            ReplaceAllButton.Visibility = visibility;
        }

        /// <summary>
        /// 検索・置換ウィンドウのキーボードショートカットの実装。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FindReplaceWindow_PreviewKeyDown( object sender, KeyEventArgs e )
        {
            // Escキーでウィンドウを閉じる
            if( e.Key == Key.Escape )
            {
                this.Close();
                e.Handled = true;
            }
        }
    }
}
