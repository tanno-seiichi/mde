// ChromiumBrowserPool.cs
//
// mde (MarkDown インラインエディタ) の一部。
// ChromiumPdfExporterが使う、headless Chromiumのブラウザインスタンスを使い回すための
// 仕組み。PuppeteerSharpでheadless Chromiumのプロセスを毎回新しく起動して終了するのは、
// （実際に印刷する内容の量に関係なく）それ自体に数秒単位の時間がかかる重い処理のため、
// PDFに書き出すたびに待たされる原因になっていた。
//
// このクラスは、ブラウザ自体はアプリの起動中ずっと1つだけ起動したままにしておき（初回の
// PDF書き出し時、または起動直後にあらかじめ準備しておく）、書き出しのたびには軽量な
// ページ（IPage）だけを作って使い捨てる方式にすることで、2回目以降の書き出しを大幅に
// 高速化する。ブラウザ自体はアプリ終了時（App.xaml.csのOnExit）にまとめて終了する。

using System;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;

namespace mde
{
    /// <summary>アプリ全体で共有する、起動済みのheadless Chromiumインスタンスを管理する。</summary>
    public static class ChromiumBrowserPool
    {
        private static readonly SemaphoreSlim s_lock = new SemaphoreSlim(1, 1);
        private static IBrowser s_browser;
        private static bool s_browserDownloadedFlg;

        /// <summary>ダウンロード済みブラウザの実行ファイルの実パス。LaunchAsync呼び出し時に
        /// 明示的に渡すために保持しておく（後述のコメント参照。ExecutablePathを渡さないと、
        /// LaunchAsync側が独自の既定バージョン解決を行ってしまい、実際にダウンロード済みの
        /// バージョンと食い違うことがあるため）。</summary>
        private static string s_executablePath;

