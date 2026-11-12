// InlineStyles.cs
//
// mde (Markdown インラインエディタ) の一部。
// 文中の書式（太字・斜体・取り消し線・下線・ハイライト・インラインコード・エスケープした文字）を、1つの Run に
// 重ねて持たせるための共通処理（書式の重ねがけ。mdew と同じく「~~**太字の取り消し線**~~」のように書ける）。
//
// 書式は Run.Tag に文字列で持たせる。1つだけの時は以前と同じ名前（"bold"・"italic"・"italic_"・"strikethrough"・
// "underline"・"highlight"・"inline-code"・"escaped"）、2つ以上の時は「+」でつないだもの（"bold+strikethrough" 等。
// 並びは s_names の順）。1つだけの時の形が以前と同じなので、Tag をそのまま写す処理（Run の分割等）はそのまま使える。
// リンクの Run は Tag が LinkInfo なので、リンクに重ねた書式は LinkInfo.Styles に同じ形の文字列で持たせる。

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace mde.common
{
    /// <summary>文中の書式（重ねて持てる）。</summary>
    [Flags]
    public enum InlineStyleFlags
    {
        None = 0,
        /// <summary>太字（**…**）。</summary>
        Bold = 1,
        /// <summary>斜体（*…*）。</summary>
        Italic = 2,
        /// <summary>斜体（_…_。保存時に _ で書き戻す）。</summary>
        ItalicUnderscore = 4,
        /// <summary>取り消し線（~~…~~）。</summary>
        Strikethrough = 8,
        /// <summary>下線（&lt;u&gt;…&lt;/u&gt;）。</summary>
        Underline = 16,
        /// <summary>ハイライト（==…==）。</summary>
        Highlight = 32,
        /// <summary>インラインコード（`…`）。</summary>
        Code = 64,
        /// <summary>\ でエスケープした文字（保存時に \ を付けて書き戻す。1文字ずつの Run）。</summary>
        Escaped = 128,
    }

    /// <summary>文中の書式の読み書きと、見た目の設定。</summary>
    public static class InlineStyles
    {
        /// <summary>重ねがけできる書式（記号で囲む書式）。</summary>
        public const InlineStyleFlags STACKABLE_MASK = InlineStyleFlags.Bold | InlineStyleFlags.Italic | InlineStyleFlags.ItalicUnderscore |
            InlineStyleFlags.Strikethrough | InlineStyleFlags.Underline | InlineStyleFlags.Highlight;

        /// <summary>斜体（* と _ のどちらか）。</summary>
        public const InlineStyleFlags ANY_ITALIC = InlineStyleFlags.Italic | InlineStyleFlags.ItalicUnderscore;

        /// <summary>書式と Tag の名前の対応（Tag の文字列の並び順でもある）。</summary>
        private static readonly (InlineStyleFlags Flag, string Name)[] s_names =
        {
            (InlineStyleFlags.Bold, "bold"),
            (InlineStyleFlags.Italic, "italic"),
            (InlineStyleFlags.ItalicUnderscore, "italic_"),
            (InlineStyleFlags.Strikethrough, "strikethrough"),
            (InlineStyleFlags.Underline, "underline"),
            (InlineStyleFlags.Highlight, "highlight"),
            (InlineStyleFlags.Code, "inline-code"),
            (InlineStyleFlags.Escaped, "escaped"),
        };

        /// <summary>Tag の文字列から書式を作った結果の記憶（同じ文字列を何度も分けないため）。</summary>
        private static readonly Dictionary<string, InlineStyleFlags> s_parseCache = new Dictionary<string, InlineStyleFlags>();

        /// <summary>書式から Tag の文字列を作った結果の記憶。</summary>
        private static readonly Dictionary<InlineStyleFlags, string> s_tagCache = new Dictionary<InlineStyleFlags, string>();

        /// <summary>取り消し線・下線の組み合わせごとの TextDecorations の記憶。</summary>
        private static readonly Dictionary<(bool Link, bool Strike, bool Under), TextDecorationCollection> s_decorationCache =
            new Dictionary<(bool, bool, bool), TextDecorationCollection>();

        /// <summary>Tag（文字列）から書式を読む。書式の文字列でない時（null・LinkInfo 等・知らない名前）は None。</summary>
        /// <param name="a_tag">Run.Tag（または LinkInfo.Styles）。</param>
        /// <returns>書式。</returns>
        public static InlineStyleFlags Parse(object a_tag)
        {
            if (!(a_tag is string text) || 0 == text.Length)
            {
                return InlineStyleFlags.None;
            }
            lock (s_parseCache)
            {
                if (s_parseCache.TryGetValue(text, out InlineStyleFlags cached))
                {
                    return cached;
                }
                InlineStyleFlags flags = InlineStyleFlags.None;
                foreach (string part in text.Split('+'))
                {
                    foreach (var (flag, name) in s_names)
                    {
                        if (name == part)
                        {
                            flags |= flag;
                            break;
                        }
                    }
                }
                s_parseCache[text] = flags;
                return flags;
            }
        }

        /// <summary>書式から Tag の文字列を作る（書式が無い時は null）。</summary>
        /// <param name="a_flags">書式。</param>
        /// <returns>Tag の文字列。</returns>
        public static string ToTag(InlineStyleFlags a_flags)
        {
            if (InlineStyleFlags.None == a_flags)
            {
                return null;
            }
            lock (s_tagCache)
            {
                if (s_tagCache.TryGetValue(a_flags, out string cached))
                {
                    return cached;
                }
                var sb = new StringBuilder();
                foreach (var (flag, name) in s_names)
                {
                    if (0 != (a_flags & flag))
                    {
                        if (0 < sb.Length)
                        {
                            sb.Append('+');
                        }
                        sb.Append(name);
                    }
                }
                string tag = sb.ToString();
                s_tagCache[a_flags] = tag;
                return tag;
            }
        }

        /// <summary>Run の書式を読む（リンクの Run は LinkInfo.Styles から）。</summary>
        /// <param name="a_run">Run。</param>
        /// <returns>書式。</returns>
        public static InlineStyleFlags Get(Run a_run)
        {
            return a_run.Tag is LinkInfo li ? Parse(li.Styles) : Parse(a_run.Tag);
        }

        /// <summary>Run が書式の Run（Tag が null か書式の文字列で、リンク・アンカー等ではない）か。</summary>
        /// <param name="a_run">Run。</param>
        /// <returns>書式の Run なら true。</returns>
        public static bool IsStyleRun(Run a_run)
        {
            return null == a_run.Tag || (a_run.Tag is string text && (0 == text.Length || InlineStyleFlags.None != Parse(text)));
        }

        /// <summary>右クリック「文字装飾」・ショートカットの書式の名前を書式にする。</summary>
        /// <param name="a_style">"bold"・"italic"・"italic_"・"strikethrough"・"underline"・"highlight"・"code"。</param>
        /// <returns>書式（知らない名前は None）。</returns>
        public static InlineStyleFlags FromStyleName(string a_style)
        {
            switch (a_style)
            {
                case "bold": return InlineStyleFlags.Bold;
                case "italic": return InlineStyleFlags.Italic;
                case "italic_": return InlineStyleFlags.ItalicUnderscore;
                case "strikethrough": return InlineStyleFlags.Strikethrough;
                case "underline": return InlineStyleFlags.Underline;
                case "highlight": return InlineStyleFlags.Highlight;
                case "code":
                case "inline-code": return InlineStyleFlags.Code;
                default: return InlineStyleFlags.None;
            }
        }

        /// <summary>書式付きの新しい Run を作る（Tag と見た目を設定する）。</summary>
        /// <param name="a_text">文字。</param>
        /// <param name="a_flags">書式。</param>
        /// <returns>新しい Run。</returns>
        public static Run CreateRun(string a_text, InlineStyleFlags a_flags)
        {
            var run = new Run(a_text) { Tag = ToTag(a_flags) };
            ApplyVisual(run, a_flags);
            return run;
        }

        /// <summary>
        /// 書式の見た目を Run に設定する（設定するだけで、書式に無いものは消さない。新しく作った Run に使う）。
        /// インラインコードは、コードの見た目（BlockStyles.ApplyInlineCodeStyle）に、太字・斜体・取り消し線・下線を
        /// 重ねる（ハイライトの背景は、コードの背景を優先して付けない）。
        /// </summary>
        /// <param name="a_run">Run。</param>
        /// <param name="a_flags">書式。</param>
        public static void ApplyVisual(Run a_run, InlineStyleFlags a_flags)
        {
            bool codeFlg = 0 != (a_flags & InlineStyleFlags.Code);
            if (codeFlg)
            {
                BlockStyles.ApplyInlineCodeStyle(a_run);
            }
            ApplyStackableVisual(a_run, a_flags, false, codeFlg);
        }

        /// <summary>リンクの Run に、重ねた書式（LinkInfo.Styles）の見た目を設定する（リンクの色・下線はそのまま）。</summary>
        /// <param name="a_run">リンクの Run。</param>
        /// <param name="a_flags">書式。</param>
        public static void ApplyLinkVisual(Run a_run, InlineStyleFlags a_flags)
        {
            ApplyStackableVisual(a_run, a_flags, true, false);
        }

        /// <summary>太字・斜体・取り消し線・下線・ハイライトの見た目を設定する。</summary>
        /// <param name="a_run">Run。</param>
        /// <param name="a_flags">書式。</param>
        /// <param name="a_linkFlg">リンクの Run か（下線はリンクの下線と重ねる）。</param>
        /// <param name="a_codeFlg">インラインコードの Run か（斜体のフォントとハイライトの背景を変えない）。</param>
        private static void ApplyStackableVisual(Run a_run, InlineStyleFlags a_flags, bool a_linkFlg, bool a_codeFlg)
        {
            if (0 != (a_flags & InlineStyleFlags.Bold))
            {
                a_run.FontWeight = BlockStyles.BoldFontWeight;
            }
            if (0 != (a_flags & ANY_ITALIC))
            {
                if (a_codeFlg)
                {
                    a_run.FontStyle = FontStyles.Italic;
                }
                else
                {
                    BlockStyles.ApplyItalicStyle(a_run);
                }
            }
            bool strikeFlg = 0 != (a_flags & InlineStyleFlags.Strikethrough);
            bool underFlg = 0 != (a_flags & InlineStyleFlags.Underline);
            if (strikeFlg || underFlg)
            {
                a_run.TextDecorations = GetDecorations(a_linkFlg, strikeFlg, underFlg);
            }
            if (0 != (a_flags & InlineStyleFlags.Highlight) && !a_codeFlg)
            {
                a_run.Background = BlockStyles.HighlightBrush;
            }
        }

        /// <summary>取り消し線・下線（とリンクの下線）を重ねた TextDecorations を返す。</summary>
        /// <param name="a_linkFlg">リンクの下線（BlockStyles.LinkTextDecorations）も含めるか。</param>
        /// <param name="a_strikeFlg">取り消し線を含めるか。</param>
        /// <param name="a_underFlg">下線を含めるか。</param>
        /// <returns>TextDecorations（固定済み）。</returns>
        private static TextDecorationCollection GetDecorations(bool a_linkFlg, bool a_strikeFlg, bool a_underFlg)
        {
            // リンクの下線は表示スタイルで変わるため、リンクの時は記憶しない
            if (!a_linkFlg)
            {
                lock (s_decorationCache)
                {
                    if (s_decorationCache.TryGetValue((false, a_strikeFlg, a_underFlg), out var cached))
                    {
                        return cached;
                    }
                }
            }
            var collection = new TextDecorationCollection();
            if (a_linkFlg && null != BlockStyles.LinkTextDecorations)
            {
                collection.Add(BlockStyles.LinkTextDecorations);
            }
            if (a_strikeFlg)
            {
                collection.Add(TextDecorations.Strikethrough);
            }
            if (a_underFlg && !(a_linkFlg && 0 < collection.Count && !a_strikeFlg))
            {
                collection.Add(TextDecorations.Underline);
            }
            collection.Freeze();
            if (!a_linkFlg)
            {
                lock (s_decorationCache)
                {
                    s_decorationCache[(false, a_strikeFlg, a_underFlg)] = collection;
                }
            }
            return collection;
        }

        /// <summary>書式の Markdown の開きの記号。</summary>
        /// <param name="a_flag">書式（1つ）。</param>
        /// <returns>記号。</returns>
        public static string OpenMarker(InlineStyleFlags a_flag)
        {
            switch (a_flag)
            {
                case InlineStyleFlags.Bold: return "**";
                case InlineStyleFlags.Italic: return "*";
                case InlineStyleFlags.ItalicUnderscore: return "_";
                case InlineStyleFlags.Strikethrough: return "~~";
                case InlineStyleFlags.Underline: return "<u>";
                case InlineStyleFlags.Highlight: return "==";
                default: return "";
            }
        }

        /// <summary>書式の Markdown の閉じの記号。</summary>
        /// <param name="a_flag">書式（1つ）。</param>
        /// <returns>記号。</returns>
        public static string CloseMarker(InlineStyleFlags a_flag)
        {
            return InlineStyleFlags.Underline == a_flag ? "</u>" : OpenMarker(a_flag);
        }

        /// <summary>重ねがけできる書式を、Tag の並び順（外側に書く順）に1つずつ返す。</summary>
        public static readonly InlineStyleFlags[] STACKABLE_ORDER =
        {
            InlineStyleFlags.Bold,
            InlineStyleFlags.Italic,
            InlineStyleFlags.ItalicUnderscore,
            InlineStyleFlags.Strikethrough,
            InlineStyleFlags.Underline,
            InlineStyleFlags.Highlight,
        };
    }
}
