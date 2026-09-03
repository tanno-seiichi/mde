// ImageManager.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 埋め込み画像を担当するクラス。画像ソースの解決・サイズ調整、エクスプローラーからの
// ドラッグ&ドロップでの挿入、画像をエクスプローラーへドラッグして書き出す機能、
// 保存前の一時フォルダへの退避などを扱う。
// MainWindow本体への参照は持たず、必要な操作はコンストラクタで渡されたdelegate経由で行う。

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

namespace mde
{
    /// <summary>
    /// 画像の挿入・表示・保存・ドラッグ&amp;ドロップ一式。相対パスの解決には現在のファイルの
    /// 保存先フォルダが必要なため、それを取得するdelegateをコンストラクタで受け取る。
    /// </summary>
    public class ImageManager
    {
        private static readonly string[] IMAGE_DROP_EXTENSIONS = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

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

        /// <summary>m_imageDragStartPointを記録した瞬間のタイムスタンプ（InputEventArgs.
        /// Timestamp、ミリ秒）。ダブルクリックの1回目のクリックのわずかな手ブレでドラッグが
        /// 誤発生してしまう（結果としてダブルクリックとして認識されなくなる）のを防ぐため、
        /// マウス押下からある程度の時間が経ってから動いた場合だけドラッグとみなすのに使う。</summary>
        private int m_imageDragStartTimestamp;

