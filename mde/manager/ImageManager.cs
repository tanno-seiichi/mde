// ImageManager.cs
//
// mde (Markdown インラインエディタ) の一部。
// 埋め込み画像を担当するクラス。画像ソースの解決・サイズ調整、エクスプローラーからの
// ドラッグ&ドロップでの挿入、画像をエクスプローラーへドラッグして書き出す機能、
// 保存前の一時フォルダへの退避などを扱う。
// MainWindow本体への参照は持たず、必要な操作はコンストラクタで渡されたdelegate経由で行う。

using mde.common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace mde.manager
{
    /// <summary>
    /// 画像の挿入・表示・保存・ドラッグ&amp;ドロップ一式。相対パスの解決には現在のファイルの
    /// 保存先フォルダが必要なため、それを取得するdelegateをコンストラクタで受け取る。
    /// </summary>
    public class ImageManager
    {
        private static readonly string[] m_imageDropExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

        private readonly RichTextBox m_editor;
        private readonly OriginalTextTracker m_originalTextTracker;
        private readonly Func<bool> m_isSourceMode;
        private readonly Func<string> m_getCurrentFileDirectory;
        private readonly Func<string> m_getCurrentFilePath;
        private readonly Action<Action> m_runAsProgrammaticChange;
        private readonly Action m_refreshOutline;
        private readonly string m_instanceTempId;

        /// <summary>右クリック時にマウス下にあった画像。右クリックメニューの「画像を保存…」から参照される。</summary>
        public Image ContextImage { get; set; }

        private Point? m_imageDragStartPoint;

        /// <summary>m_imageDragStartPointを記録した時刻（InputEventArgs.Timestamp、ミリ秒）。
        /// ダブルクリック1回目のクリックの手ブレをドラッグ開始と誤認しないための判定に使う。</summary>
        private int m_imageDragStartTimestamp;

        /// <summary>マウス押下からこの時間（ミリ秒）内の移動はドラッグとみなさない猶予。手ブレで
        /// DragDrop.DoDragDropが始まるとダブルクリックの2回目が拾えなくなるのを防ぐ。</summary>
        private const int IMAGE_DRAG_MIN_HOLD_MS = 200;

        /// <summary>
        /// ImageManagerを構築する。
        /// </summary>
        /// <param name="a_editor">対象のRichTextBox。</param>
        /// <param name="a_originalTextTracker">「元テキスト保持」の追跡役。</param>
        /// <param name="a_isSourceMode">現在ソースモードかどうかを返すdelegate。</param>
        /// <param name="a_getCurrentFileDirectory">現在のファイルの保存先フォルダを返すdelegate（相対パス解決に使う）。</param>
        /// <param name="a_getCurrentFilePath">現在のファイルの保存先パス（フルパス）を返すdelegate。
        /// 画像退避フォルダ名（"&lt;ファイル名&gt;.images"）の組み立てに使う。</param>
        /// <param name="a_runAsProgrammaticChange">処理を「プログラムによる変更」として実行するdelegate。</param>
        /// <param name="a_refreshOutline">アウトラインペインの再構築を依頼するdelegate。</param>
        /// <param name="a_instanceTempId">このウィンドウ専用の一時フォルダ識別子（複数ウィンドウでの衝突防止用）。</param>
        public ImageManager(
            RichTextBox a_editor,
            OriginalTextTracker a_originalTextTracker,
            Func<bool> a_isSourceMode,
            Func<string> a_getCurrentFileDirectory,
            Func<string> a_getCurrentFilePath,
            Action<Action> a_runAsProgrammaticChange,
            Action a_refreshOutline,
            string a_instanceTempId)
        {
            this.m_editor = a_editor;
            this.m_originalTextTracker = a_originalTextTracker;
            this.m_isSourceMode = a_isSourceMode;
            this.m_getCurrentFileDirectory = a_getCurrentFileDirectory;
            this.m_getCurrentFilePath = a_getCurrentFilePath;
            this.m_runAsProgrammaticChange = a_runAsProgrammaticChange;
            this.m_refreshOutline = a_refreshOutline;
            this.m_instanceTempId = a_instanceTempId;
        }

        /// <summary>HTMLの&lt;a_img&gt;タグを解析してImage要素を組み立てる。</summary>
        /// <param name="a_tagStr">&lt;a_img ...&gt;タグの生テキスト。</param>
        /// <returns>新しいImage要素。</returns>
        public Image BuildImageFromHtmlTag(string a_tagStr)
        {
            string src = FirstGroupOrEmpty(Regex.Match(a_tagStr, "src\\s*=\\s*\"([^\"]*)\""), Regex.Match(a_tagStr, "src\\s*=\\s*'([^']*)'"));
            string alt = FirstGroupOrEmpty(Regex.Match(a_tagStr, "alt\\s*=\\s*\"([^\"]*)\""), Regex.Match(a_tagStr, "alt\\s*=\\s*'([^']*)'"));
            string style = FirstGroupOrEmpty(Regex.Match(a_tagStr, "style\\s*=\\s*\"([^\"]*)\""), Regex.Match(a_tagStr, "style\\s*=\\s*'([^']*)'"));

            var img = new Image
            {
                Tag = new ImageInfo { OriginalSrc = src, Alt = alt, Style = style, Format = "html" },
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 4, 0, 4)
            };
            AutomationProperties.SetName(img, alt ?? "");
            img.ToolTip = src;
            SetImageSource(img, src);
            AttachImageDragHandlers(img);
            return img;
        }

        /// <summary>Markdownの ![a_alt](a_src "a_title") 記法からImage要素を組み立てる。</summary>
        /// <param name="a_alt">代替テキスト。</param>
        /// <param name="a_src">画像のパス/URL。</param>
        /// <param name="a_title">タイトル属性（省略時はnull）。</param>
        /// <returns>新しいImage要素。</returns>
        public Image BuildImageFromMarkdown(string a_alt, string a_src, string a_title = null)
        {
            var img = new Image
            {
                Tag = new ImageInfo { OriginalSrc = a_src, Alt = a_alt, Format = "md", Title = a_title },
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 4, 0, 4)
            };
            AutomationProperties.SetName(img, a_alt ?? "");
            img.ToolTip = string.IsNullOrEmpty(a_title) ? a_src : a_title;
            SetImageSource(img, a_src);
            AttachImageDragHandlers(img);
            return img;
        }

        // ======================================================================
        //  画像をエクスプローラー等の外部へドラッグして書き出す
        // ======================================================================

        /// <summary>画像にドラッグ書き出し・右クリック保存機能を紐付ける。</summary>
        /// <param name="a_img">対象の画像。</param>
        private void AttachImageDragHandlers(Image a_img)
        {
            a_img.Cursor = Cursors.Hand;
            // RichTextBox内部のカーソル制御（IBeam表示）にCursor指定が上書きされることがあるため、
            // ForceCursorでこの画像自身の指定を優先させる。
            a_img.ForceCursor = true;
            a_img.PreviewMouseLeftButtonDown += ImagePreviewMouseLeftButtonDown;
            a_img.PreviewMouseMove += ImagePreviewMouseMove;
        }

        /// <summary>マウス押下位置を記録する（後続のドラッグ判定用）。ダブルクリック検出と画像を
        /// 開く処理自体はMainWindow.EditorPreviewMouseLeftButtonDown側で行う。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void ImagePreviewMouseLeftButtonDown(object a_sender, MouseButtonEventArgs a_args)
        {
            if (a_args.ClickCount >= 2)
            {
                return; // ダブルクリックはMainWindow側で処理済み（Handled済みのはず）。
            }
            m_imageDragStartPoint = a_args.GetPosition(null);
            m_imageDragStartTimestamp = a_args.Timestamp;
        }

        /// <summary>画像をダブルクリックした時に、リンク先の画像ファイルを既定のアプリで開く。
        /// ローカル実ファイルが無くリモート画像（http/https）ならブラウザで開く。どちらも
        /// できなければダイアログで理由を知らせる。MainWindow.EditorPreviewMouseLeftButtonDownから
        /// 呼ばれる。</summary>
        /// <param name="a_img">対象の画像。</param>
        public void OpenImageFile(Image a_img)
        {
            if (!(a_img.Tag is ImageInfo info) || string.IsNullOrEmpty(info.OriginalSrc))
            {
                return;
            }

            string localPath = GetExportableFilePath(a_img);
            if (null != localPath)
            {
                OpenWithDefaultApp(localPath);
                return;
            }

            string src = info.OriginalSrc;
            if (Uri.TryCreate(src, UriKind.Absolute, out Uri u) &&
                ("http" == u.Scheme || "https" == u.Scheme))
            {
                OpenWithDefaultApp(src);
                return;
            }

            MessageBox.Show("この画像は開けません（元ファイルが見つからないか、埋め込み画像です）。",
                "画像を開く", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>指定したパス・URLをOS標準の関連付けアプリで開く。開けない環境でも例外を
        /// 握りつぶし、エディタの操作は継続させる。</summary>
        /// <param name="a_pathOrUrl">開くファイルパスまたはURL。</param>
        private static void OpenWithDefaultApp(string a_pathOrUrl)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(a_pathOrUrl)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // 関連付けアプリが無い環境などでは開けないため無視する。
            }
        }

        /// <summary>マウス押下位置から一定距離動いたら、OSのドラッグ&amp;ドロップ操作
        /// （画像ファイルの書き出し）を開始する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        private void ImagePreviewMouseMove(object a_sender, MouseEventArgs a_args)
        {
            if (a_args.LeftButton != MouseButtonState.Pressed ||
                null == m_imageDragStartPoint) return;
            if (!(a_sender is Image img))
            {
                return;
            }

            // ダブルクリック1回目の手ブレをドラッグ開始と誤認しないための猶予（IMAGE_DRAG_MIN_HOLD_MS参照）。
            if (a_args.Timestamp - m_imageDragStartTimestamp < IMAGE_DRAG_MIN_HOLD_MS)
            {
                return;
            }

            Point current = a_args.GetPosition(null);
            Vector diff = m_imageDragStartPoint.Value - current;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            m_imageDragStartPoint = null;

            string filePath = GetExportableFilePath(img);
            if (null == filePath)
            {
                return;
            }

            var data = new DataObject(DataFormats.FileDrop, new[] { filePath });
            DragDrop.DoDragDrop(img, data, DragDropEffects.Copy);
        }

        /// <summary>
        /// 埋め込み画像の現在の実ファイルパスを解決する（保存済みなら"&lt;ファイル名&gt;.images"
        /// フォルダ内、未保存ならこのウィンドウの一時フォルダ内）。リモート画像（http/https/data）や、
        /// 実ファイルが見つからない場合は null を返す。
        /// </summary>
        /// <param name="a_img">対象の画像。</param>
        /// <returns>実ファイルパス。解決できなければ null。</returns>
        public string GetExportableFilePath(Image a_img)
        {
            if (!(a_img.Tag is ImageInfo info) || string.IsNullOrEmpty(info.OriginalSrc))
            {
                return null;
            }
            string src = info.OriginalSrc;

            if (Uri.TryCreate(src, UriKind.Absolute, out Uri u) &&
                ("http" == u.Scheme ||
                 "https" == u.Scheme ||
                 "data" == u.Scheme))
                return null;

            string currentFileDirectory = m_getCurrentFileDirectory();
            string full;
            if (Path.IsPathRooted(src))
            {
                full = src;
            }
            else if (!string.IsNullOrEmpty(currentFileDirectory))
            {
                full = Path.GetFullPath(Path.Combine(currentFileDirectory, src.Replace('/', Path.DirectorySeparatorChar)));
            }
            else
            {
                return null;
            }

            return File.Exists(full) ? full : null;
        }

        /// <summary>
        /// 右クリック「画像を保存…」。エクスプローラーへのドラッグ（同名ファイルはエクスプローラー
        /// 自身が上書き確認する）とは異なり、ここでは書き込みを自前で制御するため、同名の既存
        /// ファイルを上書きすることは決してなく、代わりに自動的に連番を付ける。
        /// </summary>
        /// <param name="a_ownerWindow">ダイアログの親ウィンドウ。</param>
        public void SaveImageAs(Window a_ownerWindow)
        {
            if (null == ContextImage)
            {
                return;
            }
            string sourcePath = GetExportableFilePath(ContextImage);
            if (null == sourcePath)
            {
                MessageBox.Show("この画像は保存できません（リモート画像か、元ファイルが見つかりません）。",
                    "画像を保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileName(sourcePath),
                Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|すべてのファイル|*.*",
                OverwritePrompt = false
            };
            if (true != dlg.ShowDialog())
            {
                return;
            }

            try
            {
                string destPath = dlg.FileName;
                if (File.Exists(destPath) && !PathsReferToSameFile(sourcePath, destPath))
                {
                    string dir = Path.GetDirectoryName(destPath);
                    string baseName = Path.GetFileNameWithoutExtension(destPath);
                    string ext = Path.GetExtension(destPath);
                    int counter = 1;
                    do
                    {
                        destPath = Path.Combine(dir, baseName + "_" + counter + ext);
                        counter++;
                    } while (File.Exists(destPath));
                }
                File.Copy(sourcePath, destPath, PathsReferToSameFile(sourcePath, destPath));
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存に失敗しました: " + ex.Message, "画像を保存",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>右クリック「画像を削除」。ContextImageを含むInlineUIContainerを親段落の
        /// Inlinesから取り除く。RunAsProgrammaticChangeでくるみ、自動整形トリガーの対象にしない
        /// （挿入時のHandleDropと同様）。ダーティ化・アウトライン再構築はEditorTextChangedが
        /// 検知するため、ここでは明示的なMarkDirty呼び出しは不要。</summary>
        /// <param name="a_img">削除対象の画像要素（ContextImage）。</param>
        public void DeleteImage(Image a_img)
        {
            if (null == a_img)
            {
                return;
            }
            if (!(a_img.Parent is InlineUIContainer iuc) || !(iuc.Parent is Paragraph p))
            {
                return;
            }
            m_originalTextTracker.Invalidate(p.ContentStart);
            m_runAsProgrammaticChange(() =>
            {
                p.Inlines.Remove(iuc);
            });
            m_refreshOutline();
        }

        /// <summary>2つの正規表現マッチのうち成功した方の値を返す。どちらも失敗なら空文字。</summary>
        /// <param name="a_a">比較対象の1つ目。</param>
        /// <param name="a_b">比較対象の2つ目。</param>
        /// <returns>マッチした文字列。どちらも失敗していれば空文字。</returns>
        private string FirstGroupOrEmpty(Match a_a, Match a_b)
        {
            if (a_a.Success)
            {
                return a_a.Groups[1].Value;
            }
            if (a_b.Success)
            {
                return a_b.Groups[1].Value;
            }
            return "";
        }

        /// <summary>画像のsrc（絶対パス・http(s) URL・現在のファイルからの相対パス）を解決して読み込む。</summary>
        /// <param name="a_img">対象の画像要素。</param>
        /// <param name="a_src">Markdownに書かれていたパス/URL。</param>
        public void SetImageSource(Image a_img, string a_src)
        {
            if (string.IsNullOrWhiteSpace(a_src))
            {
                return;
            }
            try
            {
                Uri uri;
                if (Uri.TryCreate(a_src, UriKind.Absolute, out Uri absoluteUri) &&
                    ("http" == absoluteUri.Scheme ||
                     "https" == absoluteUri.Scheme ||
                     "data" == absoluteUri.Scheme))
                {
                    uri = absoluteUri;
                }
                else if (Path.IsPathRooted(a_src) && File.Exists(a_src))
                {
                    uri = new Uri(a_src, UriKind.Absolute);
                }
                else
                {
                    string currentFileDirectory = m_getCurrentFileDirectory();
                    if (!string.IsNullOrEmpty(currentFileDirectory))
                    {
                        string combined = Path.GetFullPath(Path.Combine(currentFileDirectory, a_src.Replace('/', Path.DirectorySeparatorChar)));
                        if (!File.Exists(combined))
                        {
                            return;
                        }
                        uri = new Uri(combined, UriKind.Absolute);
                    }
                    else
                    {
                        return;
                    }
                }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = uri;
                bmp.EndInit();

                if (bmp.IsDownloading)
                {
                    bmp.DownloadCompleted += (s, e) => ApplyImageSizingConsideringTable(a_img);
                }

                a_img.Source = bmp;
                ApplyImageSizingConsideringTable(a_img);
            }
            catch
            {
                // 解決できなければ何もしない（その場所は空白のまま表示される）。
            }
        }

        /// <summary>
        /// 画像を元のピクセルサイズを上限に、エディタの表示幅に収まるよう縮小する（幅が
        /// はみ出す場合のみ）。ズームはエディタ全体へのLayoutTransformで一律適用されるため、
        /// 100%幅を上限にしておけばどの倍率でも欠けない。
        /// </summary>
        /// <param name="a_img">対象の画像。</param>
        public void ApplyImageSizing(Image a_img)
        {
            if (!(a_img.Source is BitmapSource bmp))
            {
                return;
            }
            double naturalWidth = bmp.PixelWidth;
            double naturalHeight = bmp.PixelHeight;
            if (naturalWidth <= 0 ||
                naturalHeight <= 0) return;

            double availableWidth = GetAvailableImageWidth();
            double targetWidth = naturalWidth;
            if (availableWidth > 0 &&
                naturalWidth > availableWidth)
            {
                targetWidth = availableWidth;
            }

            double scale = targetWidth / naturalWidth;
            a_img.Width = targetWidth;
            a_img.Height = naturalHeight * scale;
        }

        /// <summary>
        /// 画像1枚の表示サイズを決める。表のセル内の画像は、そのセルが属する列の実際の幅
        /// （BlockStyles.ConstrainCellImagesToColumnWidths）に収まるよう縮小し、表の外の
        /// 画像はこれまで通りエディタの表示幅基準（ApplyImageSizing）で縮小する。
        ///
        /// 表内の画像を常にこちらの経路で扱うようにしたのは、次の不具合の修正のため。
        /// 従来は、表内・表外を問わずSetImageSourceから常にApplyImageSizing（エディタ全体の
        /// 幅を上限とする、表のセル幅を考慮しない縮小）だけが呼ばれていた。一方、表の列幅補正
        /// （BlockStyles.ConstrainCellImagesToColumnWidths）は表の構築時（MarkdownToDocument内で
        /// 表の各セルを組み立てる際）に呼ばれるが、その時点では埋め込み画像はまだSourceが
        /// 解決されておらず（ResolveImagesはMarkdownToDocumentの最後、表の構築より後に呼ばれる
        /// ため）、画像の自然サイズが分からずConstrainCellImagesToColumnWidths側のガード
        /// （naturalWidth/naturalHeightが0以下なら対象外）に引っかかって常にスキップされていた。
        /// 結果として、表のセル内の画像は初回表示時は列幅を考慮しない（広すぎる）サイズのまま
        /// 表示され、その後ウインドウのリサイズやズーム変更（MainWindow.EditorSizeChanged経由で
        /// TableEditor.RefreshAutoCalculatedColumnWidthsForResizeが呼ばれた時）に限って正しい
        /// サイズへ補正される、という状態になっていた（「倍率を変えると治る」という現象の原因）。
        /// このメソッドを介することで、画像のSourceが解決されるタイミング（初回表示・再読み込み
        /// 双方）で必ず列幅を考慮したサイズが適用されるようにしている。
        /// </summary>
        /// <param name="a_img">対象の画像。</param>
        public void ApplyImageSizingConsideringTable(Image a_img)
        {
            Table table = FindEnclosingTable(a_img);
            if (null != table)
            {
                BlockStyles.ConstrainCellImagesToColumnWidths(table, GetAvailableImageWidth());
                return;
            }
            ApplyImageSizing(a_img);
        }

        /// <summary>画像がテーブルのセル内にある場合、そのセルが属する表（Table）を返す。
        /// TableEditor.FindEnclosingTableと同じ考え方で、WPFの論理ツリーを
        /// Image→InlineUIContainer→Paragraph→TableCell→TableRow→TableRowGroup→Table の順に
        /// たどる（DeleteImageで使っているImage→InlineUIContainer→Paragraphの辿り方と同じ
        /// パターンを、TableCellまでさらに延長したもの）。表の外の画像であれば null を返す。</summary>
        /// <param name="a_img">対象の画像。</param>
        /// <returns>画像を含む表。表の外ならnull。</returns>
        private Table FindEnclosingTable(Image a_img)
        {
            if (!(a_img.Parent is InlineUIContainer iuc) ||
                !(iuc.Parent is Paragraph p) ||
                !(p.Parent is TableCell cell) ||
                !(cell.Parent is TableRow row) ||
                !(row.Parent is TableRowGroup rg))
            {
                return null;
            }
            return rg.Parent as Table;
        }

        /// <summary>エディタの現在のサイズとパディングから、画像に使える幅を計算する。</summary>
        /// <returns>利用可能な幅（ピクセル）。</returns>
        public double GetAvailableImageWidth()
        {
            double w = m_editor.ActualWidth;
            if (w <= 0)
            {
                return 560; // 初回レイアウト前の妥当なフォールバック値
            }
            w -= m_editor.Padding.Left + m_editor.Padding.Right;
            w -= 24; // スクロールバー＋右端が詰まりすぎないための余白
            return Math.Max(100, w);
        }

        // ======================================================================
        //  エクスプローラーから画像ファイルをドラッグ&ドロップで挿入
        // ======================================================================

        /// <summary>ファイルパスが対応済みの画像拡張子かどうかを調べる。</summary>
        /// <param name="a_path">調べるファイルパス。</param>
        /// <returns>対応している画像形式なら true。</returns>
        public bool IsImageFile(string a_path)
        {
            string ext = Path.GetExtension(a_path);
            return !string.IsNullOrEmpty(ext) && m_imageDropExtensions.Contains(ext.ToLowerInvariant());
        }

        /// <summary>
        /// エディタへの画像ファイルドラッグ&amp;ドロップの入口。RichTextBoxが内部のテキスト用
        /// ドラッグ&amp;ドロップ処理で横取りしないよう、Preview（トンネリング）方式のハンドラとして
        /// XAML側で配線する必要がある。
        /// </summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleDragEnter(object a_sender, DragEventArgs a_args)
        {
            HandleDragOver(a_sender, a_args);
        }

        /// <summary>現在のドラッグ内容に、挿入可能な画像ファイルが含まれているかを示す。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleDragOver(object a_sender, DragEventArgs a_args)
        {
            bool acceptFlg = !m_isSourceMode() &&
                          a_args.Data.GetDataPresent(DataFormats.FileDrop) &&
                          a_args.Data.GetData(DataFormats.FileDrop) is string[] dragFiles &&
                          dragFiles.Any(IsImageFile);
            a_args.Effects = acceptFlg ? DragDropEffects.Copy : DragDropEffects.None;
            a_args.Handled = true;
        }

        /// <summary>ドロップされた画像ファイルを、まずこのウィンドウの一時フォルダへ退避しつつ、
        /// ドロップ位置に挿入する。</summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">イベントの引数。</param>
        public void HandleDrop(object a_sender, DragEventArgs a_args)
        {
            if (m_isSourceMode() || !a_args.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }
            if (!(a_args.Data.GetData(DataFormats.FileDrop) is string[] files))
            {
                return;
            }

            var imageFiles = files.Where(IsImageFile).ToList();
            if (0 == imageFiles.Count)
            {
                return;
            }

            a_args.Handled = true;

            Point dropPoint = a_args.GetPosition(m_editor);
            TextPointer insertAt = m_editor.GetPositionFromPoint(dropPoint, true);
            if (null == insertAt)
            {
                return;
            }

            m_originalTextTracker.Invalidate(insertAt);

            m_runAsProgrammaticChange(() =>
            {
                foreach (var file in imageFiles)
                {
                    // 保存済み文書でも常にまずOSの一時フォルダへ退避する。実フォルダへの移動は
                    // RelocatePendingTempImagesがSave/Save Asのたびに行う。
                    string tempPath = CopyFileWithDedup(file, GetOrCreateTempImageFolder());
                    if (null == tempPath)
                    {
                        continue;
                    }

                    var img = BuildImageFromMarkdown(Path.GetFileNameWithoutExtension(file), tempPath);
                    var container = new InlineUIContainer(img, insertAt);
                    insertAt = container.ElementEnd;
                }
                m_editor.CaretPosition = insertAt;
            });

            m_refreshOutline();
            m_editor.Focus();
        }

        // ======================================================================
        //  クリップボードからの画像貼り付け
        // ======================================================================

        /// <summary>クリップボードから貼り付けられる画像の保存先ファイル名のベース部分
        /// （末尾に"_1"、"_2"…と連番を付けて保存する。ドラッグ&amp;ドロップと違い、
        /// クリップボードの生データには元のファイル名が無いため）。</summary>
        private const string PASTED_IMAGE_BASE_NAME = "clipboard_image";

        /// <summary>
        /// クリップボードに画像がある状態でのCtrl+V等の貼り付け（DataObject.Pasting）を検知し、
        /// ドラッグ&amp;ドロップと同じ一時フォルダへPNGとして保存して挿入する。Xaml/Rtf形式を伴う
        /// 貼り付け（mde自身や他のリッチテキストアプリから）はWPF標準の処理に任せ、ここでは
        /// 何もしない。TableEditor.HandlePasting・EditorHandleInlineMarkdownPastingより先に
        /// 登録し、画像を含むクリップボード内容を横取りする（MainWindow.xaml.csの登録順を参照）。
        /// </summary>
        /// <param name="a_sender">イベントの発生元。</param>
        /// <param name="a_args">貼り付けイベントの引数。</param>
        public void HandlePasting(object a_sender, DataObjectPastingEventArgs a_args)
        {
            if (m_isSourceMode())
            {
                return;
            }
            if (a_args.SourceDataObject.GetDataPresent(DataFormats.Xaml) ||
                a_args.SourceDataObject.GetDataPresent(DataFormats.Rtf))
            {
                return; // mde自身や他のリッチテキストアプリからの貼り付けはWPF標準の処理に任せる
            }

            BitmapSource bmp = TryGetClipboardImage(a_args.SourceDataObject);
            if (null == bmp)
            {
                return; // 画像でなければ何もしない（後続のハンドラ、または既定の貼り付けに任せる）
            }

            a_args.CancelCommand();

            string destDir = GetOrCreateTempImageFolder();
            string fileName = GenerateSequentialImageFileName(destDir, PASTED_IMAGE_BASE_NAME, ".png");
            string destPath = Path.Combine(destDir, fileName);
            try
            {
                using (var fs = new FileStream(destPath, FileMode.Create))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    encoder.Save(fs);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("クリップボードの画像の保存に失敗しました: " + ex.Message, "画像の貼り付け",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            m_originalTextTracker.Invalidate(m_editor.CaretPosition);
            m_runAsProgrammaticChange(() =>
            {
                if (!m_editor.Selection.IsEmpty)
                {
                    m_editor.Selection.Text = "";
                }
                var img = BuildImageFromMarkdown(Path.GetFileNameWithoutExtension(fileName), destPath);
                var container = new InlineUIContainer(img, m_editor.CaretPosition);
                m_editor.CaretPosition = container.ElementEnd;
            });

            m_refreshOutline();
            m_editor.Focus();
        }

        /// <summary>クリップボードからBitmapSourceを取り出す。透過を保つため"PNG"形式があれば
        /// 直接デコードし、なければClipboard.GetImage()にフォールバックする
        /// （GetImage()単体だとブラウザ等からのコピーで透過が失われることがある）。</summary>
        /// <param name="a_data">貼り付けイベントのクリップボードデータ。</param>
        /// <returns>取得できたBitmapSource。画像が入っていなければnull。</returns>
        private static BitmapSource TryGetClipboardImage(IDataObject a_data)
        {
            if (a_data.GetDataPresent("PNG"))
            {
                try
                {
                    if (a_data.GetData("PNG") is Stream pngStream)
                    {
                        var decoder = new PngBitmapDecoder(
                            pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        if (decoder.Frames.Count > 0)
                        {
                            return decoder.Frames[0];
                        }
                    }
                }
                catch
                {
                    // 壊れたPNGデータ等で失敗しても、下のフォールバックへ続ける。
                }
            }
            if (a_data.GetDataPresent(DataFormats.Bitmap))
            {
                try
                {
                    return Clipboard.GetImage();
                }
                catch
                {
                    // 取得に失敗した場合は画像なしとして扱う。
                }
            }
            return null;
        }

        /// <summary>指定フォルダ内で、ベース名+連番+拡張子（例："clipboard_image_1.png"）の形で
        /// 既存ファイルと衝突しない名前を組み立てる。CopyFileWithDedupと異なり、コピー元となる
        /// 実ファイルが存在しない（クリップボードの生データが元になる）ケース向け。</summary>
        /// <param name="a_destDir">保存先フォルダ。</param>
        /// <param name="a_baseName">ファイル名のベース部分（拡張子・連番を除く）。</param>
        /// <param name="a_ext">拡張子（"."を含む）。</param>
        /// <returns>衝突しないファイル名（フォルダ部分を含まない）。</returns>
        private static string GenerateSequentialImageFileName(string a_destDir, string a_baseName, string a_ext)
        {
            int counter = 1;
            string fileName;
            do
            {
                fileName = a_baseName + "_" + counter + a_ext;
                counter++;
            } while (File.Exists(Path.Combine(a_destDir, fileName)));
            return fileName;
        }

        /// <summary>このウィンドウ専用の、ドラッグ挿入画像を退避する一時フォルダを取得する
        /// （なければ作成する）。</summary>
        /// <returns>一時フォルダの絶対パス。</returns>
        public string GetOrCreateTempImageFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mde", m_instanceTempId);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// a_sourcePath を a_destDir へコピーする。同名ファイルが既にあれば "_1"、"_2" ... と
        /// 連番を付ける。失敗時は null を返す。a_sourcePath がコピー先と（結果的に）同一ファイルを
        /// 指す場合はコピーを行わず、既存のパスをそのまま使う。
        /// </summary>
        /// <param name="a_sourcePath">コピー元のパス。</param>
        /// <param name="a_destDir">コピー先のフォルダ。</param>
        /// <returns>コピー先のパス。失敗した場合はnull。</returns>
        public string CopyFileWithDedup(string a_sourcePath, string a_destDir)
        {
            try
            {
                Directory.CreateDirectory(a_destDir);

                string fileName = Path.GetFileName(a_sourcePath);
                string destPath = Path.Combine(a_destDir, fileName);

                if (File.Exists(destPath) && !PathsReferToSameFile(a_sourcePath, destPath))
                {
                    string baseName = Path.GetFileNameWithoutExtension(fileName);
                    string ext = Path.GetExtension(fileName);
                    int counter = 1;
                    do
                    {
                        fileName = baseName + "_" + counter + ext;
                        destPath = Path.Combine(a_destDir, fileName);
                        counter++;
                    } while (File.Exists(destPath));
                }

                if (!PathsReferToSameFile(a_sourcePath, destPath))
                {
                    File.Copy(a_sourcePath, destPath, false);
                }

                return destPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show("画像のコピーに失敗しました: " + ex.Message, "画像の追加",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        /// <summary>
        /// 文書の保存先フォルダが判明したタイミング（Save/Save As）で呼ばれる。OSの一時フォルダの
        /// 画像を、保存先の隣の「&lt;ファイル名&gt;.images」フォルダへ移動し、各画像のパスを
        /// Markdown書き出し用の相対パスに更新する。ファイルごとに専用フォルダ名にすることで、
        /// 同じフォルダの複数Markdownファイル間で画像が混在しない。
        /// </summary>
        /// <param name="a_doc">対象の文書。</param>
        public void RelocatePendingTempImages(FlowDocument a_doc)
        {
            string currentFileDirectory = m_getCurrentFileDirectory();
            if (string.IsNullOrEmpty(currentFileDirectory))
            {
                return;
            }

            string imagesFolderName = GetImagesFolderName();
            if (string.IsNullOrEmpty(imagesFolderName))
            {
                return;
            }

            string tempDir;
            try { tempDir = Path.GetFullPath(GetOrCreateTempImageFolder()); }
            catch { return; }

            foreach (var img in FindAllImages(a_doc))
            {
                if (!(img.Tag is ImageInfo info) || string.IsNullOrEmpty(info.OriginalSrc))
                {
                    continue;
                }
                if (!Path.IsPathRooted(info.OriginalSrc))
                {
                    continue; // 既に相対パスなら何もしない
                }

                string fullSrc;
                try { fullSrc = Path.GetFullPath(info.OriginalSrc); } catch { continue; }
                if (!fullSrc.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // このウィンドウの一時ファイルではない
                }

                string destPath = CopyFileWithDedup(fullSrc, Path.Combine(currentFileDirectory, imagesFolderName));
                if (null == destPath)
                {
                    continue;
                }

                info.OriginalSrc = imagesFolderName + "/" + Path.GetFileName(destPath);
                SetImageSource(img, info.OriginalSrc);

                try { File.Delete(fullSrc); } catch { /* 削除できなくても致命的ではない */ }
            }
        }

        /// <summary>
        /// 「名前を付けて保存」で保存先フォルダやファイル名が変わった際に呼ばれる。文書内の
        /// 画像のうち、既に相対パスになっている（＝前回の保存時点で退避済みの）ものを対象に、
        /// 変更前のファイルのフォルダを基準とした相対パスの位置から、新しい保存先の同じ相対
        /// パスの位置へファイルをコピーする。
        ///
        /// 当初は「&lt;変更前のファイル名&gt;.images」という専用フォルダの中の画像だけを対象に
        /// していたが、複数のMarkdownファイルが共通の「images」等のフォルダを共有して参照する
        /// ケース（docxから複数ファイルへ分割変換したもの等でよく見られる）では、フォルダ名が
        /// ファイル名と対応しておらず対象から漏れ、画像が消えてしまう不具合があったため、
        /// フォルダ名に依存しない、相対パスの構造をそのまま維持してコピーする方式に変更した。
        /// これにより「&lt;ファイル名&gt;.images」に限らず、任意の名前・構造の相対パス参照
        /// （複数ファイル共有フォルダ・入れ子のフォルダ等）を幅広くカバーする。
        ///
        /// なお、変更前のファイル専用の画像フォルダ（"&lt;変更前のファイル名&gt;.images"）内の
        /// 画像に限っては、ファイル名も同時に変わる場合、フォルダ名を新しいファイル名に追従
        /// させる（従来通りの挙動）。それ以外（共有フォルダ等）は、フォルダ名を変えず、その
        /// ままの相対パス構造でコピーする。
        ///
        /// RelocatePendingTempImagesとは対象とする画像が重ならない（一方は絶対パス、もう一方は
        /// 相対パスを対象にする）ため、両方呼んでも問題ない。コピー元（変更前のフォルダ内の
        /// ファイル）は削除しない。これは、変更前のファイル自体がまだ元の場所に残っている場合、
        /// そちらが引き続きその画像を参照しているため。
        /// </summary>
        /// <param name="a_doc">対象の文書。</param>
        /// <param name="a_oldFileDirectory">変更前のファイルの保存先フォルダ。まだ一度も保存して
        /// いない新規文書からの「名前を付けて保存」等、変更前の情報が無い場合はnull。</param>
        /// <param name="a_oldFilePath">変更前のファイルのパス（専用画像フォルダ名の決定に使う）。
        /// 変更前の情報が無い場合はnull。</param>
        public void RelocateImagesFromOldFileImagesFolder(FlowDocument a_doc, string a_oldFileDirectory, string a_oldFilePath)
        {
            if (string.IsNullOrEmpty(a_oldFileDirectory) || string.IsNullOrEmpty(a_oldFilePath))
            {
                return; // 変更前のファイルが無かった（新規文書など）場合は移し替える対象も無い
            }

            string currentFileDirectory = m_getCurrentFileDirectory();
            if (string.IsNullOrEmpty(currentFileDirectory))
            {
                return;
            }

            string oldDirFull;
            string newDirFull;
            try
            {
                oldDirFull = Path.GetFullPath(a_oldFileDirectory);
                newDirFull = Path.GetFullPath(currentFileDirectory);
            }
            catch { return; }

            bool sameFolder = string.Equals(oldDirFull, newDirFull, StringComparison.OrdinalIgnoreCase);

            // 変更前のファイル専用の画像フォルダ（"<変更前のファイル名>.images"）内の画像は、
            // ファイル名も同時に変わる場合、フォルダ名を新しいファイル名に追従させる。
            string oldOwnedFolderName = Path.GetFileNameWithoutExtension(a_oldFilePath) + ".images";
            string newOwnedFolderName = GetImagesFolderName(); // 決定できなければnull

            foreach (var img in FindAllImages(a_doc))
            {
                if (!(img.Tag is ImageInfo info) || string.IsNullOrEmpty(info.OriginalSrc))
                {
                    continue;
                }
                if (Path.IsPathRooted(info.OriginalSrc))
                {
                    continue; // 絶対パス（一時フォルダ内画像・外部ファイルの直接参照等）は対象外
                }
                if (Uri.TryCreate(info.OriginalSrc, UriKind.Absolute, out Uri absoluteUri) &&
                    ("http" == absoluteUri.Scheme || "https" == absoluteUri.Scheme || "data" == absoluteUri.Scheme))
                {
                    continue; // リモート画像は対象外
                }

                string relSrc = info.OriginalSrc.Replace('/', Path.DirectorySeparatorChar);
                string destRelSrc = relSrc;

                if (!string.IsNullOrEmpty(newOwnedFolderName))
                {
                    string firstSegment = relSrc.Split(Path.DirectorySeparatorChar)[0];
                    if (string.Equals(firstSegment, oldOwnedFolderName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(oldOwnedFolderName, newOwnedFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        destRelSrc = newOwnedFolderName + relSrc.Substring(firstSegment.Length);
                    }
                }

                bool relSrcChanged = !string.Equals(relSrc, destRelSrc, StringComparison.OrdinalIgnoreCase);
                if (sameFolder && !relSrcChanged)
                {
                    continue; // 保存先が同じフォルダで相対パスも変わらないなら、そのまま表示できる
                }

                string fullSrc;
                string fullDest;
                try
                {
                    fullSrc = Path.GetFullPath(Path.Combine(oldDirFull, relSrc));
                    fullDest = Path.GetFullPath(Path.Combine(newDirFull, destRelSrc));
                }
                catch { continue; }

                if (string.Equals(fullSrc, fullDest, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // コピー元・コピー先が同じ場所（実質的な移動が無い）
                }

                if (!File.Exists(fullDest))
                {
                    if (!File.Exists(fullSrc))
                    {
                        continue; // コピー元が見つからない場合は対象外
                    }
                    try
                    {
                        string destDir = Path.GetDirectoryName(fullDest);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }
                        File.Copy(fullSrc, fullDest, false);
                    }
                    catch { continue; }
                }
                // コピー先に既にファイルがある場合は上書きせず、そのまま新しい相対パスとして使う
                // （例えば直前の保存で既にコピー済みだった場合等）。

                if (relSrcChanged)
                {
                    info.OriginalSrc = destRelSrc.Replace(Path.DirectorySeparatorChar, '/');
                    SetImageSource(img, info.OriginalSrc);
                }
                // 変更前の画像フォルダの元ファイルはここでは削除しない（変更前のファイル自体が
                // まだ元の場所に残っており、そちらの画像として引き続き使われているため）。
            }
        }

        /// <summary>
        /// 一時フォルダの画像を退避する先のフォルダ名（"&lt;ファイル名（拡張子除く）&gt;.images"）
        /// を組み立てる。現在のファイルパスが分からない場合はnullを返す。
        /// </summary>
        /// <returns>フォルダ名。決定できなければnull。</returns>
        private string GetImagesFolderName()
        {
            string currentFilePath = m_getCurrentFilePath?.Invoke();
            if (string.IsNullOrEmpty(currentFilePath))
            {
                return null;
            }
            string baseName = Path.GetFileNameWithoutExtension(currentFilePath);
            return string.IsNullOrEmpty(baseName) ? null : baseName + ".images";
        }

        /// <summary>2つのパスが同一ファイルを指しているかどうかを調べる（大文字小文字を区別せず、
        /// 完全パスで比較する）。</summary>
        /// <param name="a_a">比較対象の1つ目。</param>
        /// <param name="a_b">比較対象の2つ目。</param>
        /// <returns>同一ファイルを指していればtrue。</returns>
        public bool PathsReferToSameFile(string a_a, string a_b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a_a), Path.GetFullPath(a_b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>新しく読み込んだ文書内のすべての画像に対して、ソースを解決して読み込む
        /// （解析処理そのものからは切り離してあり、現在のファイルの場所が判明してから
        /// 呼び出すことで、相対パスが正しく解決できるようにしている）。</summary>
        /// <param name="a_doc">対象の文書。</param>
        public void ResolveImages(FlowDocument a_doc)
        {
            foreach (var img in FindAllImages(a_doc))
            {
                if (img.Tag is ImageInfo info)
                {
                    SetImageSource(img, info.OriginalSrc);
                }
            }
        }

        /// <summary>文書内のすべての埋め込み画像を見つける。</summary>
        /// <param name="a_doc">対象の文書。</param>
        /// <returns>見つかったすべてのImage要素。</returns>
        public IEnumerable<Image> FindAllImages(FlowDocument a_doc)
        {
            foreach (Block block in a_doc.Blocks)
            {
                foreach (var img in FindImagesInBlock(block))
                {
                    yield return img;
                }
            }
        }

        /// <summary>1つのブロック（段落・リスト・表）の中の画像を再帰的に見つける。</summary>
        /// <param name="a_block">対象のブロック。</param>
        /// <returns>見つかった画像の列挙。</returns>
        private IEnumerable<Image> FindImagesInBlock(Block a_block)
        {
            if (a_block is Paragraph p)
            {
                foreach (var img in FindImagesInInlines(p.Inlines))
                {
                    yield return img;
                }
            }
            else if (a_block is List list)
            {
                foreach (ListItem li in list.ListItems)
                {
                    foreach (Block b in li.Blocks)
                    {
                        foreach (var img in FindImagesInBlock(b))
                        {
                            yield return img;
                        }
                    }
                }
            }
            else if (a_block is Table table)
            {
                foreach (TableRowGroup rg in table.RowGroups)
                {
                    foreach (TableRow row in rg.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Block b in cell.Blocks)
                            {
                                foreach (var img in FindImagesInBlock(b))
                                {
                                    yield return img;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Inlinesコレクションの中の画像を再帰的に見つける（ネストしたSpanも含む）。</summary>
        /// <param name="a_inlines">対象のInlineコレクション。</param>
        /// <returns>見つかった画像の列挙。</returns>
        private IEnumerable<Image> FindImagesInInlines(InlineCollection a_inlines)
        {
            foreach (Inline inline in a_inlines)
            {
                if (inline is InlineUIContainer iuc && iuc.Child is Image im)
                {
                    yield return im;
                }
                else if (inline is Span span)
                {
                    foreach (var img in FindImagesInInlines(span.Inlines))
                    {
                        yield return img;
                    }
                }
            }
        }
    }
}