        /// <summary>使い回し可能なブラウザインスタンスを取得する。まだ起動していなければ、
        /// （必要ならChromium本体のダウンロードも含めて）ここで起動する。既に起動済みで
        /// まだ生きていれば、それをそのまま返す。</summary>
        public static async Task<IBrowser> GetBrowserAsync()
        {
            if (BrowserIsUsable(s_browser))
            {
                return s_browser;
            }

            await s_lock.WaitAsync();
            try
            {
                // ロック待ちの間に他の呼び出しが起動を終えている場合がある。
                if (BrowserIsUsable(s_browser))
                {
                    return s_browser;
                }

                if (!s_browserDownloadedFlg)
                {
                    var browserFetcher = new BrowserFetcher(SupportedBrowser.Chrome);
                    // 【重要・その1】引数なしのDownloadAsync()は、PuppeteerSharpのビルド時に
                    // 固定されたバージョン文字列（例: "152.0.0977.42"）をダウンロード先として
                    // 使う。Googleの配布元（chrome-for-testing-public）は、このピン留め
                    // されたビルドがその後も公開され続けることを保証していないため、時間が
                    // 経つとそのビルドが404になり「書き出しに失敗しました」というエラーに
                    // なることがある（実際にPuppeteerSharp 25.8.0で発生を確認した。同種の
                    // 不具合は過去にも報告されている：
                    // https://github.com/hardkoded/puppeteer-sharp/issues/2447 ）。
                    // BrowserTag.Stableを指定すると、呼び出し時点の「現在のChrome Stable版」
                    // を動的に解決してからダウンロードするため、ビルド固定によるこの種の
                    // 404を避けられる。
                    InstalledBrowser installedBrowser;
                    try
                    {
                        installedBrowser = await browserFetcher.DownloadAsync(BrowserTag.Stable);
                    }
                    catch
                    {
                        // 万一BrowserTag.Stableでの解決自体が失敗した場合（Google側の
                        // バージョン情報エンドポイントに一時的に到達できない等）に備え、
                        // 最後の手段として従来どおりの固定バージョンでも試みる。
                        installedBrowser = await browserFetcher.DownloadAsync();
                    }

                    // 【重要・その2】ここで実際にダウンロードされたバージョンの実行ファイル
                    // パスを必ず控えておき、下のLaunchAsyncへExecutablePathとして明示的に
                    // 渡す。ExecutablePathを渡さずにLaunchAsyncを呼ぶと、LaunchAsync自身が
                    // 独自に（DownloadAsyncとは別に）既定バージョンを決めてその場所を探しに
                    // 行ってしまい、これもPuppeteerSharpのビルド時に固定された別のバージョン
                    // 文字列であるため、上のBrowserTag.Stableで実際にダウンロードした
                    // バージョンと食い違って「Browser was not found at the configured
                    // executablePath」になることを実機で確認した。ダウンロードと起動の
                    // バージョン解決を必ず一致させるため、DownloadAsyncの戻り値
                    // （InstalledBrowser）が返す実パスをそのまま使う。
                    s_executablePath = installedBrowser.GetExecutablePath();
                    s_browserDownloadedFlg = true;
                }

                s_browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Headless = true,
                    ExecutablePath = s_executablePath,
                });
                return s_browser;
            }
            finally
            {
                s_lock.Release();
            }
        }

        /// <summary>アプリの起動直後などに、あらかじめバックグラウンドでブラウザを起動しておく
        /// （実際にPDFへ書き出すまで待たず、事前に準備しておくことで、最初の書き出しも
        /// 速くするための呼び出し）。失敗しても（オフライン環境など）ここでは何もしない。
        /// 実際に書き出す際、GetBrowserAsync側で改めて起動を試みる。</summary>
        public static void WarmUpInBackground()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await GetBrowserAsync();
                }
                catch
                {
                    // 事前準備が失敗しても、実際の書き出し時に改めて起動を試みるため無視する
                }
            });
        }

        /// <summary>正常終了（CloseAsync）の完了をこの時間（ミリ秒）だけ待つ。headless
        /// Chromiumは実際にはメインプロセスの他にレンダラー・GPU・crashpadなど複数の
        /// 子プロセスで構成されており、CloseAsyncが送る「穏やかな終了リクエスト」への
        /// 応答が返ってこない場合、この時間が過ぎたら強制終了（KillProcessTree）へ切り替える。</summary>
        private const int SHUTDOWN_GRACEFUL_TIMEOUT_MS = 3000;

        /// <summary>起動済みのブラウザがあれば、プロセスごと終了する。アプリ終了時に呼ぶ。
        /// まずCloseAsyncで穏やかな終了を試み、一定時間内に完了しなければ、レンダラー・GPU・
        /// crashpadなどの子プロセスも含めてプロセスツリーごと強制終了する。CloseAsyncの
        /// 「穏やかな終了リクエスト」はメインプロセスへの終了指示に過ぎず、それだけでは
        /// 子プロセスが取り残されてしまうことがある（特にWindowsで知られている問題）ため。</summary>
        public static async Task ShutdownAsync()
        {
            IBrowser browser = s_browser;
            s_browser = null;
            if (null == browser)
            {
                return;
            }

            // CloseAsync後はProcessを取得できなくなる可能性があるため、強制終了の保険用に
            // 先に控えておく。
            System.Diagnostics.Process process = null;
            try
            {
                process = browser.Process;
            }
            catch
            {
                // 取得できなくても、その場合は強制終了の保険が使えないだけなので先へ進む
            }

            bool gracefulCloseSucceededFlg = false;
            try
            {
                // ConfigureAwait(false): 呼び出し元がUIスレッド（Dispatcher）から同期的に
                // 待っている場合でも、継続処理をそのDispatcherへ戻そうとしてデッドロックしない
                // ようにするため。
                Task closeTask = browser.CloseAsync();
                Task completed = await Task.WhenAny(closeTask, Task.Delay(SHUTDOWN_GRACEFUL_TIMEOUT_MS)).ConfigureAwait(false);
                if (completed == closeTask)
                {
                    await closeTask.ConfigureAwait(false); // closeTask自体が例外で終わっていた場合、ここで再スローさせる
                    gracefulCloseSucceededFlg = true;
                }
            }
            catch
            {
                // 穏やかな終了が例外で失敗した場合も、下の強制終了へフォールバックする
            }

            if (!gracefulCloseSucceededFlg)
            {
                try
                {
                    // 子プロセス（レンダラー・GPU・crashpad等）も含めて、プロセスツリーごと
                    // 強制終了する。
                    process?.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 既に終了している等の理由で失敗しても、アプリの終了自体は妨げないよう無視する
                }
            }
        }

        private static bool BrowserIsUsable(IBrowser a_browser)
        {
            if (null == a_browser)
            {
                return false;
            }
            try
            {
                return a_browser.IsConnected;
            }
            catch
            {
                return false;
            }
        }
    }
}
