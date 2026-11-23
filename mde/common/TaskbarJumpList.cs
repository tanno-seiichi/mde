// TaskbarJumpList.cs
//
// mde (Markdown インラインエディタ) の一部。
// タスクバーの mde のアイコンを右クリックした時の一覧（ジャンプリスト）に、最近使ったファイルを表示するための処理と、
// mde のアプリの ID（AppUserModelID）の設定。

using System;
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
    /// </summary>
    public static class TaskbarJumpList
    {
        /// <summary>mde のアプリの ID。msi\File.wxs のスタートメニューのショートカットの System.AppUserModel.ID と
        /// 同じにすること（違うと、タスクバーにピン留めしたアイコンと、起動したウィンドウのアイコン・ジャンプリストが分かれる）。</summary>
        public const string APP_USER_MODEL_ID = "mde-tools.mde";

        /// <summary>SHAddToRecentDocs の、パスを Unicode の文字列で渡す指定。</summary>
        private const uint SHARD_PATHW = 0x00000003;

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
        /// 自前の項目は載せない。開発中の版が、自前の項目だけの（「最近使ったもの」の欄を表示しない）ジャンプリストを
        /// 登録していた場合も、これで元に戻る。
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
