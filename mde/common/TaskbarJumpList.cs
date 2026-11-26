// TaskbarJumpList.cs
//
// mde (Markdown インラインエディタ) の一部。
// タスクバーの mde のアイコンを右クリックした時の一覧（ジャンプリスト）に、最近使ったファイル・フォルダを表示するための
// 処理と、mde のアプリの ID（AppUserModelID）の設定。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;

namespace mde.common
{
    /// <summary>
    /// タスクバーのアイコンの右クリックの一覧（ジャンプリスト）と、アプリの ID（AppUserModelID）を扱う。
    /// <para>
    /// ・アプリの ID：Windows は、同じ ID のウィンドウを1つのタスクバーのアイコンにまとめ、ジャンプリストも ID ごとに持つ。
    /// 　インストーラーが作るスタートメニューのショートカット（msi\File.wxs の System.AppUserModel.ID）と同じ ID を、
    /// 　起動した直後に自分で設定する（SetAppUserModelId）。設定しないと、ショートカットを通らずに起動した時
    /// 　（ジャンプリストのファイル・エクスプローラーの「mdeで開く」等）に、exe の場所から決まる別の ID になり、
    /// 　タスクバーのアイコンがもう1つ増え、ジャンプリストも別になってしまう。
    /// </para>
    /// <para>
    /// ・ジャンプリスト：サクラエディタと同じく、ファイルを開くたびに Windows の「最近使ったもの」へ登録し
    /// 　（AddToWindowsRecent。SHAddToRecentDocs）、表示は Windows に任せる（ジャンプリストの「最近使ったもの」の欄）。
    /// 　以前は、「開く」ダイアログで選んだファイルしか Windows に登録されず（ダイアログが登録する）、フォルダペイン・
    /// 　「最近使ったファイル」・リンク等から開いたファイルはジャンプリストに出なかった。
    /// </para>
    /// <para>
    /// ・最近使ったフォルダ：Windows の「最近使ったもの」の欄はファイルだけのため、mde の「最近使ったフォルダ」を、
    /// 　別の欄（「最近使ったフォルダ」）に自前の項目として載せる（LoadRecentFolders 等）。項目を選ぶと、そのフォルダを
    /// 　起動時引数に渡して mde を起動する（エクスプローラーでフォルダを右クリックして「mdeで開く」を選んだ時と同じ）。
    /// 　ウィンドウごとに持つ「最近使ったフォルダ」の一覧とは別に、プロセスで1つの一覧（s_recentFolders）を持つ。
    /// 　後から開いたウィンドウは、設定ファイルから読んだ（他のウィンドウで加えたフォルダがまだ入っていない）一覧を
    /// 　持つため、それでジャンプリストを上書きしないようにするため。
    /// </para>
    /// </summary>
    public static class TaskbarJumpList
    {
        /// <summary>mde のアプリの ID。msi\File.wxs のスタートメニューのショートカットの System.AppUserModel.ID と
        /// 同じにすること（違うと、タスクバーにピン留めしたアイコンと、起動したウィンドウのアイコン・ジャンプリストが分かれる）。</summary>
        public const string APP_USER_MODEL_ID = "mde-tools.mde";

        /// <summary>SHAddToRecentDocs の、パスを Unicode の文字列で渡す指定。</summary>
        private const uint SHARD_PATHW = 0x00000003;

        /// <summary>ジャンプリストに載せる「最近使ったフォルダ」の最大の件数（メニューの「最近使ったフォルダ」と同じ）。</summary>
        private const int MAX_RECENT_FOLDERS = 10;

        /// <summary>フォルダのアイコンを持つ Windows の DLL と、そのアイコンの番号。</summary>
        private const string FOLDER_ICON_DLL = "imageres.dll";
        private const int FOLDER_ICON_INDEX = 3;

        /// <summary>ジャンプリストに載せる「最近使ったフォルダ」（新しい順）。プロセスで1つ。</summary>
        private static readonly List<string> s_recentFolders = new List<string>();

