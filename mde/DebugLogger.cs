// DebugLogger.cs
//
// mde (MarkDown インラインエディタ) の一部。
// IME固まり不具合（DESIGN.md 14.12参照）の調査用に追加した、簡易なデバッグログ出力。
// この不具合は開発側の環境（Windows/WPFの実行環境）では再現手順を試せず、実機での
// 動画に頼った調査を10回以上重ねても原因を特定できなかったため、実際に何が・いつ
// 起きているかを時系列でファイルに書き出し、後から読めるようにする。
//
// 【使い方】症状を再現させた後、%LOCALAPPDATA%\mde\debug.log を開いて内容を共有してください
// （ファイルはアプリを起動するたびに新しく作り直されるので、1回の再現につき1回分の記録に
// なります）。
//
// 通常の動作に影響を与えないよう、ファイルへの書き込みはすべてtry/catchで囲んであり、
// 書き込みに失敗してもアプリの動作は継続する。
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace mde
{
    /// <summary>
    /// 時刻付きの1行ログをファイルに追記するだけの、状態を持つ簡易ロガー。
    /// </summary>
    public static class DebugLogger
    {
        private static readonly object s_lock = new object();
        private static readonly string s_logPath;
        private static readonly Stopwatch s_stopwatch = Stopwatch.StartNew();

        // 追記13（DESIGN.md 14.12参照）：WPF自身のIsKeyboardFocused/FocusedElementは、
        // あくまでWPFプロセス内部の「論理的な」フォーカス管理の状態であり、実際に今どの
        // ウィンドウ（プロセス）がWindows全体のフォアグラウンド（＝実際にキー入力を受け取る
        // 先）になっているかとは、理屈の上では食い違いうる。IME/予測変換候補のポップアップが
        // 独立した別ウィンドウ（別プロセス）として実装されている可能性を疑い、GetForegroundWindow
        // をP/Invokeで直接呼んで、実際のフォアグラウンドウィンドウがどのプロセスに属しているかを
        // 確認できるようにした。
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr a_hWnd, out uint a_processId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr a_hWnd, StringBuilder a_className, int a_maxCount);

        /// <summary>
        /// 現在のフォアグラウンドウィンドウ（Windows全体で見て、実際にキー入力を受け取る先）を
        /// 説明する文字列を返す。WPFの論理フォーカスとは独立に、実際のOSレベルの状態を確認する
        /// ためのもの。取得に失敗しても例外を投げず、その旨の文字列を返す。
        /// </summary>
        public static string DescribeForegroundWindow()
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (IntPtr.Zero == hWnd)
                {
                    return "(none)";
                }

                var sb = new StringBuilder(256);
                GetClassName(hWnd, sb, sb.Capacity);
                string className = sb.ToString();

                GetWindowThreadProcessId(hWnd, out uint pid);
                string procName = "?";
                try
                {
                    using (var proc = Process.GetProcessById((int)pid))
                    {
                        procName = proc.ProcessName;
                    }
                }
                catch
                {
                    // プロセスがすでに終了している等で取得できなくても、クラス名だけで十分価値がある。
                }

                return $"hWnd=0x{hWnd.ToInt64():X} class={className} proc={procName}(pid={pid})";
            }
            catch (Exception ex)
            {
                return $"(取得失敗: {ex.GetType().Name}: {ex.Message})";
            }
        }

        static DebugLogger()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mde");
                Directory.CreateDirectory(dir);
                s_logPath = Path.Combine(dir, "debug.log");
                // 起動のたびに新しいログにする（古いログと混ざって読みにくくなるのを防ぐ）。
                File.WriteAllText(s_logPath,
                    $"=== mde debug log started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                    $"(v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}) ===\r\n");
            }
            catch
            {
                s_logPath = null;
            }
        }

        /// <summary>
        /// 1行、経過時間・スレッドID付きでログファイルに追記する。失敗しても何もしない。
        /// </summary>
        /// <param name="a_message">記録するメッセージ。</param>
        public static void Log(string a_message)
        {
            if (null == s_logPath)
            {
                return;
            }
            try
            {
                string line = $"[{s_stopwatch.Elapsed.TotalMilliseconds,9:0.0}ms] [T{Thread.CurrentThread.ManagedThreadId}] {a_message}\r\n";
                lock (s_lock)
                {
                    File.AppendAllText(s_logPath, line);
                }
            }
            catch
            {
                // ログ書き込みの失敗でアプリの動作に影響を与えないよう、無視する。
            }
        }
    }
}
