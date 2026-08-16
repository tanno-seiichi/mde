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
        private readonly Dictionary<string, string> m_lineEndings = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        private readonly System.Func<string, string, bool> m_pathsReferToSameFile;

        /// <summary>
        /// LineEndingTrackerを構築する。
        /// </summary>
        /// <param name="a_pathsReferToSameFile">2つのパスが同一ファイルを指すかどうかを判定するdelegate。</param>
        public LineEndingTracker(System.Func<string, string, bool> a_pathsReferToSameFile)
        {
            this.m_pathsReferToSameFile = a_pathsReferToSameFile;
        }

        /// <summary>ファイル内容からCRLF/LFのどちらが主に使われているかを検出し、記憶する。</summary>
        /// <param name="a_path">ファイルパス。</param>
        /// <param name="a_content">読み込んだファイル内容。</param>
        public void DetectAndRemember(string a_path, string a_content)
        {
            m_lineEndings[a_path] = Detect(a_content);
        }

        /// <summary>ファイル内容の改行コードを判定する（曖昧・空の場合はWindowsで一般的な
        /// CRLFを既定値とする）。</summary>
        /// <param name="a_content">判定対象の内容。</param>
        /// <returns>"\r\n" または "\n"。</returns>
        public string Detect(string a_content)
        {
            if (string.IsNullOrEmpty(a_content)) return "\r\n";
            int crlfCount = 0, lfOnlyCount = 0;
            for (int i = 0; i < a_content.Length; i++)
            {
                if (a_content[i] != '\n') continue;
                if (i > 0 && a_content[i - 1] == '\r') crlfCount++;
                else lfOnlyCount++;
            }
            if (0 == crlfCount && 0 == lfOnlyCount) return "\r\n";
            return crlfCount >= lfOnlyCount ? "\r\n" : "\n";
        }

        /// <summary>以前に記憶した改行コードを取得する（未知のファイルなら既定値の "\r\n"）。</summary>
        /// <param name="a_path">ファイルパス。</param>
        /// <returns>記憶されている改行コード。未知のファイルなら既定値の"\r\n"。</returns>
        public string GetFor(string a_path)
        {
            if (!string.IsNullOrEmpty(a_path))
            {
                foreach (var kv in m_lineEndings)
                    if (m_pathsReferToSameFile(kv.Key, a_path)) return kv.Value;
            }
            return "\r\n";
        }

        /// <summary>特定のファイルパスに対して、改行コードを明示的に設定する（例:
        /// 名前を付けて保存の際、元ファイルのスタイルを引き継ぐ場合）。</summary>
        /// <param name="a_path">対象のファイルパス。</param>
        /// <param name="a_lineEnding">設定する改行コード。</param>
        public void SetFor(string a_path, string a_lineEnding)
        {
            m_lineEndings[a_path] = a_lineEnding;
        }

        /// <summary>内部的に常に "\n" を使っているMarkDown文字列を、指定した改行コードに
        /// 変換する（ファイル書き込み直前に使う）。</summary>
        /// <param name="a_text">"\n" 区切りのテキスト。</param>
        /// <param name="a_lineEnding">適用する改行コード。</param>
        /// <returns>指定した改行コードに変換したテキスト。</returns>
        public string Apply(string a_text, string a_lineEnding)
        {
            string normalized = a_text.Replace("\r\n", "\n");
            return "\n" == a_lineEnding ? normalized : normalized.Replace("\n", a_lineEnding);
        }
    }
}
