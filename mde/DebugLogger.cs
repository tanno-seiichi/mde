// DebugLogger.cs
//
// mde (Markdown インラインエディタ) の一部。
// IME固まり不具合の調査用に追加した、簡易なデバッグログ出力。この不具合は開発側の環境
// （Windows/WPFの実行環境）では再現手順を試せないため、実際に何が・いつ起きているかを
// 時系列でファイルに書き出し、後から読めるようにする。
//
// 【使い方】メニュー「表示」→「デバッグログを有効にする」で有効化してから症状を再現させ、
// デスクトップの mdelog フォルダに書き出される mde_v<バージョン>_pid<プロセスID>.log を
// 開いて内容を共有してください（有効化するたびに新しく書き出し直されるので、1回の再現に
// つき1回分の記録になります。ファイル名にプロセスIDが入っているのは、mdeを同時に複数
// 起動して比較する場合に、それぞれのログが同じファイルへ混ざって書き込まれないように
// するため）。既定では無効で、この設定は次回起動時にも復元される（AppSettings参照）。
//
// 通常の動作に影響を与えないよう、ファイルへの書き込みはすべてtry/catchで囲んであり、
// 書き込みに失敗してもアプリの動作は継続する。無効時はLog()が即returnするだけの、
// ほぼ無コストな早期returnになる（文字入力のたびに呼ばれても、ディスクI/Oは一切発生しない）。
//
// 2026-09追記（もっさり感の追加調査）：有効時のLog()は、以前はFile.AppendAllTextで
// 1行ごとにファイルを開く→書き込む→閉じる、を毎回繰り返していた。実機のログ（本ログ
// 機構自身が生成したもの）を分析したところ、「入力を続けるほど、キー入力からの反映が
// 徐々に遅くなる」症状の主な原因がこれだったと判明した。File.AppendAllTextの開く→
// 閉じるという操作自体のコストが、ログファイルが大きくなるにつれて増えていく（環境に
// よってはウイルス対策ソフトの常駐スキャンがファイルを開く/閉じるたびに再スキャンする
// ことなどが疑われる）ため、デバッグログを有効にして長時間使うほど、Log()の呼び出し
// コスト自体がじわじわ重くなっていた。SetEnabled(true)の時点でファイルを一度だけ開き、
// 無効化されるかSetEnabled(true)が再度呼ばれるまで同じハンドルを使い回す方式に変更する
// ことで、この「開く→閉じる」の繰り返しを無くした。なお、この不具合調査用のログは
// アプリが実際に固まった瞬間の直前までの記録を確実に残す必要があるため、Log()の中で
// 毎回Flush()を呼び、書き込みをその都度確実にディスクへ反映させる点は変更していない。
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
        private static readonly object m_lock = new object();
        private static readonly string m_logPath = BuildLogPath();
        private static readonly Stopwatch m_stopwatch = Stopwatch.StartNew();
        private static bool m_enabledFlg = false;

        /// <summary>有効化されている間、ログファイルへ書き込み続けるための、開きっぱなしの
        /// StreamWriter（内部でm_logStreamを保持）。Log()を呼ぶたびにファイルを開き直す
        /// コストを避けるため、SetEnabled(true)で一度だけ開き、無効化される（または
        /// 再度有効化される）までそのまま使い回す。m_lock配下でのみ読み書きすること。</summary>
        private static StreamWriter m_logWriter;

        /// <summary>m_logWriterが内部で使っているFileStream。Disposeの対象はm_logWriter
        /// 経由で行うため、こちらは直接は使わないが、参照を保持しておかないとGCの対象に
        /// なりうるため保持している。</summary>
        private static FileStream m_logStream;

        /// <summary>現在デバッグログが有効かどうか。</summary>
        public static bool IsEnabled => m_enabledFlg;

        /// <summary>ログの保存先（デスクトップの mdelog フォルダ内、
        /// mde_v&lt;バージョン&gt;_pid&lt;プロセスID&gt;.log）のパス文字列を組み立てる。
        /// ファイル名にプロセスIDを含めているのは、mdeを同時に複数起動して比較する場合
        /// （バージョン情報を見比べる、修正前後の挙動を見比べる等）に、それぞれの
        /// プロセスが同じログファイルへ同時に書き込んでしまうのを避けるため。以前は
        /// バージョン番号だけをファイル名にしていたため、2つのプロセスで同時にログを
        /// 有効にすると、両方が同じファイルへ交互に書き込み、記録内容が互いに混ざって
        /// しまい、どちらのプロセスの記録か判別できなくなる・書き込みが重なった瞬間に
        /// 失敗する、といった問題があった（書き込み自体はtry/catchで保護されているため
        /// アプリの動作に影響はないが、ログの中身が信用できなくなってしまう）。
        /// ここではまだmdelogフォルダを作成しない（「デバッグログを有効にする」がオフの
        /// ままの利用者の環境に、使われないmdelogフォルダを作ってしまわないようにする
        /// ため）。このメソッドはアプリ起動時、DebugLoggerの静的フィールド初期化の
        /// タイミングで、有効/無効に関わらず必ず一度実行される。実際のフォルダ作成は
        /// SetEnabled(true)の中まで遅延する。失敗した場合はnullを返し、以後Log()は
        /// 常に何もしない。</summary>
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
        /// 有効化のたびにその時点からの新しい記録として扱えるようにするため）。無効化する
        /// 場合も含め、まず既存のファイルハンドル（前回有効化していた分）を必ず閉じてから
        /// 処理する。</summary>
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
                    // 「デバッグログを有効にする」がオフのままならmdelogフォルダ自体を
                    // 作らずに済ませるため、フォルダの作成は実際に有効化された、この時点まで
                    // 遅延している（BuildLogPathの説明も参照）。
                    Directory.CreateDirectory(Path.GetDirectoryName(m_logPath));

                    // FileShare.ReadWrite|Deleteを指定し、mde自身がこのハンドルを開いた
                    // ままの間も、他のアプリ（テキストエディタでの確認、他プロセスへの共有
                    // 目的でのコピー・移動・削除等）から支障なくアクセスできるようにする。
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
        /// 失敗した場合は何もしない。
        ///
        /// 以前はFile.AppendAllTextで毎回ファイルを開き直して追記していたが、この
        /// 開く→書く→閉じるというサイクル自体のコストが、ログファイルが大きくなるにつれて
        /// 増えていくことが実機ログの分析で判明した（DebugLogger.cs冒頭のコメント参照）。
        /// そのため、SetEnabled(true)で開いたハンドル（m_logWriter）を使い回す方式に
        /// 変更している。IME固まり等の調査用ログという性質上、アプリが直後に固まっても
        /// それまでの記録を確実に残す必要があるため、Flush()は引き続き毎回呼んでいる
        /// （ハンドルを開けっぱなしにしても、書き込み内容がディスクへ反映されるタイミングは
        /// 変えない）。
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
