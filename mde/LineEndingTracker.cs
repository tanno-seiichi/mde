// LineEndingTracker.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 各ファイルの改行コード（CRLF/LF）を検出・記憶し、保存時に元の改行コードを維持するための
// クラス。SearchReplaceService（ファイル読み込み時）とMainWindow（保存時）の両方から
// 共有される協力オブジェクトとして使う。

using System.Collections.Generic;

namespace mde
{
    /// <summary>各ファイルパスに対して、検出された改行コードのスタイルを記憶する。</summary>
    public class LineEndingTracker
    {
        private readonly Dictionary<string, string> lineEndings = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        private readonly System.Func<string, string, bool> pathsReferToSameFile;

        /// <summary>
        /// LineEndingTrackerを構築する。
        /// </summary>
        /// <param name="pathsReferToSameFile">2つのパスが同一ファイルを指すかどうかを判定するdelegate。</param>
        public LineEndingTracker(System.Func<string, string, bool> pathsReferToSameFile)
        {
            this.pathsReferToSameFile = pathsReferToSameFile;
        }

        /// <summary>ファイル内容からCRLF/LFのどちらが主に使われているかを検出し、記憶する。</summary>
        /// <param name="path">ファイルパス。</param>
        /// <param name="content">読み込んだファイル内容。</param>
        public void DetectAndRemember(string path, string content)
        {
            lineEndings[path] = Detect(content);
        }

        /// <summary>ファイル内容の改行コードを判定する（曖昧・空の場合はWindowsで一般的な
        /// CRLFを既定値とする）。</summary>
        /// <param name="content">判定対象の内容。</param>
        /// <returns>"\r\n" または "\n"。</returns>
        public string Detect(string content)
        {
            if (string.IsNullOrEmpty(content)) return "\r\n";
            int crlfCount = 0, lfOnlyCount = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] != '\n') continue;
                if (i > 0 && content[i - 1] == '\r') crlfCount++;
                else lfOnlyCount++;
            }
            if (crlfCount == 0 && lfOnlyCount == 0) return "\r\n";
            return crlfCount >= lfOnlyCount ? "\r\n" : "\n";
        }

        /// <summary>以前に記憶した改行コードを取得する（未知のファイルなら既定値の "\r\n"）。</summary>
        /// <param name="path">ファイルパス。</param>
        public string GetFor(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var kv in lineEndings)
                    if (pathsReferToSameFile(kv.Key, path)) return kv.Value;
            }
            return "\r\n";
        }

        /// <summary>特定のファイルパスに対して、改行コードを明示的に設定する（例:
        /// 名前を付けて保存の際、元ファイルのスタイルを引き継ぐ場合）。</summary>
        public void SetFor(string path, string lineEnding)
        {
            lineEndings[path] = lineEnding;
        }

        /// <summary>内部的に常に "\n" を使っているMarkDown文字列を、指定した改行コードに
        /// 変換する（ファイル書き込み直前に使う）。</summary>
        /// <param name="text">"\n" 区切りのテキスト。</param>
        /// <param name="lineEnding">適用する改行コード。</param>
        public string Apply(string text, string lineEnding)
        {
            string normalized = text.Replace("\r\n", "\n");
            return lineEnding == "\n" ? normalized : normalized.Replace("\n", lineEnding);
        }
    }
}
