// App.xaml.cs
//
// Part of mde (MarkDown インラインエディタ).
// Application entry point: sets up a top-level exception handler so an unexpected error shows a
// message box instead of silently crashing the app. Also registers a Window-wide class handler so
// F1 opens README.md from anywhere in the application (see GlobalPreviewKeyDown below).

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
        /// starts warming up the headless Chromium instance used for PDF export
        /// (ChromiumBrowserPool) in the background so the first export doesn't have to pay the
        /// full startup cost.</summary>
        /// <param name="a_args">Startup event args.</param>
        protected override void OnStartup(StartupEventArgs a_args)
        {
            base.OnStartup(a_args);
            DispatcherUnhandledException += AppDispatcherUnhandledException;
            ChromiumBrowserPool.WarmUpInBackground();

            // F1キーで「Readmeを開く」を、アプリ内のどのウインドウ（メインウインドウ本体・
            // 検索と置換・バージョン情報・行間の設定等、各種ダイアログ）からでも呼び出せるように
            // する。個々のウインドウのPreviewKeyDownへ1つずつ実装する代わりに、Windowクラス
            // 全体に対するクラスハンドラーとして登録することで、今後ウインドウ（ダイアログ）が
            // 増えた場合にも、個別の対応漏れなく効くようにしている。
            EventManager.RegisterClassHandler(typeof(Window), Window.PreviewKeyDownEvent,
                new KeyEventHandler(GlobalPreviewKeyDown));
        }

        /// <summary>F1キーで「Readmeを開く」を実行する。押した時にキーボードフォーカスが
        /// あったウインドウ自身がMainWindowであればそのまま、検索と置換・バージョン情報等の
        /// ダイアログであれば、そのOwnerを辿って対応するMainWindowを探し、そちらでReadmeを
        /// 開く（ダイアログは各々Owner=（そのダイアログを開いたMainWindow）で生成されている
        /// ため、必ずどこかでMainWindowに辿り着く）。</summary>
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
                // ここ（UIスレッド）で ChromiumBrowserPool.ShutdownAsync() を直接
                // GetAwaiter().GetResult() すると、内部のawaitがUIスレッドの
                // SynchronizationContext（Dispatcher）へ結果を戻そうとして、まさにその
                // Dispatcherをブロックして待っている当スレッドと待ち合ってしまい、永久に
                // デッドロックする（＝OnExitがここで固まり、後続のEnvironment.Exitにも
                // 到達できず、mde.exeプロセスが終了できずに残り続ける）。
                // Task.Runで別スレッド（SynchronizationContextを持たないスレッドプール）
                // 上でShutdownAsyncを開始させることで、このデッドロックを避ける。
                Task.Run(() => ChromiumBrowserPool.ShutdownAsync()).GetAwaiter().GetResult();
            }
            catch
            {
                // 終了処理での失敗は、アプリの終了自体を妨げないよう無視する
            }

            base.OnExit(a_args);

            // 上記の対策後も、何らかの理由でプロセスが自然終了しないケースに備えて、
            // ここで確実にプロセスを終了させる。終了処理はここまでで完了しているため安全に行える。
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
                "MarkDown インラインエディタ - エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            a_args.Handled = true;
        }
    }
}
