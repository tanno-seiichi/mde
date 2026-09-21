// App.xaml.cs
//
// Part of mde (Markdown インラインエディタ).
// Application entry point: sets up a top-level exception handler so an unexpected error shows a
// message box instead of silently crashing the app. Also registers a Window-wide class handler so
// F1 opens README.md from anywhere in the application (see GlobalPreviewKeyDown below).

using mde.chromium;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace mde
{
    /// <summary>
    /// mde's WPF Application object. StartupUri (see App.xaml) opens MainWindow; this class only
    /// adds global unhandled-exception handling and the F1 (Readmeを開く) shortcut.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Registers the unhandled-exception handler and the F1 shortcut on startup, and
        /// (only if Chromium has already been downloaded before) starts warming up the headless
        /// Chromium instance used for PDF export (ChromiumBrowserPool) in the background so the
        /// first export doesn't have to pay the full startup cost. If Chromium has not been
        /// downloaded yet, this intentionally does nothing here, so the user is asked for
        /// confirmation at export time instead of a multi-hundred-MB download silently starting
        /// at app launch (see ChromiumBrowserPool.WarmUpInBackground / MainWindow.ExportPdfBtnClick).</summary>
        /// <param name="a_args">Startup event args.</param>
        protected override void OnStartup(StartupEventArgs a_args)
        {
            base.OnStartup(a_args);
            DispatcherUnhandledException += AppDispatcherUnhandledException;
            ChromiumBrowserPool.WarmUpInBackground();

            // F1キーで「Readmeを開く」を、アプリ内のどのウインドウ（メインウインドウ本体・
            // 検索と置換・バージョン情報等、各種ダイアログ）からでも呼び出せるようにする。個々の
            // ウインドウのPreviewKeyDownへ実装する代わりにWindowクラス全体のクラスハンドラーと
            // して登録し、今後ダイアログが増えても対応漏れが出ないようにしている。
            EventManager.RegisterClassHandler(typeof(Window), Window.PreviewKeyDownEvent,
                new KeyEventHandler(GlobalPreviewKeyDown));
        }

        /// <summary>F1キーで「Readmeを開く」を実行する。フォーカスのあったウインドウ自身が
        /// MainWindowならそのまま、ダイアログであればOwnerを辿って対応するMainWindowを探して
        /// 開く（各ダイアログはOwner=それを開いたMainWindowで生成されるため必ず辿り着く）。</summary>
        /// <param name="a_sender">キー入力があった時にフォーカスを持っていたウインドウ。</param>
        /// <param name="a_args">キーイベントの引数。</param>
        private void GlobalPreviewKeyDown(object a_sender, KeyEventArgs a_args)
        {
            if (Key.F1 != a_args.Key)
            {
                return;
            }
            a_args.Handled = true;

            Window window = a_sender as Window;
            while (null != window && !(window is MainWindow))
            {
                window = window.Owner;
            }
            (window as MainWindow)?.OpenReadme();
        }

        /// <summary>Shuts down the shared headless Chromium instance used for PDF export (if one
        /// was started) so its process doesn't linger after the app closes.</summary>
        /// <param name="a_args">Exit event args.</param>
        protected override void OnExit(ExitEventArgs a_args)
        {
            try
            {
                // UIスレッドでChromiumBrowserPool.ShutdownAsync()を直接GetAwaiter().GetResult()
                // すると、内部のawaitがUIスレッドのSynchronizationContext（Dispatcher）へ結果を
                // 戻そうとして、それをブロックして待つ当スレッドと永久にデッドロックする
                // （mde.exeが終了できず残り続ける）。Task.Runで別スレッド上で開始させることで
                // これを避ける。
                Task.Run(() => ChromiumBrowserPool.ShutdownAsync()).GetAwaiter().GetResult();
            }
            catch
            {
                // 終了処理での失敗は、アプリの終了自体を妨げないよう無視する
            }

            base.OnExit(a_args);

            // 上記の対策後も何らかの理由でプロセスが自然終了しないケースに備え、終了処理完了
            // 後のここで確実にプロセスを終了させる。
            Environment.Exit(0);
        }

        /// <summary>Shows any otherwise-unhandled exception in a message box rather than letting the
        /// app crash silently.</summary>
        /// <param name="a_sender">The Application.</param>
        /// <param name="a_args">Exception details; Handled is set to true to keep the app running.</param>
        private void AppDispatcherUnhandledException(object a_sender, DispatcherUnhandledExceptionEventArgs a_args)
        {
            MessageBox.Show(
                "予期しないエラーが発生しました:\n\n" + a_args.Exception,
                "Markdown インラインエディタ - エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            a_args.Handled = true;
        }
    }
}
