// PdfPrintToFile.cs
//
// mde (Markdown インラインエディタ) の一部。
// 「Microsoft Print to PDF」に、保存先を聞かせずに、決めたファイルへ PDF を書き出させる（PDF への書き出し）。

using System;
using System.ComponentModel;
using System.IO;
using System.IO.Packaging;
using System.Printing;
using System.Runtime.InteropServices;
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
    /// そこで、mde が自分の画面で保存先を聞き、文書をいったん XPS ファイルに書き出してから、Windows の印刷の機能
    /// （winspool。StartDocPrinter の出力先ファイル pOutputFile と、XPS をそのままプリンターへ渡すデータ形式 XPS_PASS）で
    /// プリンターへ渡す。出力先ファイルを指定した印刷では、プリンターは保存先を聞かない。
    /// </summary>
    public static class PdfPrintToFile
    {
        /// <summary>「Microsoft Print to PDF」のプリンター名・ドライバー名（Windows の表示言語によらず同じ）。</summary>
        private const string MICROSOFT_PRINT_TO_PDF = "Microsoft Print to PDF";

        /// <summary>XPS をそのままプリンターへ渡すデータ形式（XPS のドライバーが対応する）。</summary>
        private const string XPS_PASS_DATATYPE = "XPS_PASS";

        /// <summary>一度に WritePrinter へ渡す大きさ（バイト）。</summary>
        private const int WRITE_CHUNK_SIZE = 1024 * 1024;

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
        /// PDF ファイルは、この処理が戻った後に、Windows の印刷の処理（スプーラー）が作る。
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

        /// <summary>XPS ファイルを、出力先ファイルを指定してプリンターへ渡す（XPS_PASS）。</summary>
        /// <param name="a_printerName">プリンター名。</param>
        /// <param name="a_jobName">印刷ジョブの名前。</param>
        /// <param name="a_outputPath">出力先ファイル。</param>
        /// <param name="a_xpsPath">渡す XPS ファイル。</param>
        private static void SendXpsToPrinter(string a_printerName, string a_jobName, string a_outputPath, string a_xpsPath)
        {
            if (!OpenPrinter(a_printerName, out IntPtr printer, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                var docInfo = new DOC_INFO_1
                {
                    pDocName = a_jobName,
                    pOutputFile = a_outputPath,
                    pDatatype = XPS_PASS_DATATYPE
                };
                if (0 == StartDocPrinter(printer, 1, docInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                bool docCompletedFlg = false;
                try
                {
                    if (!StartPagePrinter(printer))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    using (var stream = new FileStream(a_xpsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var buffer = new byte[WRITE_CHUNK_SIZE];
                        int read;
                        while (0 < (read = stream.Read(buffer, 0, buffer.Length)))
                        {
                            int offset = 0;
                            while (offset < read)
                            {
                                byte[] chunk = buffer;
                                if (0 != offset)
                                {
                                    chunk = new byte[read - offset];
                                    Array.Copy(buffer, offset, chunk, 0, chunk.Length);
                                }
                                if (!WritePrinter(printer, chunk, read - offset, out int written) || written <= 0)
                                {
                                    throw new Win32Exception(Marshal.GetLastWin32Error());
                                }
                                offset += written;
                            }
                        }
                    }
                    EndPagePrinter(printer);
                    docCompletedFlg = true;
                }
                finally
                {
                    if (!docCompletedFlg)
                    {
                        // 途中で失敗した時は、作りかけのジョブを取り消す（EndDocPrinter で閉じると、壊れた PDF ができることがある）
                        AbortPrinter(printer);
                    }
                    else
                    {
                        EndDocPrinter(printer);
                    }
                }
            }
            finally
            {
                ClosePrinter(printer);
            }
        }

        /// <summary>StartDocPrinter に渡す、印刷ジョブの情報（DOC_INFO_1）。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class DOC_INFO_1
        {
            public string pDocName;
            public string pOutputFile;
            public string pDatatype;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool OpenPrinter(string a_printerName, out IntPtr a_printer, IntPtr a_default);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int StartDocPrinter(IntPtr a_printer, int a_level, [In] DOC_INFO_1 a_docInfo);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr a_printer);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr a_printer, byte[] a_buffer, int a_count, out int a_written);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr a_printer);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr a_printer);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool AbortPrinter(IntPtr a_printer);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr a_printer);
    }
}
