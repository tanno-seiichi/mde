// PdfPrintToFile.cs
//
// mde (Markdown インラインエディタ) の一部。
// 「Microsoft Print to PDF」に、保存先を聞かせずに、決めたファイルへ PDF を書き出させる（PDF への書き出し）。

using System;
using System.IO;
using System.IO.Packaging;
using System.Printing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Documents;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;

namespace mde.common
{
    /// <summary>
    /// 「Microsoft Print to PDF」に、保存先を聞かせずに、決めたファイルへ PDF を書き出させる。
    /// WPF の印刷（PrintDialog.PrintDocument）で「Microsoft Print to PDF」へ印刷すると、プリンター側が保存先を選ぶ画面を出す。
    /// その画面は、アプリから初期フォルダもファイル名も指定できない（Windows がアプリごとに覚えている、最後にファイルの画面で
    /// 使ったフォルダが開くため、.md をダブルクリックして開いた時などは、前回の作業フォルダが開いていた）。
    /// そこで、mde が自分の画面で保存先を聞き、文書をいったん XPS ファイルに書き出してから、Windows の XPS の印刷の機能
    /// （XpsPrint の StartXpsPrintJob。出力先ファイルを指定できる）でプリンターへ渡す。出力先ファイルを指定した印刷では、
    /// プリンターは保存先を聞かない。
    /// <para>
    /// 以前は、winspool（StartDocPrinter の出力先ファイル pOutputFile と、XPS をそのままプリンターへ渡すデータ形式 XPS_PASS）で
    /// 渡していたが、PDF に変換されずに XPS のまま「.pdf」のファイルに書き込まれ、開けない PDF ができるようになった
    /// （2026年11月。Windows 側の変更によるものと思われる）。XpsPrint で渡すと、PDF に変換される。
    /// </para>
    /// </summary>
    public static class PdfPrintToFile
    {
        /// <summary>「Microsoft Print to PDF」のプリンター名・ドライバー名（Windows の表示言語によらず同じ）。</summary>
        private const string MICROSOFT_PRINT_TO_PDF = "Microsoft Print to PDF";

        /// <summary>一度に XpsPrint へ渡す大きさ（バイト）。</summary>
        private const int WRITE_CHUNK_SIZE = 1024 * 1024;

        /// <summary>プリンターが PDF を書き終えるのを待つ時間の上限（大きな文書でも数十秒で終わるため、十分に長くしてある）。</summary>
        private static readonly TimeSpan PRINT_COMPLETION_TIMEOUT = TimeSpan.FromMinutes(10);

