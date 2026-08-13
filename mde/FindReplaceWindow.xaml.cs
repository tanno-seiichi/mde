using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace mde
{
    public partial class FindReplaceWindow : Window
    {
        private readonly MainWindow owner;
        private List<string> resultsPaths = new List<string>();

        // ---- step-by-step session state (used only for FOLDER scope; current-file scope is
        // driven live off the editor's own selection, see owner.StepFindNext etc.) ----
        private bool sessionActive = false;
        private bool sessionFolderScope;
        private List<string> sessionFiles;
        private int sessionFileIndex;
        private string sessionContent;
        private bool sessionFileChanged;
        private int sessionSearchPos;
        private (int index, int length)? sessionPendingMatch;
        private int sessionTotalReplaced;
        private int sessionFilesChanged;

        public FindReplaceWindow(MainWindow owner)
        {
            InitializeComponent();
            this.owner = owner;
            SearchBox.Focus();
        }

        private string Term => SearchBox.Text;
        private string Replacement => ReplaceBox.Text ?? "";
        private bool CaseSensitive => CaseSensitiveBox.IsChecked == true;
        private bool UseRegex => UseRegexBox.IsChecked == true;

        // ---------------- find / replace all ----------------

        private void FindNext_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Term)) return;

            if (ScopeCurrentFile.IsChecked == true)
            {
                ResultsList.Visibility = Visibility.Collapsed;
                bool found = owner.FindNextInCurrentFile(Term, CaseSensitive, UseRegex);
                StatusText.Text = found ? "見つかりました（エディタ内で強調表示しています）。" : "見つかりませんでした。";
            }
            else
            {
                var results = owner.FindAllInFolder(Term, CaseSensitive, UseRegex);
                resultsPaths = results.Select(r => r.Item1).ToList();
                ResultsList.ItemsSource = results.Select(r => r.Item1 + "　（" + r.Item2 + "件）").ToList();
                ResultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                StatusText.Text = results.Count > 0
                    ? results.Count + " 個のファイルで見つかりました。ダブルクリックで開きます。"
                    : "見つかりませんでした。";
            }
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            int idx = ResultsList.SelectedIndex;
            if (idx < 0 || idx >= resultsPaths.Count) return;

            owner.OpenFileForFindReplace(resultsPaths[idx]);
            owner.FindNextInCurrentFile(Term, CaseSensitive, UseRegex);
        }

        private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Term)) return;

            if (ScopeCurrentFile.IsChecked == true)
            {
                int count = owner.ReplaceAllInCurrentFile(Term, Replacement, CaseSensitive, UseRegex);
                StatusText.Text = count + " 件を置換しました（保存するまでファイルには反映されません）。";
                ResultsList.Visibility = Visibility.Collapsed;
            }
            else
            {
                var results = owner.ReplaceAllInFolder(Term, Replacement, CaseSensitive, UseRegex);
                int totalCount = results.Sum(r => r.Item2);
                StatusText.Text = results.Count + " 個のファイルで合計 " + totalCount +
                    " 件を置換しました（保存するまでファイルには書き出されません。ツールバーの「すべて保存」で反映してください）。";
                resultsPaths = results.Select(r => r.Item1).ToList();
                ResultsList.ItemsSource = results.Select(r => r.Item1 + "　（" + r.Item2 + "件置換）").ToList();
                ResultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ---------------- step-by-step session ----------------

        private void StepReplace_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Term)) return;

            sessionActive = true;
            sessionFolderScope = ScopeFolder.IsChecked == true;
            sessionTotalReplaced = 0;
            sessionFilesChanged = 0;
            ResultsList.Visibility = Visibility.Collapsed;

            if (sessionFolderScope)
            {
                sessionFiles = owner.GetFolderFiles();
                sessionFileIndex = -1;
                AdvanceToNextSessionFile();
                AdvanceToNextSessionMatch();
            }
            else
            {
                bool found = owner.StepFindNext(Term, CaseSensitive, UseRegex, fromSelectionEnd: false);
                if (found) ShowLiveStepPanel();
                else EndSession();
            }
        }

        private void ShowLiveStepPanel()
        {
            StepFileText.Text = "現在のファイル";
            StepContextText.Text = "エディタ内で強調表示された箇所をご確認ください。";
            StepPanel.Visibility = Visibility.Visible;
            StatusText.Text = "";
        }

        private void AdvanceToNextSessionFile()
        {
            CommitSessionFileIfChanged();

            sessionFileIndex++;
            if (sessionFiles == null || sessionFileIndex >= sessionFiles.Count)
            {
                sessionContent = null;
                return;
            }

            sessionContent = owner.GetFileContentForReplace(sessionFiles[sessionFileIndex]);
            sessionSearchPos = 0;
            sessionFileChanged = false;
        }

        private void CommitSessionFileIfChanged()
        {
            if (!sessionFileChanged || sessionContent == null) return;
            if (sessionFiles == null || sessionFileIndex < 0 || sessionFileIndex >= sessionFiles.Count) return;

            owner.SetFileContentForReplace(sessionFiles[sessionFileIndex], sessionContent);
            sessionFilesChanged++;
            sessionFileChanged = false;
        }

        private void AdvanceToNextSessionMatch()
        {
            while (sessionContent != null)
            {
                var match = owner.FindNextMatchInText(sessionContent, Term, CaseSensitive, UseRegex, sessionSearchPos);
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

        private void StepReplaceOne_Click(object sender, RoutedEventArgs e)
        {
            if (!sessionActive) return;

            if (!sessionFolderScope)
            {
                sessionTotalReplaced++;
                bool found = owner.StepReplaceAndFindNext(Term, Replacement, CaseSensitive, UseRegex);
                if (found) ShowLiveStepPanel();
                else EndSession();
                return;
            }

            if (sessionPendingMatch == null) return;
            var (idx, len) = sessionPendingMatch.Value;
            sessionContent = owner.ReplaceOneMatch(sessionContent, Term, Replacement, CaseSensitive, UseRegex, idx, len);
            sessionFileChanged = true;
            sessionTotalReplaced++;
            sessionSearchPos = idx + Replacement.Length;
            AdvanceToNextSessionMatch();
        }

        private void StepSkip_Click(object sender, RoutedEventArgs e)
        {
            if (!sessionActive) return;

            if (!sessionFolderScope)
            {
                bool found = owner.StepSkipAndFindNext(Term, CaseSensitive, UseRegex);
                if (found) ShowLiveStepPanel();
                else EndSession();
                return;
            }

            if (sessionPendingMatch == null) return;
            var (idx, len) = sessionPendingMatch.Value;
            sessionSearchPos = idx + len;
            AdvanceToNextSessionMatch();
        }

        private void StepReplaceRemaining_Click(object sender, RoutedEventArgs e)
        {
            if (!sessionActive) return;

            if (!sessionFolderScope)
            {
                sessionTotalReplaced += owner.StepReplaceAllRemaining(Term, Replacement, CaseSensitive, UseRegex);
                EndSession();
                return;
            }

            while (sessionContent != null)
            {
                var match = owner.FindNextMatchInText(sessionContent, Term, CaseSensitive, UseRegex, sessionSearchPos);
                if (match == null)
                {
                    AdvanceToNextSessionFile();
                    continue;
                }
                var (idx, len) = match.Value;
                sessionContent = owner.ReplaceOneMatch(sessionContent, Term, Replacement, CaseSensitive, UseRegex, idx, len);
                sessionFileChanged = true;
                sessionTotalReplaced++;
                sessionSearchPos = idx + Replacement.Length;
            }
            EndSession();
        }

        private void StepEnd_Click(object sender, RoutedEventArgs e)
        {
            EndSession();
        }

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

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
