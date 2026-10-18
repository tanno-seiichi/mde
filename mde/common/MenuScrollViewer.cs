// MenuScrollViewer.cs
//
// mde (Markdown インラインエディタ) の一部。
// メニューの枠（MenuStyles.xaml の MenuPanel）の中で使うスクロール領域。

using System.Windows.Controls;
using System.Windows.Input;

namespace mde.common
{
    /// <summary>
    /// キー操作を受け取らない ScrollViewer。メニューの枠（MenuStyles.xaml の MenuPanel）の中で、項目が多くて
    /// 入りきらない時にマウスホイールでスクロールするためだけに使う。
    /// 普通の ScrollViewer は ←→↑↓ 等のキーを横・縦のスクロールに使って「処理済み」にしてしまうため、メニューの中で
    /// ←→ キーを押しても、メニューバーの隣のメニューへ移る処理（MenuItem）まで届かなくなる。WPF 標準のメニューは
    /// ScrollViewer を MenuItem 自身のテンプレートの中に直接置き、ScrollViewer がキーを処理しないようにしているが
    /// （MenuItem.HandlesScrolling）、MenuPanel は複数のメニューで共有するため、ContentControl のテンプレートの
    /// 中にあり、その仕組みが働かない。そこでキーの処理だけを止める。
    /// </summary>
    public class MenuScrollViewer : ScrollViewer
    {
        /// <summary>キーは何も処理しない（メニューの項目・メニューバーに任せる）。</summary>
        /// <param name="a_args">キーのイベントの引数。</param>
        protected override void OnKeyDown(KeyEventArgs a_args)
        {
        }
    }
}