        /// <summary>マウス押下からこの時間（ミリ秒）が経過するまでは、多少動いてもドラッグとは
        /// みなさない。ダブルクリックの1回目のクリックで手ブレにより数ピクセル動いただけで
        /// DragDrop.DoDragDrop（モーダルなドラッグ操作）が始まってしまうと、その裏で2回目の
        /// クリックが正しく認識されなくなり、ダブルクリックが機能しなくなってしまうため。</summary>
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
                Tag = new ImageInfo { m_originalSrc = src, m_alt = alt, m_style = style, m_format = "html" },
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 4, 0, 4)
            };
            AutomationProperties.SetName(img, alt ?? "");
            img.ToolTip = src;
            SetImageSource(img, src);
            AttachImageDragHandlers(img);
            return img;
        }

        /// <summary>MarkDownの ![a_alt](a_src "a_title") 記法からImage要素を組み立てる。</summary>
        /// <param name="a_alt">代替テキスト。</param>
        /// <param name="a_src">画像のパス/URL。</param>
        /// <param name="a_title">タイトル属性（省略時はnull）。</param>
        /// <returns>新しいImage要素。</returns>
        public Image BuildImageFromMarkdown(string a_alt, string a_src, string a_title = null)
        {
            var img = new Image
            {
                Tag = new ImageInfo { m_originalSrc = a_src, m_alt = a_alt, m_format = "md", m_title = a_title },
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
            // RichTextBox内に埋め込まれたInlineUIContainerの子要素は、RichTextBox内部の
            // テキスト編集用カーソル制御（IBeam表示）にCursor指定が上書きされてしまうことが
            // あるため、ForceCursorでこの画像自身の指定を優先させる。
            a_img.ForceCursor = true;
            a_img.PreviewMouseLeftButtonDown += ImagePreviewMouseLeftButtonDown;
            a_img.PreviewMouseMove += ImagePreviewMouseMove;
        }

        /// <summary>マウス押下位置を記録する（後続のドラッグ判定に使う）。ダブルクリックの検出・
        /// 画像を開く処理自体は、MainWindow.EditorPreviewMouseLeftButtonDown側で
        /// （RichTextBoxからのVisualTreeHelper.HitTestを使って、より確実に）行う。</summary>
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

        /// <summary>画像をダブルクリックした時に、そのリンク先の画像ファイルを既定のアプリ
        /// （画像ビューア）で開く。ローカルの実ファイルが見つからずリモート画像（http/https）で
        /// あれば、代わりにブラウザで開く。data:URIの埋め込み画像や、実ファイルが見つからない
        /// 相対パスなど、開きようが無い場合は、SaveImageAsと同様に理由をダイアログで知らせる
        /// （ユーザーが意図して開こうとした操作なので、原因が分からないまま何も起きないより、
        /// 明示的に知らせた方が親切なため）。MainWindow.EditorPreviewMouseLeftButtonDownから
        /// 呼ばれる。</summary>
        /// <param name="a_img">対象の画像。</param>
        public void OpenImageFile(Image a_img)
        {
            if (!(a_img.Tag is ImageInfo info) || string.IsNullOrEmpty(info.m_originalSrc))
            {
                return;
            }

            string localPath = GetExportableFilePath(a_img);
            if (null != localPath)
            {
                OpenWithDefaultApp(localPath);
                return;
            }

            string src = info.m_originalSrc;
            if (Uri.TryCreate(src, UriKind.Absolute, out Uri u) &&
                ("http" == u.Scheme || "https" == u.Scheme))
            {
                OpenWithDefaultApp(src);
                return;
            }

            MessageBox.Show("この画像は開けません（元ファイルが見つからないか、埋め込み画像です）。",
                "画像を開く", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>指定したパス・URLを、OS標準の関連付けアプリ（画像ファイルならビューア、
        /// URLならブラウザ）で開く。関連付けアプリが無い環境などで開けなくても、例外を
        /// 握りつぶしてエディタの操作自体は継続できるようにする。</summary>
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

            // ダブルクリックの1回目のクリックでの手ブレを、ドラッグ開始と誤認しないようにする
            // （下のIMAGE_DRAG_MIN_HOLD_MSの説明を参照）。
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
            if (!(a_img.Tag is ImageInfo info) || string.IsNullOrEmpty(info.m_originalSrc))
            {
                return null;
            }
            string src = info.m_originalSrc;

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

        /// <summary>右クリック「画像を削除」。ContextImageが埋め込まれているInlineUIContainerを、
        /// それが属する段落のInlinesから取り除く（HandleDropでの挿入と対になる操作のため、
        /// 挿入時と同様にRunAsProgrammaticChangeでくるみ、削除に伴う段落文字列の変化を
        /// 自動整形トリガーの対象にしないようにしている）。ダーティ扱い・アウトライン再構築の
        /// 記憶破棄は、Inlinesの変更として通常どおりEditorTextChangedが検知するため、ここで
        /// 明示的なMarkDirty呼び出しは行っていない（HandleDropが挿入時に行っていないのと同じ
        /// 理由）。</summary>
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
        /// <param name="a_src">MarkDownに書かれていたパス/URL。</param>
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
                    bmp.DownloadCompleted += (s, e) => ApplyImageSizing(a_img);
                }

                a_img.Source = bmp;
                ApplyImageSizing(a_img);
            }
            catch
            {
                // 解決できなければ何もしない（その場所は空白のまま表示される）。
            }
        }

        /// <summary>
        /// 画像を元のピクセルサイズを上限に、エディタの表示幅に収まるようにサイズ調整する
        /// （幅がはみ出す場合のみ縮小し、高さだけの理由で縮小することはない）。エディタの
        /// ズームはエディタ全体への一律のLayoutTransformとして適用されるため、100%ズーム時に
        /// 表示幅の100%を上限としておけば、どのズーム倍率でも欠けずに表示され続ける。
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
            return !string.IsNullOrEmpty(ext) && IMAGE_DROP_EXTENSIONS.Contains(ext.ToLowerInvariant());
        }

        /// <summary>
        /// エディタへの画像ファイルドラッグ&amp;ドロップの入口。RichTextBoxは自前のテキスト用
        /// ドラッグ&amp;ドロップ処理を内部に持っており、通常のバブリング方式のDragEnter/DragOver/
        /// Dropイベントだと横取りされてしまうため、これらはPreview（トンネリング）方式の
        /// ハンドラとしてXAML側で配線されている必要がある。
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
                    // 常にまずOSの一時フォルダに退避する（保存済みの文書であっても同様）。
                    // こうすることで、前回保存以降に追加された画像は、ユーザーが明示的に保存する
                    // までは実際の"<ファイル名>.images"フォルダに一切触れない。実フォルダへの
                    // 移動は RelocatePendingTempImages が Save / Save As のたびに行う。
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
        /// 文書の保存先フォルダが判明したタイミング（初回のSave/Save As）で呼ばれる。OSの
        /// 一時フォルダに退避されていた画像を、保存先の隣にある「&lt;ファイル名&gt;.images」
        /// フォルダ（ファイルごとに専用、なければ作成する）へ移動し、各画像が記憶している
        /// パスをMarkDown書き出し用の最終的な相対パスに更新する。ファイルごとに専用の
        /// フォルダ名（拡張子を除いたファイル名+".images"）を使うことで、同じフォルダに
        /// 複数のMarkDownファイルを保存しても、画像が混在せず区別できるようにしている。
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
                if (!(img.Tag is ImageInfo info) || string.IsNullOrEmpty(info.m_originalSrc))
                {
                    continue;
                }
                if (!Path.IsPathRooted(info.m_originalSrc))
                {
                    continue; // 既に相対パスなら何もしない
                }

                string fullSrc;
                try { fullSrc = Path.GetFullPath(info.m_originalSrc); } catch { continue; }
                if (!fullSrc.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // このウィンドウの一時ファイルではない
                }

                string destPath = CopyFileWithDedup(fullSrc, Path.Combine(currentFileDirectory, imagesFolderName));
                if (null == destPath)
                {
                    continue;
                }

                info.m_originalSrc = imagesFolderName + "/" + Path.GetFileName(destPath);
                SetImageSource(img, info.m_originalSrc);

                try { File.Delete(fullSrc); } catch { /* 削除できなくても致命的ではない */ }
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
                    SetImageSource(img, info.m_originalSrc);
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
