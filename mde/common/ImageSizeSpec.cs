// ImageSizeSpec.cs
//
// mde (Markdown インラインエディタ) の一部。
// 画像の大きさの指定（HTML の画像のタグの width・height 属性と、style の width・height・zoom）を読み書きする。

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace mde.common
{
    /// <summary>
    /// 画像の大きさの指定（HTML の画像のタグの width・height 属性と、style の width・height・zoom）を読み書きする。
    /// ・表示：指定のとおりの大きさにする（GetSpecifiedSize。Typora で大きさを変えた画像の style="zoom:50%;" も読む）。
    /// ・右クリックの「画像のサイズ」：元の画像の大きさに対する割合で、大きさを変える（WithWidth。大きさは width 属性で
    /// 　書く。GitHub・VS Code のプレビュー・Typora のどれでも同じ大きさで表示されるため）。mdevc・mdevs と同じ。
    /// 属性の値は、タグに書かれたまま（文字参照を戻さない）で扱う（ImageManager.BuildImageFromHtmlTag の src・alt と同じ）。
    /// </summary>
    public static class ImageSizeSpec
    {
        /// <summary>右クリックの「画像のサイズ」の倍率（%。Typora と同じく、元の画像の大きさに対する割合）。</summary>
        public static readonly int[] PERCENTS = { 25, 33, 50, 67, 80, 100, 150, 200 };

        /// <summary>
        /// HTML のタグの属性の値（書かれたまま）。無ければ null。
        /// </summary>
        /// <param name="a_tag">タグの文字列。</param>
        /// <param name="a_name">属性の名前。</param>
        /// <returns>属性の値。</returns>
        public static string GetAttribute(string a_tag, string a_name)
        {
            if (string.IsNullOrEmpty(a_tag))
            {
                return null;
            }
            Match m = Regex.Match(a_tag, "\\s" + a_name + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s\"'>/]+))", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return null;
            }
            return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        }

        /// <summary>HTML のタグから、属性を取り除く（値の無い属性も）。</summary>
        /// <param name="a_tag">タグの文字列。</param>
        /// <param name="a_name">属性の名前。</param>
        /// <returns>タグの文字列。</returns>
        public static string RemoveAttribute(string a_tag, string a_name)
        {
            return Regex.Replace(a_tag, "\\s+" + a_name + "(?:\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s\"'>/]+))?(?=[\\s/>])", "",
                RegexOptions.IgnoreCase);
        }

        /// <summary>HTML のタグの最後（「&gt;」・「/&gt;」の前）に、属性を加える（値の「"」は文字参照にする）。</summary>
        /// <param name="a_tag">タグの文字列。</param>
        /// <param name="a_name">属性の名前。</param>
        /// <param name="a_value">属性の値（書かれたままの形）。</param>
        /// <returns>タグの文字列。</returns>
        public static string AddAttribute(string a_tag, string a_name, string a_value)
        {
            Match m = Regex.Match(a_tag, "\\s*(/?)>$");
            if (!m.Success)
            {
                return a_tag;
            }
            return a_tag.Substring(0, m.Index) + " " + a_name + "=\"" + (a_value ?? "").Replace("\"", "&quot;") + "\"" +
                (0 < m.Groups[1].Length ? " />" : ">");
        }

        /// <summary>
        /// 画像の HTML のタグ（書かれたまま。ImageInfo.HtmlTag）の参照先を、今の参照先（ImageInfo.OriginalSrc）にしたもの。
        /// 貼り付けた画像を保存した時・別のフォルダへ保存した時は、参照先（OriginalSrc）だけが変わるため。
        /// </summary>
        /// <param name="a_info">画像の情報。</param>
        /// <returns>タグの文字列。</returns>
        public static string CurrentHtmlTag(ImageInfo a_info)
        {
            string tag = a_info.HtmlTag;
            if (string.IsNullOrEmpty(tag))
            {
                // タグを覚えていない（以前の形の情報）：src・alt・style だけで作る
                tag = "<img src=\"" + (a_info.OriginalSrc ?? "") + "\" alt=\"" + (a_info.Alt ?? "") + "\"";
                if (!string.IsNullOrEmpty(a_info.Style))
                {
                    tag += " style=\"" + a_info.Style + "\"";
                }
                return tag + " />";
            }
            string src = a_info.OriginalSrc ?? "";
            if (GetAttribute(tag, "src") == src)
            {
                return tag;
            }
            Match m = Regex.Match(tag, "(\\ssrc\\s*=\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s\"'>/]+)", RegexOptions.IgnoreCase);
            return m.Success
                ? tag.Substring(0, m.Index) + m.Groups[1].Value + "\"" + src.Replace("\"", "&quot;") + "\"" + tag.Substring(m.Index + m.Length)
                : AddAttribute(tag, "src", src);
        }

        /// <summary>
        /// 画像の大きさを変えた後の、画像の HTML のタグ（右クリックの「画像のサイズ」）。それまでの大きさの指定（width・height
        /// 属性と、style の width・height・zoom）は取り除き、他の属性・style はそのまま残す。「![文字](参照先)」の形で書かれて
        /// いた画像は、HTML のタグにする。大きさの指定を外した（a_width が 0）結果、タグに src・alt・title しか残らない時は、
        /// 「![文字](参照先)」の形に戻すため、null を返す。
        /// </summary>
        /// <param name="a_info">画像の情報。</param>
        /// <param name="a_width">幅（px）。0 の時は大きさの指定を外す（元の大きさ）。</param>
        /// <returns>HTML のタグ。「![文字](参照先)」の形にする時は null。</returns>
        public static string WithWidth(ImageInfo a_info, int a_width)
        {
            string tag;
            if ("html" == a_info.Format)
            {
                tag = CurrentHtmlTag(a_info);
            }
            else
            {
                tag = "<img src=\"" + (a_info.OriginalSrc ?? "").Replace("\"", "&quot;") + "\" alt=\"" + (a_info.Alt ?? "").Replace("\"", "&quot;") + "\"";
                if (!string.IsNullOrEmpty(a_info.Title))
                {
                    tag += " title=\"" + a_info.Title.Replace("\"", "&quot;") + "\"";
                }
                tag += " />";
            }
            tag = RemoveAttribute(RemoveAttribute(tag, "width"), "height");
            string style = GetAttribute(tag, "style");
            if (null != style)
            {
                tag = RemoveAttribute(tag, "style");
                string[] rest = style.Split(';').Select(d => d.Trim())
                    .Where(d => 0 < d.Length && !Regex.IsMatch(d, "^(?:width|height|zoom)\\s*:", RegexOptions.IgnoreCase)).ToArray();
                if (0 < rest.Length)
                {
                    tag = AddAttribute(tag, "style", string.Join("; ", rest) + ";");
                }
            }
            if (0 < a_width)
            {
                return AddAttribute(tag, "width", a_width.ToString(CultureInfo.InvariantCulture));
            }
            string others = new[] { "src", "alt", "title" }.Aggregate(tag, (a_rest, a_name) => RemoveAttribute(a_rest, a_name));
            return Regex.IsMatch(others, "^<img\\s*/?>$", RegexOptions.IgnoreCase) ? null : tag;
        }

        /// <summary>
        /// 大きさの指定のとおりの、画像の表示の大きさ（画面の幅に収めるための縮小は含めない）。指定が無ければ、元の画像の
        /// 大きさ。ブラウザーと同じく、style の width・height は、width・height 属性より優先し、片方だけの時は縦横比を保ち、
        /// zoom は両方に掛ける。
        /// </summary>
        /// <param name="a_info">画像の情報（null の時は、元の大きさ）。</param>
        /// <param name="a_naturalWidth">元の画像の幅（px）。</param>
        /// <param name="a_naturalHeight">元の画像の高さ（px）。</param>
        /// <param name="a_availableWidth">画像を置ける幅（px。幅を % で指定した時に使う）。</param>
        /// <returns>表示の大きさ。</returns>
        public static Size GetSpecifiedSize(ImageInfo a_info, double a_naturalWidth, double a_naturalHeight, double a_availableWidth)
        {
            string tag = null != a_info && "html" == a_info.Format ? CurrentHtmlTag(a_info) : null;
            if (null == tag)
            {
                return new Size(a_naturalWidth, a_naturalHeight);
            }
            string style = GetAttribute(tag, "style") ?? "";
            double? width = StyleLength(style, "width", a_availableWidth) ?? AttributeLength(GetAttribute(tag, "width"), a_availableWidth);
            double? height = StyleLength(style, "height", null) ?? AttributeLength(GetAttribute(tag, "height"), null);
            double w;
            double h;
            if (width.HasValue && height.HasValue)
            {
                w = width.Value;
                h = height.Value;
            }
            else if (width.HasValue)
            {
                w = width.Value;
                h = a_naturalHeight * w / a_naturalWidth;
            }
            else if (height.HasValue)
            {
                h = height.Value;
                w = a_naturalWidth * h / a_naturalHeight;
            }
            else
            {
                w = a_naturalWidth;
                h = a_naturalHeight;
            }
            Match zoom = Regex.Match(style, "(?:^|;)\\s*zoom\\s*:\\s*([\\d.]+)\\s*(%?)", RegexOptions.IgnoreCase);
            if (zoom.Success && double.TryParse(zoom.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double z) && 0 < z)
            {
                if (0 < zoom.Groups[2].Length)
                {
                    z /= 100;
                }
                w *= z;
                h *= z;
            }
            return new Size(Math.Max(1, w), Math.Max(1, h));
        }

        /// <summary>
        /// 画像の大きさの指定に当たる、「画像のサイズ」の倍率（元の画像の幅に対する割合が、PERCENTS のどれかと 1px 以内で
        /// 合う時）。合うものが無ければ 0。
        /// </summary>
        /// <param name="a_info">画像の情報。</param>
        /// <param name="a_naturalWidth">元の画像の幅（px）。</param>
        /// <param name="a_naturalHeight">元の画像の高さ（px）。</param>
        /// <param name="a_availableWidth">画像を置ける幅（px）。</param>
        /// <returns>倍率（%）。</returns>
        public static int GetCurrentPercent(ImageInfo a_info, double a_naturalWidth, double a_naturalHeight, double a_availableWidth)
        {
            double width = GetSpecifiedSize(a_info, a_naturalWidth, a_naturalHeight, a_availableWidth).Width;
            foreach (int p in PERCENTS)
            {
                if (Math.Abs(width - a_naturalWidth * p / 100) <= 1)
                {
                    return p;
                }
            }
            return 0;
        }

        /// <summary>style の width・height の長さ（px。「320px」・「50%」）。無い・読めない時は null。</summary>
        /// <param name="a_style">style の文字列。</param>
        /// <param name="a_name">width・height。</param>
        /// <param name="a_base">% の時の基準の長さ（null の時は % を読まない）。</param>
        /// <returns>長さ。</returns>
        private static double? StyleLength(string a_style, string a_name, double? a_base)
        {
            Match m = Regex.Match(a_style, "(?:^|;)\\s*" + a_name + "\\s*:\\s*([^;]+)", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return null;
            }
            Match v = Regex.Match(m.Groups[1].Value.Trim(), "^([\\d.]+)\\s*(px|%)$", RegexOptions.IgnoreCase);
            if (!v.Success || !double.TryParse(v.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n <= 0)
            {
                return null;
            }
            if ("%" == v.Groups[2].Value)
            {
                return a_base.HasValue ? a_base.Value * n / 100 : (double?)null;
            }
            return n;
        }

        /// <summary>width・height 属性の長さ（px。「320」・「50%」）。無い・読めない時は null。</summary>
        /// <param name="a_value">属性の値。</param>
        /// <param name="a_base">% の時の基準の長さ（null の時は % を読まない）。</param>
        /// <returns>長さ。</returns>
        private static double? AttributeLength(string a_value, double? a_base)
        {
            if (string.IsNullOrWhiteSpace(a_value))
            {
                return null;
            }
            Match v = Regex.Match(a_value.Trim(), "^([\\d.]+)\\s*(px|%)?$", RegexOptions.IgnoreCase);
            if (!v.Success || !double.TryParse(v.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n <= 0)
            {
                return null;
            }
            if ("%" == v.Groups[2].Value)
            {
                return a_base.HasValue ? a_base.Value * n / 100 : (double?)null;
            }
            return n;
        }
    }
}
