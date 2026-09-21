// FindReplaceLauncher.cs
//
// mde (Markdown インラインエディタ) の一部。
// 検索と置換ウィンドウ（FindReplaceWindow）を開く・前面に出す処理を担当するクラス。
// MainWindow.xaml.csの「検索と置換の呼び出し」区画のうち、ウィンドウの開閉管理部分を
// そのまま抽出したもの。
//
// FindReplaceWindow自体は、検索・置換の実処理（SearchReplaceService）や現在のファイル・
// アウトライン・フォルダツリーへ、コンストラクタで渡されたMainWindow経由で直接アクセスする
// 設計（FindReplaceWindow.xaml.csのm_owner参照）になっているため、このクラスはMainWindow
// そのものへの参照は持たず、代わりに「FindReplaceWindowを1つ新しく作る」処理そのものを
// delegate（Func&lt;FindReplaceWindow&gt;）としてコンストラクタで受け取る。MainWindow側は
// `() => new FindReplaceWindow(this) { Owner = this }` を渡すことで、このクラスに
// 自分自身への参照を直接持たせずに済む。

using System;

namespace mde.findreplace
{
    /// <summary>
    /// 検索と置換ウィンドウを開く・すでに開いていれば前面に出す。同時に2つ開かないよう、
    /// 現在開いているインスタンスを保持する。
    /// </summary>
    public class FindReplaceLauncher
    {
        private readonly Func<FindReplaceWindow> m_createFindReplaceWindow;

        /// <summary>現在開いている検索と置換ウィンドウ（開いていなければnull）。
        /// FileOperationsManagerが、ファイルを開いた際のハイライト再適用に使う。</summary>
        private FindReplaceWindow m_openFindReplaceWindow;

        /// <summary>現在開いている検索と置換ウィンドウ（開いていなければnull）。</summary>
        public FindReplaceWindow CurrentWindow => m_openFindReplaceWindow;

        /// <param name="a_createFindReplaceWindow">FindReplaceWindowを1つ新しく作る処理。
        /// Ownerの設定も含め、呼び出し側（MainWindow）が用意する。</param>
        public FindReplaceLauncher(Func<FindReplaceWindow> a_createFindReplaceWindow)
        {
            this.m_createFindReplaceWindow = a_createFindReplaceWindow;
        }

        /// <summary>検索と置換ウィンドウを開く（すでに開いていれば、そちらを前面に出す）。</summary>
        public void Open()
        {
            if (null != m_openFindReplaceWindow)
            {
                m_openFindReplaceWindow.Activate();
                m_openFindReplaceWindow.Focus();
                return;
            }
            m_openFindReplaceWindow = m_createFindReplaceWindow();
            m_openFindReplaceWindow.Closed += (s, e) => m_openFindReplaceWindow = null;
            m_openFindReplaceWindow.Show();
        }
    }
}