        /// <summary>s_recentFolders を、設定から読んだ一覧で初期化したか（最初のウィンドウだけが初期化する）。</summary>
        private static bool s_recentFoldersLoadedFlg;

        /// <summary>言語を切り替えた時に、欄の名前を作り直すように登録したか。</summary>
        private static bool s_languageHandlerAddedFlg;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string a_appId);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHAddToRecentDocs(uint a_flags, string a_path);

        /// <summary>このアプリ（プロセス）の ID を設定する。ウィンドウを作る前（App.OnStartup の最初）に呼ぶこと。</summary>
        public static void SetAppUserModelId()
        {
            try
            {
                SetCurrentProcessExplicitAppUserModelID(APP_USER_MODEL_ID);
            }
            catch
            {
                // 設定できなくても、タスクバーのアイコンのまとまり方が変わるだけなので、そのまま起動する
            }
        }

        /// <summary>
        /// ジャンプリストに、Windows の「最近使ったもの」の欄を表示する設定にする（起動した時に1回。App.OnStartup）。
        /// 「最近使ったフォルダ」の欄は、最初のウィンドウを作った時に載せる（LoadRecentFolders。言語の設定を読んだ後にするため）。
        /// 開発中の版が、自前の項目だけの（「最近使ったもの」の欄を表示しない）ジャンプリストを登録していた場合も、これで元に戻る。
        /// </summary>
        public static void Initialize()
        {
            try
            {
                if (null == Application.Current)
                {
                    return;
                }
                var jumpList = new JumpList { ShowRecentCategory = true, ShowFrequentCategory = false };
                JumpList.SetJumpList(Application.Current, jumpList);
                jumpList.Apply();
            }
            catch
            {
                // ジャンプリストを設定できなくても、mde の動作には関係ないため無視する
            }
        }

        /// <summary>
        /// ジャンプリストの「最近使ったフォルダ」の欄を、設定から読んだ一覧で初期化する（最初のウィンドウを作った時に1回。
        /// 2回目以降は何もしない。後から開いたウィンドウの一覧は古いことがあるため。クラスの説明を参照）。
        /// </summary>
        /// <param name="a_folders">設定から読んだ「最近使ったフォルダ」（新しい順）。</param>
        public static void LoadRecentFolders(IEnumerable<string> a_folders)
        {
            if (s_recentFoldersLoadedFlg)
            {
                return;
            }
            s_recentFoldersLoadedFlg = true;
            s_recentFolders.Clear();
            if (null != a_folders)
            {
                foreach (string folder in a_folders)
                {
                    if (!string.IsNullOrEmpty(folder) && !ContainsFolder(folder) && s_recentFolders.Count < MAX_RECENT_FOLDERS)
                    {
                        s_recentFolders.Add(folder);
                    }
                }
            }
            if (!s_languageHandlerAddedFlg)
            {
                s_languageHandlerAddedFlg = true;
                Loc.LanguageChanged += ApplyJumpList;
            }
            ApplyJumpList();
        }

        /// <summary>開いたフォルダを、ジャンプリストの「最近使ったフォルダ」の先頭に加える（既にあれば先頭へ移す）。</summary>
        /// <param name="a_folder">フォルダの絶対パス（末尾の「\」をそろえたもの。MainWindow.AddToRecentFolders）。</param>
        public static void AddRecentFolder(string a_folder)
        {
            if (string.IsNullOrEmpty(a_folder))
            {
                return;
            }
            s_recentFoldersLoadedFlg = true;
            s_recentFolders.RemoveAll(p => string.Equals(p, a_folder, StringComparison.OrdinalIgnoreCase));
            s_recentFolders.Insert(0, a_folder);
            if (s_recentFolders.Count > MAX_RECENT_FOLDERS)
            {
                s_recentFolders.RemoveRange(MAX_RECENT_FOLDERS, s_recentFolders.Count - MAX_RECENT_FOLDERS);
            }
            ApplyJumpList();
        }

