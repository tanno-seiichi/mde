// ClickOpenMenuItem.cs
//
// mde (Markdown インラインエディタ) の一部。
// サブメニューを、マウスを乗せただけでは開かず、クリック（またはキーボードの操作）した時だけ開く
// メニュー項目。メニュー「ファイル」→「最近使ったファイル」「最近使ったフォルダ」に使う
// （フルパスの長いサブメニューが、下の項目へマウスを動かす途中で開いて、ほかの項目を隠して
// しまわないように。mdew と同じ動き）。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace mde.common
{
    /// <summary>
    /// サブメニューをクリック・キー操作の時だけ開くメニュー項目。通常のMenuItemは、マウスを乗せて少し
    /// 待つとサブメニューを開く（IsSubmenuOpenをtrueにする）ため、IsSubmenuOpenの値を強制（Coerce）して、
    /// クリック・キー操作でサブメニューを開こうとした時以外は開かないようにしている。
    /// </summary>
    public class ClickOpenMenuItem : MenuItem
    {
        /// <summary>MenuItem本来のIsSubmenuOpenの強制処理（ここで置き換えるため、先に呼んでから判定する）。</summary>
        private static readonly CoerceValueCallback s_baseCoerceIsSubmenuOpen =
            IsSubmenuOpenProperty.GetMetadata(typeof(MenuItem)).CoerceValueCallback;

        /// <summary>クリック・キー操作で、サブメニューを開いてよい状態か（サブメニューが閉じたら戻す）。</summary>
        private bool m_openRequestedFlg;

        /// <summary>IsSubmenuOpenの強制処理を、このクラスのものに置き換える。</summary>
        static ClickOpenMenuItem()
        {
            IsSubmenuOpenProperty.OverrideMetadata(typeof(ClickOpenMenuItem),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, null, CoerceIsSubmenuOpen));
        }

        /// <summary>サブメニューを開こうとした時（IsSubmenuOpenをtrueにしようとした時）、クリック・キー操作に
        /// よるもの以外（マウスを乗せて待った時）は開かない（falseにする）。</summary>
        /// <param name="a_d">このメニュー項目。</param>
        /// <param name="a_value">設定しようとしている値。</param>
        /// <returns>実際に使う値。</returns>
        private static object CoerceIsSubmenuOpen(DependencyObject a_d, object a_value)
        {
            object value = null != s_baseCoerceIsSubmenuOpen ? s_baseCoerceIsSubmenuOpen(a_d, a_value) : a_value;
            if (value is bool open && open && !((ClickOpenMenuItem)a_d).m_openRequestedFlg)
            {
                return false;
            }
            return value;
        }

        /// <summary>クリック：サブメニューを開く（開いている時は閉じる。mdew と同じ）。</summary>
        /// <param name="a_args">マウスのイベントの引数。</param>
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs a_args)
        {
            // サブメニューの中の項目のクリックも、ここまで伝わってくることがあるため、この項目自身
            // （見出しの部分）をクリックした時だけ扱う
            if (IsClickOnHeader(a_args.OriginalSource as DependencyObject))
            {
                if (IsSubmenuOpen)
                {
                    SetCurrentValue(IsSubmenuOpenProperty, false);
                    a_args.Handled = true;
                    return;
                }
                m_openRequestedFlg = true;
            }
            base.OnMouseLeftButtonDown(a_args);
        }

        /// <summary>クリックした場所が、この項目自身（サブメニューの中の項目ではない）か。</summary>
        /// <param name="a_source">クリックした要素。</param>
        /// <returns>この項目自身ならtrue。</returns>
        private bool IsClickOnHeader(DependencyObject a_source)
        {
            for (DependencyObject current = a_source; null != current; current = GetParentObject(current))
            {
                if (current is MenuItem menuItem)
                {
                    return ReferenceEquals(this, menuItem);
                }
            }
            return false;
        }

        /// <summary>見た目の上の親（無ければ論理上の親）を返す。</summary>
        /// <param name="a_child">要素。</param>
        /// <returns>親の要素（無ければnull）。</returns>
        private static DependencyObject GetParentObject(DependencyObject a_child)
        {
            DependencyObject parent = a_child is System.Windows.Media.Visual || a_child is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(a_child)
                : null;
            return parent ?? LogicalTreeHelper.GetParent(a_child);
        }

        /// <summary>キー操作（→・Enter・Space）：サブメニューを開いてよいことにしてから、通常の処理を行う。</summary>
        /// <param name="a_args">キーのイベントの引数。</param>
        protected override void OnKeyDown(KeyEventArgs a_args)
        {
            if (Key.Right == a_args.Key || Key.Enter == a_args.Key || Key.Space == a_args.Key)
            {
                m_openRequestedFlg = true;
            }
            base.OnKeyDown(a_args);
        }

        /// <summary>アクセスキー（「最近使ったファイル(_R)」の R 等）：サブメニューを開いてよいことにしてから、
        /// 通常の処理を行う。</summary>
        /// <param name="a_args">アクセスキーのイベントの引数。</param>
        protected override void OnAccessKey(AccessKeyEventArgs a_args)
        {
            m_openRequestedFlg = true;
            base.OnAccessKey(a_args);
        }

        /// <summary>サブメニューが閉じたら、次はまたクリック・キー操作の時だけ開くように戻す。</summary>
        /// <param name="a_args">イベントの引数。</param>
        protected override void OnSubmenuClosed(RoutedEventArgs a_args)
        {
            base.OnSubmenuClosed(a_args);
            if (ReferenceEquals(this, a_args.OriginalSource))
            {
                m_openRequestedFlg = false;
            }
        }
    }
}
