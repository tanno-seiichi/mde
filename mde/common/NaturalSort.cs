// NaturalSort.cs
//
// mde (Markdown インラインエディタ) の一部。
// ファイル名・フォルダ名を、Windows エクスプローラーと同じ「自然順」（"2" が "10" より前）で比べる。

using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace mde.common
{
    /// <summary>
    /// Windows エクスプローラーの「自然順ソート」（"2" が "10" より前に来る、数字の部分を数値として比べる並び順）と
    /// 同じ順に並べるための比較。エクスプローラーが内部で使う shlwapi.dll の StrCmpLogicalW をそのまま使う。
    /// フォルダビュー（FolderTreeManager）と検索結果の一覧（SearchReplaceService）で、同じ並び順にするために共有する。
    /// </summary>
    public static class NaturalSort
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a_x, string a_y);

        /// <summary>自然順で比べる比較。</summary>
        public static readonly IComparer<string> Comparer = Comparer<string>.Create((a_x, a_y) => StrCmpLogicalW(a_x, a_y));
    }
}
