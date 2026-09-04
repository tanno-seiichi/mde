// DebugLogger.cs
//
// mde (MarkDown インラインエディタ) の一部。
// IME固まり不具合の調査用に追加した、簡易なデバッグログ出力。この不具合は開発側の環境
// （Windows/WPFの実行環境）では再現手順を試せないため、実際に何が・いつ起きているかを
// 時系列でファイルに書き出し、後から読めるようにする。
//
// 【使い方】メニュー「表示」→「デバッグログを有効にする」で有効化してから症状を再現させ、
// デスクトップの mdelog フォルダに書き出される mde_v<バージョン>.log を開いて内容を
// 共有してください（有効化するたびに新しく書き出し直されるので、1回の再現につき1回分の
// 記録になります）。既定では無効で、この設定は次回起動時にも復元される（AppSettings参照）。
//
// 通常の動作に影響を与えないよう、ファイルへの書き込みはすべてtry/catchで囲んであり、
// 書き込みに失敗してもアプリの動作は継続する。無効時はLog()が即returnするだけの、
// ほぼ無コストな早期returnになる（文字入力のたびに呼ばれても、ディスクI/Oは一切発生しない）。
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace mde
{
    /// <summary>
    /// 時刻付きの1行ログをファイルに追記するだけの、状態を持つ簡易ロガー。有効/無効は
    /// メニュー「表示」→「デバッグログを有効にする」から切り替えられる。
    /// </summary>
    public static class DebugLogger
    {
        private static readonly object s_lock = new object();
        private static readonly string s_logPath = BuildLogPath();
        private static readonly Stopwatch s_stopwatch = Stopwatch.StartNew();
        private static bool s_enabledFlg = false;

        /// <summary>現在デバッグログが有効かどうか。</summary>
        public static bool IsEnabled => s_enabledFlg;

        /// <summary>ログの保存先（デスクトップの mdelog フォルダ内、
        /// mde_v&lt;バージョン&gt;.log）のパス文字列を組み立てる。ここではまだmdelogフォルダを
        /// 作成しない（「デバッグログを有効にする」がオフのままの利用者の環境に、使われない
        /// mdelogフォルダを作ってしまわないようにするため）。このメソッドはアプリ起動時、
        /// DebugLoggerの静的フィールド初期化のタイミングで、有効/無効に関わらず必ず一度
        /// 実行される。実際のフォルダ作成はSetEnabled(true)の中まで遅延する。
        /// 失敗した場合はnullを返し、以後Log()は常に何もしない。</summary>
        private static string BuildLogPath()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "mdelog");
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string fileName = $"mde_v{version.Major}.{version.Minor}.{version.Build}.log";
                return Path.Combine(dir, fileName);
            }
            catch
            {
                return null;
            }
        }

        // WPF自身のIsKeyboardFocused/FocusedElementは、あくまでWPFプロセス内部の「論理的な」
        // フォーカス管理の状態であり、実際に今どのウィンドウ（プロセス）がWindows全体の
        // フォアグラウンド（＝実際にキー入力を受け取る先）になっているかとは、理屈の上では
        // 食い違いうる。GetForegroundWindowをP/Invokeで直接呼んで、実際のフォアグラウンド
        // ウィンドウがどのプロセスに属しているかを確認できるようにする。
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

        /// <summary>デバッグログの有効/無効を切り替える。有効化した瞬間にログファイルを
        /// 新しく書き出し直す（起動時の設定復元・メニューからの切り替え、どちらの場合でも、
        /// 有効化のたびにその時点からの新しい記録として扱えるようにするため）。</summary>
        /// <param name="a_enabledFlg">true＝有効化、false＝無効化。</param>
        public static void SetEnabled(bool a_enabledFlg)
        {
            s_enabledFlg = a_enabledFlg;
            if (!a_enabledFlg || null == s_logPath)
            {
                return;
            }
            try
            {
                lock (s_lock)
                {
                    // 「デバッグログを有効にする」がオフのままならmdelogフォルダ自体を
                    // 作らずに済ませるため、フォルダの作成は実際に有効化された、この時点まで
                    // 遅延している（BuildLogPathの説明も参照）。
                    Directory.CreateDirectory(Path.GetDirectoryName(s_logPath));
                    File.WriteAllText(s_logPath,
                        $"=== mde debug log started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                        $"(v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}) ===\r\n");
                }
            }
            catch
            {
                // 書き込みに失敗しても致命的ではない（次回のLog()呼び出しも同様に無視される）。
            }
        }

        /// <summary>
        /// 1行、経過時間・スレッドID付きでログファイルに追記する。無効時、または書き込みに
        /// 失敗した場合は何もしない。
        /// </summary>
        /// <param name="a_message">記録するメッセージ。</param>
        public static void Log(string a_message)
        {
            if (!s_enabledFlg || null == s_logPath)
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
