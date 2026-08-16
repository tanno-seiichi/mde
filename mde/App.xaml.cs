// App.xaml.cs
//
// Part of mde (MarkDown インラインエディタ).
// Application entry point: sets up a top-level exception handler so an unexpected error shows a
// message box instead of silently crashing the app.

using System.Windows;
using System.Windows.Threading;

namespace mde
{
    /// <summary>
    /// mde's WPF Application object. StartupUri (see App.xaml) opens MainWindow; this class only
    /// adds global unhandled-exception handling.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Registers the unhandled-exception handler on startup.</summary>
        /// <param name="a_args">Startup event args.</param>
        protected override void OnStartup(StartupEventArgs a_args)
        {
            base.OnStartup(a_args);
            DispatcherUnhandledException += AppDispatcherUnhandledException;
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