        /// <summary>フォルダを、ジャンプリストの「最近使ったフォルダ」から外す（見つからなくなったフォルダ）。</summary>
        /// <param name="a_folder">フォルダの絶対パス。</param>
        public static void RemoveRecentFolder(string a_folder)
        {
            if (0 < s_recentFolders.RemoveAll(p => string.Equals(p, a_folder, StringComparison.OrdinalIgnoreCase)))
            {
                ApplyJumpList();
            }
        }

        /// <summary>ジャンプリストの「最近使ったフォルダ」を空にする（メニューの「最近使ったフォルダ」→「履歴をクリア」）。</summary>
        public static void ClearRecentFolders()
        {
            s_recentFoldersLoadedFlg = true;
            if (0 < s_recentFolders.Count)
            {
                s_recentFolders.Clear();
                ApplyJumpList();
            }
        }

        /// <summary>s_recentFolders に、指定したフォルダがあるか（大文字小文字は区別しない）。</summary>
        private static bool ContainsFolder(string a_folder)
        {
            return s_recentFolders.Exists(p => string.Equals(p, a_folder, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// ジャンプリストを作り直す。Windows の「最近使ったもの」の欄（ファイル）を表示し、それとは別に「最近使ったフォルダ」の
        /// 欄を載せる。見つからないフォルダ（移動・削除済み）は載せない（ネットワークのフォルダは、確かめると時間が
        /// かかることがあるため、確かめずに載せる）。
        /// </summary>
        private static void ApplyJumpList()
        {
            try
            {
                if (null == Application.Current)
                {
                    return;
                }
                var jumpList = new JumpList { ShowRecentCategory = true, ShowFrequentCategory = false };
                string exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    string category = Loc.T("最近使ったフォルダ", "Recent Folders");
                    string iconPath = Path.Combine(Environment.SystemDirectory, FOLDER_ICON_DLL);
                    foreach (string folder in s_recentFolders)
                    {
                        if (!IsNetworkPath(folder) && !Directory.Exists(folder))
                        {
                            continue;
                        }
                        string name = Path.GetFileName(folder);
                        jumpList.JumpItems.Add(new JumpTask
                        {
                            // ドライブのルート（「C:\」）は名前がないため、パスをそのまま表示する
                            Title = string.IsNullOrEmpty(name) ? folder : name,
                            Description = folder,
                            ApplicationPath = exePath,
                            // 末尾が「\」のパス（「C:\」）を「"」で囲むと、「\"」が「"」の文字と読まれてしまうため、「\」を重ねる
                            Arguments = "\"" + (folder.EndsWith("\\", StringComparison.Ordinal) ? folder + "\\" : folder) + "\"",
                            IconResourcePath = iconPath,
                            IconResourceIndex = FOLDER_ICON_INDEX,
                            CustomCategory = category
                        });
                    }
                }
                JumpList.SetJumpList(Application.Current, jumpList);
                jumpList.Apply();
            }
            catch
            {
                // ジャンプリストを設定できなくても、mde の動作には関係ないため無視する
            }
        }

        /// <summary>ネットワークのフォルダ（「\\サーバー\共有」）か。</summary>
        private static bool IsNetworkPath(string a_path)
        {
            return a_path.StartsWith("\\\\", StringComparison.Ordinal);
        }

        /// <summary>
        /// 開いた・保存したファイルを、Windows の「最近使ったもの」に登録する（サクラエディタと同じ。mde の
        /// 「最近使ったファイル」に加える時に呼ぶ。FileOperationsManager.AddToRecentFiles）。タスクバーの mde の
        /// アイコンの右クリックの一覧と、エクスプローラーの「最近使ったファイル」に表示される。
        /// </summary>
        /// <param name="a_path">ファイルの絶対パス。</param>
        public static void AddToWindowsRecent(string a_path)
        {
            if (string.IsNullOrEmpty(a_path))
            {
                return;
            }
            try
            {
                SHAddToRecentDocs(SHARD_PATHW, a_path);
            }
            catch
            {
                // 登録できなくても、mde の動作には関係ないため無視する
            }
        }
    }
}
