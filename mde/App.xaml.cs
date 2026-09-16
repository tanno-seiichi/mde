// App.xaml.cs
//
// Part of mde (Markdown インラインエディタ).
// Application entry point: sets up a top-level exception handler so an unexpected error shows a
// message box instead of silently crashing the app. Also registers a Window-wide class handler so
// F1 opens README.md from anywhere in the application (see GlobalPreviewKeyDown below).

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
        /// <summary>Registers the unhandled-exception handler and the F1 shortcut on startup.</summary>
        /// <param name="a_args">Startup event args.</param>
        protected override void OnStartup(StartupEventArgs a_args)
        {
            base.OnStartup(a_args);
            DispatcherUnhandledException += AppDispatcherUnhandledException;

            // F1でReadmeを開く操作を全ウインドウ（メインウインドウ・各種ダイアログ）で有効にする。
            // 個別にPreviewKeyDownを実装せず、Windowクラス全体のハンドラーとして登録することで、
            // 新規ウインドウ追加時の対応漏れを防ぐ。
            EventManager.RegisterClassHandler(typeof(Window), Window.PreviewKeyDownEvent,
                new KeyEventHandler(GlobalPreviewKeyDown));
        }

        /// <summary>F1キーで「Readmeを開く」を実行する。フォーカスのあったウインドウがダイアログ
        /// の場合はOwnerを辿ってMainWindowを探す（各ダイアログはOwner=呼び出し元のMainWindowで
        /// 生成されるため必ず辿り着く）。</summary>
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
