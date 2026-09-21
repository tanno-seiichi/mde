// DebugLogger.cs
//
// mde (Markdown インラインエディタ) の一部。
// IME固まり不具合調査用の簡易デバッグログ出力。開発環境では再現できないため、実機での
// 挙動を時系列でファイルに残す。
//
// 【使い方】メニュー「表示」→「デバッグログを有効にする」で有効化してから症状を再現させ、
// デスクトップの mdelog フォルダに出力される mde_v<バージョン>_pid<プロセスID>.log を
// 共有する。有効化のたびに新規作成される。ファイル名にプロセスIDを含めるのは、mdeを
// 複数同時起動して比較する場合にログが混ざらないようにするため。既定は無効で、設定は
// 次回起動時にも復元される（AppSettings参照）。
//
// ファイルへの書き込みはすべてtry/catchで保護し、失敗してもアプリの動作は継続する。
// 無効時のLog()は即returnのみで、ディスクI/Oは発生しない。
//
// SetEnabled(true)でファイルを一度だけ開き、無効化されるか再度有効化されるまで同じ
// StreamWriterを使い回す（毎回開閉すると、ログファイルが大きくなるにつれ開閉コストが
// 増加し、入力の反映が徐々に遅くなるため）。アプリが固まった瞬間の直前までの記録を
// 確実に残す必要があるため、Log()内では毎回Flush()を呼ぶ。
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace mde.logger
{
    /// <summary>
    /// 時刻付きの1行ログをファイルに追記するだけの、状態を持つ簡易ロガー。有効/無効は
    /// メニュー「表示」→「デバッグログを有効にする」から切り替えられる。
    /// </summary>
    public static class DebugLogger
    {
        private static readonly object m_lock = new object();
        private static readonly string m_logPath = BuildLogPath();
        private static readonly Stopwatch m_stopwatch = Stopwatch.StartNew();
        private static bool m_enabledFlg = false;

        /// <summary>有効化中はログファイルへ書き込み続ける、開きっぱなしのStreamWriter
        /// （内部でm_logStreamを保持）。SetEnabled(true)で一度だけ開き、無効化される（または
        /// 再度有効化される）まで使い回す。m_lock配下でのみ読み書きすること。</summary>
        private static StreamWriter m_logWriter;

        /// <summary>m_logWriterが内部で使うFileStream。Disposeはm_logWriter経由で行うが、
        /// GCに回収されないよう参照を保持している。</summary>
        private static FileStream m_logStream;

        /// <summary>現在デバッグログが有効かどうか。</summary>
        public static bool IsEnabled => m_enabledFlg;

        /// <summary>ログの保存先（デスクトップの mdelog フォルダ内、
        /// mde_v&lt;バージョン&gt;_pid&lt;プロセスID&gt;.log）のパス文字列を組み立てる。
        /// プロセスIDをファイル名に含めるのは、mdeを複数同時起動して比較する場合に
        /// ログファイルが競合しないようにするため。mdelogフォルダはここでは作成せず、
        /// 実際に有効化されたSetEnabled(true)の中まで遅延する。DebugLoggerの静的
        /// フィールド初期化時に、有効/無効に関わらず必ず一度実行される。失敗した場合は
        /// nullを返し、以後Log()は常に何もしない。</summary>
        private static string BuildLogPath()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "mdelog");
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                int pid = Process.GetCurrentProcess().Id;
                string fileName = $"mde_v{version.Major}.{version.Minor}.{version.Build}_pid{pid}.log";
                return Path.Combine(dir, fileName);
            }
            catch
            {
                return null;
            }
        }

        // WPFのIsKeyboardFocused/FocusedElementはWPFプロセス内部の論理的なフォーカス状態であり、
        // Windows全体のフォアグラウンドウィンドウとは食い違いうる。GetForegroundWindowを
        // P/Invokeで直接呼び、実際のフォアグラウンドウィンドウの所属プロセスを確認する。
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr a_hWnd, out uint a_processId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr a_hWnd, StringBuilder a_className, int a_maxCount);

        /// <summary>
        /// 現在のフォアグラウンドウィンドウ（Windows全体で実際にキー入力を受け取る先）を
        /// 説明する文字列を返す。取得に失敗しても例外を投げず、その旨の文字列を返す。
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

        /// <summary>デバッグログの有効/無効を切り替える。有効化のたびにログファイルを
        /// 新規作成する。無効化時も含め、既存のファイルハンドルは必ず先に閉じる。</summary>
        /// <param name="a_enabledFlg">true＝有効化、false＝無効化。</param>
        public static void SetEnabled(bool a_enabledFlg)
        {
            m_enabledFlg = a_enabledFlg;
            lock (m_lock)
            {
                // 無効化時・再有効化時のどちらでも、それまで開いていたハンドルは必ず閉じる。
                CloseWriterLocked();

                if (!a_enabledFlg || null == m_logPath)
                {
                    return;
                }
                try
                {
                    // フォルダ作成は無効のままなら不要なため、実際に有効化されたこの時点まで遅延する。
                    Directory.CreateDirectory(Path.GetDirectoryName(m_logPath));

                    // FileShare.ReadWrite|Deleteにより、mdeがハンドルを開いたままでも
                    // 他プロセス（テキストエディタでの確認、コピー・削除等）から支障なく
                    // アクセスできるようにする。
                    m_logStream = new FileStream(
                        m_logPath, FileMode.Create, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    m_logWriter = new StreamWriter(m_logStream, new UTF8Encoding(false));
                    m_logWriter.Write(
                        $"=== mde debug log started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                        $"(v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}) ===\r\n");
                    m_logWriter.Flush();
                }
                catch
                {
                    // 書き込みに失敗しても致命的ではない（次回のLog()呼び出しも同様に無視される）。
                    CloseWriterLocked();
                }
            }
        }

        /// <summary>
        /// 1行、経過時間・スレッドID付きでログファイルに追記する。無効時、または書き込みに
        /// 失敗した場合は何もしない。SetEnabled(true)で開いたハンドル（m_logWriter）を
        /// 使い回すことで毎回の開閉コストを避けている。アプリが直後に固まっても記録を
        /// 確実に残す必要があるため、Flush()は毎回呼ぶ。
        /// </summary>
        /// <param name="a_message">記録するメッセージ。</param>
        public static void Log(string a_message)
        {
            if (!m_enabledFlg || null == m_logPath)
            {
                return;
            }
            try
            {
                string line = $"[{m_stopwatch.Elapsed.TotalMilliseconds,9:0.0}ms] [T{Thread.CurrentThread.ManagedThreadId}] {a_message}\r\n";
                lock (m_lock)
                {
                    if (null == m_logWriter)
                    {
                        return;
                    }
                    m_logWriter.Write(line);
                    m_logWriter.Flush();
                }
            }
            catch
            {
                // ログ書き込みの失敗でアプリの動作に影響を与えないよう、無視する。
            }
        }

        /// <summary>開いたままのログファイルハンドル（あれば）を閉じる。呼び出し前に
        /// m_lockを取得しておくこと。</summary>
        private static void CloseWriterLocked()
        {
            try
            {
                m_logWriter?.Dispose();
            }
            catch
            {
                // 破棄時の失敗でアプリの動作に影響を与えないよう、無視する。
            }
            m_logWriter = null;
            m_logStream = null;
        }
    }
}
