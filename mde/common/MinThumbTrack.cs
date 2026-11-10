// MinThumbTrack.cs
//
// mde (Markdown インラインエディタ) の一部。
// スクロールバーのつまみが短くなりすぎないようにする Track（ScrollBarStyles.xaml のスクロールバーで使う）。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace mde.common
{
    /// <summary>
    /// つまみ（Thumb）の長さに最小値（MinThumbLength）を持たせた Track。
    /// WPF の Track は、つまみの長さを「表示している割合」から決め、最小の長さは Windows のスクロールバーのボタンの
    /// 半分（9px 前後）しか確保しない。以前はつまみに MinHeight（18px）を付けていたが、Track はそれを見ずに、
    /// もっと短い長さで並べるため、長い文書（つまみが短くなる時）では、つまみが並べた長さで切り取られ、片側の端が
    /// 丸くならなかった（角丸の端が切れて、まっすぐな端になる）。
    /// ここでは、WPF の Track がつまみを並べた後、つまみが MinThumbLength より短ければ、つまみの長さを MinThumbLength に
    /// して、今の位置（Value）に合うよう、つまみと前後の空いた部分（ページ単位でスクロールするボタン）を並べ直す
    /// （つまみは切り取られないため、両端とも丸くなる。mdew（WebView2）のスクロールバーと同じく、短くなりすぎない）。
    /// つまみをドラッグした時の動く量は、WPF の Track の計算のまま（つまみの長さの差の分だけ、ごくわずかにずれるが、
    /// 気にならない程度）。
    /// </summary>
    public class MinThumbTrack : Track
    {
        /// <summary>つまみの最小の長さ（px）。</summary>
        public double MinThumbLength { get; set; } = 18;

        /// <inheritdoc/>
        protected override Size ArrangeOverride(Size a_arrangeSize)
        {
            Size result = base.ArrangeOverride(a_arrangeSize);
            Thumb thumb = Thumb;
            if (null == thumb || Visibility.Visible != thumb.Visibility || double.IsNaN(ViewportSize))
            {
                return result;
            }
            bool verticalFlg = Orientation.Vertical == Orientation;
            double trackLength = verticalFlg ? a_arrangeSize.Height : a_arrangeSize.Width;
            double thumbLength = verticalFlg ? thumb.RenderSize.Height : thumb.RenderSize.Width;
            if (thumbLength <= 0 || thumbLength >= MinThumbLength || trackLength <= MinThumbLength)
            {
                return result;
            }
            double range = Maximum - Minimum;
            double ratio = 0 < range ? Math.Max(0, Math.Min(1, (Value - Minimum) / range)) : 0;
            double newThumbLength = MinThumbLength;
            // 最小値の側（縦は IsDirectionReversed の時に上、横は IsDirectionReversed でない時に左）からの、つまみの位置
            double fromMinSide = (trackLength - newThumbLength) * ratio;
            bool minAtStartFlg = verticalFlg ? IsDirectionReversed : !IsDirectionReversed;
            double thumbStart = minAtStartFlg ? fromMinSide : trackLength - newThumbLength - fromMinSide;
            double thumbEnd = thumbStart + newThumbLength;
            RepeatButton startButton = minAtStartFlg ? DecreaseRepeatButton : IncreaseRepeatButton;
            RepeatButton endButton = minAtStartFlg ? IncreaseRepeatButton : DecreaseRepeatButton;
            if (verticalFlg)
            {
                double width = a_arrangeSize.Width;
                startButton?.Arrange(new Rect(0, 0, width, thumbStart));
                thumb.Arrange(new Rect(0, thumbStart, width, newThumbLength));
                endButton?.Arrange(new Rect(0, thumbEnd, width, Math.Max(0, trackLength - thumbEnd)));
            }
            else
            {
                double height = a_arrangeSize.Height;
                startButton?.Arrange(new Rect(0, 0, thumbStart, height));
                thumb.Arrange(new Rect(thumbStart, 0, newThumbLength, height));
                endButton?.Arrange(new Rect(thumbEnd, 0, Math.Max(0, trackLength - thumbEnd), height));
            }
            return result;
        }
    }
}
