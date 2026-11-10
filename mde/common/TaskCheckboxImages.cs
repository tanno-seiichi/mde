// TaskCheckboxImages.cs
//
// mde (Markdown インラインエディタ) の一部。
// タスクリストのチェックボックス（チェックした時）の絵を作る（App.xaml の TaskCheckboxStyle で使う）。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace mde.common
{
    /// <summary>
    /// タスクリストのチェックボックスを、チェックした時の絵（角の丸い青い四角に、白いチェックマーク）。
    /// mdew（WebView2。Chromium のチェックボックス）と同じ形・色にする。
    /// 編集ペインの文書は、罫線をくっきり描くために、なめらかに描く処理を止めている（MainWindow.ApplyEditorEdgeMode。
    /// その中の一部だけ元に戻すことはできない）。そのため、チェックマークを線（Path）で描くと、斜めの線がギザギザになり、
    /// mdew よりシャープに見えなかった。ここでは、なめらかに描いた絵（4倍の細かさのビットマップ）を作って Image で表示する
    /// （ビットマップは、なめらかに描く処理を止めても影響を受けない。縮小は App.xaml で高画質にする）。
    /// 形は、mdew のチェックボックス（16px）の画面の画素に合うよう求めた値（チェックマークの3つの点と太さ）を、mde の
    /// チェックボックスの大きさ（BOX_SIZE）に合わせて縮めたもの。
    /// </summary>
    public static class TaskCheckboxImages
    {
        /// <summary>チェックボックスの大きさ（DIP。BlockStyles.CreateTaskCheckbox の Width・Height と同じ）。</summary>
        private const double BOX_SIZE = 15;

        /// <summary>絵の細かさ（1 DIP を何ピクセルで描くか）。表示倍率 200% 程度まで、縮小して表示するようにする。</summary>
        private const int SCALE = 4;

        private static ImageSource s_checkedImage;

        /// <summary>チェックした時の絵（初めて使う時に作る）。</summary>
        public static ImageSource CheckedImage => s_checkedImage ??= CreateCheckedImage();

        /// <summary>チェックした時の絵を作る。</summary>
        private static ImageSource CreateCheckedImage()
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // mdew のチェックボックス（16px）で測った値を、この大きさに縮める
                double k = BOX_SIZE / 16.0;
                // 角の丸い青い四角（Chromium のチェックボックスと同じ #0075FF、角の丸みは 2px）
                var fill = new SolidColorBrush(Color.FromRgb(0x00, 0x75, 0xFF));
                fill.Freeze();
                dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, BOX_SIZE, BOX_SIZE), 2 * k, 2 * k);
                // 白いチェックマーク（太さ 2.34px。端は切りそろえ、角はとがらせる。Chromium と同じ）
                var pen = new Pen(Brushes.White, 2.34 * k)
                {
                    StartLineCap = PenLineCap.Flat,
                    EndLineCap = PenLineCap.Flat,
                    LineJoin = PenLineJoin.Miter,
                };
                pen.Freeze();
                var geometry = new StreamGeometry();
                using (StreamGeometryContext ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(3.37 * k, 7.69 * k), false, false);
                    ctx.LineTo(new Point(6.16 * k, 11.44 * k), true, true);
                    ctx.LineTo(new Point(12.62 * k, 3.28 * k), true, true);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
            int pixels = (int)Math.Round(BOX_SIZE * SCALE);
            var bitmap = new RenderTargetBitmap(pixels, pixels, 96.0 * SCALE, 96.0 * SCALE, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }

    /// <summary>
    /// 画面のピクセルの境目に合わせて表示する Image（チェックした時のチェックボックスの絵。App.xaml の TaskCheckboxStyle）。
    /// 文書の中のチェックボックスは、文字の行に合わせて置かれるため、画面のピクセルの途中から始まることがある。そのまま
    /// 絵を表示すると、絵が隣のピクセルにまたがって少しぼやける（mdew のチェックボックスはピクセルに合わせて描かれる）。
    /// レイアウトが変わるたびに、画面上の位置の端数を求め、その分だけ表示をずらしてピクセルの境目に合わせる（表示だけを
    /// ずらすため、レイアウトは変わらない）。見えている間（チェックした時）だけ行う。
    /// </summary>
    public class PixelSnappedImage : Image
    {
        /// <summary>今、表示をずらしている量（DIP）。</summary>
        private Vector m_shift;

        public PixelSnappedImage()
        {
            Loaded += (a_sender, a_args) => LayoutUpdated += OnLayoutUpdated;
            Unloaded += (a_sender, a_args) => LayoutUpdated -= OnLayoutUpdated;
        }

        /// <summary>レイアウトが変わった時：画面上の位置の端数の分だけ、表示をずらす。</summary>
        private void OnLayoutUpdated(object a_sender, EventArgs a_args)
        {
            if (!IsVisible)
            {
                return;
            }
            try
            {
                PresentationSource source = PresentationSource.FromVisual(this);
                if (null == source?.CompositionTarget || null == source.RootVisual ||
                    !(VisualTreeHelper.GetParent(this) is Visual parent))
                {
                    return;
                }
                // 本来の位置（今ずらしている分（RenderTransform）を含まない。親の座標で、この要素を置いた位置）を、画面の
                // ピクセル単位にする。1 DIP が何ピクセルか（表示倍率・拡大の分）も、同じ変換で求める
                GeneralTransform toRoot = parent.TransformToAncestor(source.RootVisual);
                Vector offset = VisualTreeHelper.GetOffset(this);
                Matrix toDevice = source.CompositionTarget.TransformToDevice;
                Point device0 = toDevice.Transform(toRoot.Transform(new Point(offset.X, offset.Y)));
                Point device1 = toDevice.Transform(toRoot.Transform(new Point(offset.X + 1, offset.Y + 1)));
                double pixelsPerDipX = device1.X - device0.X;
                double pixelsPerDipY = device1.Y - device0.Y;
                if (pixelsPerDipX <= 0 || pixelsPerDipY <= 0)
                {
                    return;
                }
                var shift = new Vector((Math.Round(device0.X) - device0.X) / pixelsPerDipX,
                    (Math.Round(device0.Y) - device0.Y) / pixelsPerDipY);
                if (0.001 < (shift - m_shift).Length)
                {
                    m_shift = shift;
                    RenderTransform = new TranslateTransform(shift.X, shift.Y);
                }
            }
            catch (InvalidOperationException)
            {
                // 画面に表示していない（印刷用の文書等）時は、ずらさない
            }
        }
    }
}
