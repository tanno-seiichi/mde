// FindReplaceWindow.xaml.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 検索と置換ウィンドウ。対象範囲（現在のファイル／読み込んでいるフォルダ全体）と、
// 動作モード（一度にすべて検索・置換する／1件ずつ確認しながら進めるセッション）の
// 2軸に対応する。実際の検索・置換処理はすべて m_owner.SearchReplace（SearchReplaceService）
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
        private readonly MainWindow m_owner;

        /// <summary>現在表示中の「すべて検索/すべて置換」結果一覧の背後にあるファイルパス。</summary>
        private List<string> m_resultsPaths = new List<string>();

        /// <summary>「すべて検索」（現在のファイル範囲）で見つかった、ライブなTextRangeの一覧。
        /// ResultsListの各項目と対応しており、ダブルクリックでその位置へジャンプするために使う。
        /// フォルダ範囲の結果を表示している間はnull。</summary>
        private List<TextRange> m_currentFileMatchRanges;

        /// <summary>直前に「すべて検索」を実行した際の検索語。次を検索/前を検索が同じ検索語で
        /// 呼ばれた場合、結果一覧を消さずに残しておくために使う。</summary>
        private string m_lastFindAllTerm;

        // ---- 1件ずつ確認するセッションの状態（フォルダ範囲の場合のみ使用。現在ファイル範囲は
        // エディタ自身の選択状態を直接使って進める。m_owner.SearchReplace.StepFindNext等を参照） ----

        /// <summary>1件ずつ確認するセッションが進行中かどうか。</summary>
        private bool m_sessionActiveFlg = false;

        /// <summary>セッションの対象がフォルダ全体（true）か現在のファイルのみ（false）か。</summary>
        private bool m_sessionFolderScopeFlg;

        /// <summary>フォルダ範囲セッションで巡回するすべてのファイル。</summary>
        private List<string> m_sessionFiles;

        /// <summary>sessionFiles中、現在検索対象になっているファイルのインデックス。</summary>
        private int m_sessionFileIndex;

        /// <summary>sessionFileIndexのファイルの現在の内容（一致箇所が置換されるたびその場で
        /// 書き換えられ、その後owner.SearchReplace.SetFileContentForReplace経由で反映される）。</summary>
        private string m_sessionContent;

        /// <summary>sessionContentに、まだownerへ反映していない変更があるかどうか。</summary>
        private bool m_sessionFileChangedFlg;

        /// <summary>sessionContent内で、検索を再開する位置。</summary>
        private int m_sessionSearchPos;

        /// <summary>現在ユーザーに提示中で、置換/スキップの判断待ちの一致箇所。</summary>
        private (int index, int length)? m_sessionPendingMatch;

        /// <summary>現在のセッションでこれまでに置換した件数の合計（結果サマリー表示用）。</summary>
        private int m_sessionTotalReplaced;

        /// <summary>フォルダ範囲セッションで、これまでに変更されたファイルの数。</summary>
        private int m_sessionFilesChanged;

        /// <summary>ダイアログを作成し、検索ボックスへフォーカスする。</summary>
        /// <param name="a_owner">検索・置換対象のメインウィンドウ。</param>
        public FindReplaceWindow(MainWindow a_owner)
        {
            InitializeComponent();
            this.m_owner = a_owner;
            m_searchBox.Focus();
            Closed += (s, e) =>
            {
                a_owner.SearchReplace.ClearHighlight();
                a_owner.OutlinePane.ClearSearchMatches();
                a_owner.FolderTreePane.ClearSearchMatches();
            };
        }

        private string Term => m_searchBox.Text;
        private string Replacement => m_replaceBox.Text ?? "";
        private bool CaseSensitive => true == m_caseSensitiveBox.IsChecked;
        private bool UseRegex => true == m_useRegexBox.IsChecked;

        // ---------------- 一括検索・一括置換 ----------------

        /// <summary>「次を検索」ボタン。現在ファイル範囲なら次の一致箇所を探して強調表示し、
        /// フォルダ範囲なら一致箇所を含むすべてのファイルを一覧表示する。</summary>
        /// <param name="sender">ボタン。</param>
        /// <param name="e">クリックイベント。</param>
        // ---- フォルダ全体スコープでの「次を検索」「前を検索」共通の位置情報。
        // 「今どのファイルの何件目の一致箇所を見ているか」という1つの状態を、次を検索・前を検索の
        // 両方で共有して使う（別々の状態にしていると、方向を切り替えた時に「今どこにいるか」が
        // 引き継がれず、そのファイルの先頭または末尾からやり直してしまう不具合があったため）。 ----
        private List<string> m_folderFindFileList;
        private int m_folderFindCurrentFileIdx = -1;
        private List<TextRange> m_folderFindCurrentFileMatches;
        private int m_folderFindCurrentMatchIdx = -1;

        private void FindNextClick(object a_sender, RoutedEventArgs a_args)
        {
            if (string.IsNullOrEmpty(Term)) return;
            if (Term != m_lastFindAllTerm)
            {
                m_currentFileMatchRanges = null;
                m_resultsList.Visibility = Visibility.Collapsed;
            }

            if (true == m_scopeCurrentFile.IsChecked)
            {
                bool foundFlg = m_owner.SearchReplace.FindNextInCurrentFile(Term, CaseSensitive, UseRegex);
                m_statusText.Text = foundFlg ? "見つかりました（エディタ内で強調表示しています）。" : "見つかりませんでした。";
            }
            else
            {
                FindNextInFolder();
            }
        }

        private bool PathsEqual(string a_a, string a_b) =>
            !string.IsNullOrEmpty(a_a) && !string.IsNullOrEmpty(a_b) &&
            string.Equals(System.IO.Path.GetFullPath(a_a), System.IO.Path.GetFullPath(a_b), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// フォルダ全体スコープでの位置情報を、まだ無ければ初期化する。現在開いているファイルが
        /// このフォルダ内にあれば、そのファイルを先頭に来るよう並び順を回転させ、位置情報の
        /// 「今のファイル」として扱う（ただし一致箇所の一覧はまだ設定しない。呼び出し側が
        /// 必要に応じてSeedFolderWalkOrHighlightCurrentFileで補う）。
        /// </summary>
        private void EnsureFolderWalkInitialized()
        {
            if (null != m_folderFindFileList) return;

            m_folderFindFileList = m_owner.SearchReplace.GetFolderFiles();
            m_folderFindCurrentFileIdx = -1;
            m_folderFindCurrentFileMatches = null;
            m_folderFindCurrentMatchIdx = -1;

            string currentPath = m_owner.CurrentFilePath;
            if (!string.IsNullOrEmpty(currentPath))
            {
                int idx = m_folderFindFileList.FindIndex(f => PathsEqual(f, currentPath));
                if (idx >= 0)
                {
                    if (idx > 0)
                        m_folderFindFileList = m_folderFindFileList.Skip(idx).Concat(m_folderFindFileList.Take(idx)).ToList();
                    m_folderFindCurrentFileIdx = 0;
                }
            }
        }

        /// <summary>
        /// 「すべて検索」の結果一覧からファイルをダブルクリックで開いた直後など、すでに
        /// ある特定のファイル・一致箇所を表示している状態から、次を検索/前を検索がそこから
        /// 正しく続けられるよう位置情報を作り直す（合わせておかないと、直後の次を検索が
        /// 「まだこのファイルの1件目を見ていない」と誤解して同じ箇所を再度表示してしまう）。
        /// </summary>
        /// <param name="a_path">今表示しているファイルの絶対パス。</param>
        /// <param name="a_matches">そのファイル内のすべての一致箇所。</param>
        /// <param name="a_shownMatchIndex">その中で、今まさに表示している一致箇所のインデックス。</param>
        private void SeedFolderWalkFromCurrentFile(string a_path, List<TextRange> a_matches, int a_shownMatchIndex)
        {
            m_folderFindFileList = m_owner.SearchReplace.GetFolderFiles();
            int idx = m_folderFindFileList.FindIndex(f => PathsEqual(f, a_path));
            if (idx > 0)
                m_folderFindFileList = m_folderFindFileList.Skip(idx).Concat(m_folderFindFileList.Take(idx)).ToList();
            m_folderFindCurrentFileIdx = idx >= 0 ? 0 : -1;
            m_folderFindCurrentFileMatches = a_matches;
            m_folderFindCurrentMatchIdx = a_shownMatchIndex;
        }

        /// <summary>
        /// フォルダ全体スコープでの「次を検索」。今のファイルの一致箇所一覧の中でまだ次に
        /// 進めるならそちらへ、進めなければ次のファイルへ切り替えて、そのファイルのすべての
        /// 一致箇所を改めて強調表示してから先頭の一致箇所へ移動する。
        /// </summary>
        private void FindNextInFolder()
        {
            EnsureFolderWalkInitialized();
            if (0 == m_folderFindFileList.Count) { m_statusText.Text = "見つかりませんでした。"; return; }

            // 今のファイルの一致箇所一覧の中で、まだ次に進める一致箇所があるか。
            if (null != m_folderFindCurrentFileMatches && m_folderFindCurrentMatchIdx + 1 < m_folderFindCurrentFileMatches.Count)
            {
                m_folderFindCurrentMatchIdx++;
                m_owner.SearchReplace.SelectAndScrollTo(m_folderFindCurrentFileMatches[m_folderFindCurrentMatchIdx]);
                m_statusText.Text = "見つかりました：" + System.IO.Path.GetFileName(m_folderFindFileList[m_folderFindCurrentFileIdx]) +
                    "（" + (m_folderFindCurrentMatchIdx + 1) + " / " + m_folderFindCurrentFileMatches.Count + "件目）";
                return;
            }

            // 今のファイルでは尽きたので、次に一致箇所があるファイルへ進む（末尾まで来たら
            // 先頭へ折り返し、フォルダ内を何周でも回り続ける）。
            int filesChecked = 0;
            while (filesChecked <= m_folderFindFileList.Count)
            {
                m_folderFindCurrentFileIdx++;
                if (m_folderFindCurrentFileIdx >= m_folderFindFileList.Count) m_folderFindCurrentFileIdx = 0;
                filesChecked++;

                string path = m_folderFindFileList[m_folderFindCurrentFileIdx];
                if (!PathsEqual(path, m_owner.CurrentFilePath))
                    m_owner.SearchReplace.OpenFileForFindReplace(path);
                SyncResultsListSelection(path);

                m_folderFindCurrentFileMatches = m_owner.SearchReplace.HighlightAllMatchesInCurrentFile(Term, CaseSensitive, UseRegex);
                if (m_folderFindCurrentFileMatches.Count > 0)
                {
                    m_folderFindCurrentMatchIdx = 0;
                    m_owner.SearchReplace.SelectAndScrollTo(m_folderFindCurrentFileMatches[0]);
                    m_statusText.Text = "見つかりました：" + System.IO.Path.GetFileName(path) +
                        "（1 / " + m_folderFindCurrentFileMatches.Count + "件目）";
                    return;
                }
            }
            // フォルダを一周しても一致箇所が1つも見つからなかった場合のみ、ここに到達する。
            m_statusText.Text = "見つかりませんでした。";
        }

        /// <summary>「すべて検索」（フォルダ全体）の結果一覧の選択項目を、今表示しているファイルに
        /// 合わせて切り替える。一覧にそのファイルが無ければ何もしない。</summary>
        /// <param name="a_path">今表示しているファイルの絶対パス。</param>
        private void SyncResultsListSelection(string a_path)
        {
            if (null == m_resultsPaths || 0 == m_resultsPaths.Count) return;
            int idx = m_resultsPaths.FindIndex(p => PathsEqual(p, a_path));
            if (idx >= 0 && idx < m_resultsList.Items.Count)
                m_resultsList.SelectedIndex = idx;
        }

        /// <summary>「前を検索」ボタン。</summary>
        /// <param name="a_sender">ボタン。</param>
        /// <param name="a_args">クリックイベント。</param>
        private void FindPreviousClick(object a_sender, RoutedEventArgs a_args)
        {
            if (string.IsNullOrEmpty(Term)) return;
            if (Term != m_lastFindAllTerm)
            {
                m_currentFileMatchRanges = null;
                m_resultsList.Visibility = Visibility.Collapsed;
            }

            if (true == m_scopeCurrentFile.IsChecked)
            {
                bool foundFlg = m_owner.SearchReplace.FindPreviousInCurrentFile(Term, CaseSensitive, UseRegex);
                m_statusText.Text = foundFlg ? "見つかりました（エディタ内で強調表示しています）。" : "見つかりませんでした。";
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
            if (0 == m_folderFindFileList.Count) { m_statusText.Text = "見つかりませんでした。"; return; }

            if (null != m_folderFindCurrentFileMatches && m_folderFindCurrentMatchIdx - 1 >= 0)
            {
                m_folderFindCurrentMatchIdx--;
                m_owner.SearchReplace.SelectAndScrollTo(m_folderFindCurrentFileMatches[m_folderFindCurrentMatchIdx]);
                m_statusText.Text = "見つかりました：" + System.IO.Path.GetFileName(m_folderFindFileList[m_folderFindCurrentFileIdx]) +
                    "（" + (m_folderFindCurrentMatchIdx + 1) + " / " + m_folderFindCurrentFileMatches.Count + "件目）";
                return;
            }

            int filesChecked = 0;
            while (filesChecked <= m_folderFindFileList.Count)
            {
                m_folderFindCurrentFileIdx--;
                if (m_folderFindCurrentFileIdx < 0) m_folderFindCurrentFileIdx = m_folderFindFileList.Count - 1;
                filesChecked++;

                string path = m_folderFindFileList[m_folderFindCurrentFileIdx];
                if (!PathsEqual(path, m_owner.CurrentFilePath))
                    m_owner.SearchReplace.OpenFileForFindReplace(path);
                SyncResultsListSelection(path);

                m_folderFindCurrentFileMatches = m_owner.SearchReplace.HighlightAllMatchesInCurrentFile(Term, CaseSensitive, UseRegex);
                if (m_folderFindCurrentFileMatches.Count > 0)
                {
                    m_folderFindCurrentMatchIdx = m_folderFindCurrentFileMatches.Count - 1;
                    m_owner.SearchReplace.SelectAndScrollTo(m_folderFindCurrentFileMatches[m_folderFindCurrentMatchIdx]);
                    m_statusText.Text = "見つかりました：" + System.IO.Path.GetFileName(path) +
                        "（" + m_folderFindCurrentFileMatches.Count + " / " + m_folderFindCurrentFileMatches.Count + "件目）";
                    return;
                }
            }
            m_statusText.Text = "見つかりませんでした。";
        }

        /// <summary>「すべて検索」ボタン：選択されている範囲内の一致箇所をすべて一覧表示する
        /// （現在のファイル範囲では、各一致箇所を前後の文脈付きで一覧表示する）。</summary>
        /// <param name="a_sender">ボタン。</param>
        /// <param name="a_args">クリックイベント。</param>
        private void FindAllClick(object a_sender, RoutedEventArgs a_args)
        {
            if (string.IsNullOrEmpty(Term)) return;
            m_folderFindFileList = null; // 次を検索/前を検索の位置情報が古いまま残らないようリセット
            m_lastFindAllTerm = Term;

            if (true == m_scopeCurrentFile.IsChecked)
            {
                var matches = m_owner.SearchReplace.HighlightAllMatchesInCurrentFile(Term, CaseSensitive, UseRegex);
                m_currentFileMatchRanges = matches;
                m_owner.FolderTreePane.ClearSearchMatches();

                var snippets = new List<string>();
                foreach (var range in matches)
                {
                    string context = "";
                    try
                    {
                        var para = range.Start.Paragraph;
                        if (null != para) context = new TextRange(para.ContentStart, para.ContentEnd).Text.Trim();
                    }
                    catch { /* 取得できなければ空のまま表示する */ }
                    if (context.Length > 70) context = context.Substring(0, 70) + "…";
                    snippets.Add(context.Length > 0 ? context : range.Text);
                }

                m_resultsPaths = new List<string>(); // ファイル単位の結果ではないので、こちらは使わない
                m_resultsList.ItemsSource = snippets;
                m_resultsList.Visibility = snippets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                m_statusText.Text = snippets.Count > 0
                    ? snippets.Count + " 件見つかりました（すべてエディタ内・アウトラインで強調表示しています。ダブルクリックでその箇所へ移動します）。"
                    : "見つかりませんでした。";
            }
            else
            {
                m_currentFileMatchRanges = null;
                m_owner.OutlinePane.ClearSearchMatches();
                var results = m_owner.SearchReplace.FindAllInFolder(Term, CaseSensitive, UseRegex);
                m_resultsPaths = results.Select(r => r.Item1).ToList();
                m_owner.FolderTreePane.MarkSearchMatches(m_resultsPaths);
                m_resultsList.ItemsSource = results.Select(r => r.Item1 + "　（" + r.Item2 + "件）").ToList();
                m_resultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                m_statusText.Text = results.Count > 0
                    ? results.Count + " 個のファイルで見つかりました（フォルダビューでも強調表示しています）。ダブルクリックで開きます。"
                    : "見つかりませんでした。";
            }
        }

        /// <summary>フォルダ範囲の検索結果一覧でダブルクリックされたファイルを開き、
        /// その最初の一致箇所へジャンプする。</summary>
        /// <param name="a_sender">結果一覧。</param>
        /// <param name="a_args">マウスイベント。</param>
        private void ResultsListMouseDoubleClick(object a_sender, MouseButtonEventArgs a_args)
        {
            int idx = m_resultsList.SelectedIndex;
            if (idx < 0) return;

            if (null != m_currentFileMatchRanges)
            {
                if (idx < m_currentFileMatchRanges.Count)
                    m_owner.SearchReplace.SelectAndScrollTo(m_currentFileMatchRanges[idx]);
                return;
            }

            if (idx >= m_resultsPaths.Count) return;
            m_owner.SearchReplace.OpenFileForFindReplace(m_resultsPaths[idx]);
            var matches = m_owner.SearchReplace.HighlightAllMatchesInCurrentFile(Term, CaseSensitive, UseRegex);
            if (matches.Count > 0)
            {
                m_owner.SearchReplace.SelectAndScrollTo(matches[0]);
                SeedFolderWalkFromCurrentFile(m_resultsPaths[idx], matches, 0);
            }
        }

        /// <summary>「すべて置換」ボタン。選択されている範囲のすべての一致箇所を一度に置換する。</summary>
        /// <param name="a_sender">ボタン。</param>
        /// <param name="a_args">クリックイベント。</param>
        private void ReplaceAllClick(object a_sender, RoutedEventArgs a_args)
        {
            if (string.IsNullOrEmpty(Term)) return;
            m_folderFindFileList = null;

            if (true == m_scopeCurrentFile.IsChecked)
            {
                int count = m_owner.SearchReplace.ReplaceAllInCurrentFile(Term, Replacement, CaseSensitive, UseRegex);
                m_statusText.Text = count + " 件を置換しました（保存するまでファイルには反映されません）。";
                m_resultsList.Visibility = Visibility.Collapsed;
            }
            else
            {
                var results = m_owner.SearchReplace.ReplaceAllInFolder(Term, Replacement, CaseSensitive, UseRegex);
                int totalCount = results.Sum(r => r.Item2);
                m_statusText.Text = results.Count + " 個のファイルで合計 " + totalCount +
                    " 件を置換しました（保存するまでファイルには書き出されません。ツールバーの「すべて保存」で反映してください）。";
                m_resultsPaths = results.Select(r => r.Item1).ToList();
                m_resultsList.ItemsSource = results.Select(r => r.Item1 + "　（" + r.Item2 + "件置換）").ToList();
                m_resultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ---------------- 1件ずつ確認するセッション ----------------

        /// <summary>「1件ずつ置換」ボタン。選択されている範囲を対象に、1件ずつ確認しながら
        /// 進めるセッションを開始する。</summary>
        /// <param name="a_sender">ボタン。</param>
        /// <param name="a_args">クリックイベント。</param>
        private void StepReplaceClick(object a_sender, RoutedEventArgs a_args)
        {
            if (string.IsNullOrEmpty(Term)) return;
            m_folderFindFileList = null;

            m_sessionActiveFlg = true;
            m_sessionFolderScopeFlg = true == m_scopeFolder.IsChecked;
            m_sessionTotalReplaced = 0;
            m_sessionFilesChanged = 0;
            m_resultsList.Visibility = Visibility.Collapsed;

            if (m_sessionFolderScopeFlg)
            {
                m_sessionFiles = m_owner.SearchReplace.GetFolderFiles();
                m_sessionFileIndex = -1;
                AdvanceToNextSessionFile();
                AdvanceToNextSessionMatch();
            }
            else
            {
                bool foundFlg = m_owner.SearchReplace.StepFindNext(Term, CaseSensitive, UseRegex, a_fromSelectionEndFlg: false);
                if (foundFlg) ShowLiveStepPanel();
                else EndSession();
            }
        }

        /// <summary>現在ファイル範囲でのセッションパネル表示。一致箇所はテキスト断片ではなく、
        /// エディタ内で直接強調表示される。</summary>
        private void ShowLiveStepPanel()
        {
            m_stepFileText.Text = "現在のファイル";
            m_stepContextText.Text = "エディタ内で強調表示された箇所をご確認ください。";
            m_stepPanel.Visibility = Visibility.Visible;
            m_statusText.Text = "";
        }

        /// <summary>現在のセッションファイルへの未反映の変更があれば書き込み、sessionFiles内の
        /// 次のファイルへ進む（一覧を使い切ったらsessionContentをnullにする）。</summary>
        private void AdvanceToNextSessionFile()
        {
            CommitSessionFileIfChanged();

            m_sessionFileIndex++;
            if (null == m_sessionFiles || m_sessionFileIndex >= m_sessionFiles.Count)
            {
                m_sessionContent = null;
                return;
            }

            m_sessionContent = m_owner.SearchReplace.GetFileContentForReplace(m_sessionFiles[m_sessionFileIndex]);
            m_sessionSearchPos = 0;
            m_sessionFileChangedFlg = false;
        }

        /// <summary>現在のセッションファイルに未反映の変更があれば、m_owner（ライブエディタまたは
        /// 保留中の編集）へ書き戻す。</summary>
        private void CommitSessionFileIfChanged()
        {
            if (!m_sessionFileChangedFlg || null == m_sessionContent) return;
            if (null == m_sessionFiles || m_sessionFileIndex < 0 || m_sessionFileIndex >= m_sessionFiles.Count) return;

            m_owner.SearchReplace.SetFileContentForReplace(m_sessionFiles[m_sessionFileIndex], m_sessionContent);
            m_sessionFilesChanged++;
            m_sessionFileChangedFlg = false;
        }

        /// <summary>現在のセッションファイル内のsessionSearchPos以降で次の一致箇所を探す。
        /// 現在のファイルに一致箇所が残っていなければ、後続のファイルへ順に進む。
        /// どのファイルにも一致箇所が残っていなければセッションを終了する。</summary>
        private void AdvanceToNextSessionMatch()
        {
            while (null != m_sessionContent)
            {
                var match = m_owner.SearchReplace.FindNextMatchInText(m_sessionContent, Term, CaseSensitive, UseRegex, m_sessionSearchPos);
                if (null != match)
                {
                    m_sessionPendingMatch = match;
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
            var (idx, len) = m_sessionPendingMatch.Value;
            int ctxStart = Math.Max(0, idx - 30);
            int ctxEnd = Math.Min(m_sessionContent.Length, idx + len + 30);
            string before = m_sessionContent.Substring(ctxStart, idx - ctxStart).Replace("\r", "").Replace("\n", "⏎");
            string matched = m_sessionContent.Substring(idx, len).Replace("\r", "").Replace("\n", "⏎");
            string after = m_sessionContent.Substring(idx + len, ctxEnd - (idx + len)).Replace("\r", "").Replace("\n", "⏎");

            m_stepFileText.Text = Path.GetFileName(m_sessionFiles[m_sessionFileIndex]);
            m_stepContextText.Text = "…" + before + "【" + matched + "】" + after + "…";
            m_stepPanel.Visibility = Visibility.Visible;
            m_statusText.Text = "";
        }

        /// <summary>セッションパネルの「置換」ボタン。現在の一致箇所を置換し、次の一致箇所へ進む。</summary>
        /// <param name="a_sender">ボタン。</param>
        /// <param name="a_args">クリックイベント。</param>
        private void StepReplaceOneClick(object a_sender, RoutedEventArgs a_args)
        {
            if (!m_sessionActiveFlg) return;

            if (!m_sessionFolderScopeFlg)
            {
                m_sessionTotalReplaced++;
                bool foundFlg = m_owner.SearchReplace.StepReplaceAndFindNext(Term, Replacement, CaseSensitive, UseRegex);
                if (foundFlg) ShowLiveStepPanel();
                else EndSession();
                return;
            }

            if (null == m_sessionPendingMatch) return;
            var (idx, len) = m_sessionPendingMatch.Value;
            m_sessionContent = m_owner.SearchReplace.ReplaceOneMatch(m_sessionContent, Term, Replacement, CaseSensitive, UseRegex, idx, len);
            m_sessionFileChangedFlg = true;
            m_sessionTotalReplaced++;
            m_sessionSearchPos = idx + Replacement.Length;
            AdvanceToNextSessionMatch();
        }

        /// <summary>セッションパネルの「スキップ」ボタン。現在の一致箇所には触れず、
        /// 次の一致箇所へ進む。</summary>
        /// <param name="a_sender">ボタン。</param>
        /// <param name="a_args">クリックイベント。</param>
        private void StepSkipClick(object a_sender, RoutedEventArgs a_args)
        {
            if (!m_sessionActiveFlg) return;

            if (!m_sessionFolderScopeFlg)
            {
                bool foundFlg = m_owner.SearchReplace.StepSkipAndFindNext(Term, CaseSensitive, UseRegex);
                if (foundFlg) ShowLiveStepPanel();
                else EndSession();
                return;
            }

            if (null == m_sessionPendingMatch) return;
            var (idx, len) = m_sessionPendingMatch.Value;
            m_sessionSearchPos = idx + len;
            AdvanceToNextSessionMatch();
        }

        /// <summary>セッションパネルの「残りをすべて置換」ボタン。現在の一致箇所と、
        /// 残りすべての一致箇所を確認なしで置換し、セッションを終了する。</summary>
        /// <param name="a_sender">ボタン。</param>
        /// <param name="a_args">クリックイベント。</param>
        private void StepReplaceRemainingClick(object a_sender, RoutedEventArgs a_args)
        {
            if (!m_sessionActiveFlg) return;

            if (!m_sessionFolderScopeFlg)
            {
                m_sessionTotalReplaced += m_owner.SearchReplace.StepReplaceAllRemaining(Term, Replacement, CaseSensitive, UseRegex);
                EndSession();
                return;
            }

            while (null != m_sessionContent)
            {
                var match = m_owner.SearchReplace.FindNextMatchInText(m_sessionContent, Term, CaseSensitive, UseRegex, m_sessionSearchPos);
                if (null == match)
                {
                    AdvanceToNextSessionFile();
                    continue;
                }
                var (idx, len) = match.Value;
                m_sessionContent = m_owner.SearchReplace.ReplaceOneMatch(m_sessionContent, Term, Replacement, CaseSensitive, UseRegex, idx, len);
                m_sessionFileChangedFlg = true;
                m_sessionTotalReplaced++;
                m_sessionSearchPos = idx + Replacement.Length;
            }
            EndSession();
        }

        /// <summary>セッションパネルの「終了」ボタン。セッションを途中で終了する。</summary>
        /// <param name="a_sender">ボタン。</param>
        /// <param name="a_args">クリックイベント。</param>
        private void StepEndClick(object a_sender, RoutedEventArgs a_args)
        {
            EndSession();
        }

        /// <summary>未反映のファイル変更を書き戻し、セッションパネルを閉じ、置換件数の
        /// サマリーを表示する。</summary>
        private void EndSession()
        {
            if (m_sessionFolderScopeFlg) CommitSessionFileIfChanged();
            m_sessionActiveFlg = false;
            m_sessionPendingMatch = null;
            m_stepPanel.Visibility = Visibility.Collapsed;

            m_statusText.Text = m_sessionTotalReplaced + " 件を置換しました" +
                (m_sessionFolderScopeFlg
                    ? "（" + m_sessionFilesChanged + " 個のファイル、保存するまでファイルには反映されません）。"
                    : "（現在のファイルに直接反映されました。保存するまでファイルには書き出されません）。");
        }

        /// <summary>
        /// 「置換後の文字列」エキスパンダーの開閉に合わせて、置換系のボタン（1件ずつ置換…・
        /// すべて置換）の表示/非表示を切り替える。初期状態（閉じている）では検索系の
        /// ボタンだけを表示し、開くと置換操作もできるようになる。
        /// </summary>
        /// <param name="a_sender">イベントの発生元（エキスパンダー）。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void ReplaceExpanderToggled(object a_sender, RoutedEventArgs a_args)
        {
            var visibility = m_replaceExpander.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            m_stepReplaceButton.Visibility = visibility;
            m_replaceAllButton.Visibility = visibility;
        }

        /// <summary>検索と置換ウィンドウのキーボードショートカットの実装。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">キーイベントの引数。</param>
        private void FindReplaceWindowPreviewKeyDown(object a_sender, KeyEventArgs a_args)
        {
            // Escキーでウィンドウを閉じる
            if (a_args.Key == Key.Escape)
            {
                this.Close();
                a_args.Handled = true;
            }
        }
    }
}