        /// <summary>選んだプリンターが「Microsoft Print to PDF」かを調べる（名前を変えたプリンターでも、ドライバーで分かる）。</summary>
        /// <param name="a_queue">印刷ダイアログで選んだプリンター。</param>
        /// <returns>「Microsoft Print to PDF」なら true。</returns>
        public static bool IsMicrosoftPrintToPdf(PrintQueue a_queue)
        {
            if (null == a_queue)
            {
                return false;
            }
            try
            {
                if (string.Equals(a_queue.FullName, MICROSOFT_PRINT_TO_PDF, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                return string.Equals(a_queue.QueueDriver?.Name, MICROSOFT_PRINT_TO_PDF, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // プリンターの情報を取れない時は、「Microsoft Print to PDF」ではないものとして扱う（これまでどおりの印刷にする）
                return false;
            }
        }

        /// <summary>
        /// 文書を、プリンター（「Microsoft Print to PDF」）で、指定したファイルへ書き出す。
        /// 文書はいったん一時フォルダの XPS ファイルに書き出す（ページを1枚ずつ求めるため、a_paginator の GetPage が呼ばれる）。
        /// プリンターが PDF のファイルを書き終えるまで待ってから戻る。
        /// </summary>
        /// <param name="a_queue">プリンター。</param>
        /// <param name="a_printTicket">用紙等の設定（印刷ダイアログの PrintTicket）。</param>
        /// <param name="a_paginator">書き出す文書のページ。</param>
        /// <param name="a_jobName">印刷ジョブの名前。</param>
        /// <param name="a_outputPath">書き出す PDF ファイルの絶対パス。</param>
        public static void Print(PrintQueue a_queue, PrintTicket a_printTicket, DocumentPaginator a_paginator, string a_jobName, string a_outputPath)
        {
            string xpsPath = Path.Combine(Path.GetTempPath(), "mde_" + Guid.NewGuid().ToString("N") + ".xps");
            try
            {
                using (var xpsDocument = new XpsDocument(xpsPath, FileAccess.ReadWrite, CompressionOption.Normal))
                {
                    XpsDocumentWriter writer = XpsDocument.CreateXpsDocumentWriter(xpsDocument);
                    if (null != a_printTicket)
                    {
                        writer.Write(a_paginator, a_printTicket);
                    }
                    else
                    {
                        writer.Write(a_paginator);
                    }
                }
                SendXpsToPrinter(a_queue.FullName, a_jobName, a_outputPath, xpsPath);
            }
            finally
            {
                try
                {
                    File.Delete(xpsPath);
                }
                catch (Exception)
                {
                    // 一時ファイルを消せなくても、書き出し自体には支障がないため無視する
                }
            }
        }

        /// <summary>
        /// XPS ファイルを、出力先ファイルを指定してプリンターへ渡し（XpsPrint の StartXpsPrintJob）、プリンターが書き終えるのを待つ。
        /// 用紙等の設定は、XPS ファイルの中の PrintTicket（Print で書き込んだもの）が使われる。
        /// </summary>
        /// <param name="a_printerName">プリンター名。</param>
        /// <param name="a_jobName">印刷ジョブの名前。</param>
        /// <param name="a_outputPath">出力先ファイル。</param>
        /// <param name="a_xpsPath">渡す XPS ファイル。</param>
        private static void SendXpsToPrinter(string a_printerName, string a_jobName, string a_outputPath, string a_xpsPath)
        {
            using var completionEvent = new ManualResetEvent(false);
            int hr = StartXpsPrintJob(a_printerName, a_jobName, a_outputPath, IntPtr.Zero,
                completionEvent.SafeWaitHandle.DangerousGetHandle(), null, 0,
                out IXpsPrintJob job, out IXpsPrintJobStream documentStream, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hr);
            bool sentFlg = false;
            try
            {
                using (var stream = new FileStream(a_xpsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buffer = new byte[WRITE_CHUNK_SIZE];
                    int read;
                    while (0 < (read = stream.Read(buffer, 0, buffer.Length)))
                    {
                        documentStream.Write(buffer, (uint)read, out uint _);
                    }
                }
                // 閉じると、プリンターが書き出しを始める（閉じるまでは、ジョブは終わらない）
                documentStream.Close();
                sentFlg = true;
                if (!completionEvent.WaitOne(PRINT_COMPLETION_TIMEOUT))
                {
                    throw new TimeoutException("Print to PDF did not finish.");
                }
                job.GetJobStatus(out XPS_JOB_STATUS status);
                if (XPS_JOB_COMPLETED != status.completion)
                {
                    Marshal.ThrowExceptionForHR(status.jobStatus);
                    throw new InvalidOperationException("Print to PDF failed (" + status.completion + ").");
                }
            }
            catch
            {
                if (!sentFlg)
                {
                    // 途中で失敗した時は、作りかけのジョブを取り消す
                    try
                    {
                        job.Cancel();
                    }
                    catch (Exception)
                    {
                        // 取り消せなくても、元の失敗を伝える
                    }
                }
                throw;
            }
            finally
            {
                if (OperatingSystem.IsWindows())
                {
                    Marshal.ReleaseComObject(documentStream);
                    Marshal.ReleaseComObject(job);
                }
            }
        }

        /// <summary>XPS_JOB_COMPLETION の、ジョブが終わったことを表す値（XPS_JOB_COMPLETED）。</summary>
        private const int XPS_JOB_COMPLETED = 1;

        /// <summary>XpsPrint のジョブの状態（XPS_JOB_STATUS）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct XPS_JOB_STATUS
        {
            public uint jobId;
            public int currentDocument;
            public int currentPage;
            public int currentPageTotal;
            public int completion;
            public int jobStatus;
        }

        /// <summary>XpsPrint のジョブ（IXpsPrintJob）。</summary>
        [ComImport, Guid("5ab89b06-8194-425f-ab3b-d7a96e350161"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IXpsPrintJob
        {
            void Cancel();
            void GetJobStatus(out XPS_JOB_STATUS a_status);
        }

        /// <summary>XpsPrint のジョブへ XPS を渡すストリーム（IXpsPrintJobStream。ISequentialStream の Read・Write と Close）。</summary>
        [ComImport, Guid("7a77dc5f-45d6-4dff-9307-d8cb846347ca"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IXpsPrintJobStream
        {
            void Read([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] a_buffer, uint a_count, out uint a_read);
            void Write([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] a_buffer, uint a_count, out uint a_written);
            void Close();
        }

        [DllImport("XpsPrint.dll", CharSet = CharSet.Unicode)]
        private static extern int StartXpsPrintJob(string a_printerName, string a_jobName, string a_outputFileName,
            IntPtr a_progressEvent, IntPtr a_completionEvent, byte[] a_printablePagesOn, uint a_printablePagesOnCount,
            out IXpsPrintJob a_xpsPrintJob, out IXpsPrintJobStream a_documentStream, IntPtr a_printTicketStream);
    }
}
