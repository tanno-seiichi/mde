// PdfExportProgress.cs
//
// mde (Markdown インラインエディタ) の一部。
// PDFへの書き出しの進み具合を表示する小さなウィンドウ。

using mde.common;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace mde.dialog
{
    /// <summary>
    /// PDFへの書き出しの進み具合を表示するウィンドウ。
    /// 書き出しの処理は、終わるまで画面（UI）のスレッドを止めて行う（WPF の文書は、作ったスレッドでしか扱えないため）。
    /// そのため、このウィンドウは専用のスレッドで表示し、書き出しの途中でも描き直されるようにする。
    /// 表示する割合は、知らされた割合（Report の a_fraction）へ滑らかに近づける。その後、次の知らせが来るまでは、次に
    /// 知らされる見込みの割合（a_nextFraction）へ向けてゆっくり進め、止まって見えないようにする（ページ1枚の処理の間や、
    /// 最後の仕上げのように進み具合が分からない間も、少しずつ進む）。見込みの割合には届かないので、追い越すことは無い。
    /// メインウィンドウを止めたまま表示するため、このウィンドウはアクティブにしない（アクティブにすると、メインウィンドウへ
    /// 非アクティブになった知らせが送られ、止まっているメインウィンドウの応答を待って、このウィンドウも止まることがある）。
    /// </summary>
    public sealed class PdfExportProgress : System.IDisposable
    {
        /// <summary>表示を更新する間隔（ミリ秒）。</summary>
        private const int TICK_MS = 50;
        /// <summary>知らされた割合へ近づける速さ（1回の更新で、残りの何割を進めるか）。</summary>
        private const double CATCH_UP_RATE = 0.35;
        /// <summary>次に知らされる見込みの割合へ、ゆっくり進める速さ（1回の更新で、残りの何割を進めるか）。</summary>
        private const double CREEP_RATE = 0.006;
        /// <summary>ウィンドウの幅。</summary>
        private const double WINDOW_WIDTH = 380;

        private readonly ManualResetEventSlim m_readyEvent = new ManualResetEventSlim(false);
        private Dispatcher m_dispatcher;
        private Window m_window;
        private TextBlock m_titleText;
        private TextBlock m_detailText;
        private Border m_barTrack;
        private Border m_barFill;
        // 以下は、表示用のスレッドだけが読み書きする
        private double m_shownFraction;
        private double m_targetFraction;
        private double m_creepLimit;
        private bool m_closingFlg;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(System.IntPtr a_hwnd, int a_index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(System.IntPtr a_hwnd, int a_index, int a_value);

        private const int GWL_STYLE = -16;
        private const int WS_SYSMENU = 0x00080000;

        private PdfExportProgress()
        {
        }

        /// <summary>
        /// 進み具合のウィンドウを、a_owner の中央に表示する（専用のスレッドで表示する）。
        /// </summary>
        /// <param name="a_owner">中央に表示する基準のウィンドウ（メインウィンドウ）。</param>
        /// <param name="a_title">見出し（「PDF用のデータを生成しています」等。後から SetTitle で変えられる）。</param>
        /// <returns>表示したウィンドウ。使い終わったら Dispose で閉じること。</returns>
        public static PdfExportProgress Show(Window a_owner, string a_title)
        {
            var progress = new PdfExportProgress();
            Rect ownerRect = GetScreenRectInDips(a_owner);
            var thread = new Thread(() => progress.Run(ownerRect, a_title))
            {
                IsBackground = true,
                Name = "PdfExportProgress",
            };
            // WPF のウィンドウは STA のスレッドでしか作れない（OperatingSystem.IsWindows は、Windows 専用の処理である
            // ことをビルド時の検査に伝えるため。mde は Windows でしか動かない）
            if (System.OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(ApartmentState.STA);
            }
            thread.Start();
            // 表示されるまで待つ（表示されないまま書き出しが始まり、最初の知らせが失われないように）。表示に失敗しても、
            // 書き出しは進めるため、長くは待たない
            progress.m_readyEvent.Wait(3000);
            return progress;
        }

        /// <summary>
        /// 進み具合を知らせる。
        /// </summary>
        /// <param name="a_detail">今行っていることの説明（「書き出しています（3 ページ目）」等）。</param>
        /// <param name="a_fraction">全体のうち、終わった割合（0〜1）。</param>
        /// <param name="a_nextFraction">次に知らせる見込みの割合（0〜1）。次の知らせまでは、ここへ向けてゆっくり進める。</param>
        public void Report(string a_detail, double a_fraction, double a_nextFraction)
        {
            m_dispatcher?.BeginInvoke(new System.Action(() =>
            {
                if (null != a_detail)
                {
                    m_detailText.Text = a_detail;
                }
                m_targetFraction = System.Math.Max(m_targetFraction, Clamp(a_fraction));
                m_creepLimit = System.Math.Max(m_targetFraction, Clamp(a_nextFraction));
            }));
        }

        /// <summary>見出しを変える（PDF用のデータを作り終えて、書き出しを始める時等）。</summary>
        /// <param name="a_title">見出し。</param>
        public void SetTitle(string a_title)
        {
            m_dispatcher?.BeginInvoke(new System.Action(() =>
            {
                if (null != m_titleText)
                {
                    m_titleText.Text = a_title;
                }
            }));
        }

        /// <summary>ウィンドウを隠す・また表示する（PDFの保存先を選ぶ画面を出している間は隠す）。</summary>
        /// <param name="a_visibleFlg">表示するなら true。</param>
        public void SetVisible(bool a_visibleFlg)
        {
            m_dispatcher?.BeginInvoke(new System.Action(() =>
            {
                if (null == m_window || m_closingFlg)
                {
                    return;
                }
                if (a_visibleFlg)
                {
                    m_window.Show();
                    BringToFront();
                }
                else
                {
                    m_window.Hide();
                }
            }));
        }

        /// <summary>ウィンドウを閉じ、表示用のスレッドを終える。</summary>
        public void Dispose()
        {
            Dispatcher dispatcher = m_dispatcher;
            m_dispatcher = null;
            dispatcher?.BeginInvoke(new System.Action(() =>
            {
                m_closingFlg = true;
                m_window?.Close();
                dispatcher.InvokeShutdown();
            }));
        }

        /// <summary>表示用のスレッドの本体：ウィンドウを作って表示し、閉じるまでメッセージを処理する。</summary>
        private void Run(Rect a_ownerRect, string a_title)
        {
            try
            {
                m_dispatcher = Dispatcher.CurrentDispatcher;
                m_window = BuildWindow(a_title);
                m_window.Left = a_ownerRect.Left + (a_ownerRect.Width - WINDOW_WIDTH) / 2;
                // 高さは中身で決まるため、仮の高さで中央に置き、表示した後に置き直す
                m_window.Top = a_ownerRect.Top + a_ownerRect.Height / 2 - 70;
                m_window.SourceInitialized += (s, e) =>
                {
                    // 閉じるボタン（×）を出さない（書き出しの途中で閉じられないように）
                    System.IntPtr hwnd = new WindowInteropHelper(m_window).Handle;
                    SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) & ~WS_SYSMENU);
                };
                m_window.ContentRendered += (s, e) =>
                {
                    m_window.Top = a_ownerRect.Top + (a_ownerRect.Height - m_window.ActualHeight) / 2;
                };
                m_window.Closing += (s, e) => e.Cancel = !m_closingFlg;
                var timer = new DispatcherTimer(DispatcherPriority.Normal, m_dispatcher)
                {
                    Interval = System.TimeSpan.FromMilliseconds(TICK_MS),
                };
                timer.Tick += (s, e) => Tick();
                m_window.Show();
                BringToFront();
                timer.Start();
            }
            catch
            {
                // 表示できなくても、書き出しは続ける
            }
            finally
            {
                m_readyEvent.Set();
            }
            Dispatcher.Run();
        }

        /// <summary>
        /// ウィンドウを、アクティブにしないまま、他のウィンドウ（最前面のものを除く）より手前に出す（一番手前に出したまま
        /// にはしない）。保存先を選ぶ画面を閉じた後等は、メインウィンドウが手前に来て、隠したウィンドウを表示し直しても
        /// その後ろに隠れてしまうため。
        /// </summary>
        private void BringToFront()
        {
            m_window.Topmost = true;
            m_window.Topmost = false;
        }

        /// <summary>ウィンドウを組み立てる。</summary>
        private Window BuildWindow(string a_title)
        {
            m_titleText = new TextBlock
            {
                Text = a_title,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            };
            m_detailText = new TextBlock
            {
                Text = Loc.T("準備しています…", "Preparing…"),
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            m_barFill = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
            };
            m_barTrack = new Border
            {
                Height = 6,
                Margin = new Thickness(0, 14, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                CornerRadius = new CornerRadius(3),
                Child = m_barFill,
            };
            var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 24) };
            panel.Children.Add(m_titleText);
            panel.Children.Add(m_detailText);
            panel.Children.Add(m_barTrack);
            return new Window
            {
                Title = Loc.T("PDFに書き出し", "Export to PDF"),
                Width = WINDOW_WIDTH,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI"),
                FontSize = 12,
                Background = Brushes.White,
                Content = panel,
            };
        }

        /// <summary>表示する割合を、知らされた割合・見込みの割合へ近づける。</summary>
        private void Tick()
        {
            if (m_shownFraction < m_targetFraction)
            {
                m_shownFraction += System.Math.Max((m_targetFraction - m_shownFraction) * CATCH_UP_RATE, 0.002);
                m_shownFraction = System.Math.Min(m_shownFraction, m_targetFraction);
            }
            else if (m_shownFraction < m_creepLimit)
            {
                m_shownFraction += (m_creepLimit - m_shownFraction) * CREEP_RATE;
            }
            double trackWidth = m_barTrack.ActualWidth;
            m_barFill.Width = System.Math.Max(0, trackWidth * m_shownFraction);
        }

        /// <summary>割合を 0〜1 に収める。</summary>
        private static double Clamp(double a_value)
        {
            return double.IsNaN(a_value) ? 0 : System.Math.Max(0, System.Math.Min(1, a_value));
        }

        /// <summary>ウィンドウの、画面上の位置と大きさ（DIP 単位）。</summary>
        private static Rect GetScreenRectInDips(Window a_window)
        {
            try
            {
                PresentationSource source = PresentationSource.FromVisual(a_window);
                if (null != source?.CompositionTarget)
                {
                    Point topLeft = a_window.PointToScreen(new Point(0, 0));
                    Point bottomRight = a_window.PointToScreen(new Point(a_window.ActualWidth, a_window.ActualHeight));
                    Matrix toDips = source.CompositionTarget.TransformFromDevice;
                    return new Rect(toDips.Transform(topLeft), toDips.Transform(bottomRight));
                }
            }
            catch
            {
            }
            return new Rect(a_window.Left, a_window.Top, a_window.ActualWidth, a_window.ActualHeight);
        }
    }
}
