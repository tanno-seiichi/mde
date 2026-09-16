// Models.cs
//
// mde (Markdown インラインエディタ) の一部。
// 複数のクラスで共有される、状態を持たない小さなデータ保持用クラス群。

using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;

namespace mde
{
    /// <summary>
    /// 埋め込み画像(Image要素)のTagに設定するメタデータ。どこから来た画像か、
    /// 保存時にどう書き戻すかを覚えておく。
    /// </summary>
    public class ImageInfo
    {
        // 自動実装プロパティにしているのは、WPFのクリップボードXaml形式（RichTextBox間の
        // コピー&ペーストで使用）がXamlWriterで公開プロパティのみをシリアライズするため。
        // フィールドのままだと貼り付け時にTagの内容が失われる。

        /// <summary>Markdown/HTML上に書かれていた元のsrc（相対パス・絶対パス・URLなど）。</summary>
        public string OriginalSrc { get; set; }

        /// <summary>alt属性（代替テキスト）。</summary>
        public string Alt { get; set; }

        /// <summary>HTML形式の場合のstyle属性。Markdown形式では未使用。</summary>
        public string Style { get; set; }

        /// <summary>元の記法。"html"（&lt;img&gt;タグ）または"md"（![alt](src)）。</summary>
        public string Format { get; set; }

        /// <summary>Markdown形式の場合の、![alt](src "title")のタイトル部分（省略時はnull）。</summary>
        public string Title { get; set; }
    }

    /// <summary>
    /// 水平線（&lt;hr&gt;相当）の段落（Paragraph）のTagに設定するマーカー用クラス。
    /// 中身を持たない目印としてのみ使う。
    /// </summary>
    public class HorizontalRuleInfo
    {
    }

    /// <summary>
    /// カスタムジャンプ先（&lt;a id="mytag"&gt;&lt;/a&gt; で作られる、見出し以外の任意の場所への
    /// ジャンプ先マーカー）の、目印用の空のRunのTagに設定するメタデータ。
    /// </summary>
    public class AnchorInfo
    {
        /// <summary>アンカーのid（[text](#id) からのジャンプ先として参照される）。</summary>
        public string Id { get; set; }
    }

    /// <summary>
    /// コードブロックの段落（Paragraph）のTagに設定するメタデータ。言語タグを覚えておく。
    /// </summary>
    public class CodeBlockInfo
    {
        /// <summary>```の直後に書かれた言語名（例: "csharp"）。未指定なら空文字。</summary>
        public string Language { get; set; } = "";
    }

    /// <summary>
    /// 通常の段落中に書かれた&lt;br&gt;による行内改行の「続き」段落（Paragraph）のTagに
    /// 設定するマーカー用クラス。中身を持たない目印としてのみ使う。表のセル・箇条書き項目と
    /// 同じ理由（WPFのLineBreakとIMEの組み合わせの不具合対策。HeadingCodeBlockEditor.
    /// InsertParagraphSplitAtCaretのコメント参照）で、LineBreakではなく独立した段落として
    /// 表現するために使う（MarkdownConverter.MarkdownToDocument・DocumentToMarkdown参照）。
    /// </summary>
    public class BrContinuationInfo
    {
    }

    /// <summary>
    /// リンクのRun要素のTagに設定するメタデータ。リンク先URLと、山括弧形式（自動リンク）
    /// で読み込まれたかどうかを覚えておく。
    /// </summary>
    public class LinkInfo
    {
        /// <summary>リンク先URL。メールアドレス自動リンクの場合は "mailto:" 付きで格納する。</summary>
        public string Url { get; set; }

        /// <summary>true の場合、&lt;url&gt; 形式（山括弧の自動リンク）から読み込まれたことを示す。</summary>
        public bool IsAutoLinkFlg { get; set; }

        /// <summary>Markdown形式の場合の、[text](url "title")のタイトル部分（省略時はnull）。</summary>
        public string Title { get; set; }

        /// <summary>true の場合、&lt;email@example.com&gt; 形式（山括弧のメール自動リンク）から
        /// 読み込まれたことを示す。保存時に mailto: を除いた元のアドレス形式で書き戻すために使う。</summary>
        public bool IsEmailAutoLinkFlg { get; set; }
    }

    /// <summary>アウトラインペインの1項目（見出し1つ分）。FileSystemItemと同様、折りたたみ可能な
    /// ツリーとして扱うため子見出し一覧・展開状態・選択状態を持つ。</summary>
    public class OutlineEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>見出しのテキスト。</summary>
        public string Text { get; set; }

        /// <summary>見出しレベル（1〜6）。</summary>
        public int Level { get; set; }

        /// <summary>この見出しが属する段落（クリック時にスクロール先として使う）。</summary>
        public Paragraph Target { get; set; }

        /// <summary>アウトラインの表示上のフォントサイズ（レベル1〜2は少し大きめ）。インデント幅は
        /// TreeViewの入れ子構造から自動的に決まるため、フォルダビューと違って個別には持たない。</summary>
        public double FontSizeValue => Level <= 2 ? 13 : 12;

        private bool m_isExpandedFlg = true;

        /// <summary>TreeViewの展開状態（バインディング用）。既定はtrue。OutlineManager.Refresh
        /// で一覧を作り直す際、同じParagraphの見出しは元の展開状態を引き継ぐ。</summary>
        public bool IsExpanded
        {
            get => m_isExpandedFlg;
            set
            {
                if (m_isExpandedFlg == value)
                {
                    return;
                }
                m_isExpandedFlg = value;
                OnPropertyChanged(nameof(IsExpanded));
            }
        }

        private bool m_isSelectedFlg;

        /// <summary>TreeViewの選択状態（バインディング用）。</summary>
        public bool IsSelected
        {
            get => m_isSelectedFlg;
            set
            {
                if (m_isSelectedFlg == value)
                {
                    return;
                }
                m_isSelectedFlg = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        /// <summary>
        /// 子見出し一覧。自分より深いレベルの見出しのうち、次に自分と同じか浅いレベルの見出しが
        /// 現れるまでの区間にあるものが子として入る（OutlineManager.Refresh参照）。
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<OutlineEntry> Children { get; }
            = new System.Collections.ObjectModel.ObservableCollection<OutlineEntry>();

        private bool m_isSearchMatchFlg;

        /// <summary>「すべて検索」の結果、この見出しの区間（または、折りたたまれている場合は
        /// その子孫の見出しの区間）に一致箇所があるかどうか。アウトラインペインでの強調表示に使う。</summary>
        public bool IsSearchMatch
        {
            get => m_isSearchMatchFlg;
            set
            {
                if (m_isSearchMatchFlg == value)
                {
                    return;
                }
                m_isSearchMatchFlg = value;
                OnPropertyChanged(nameof(IsSearchMatch));
            }
        }

        private void OnPropertyChanged(string a_propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(a_propertyName));
    }

    /// <summary>
    /// フォルダツリーペインの1ノード（ファイルまたはフォルダ）。ダーティ（未保存の変更あり）
    /// マーカーの表示をライブ更新するため INotifyPropertyChanged を実装している。
    /// </summary>
    public class FileSystemItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool m_isDirtyFlg;

        /// <summary>ファイル名（拡張子込み）。フォルダの場合はフォルダ名。</summary>
        public string Name { get; set; }

        /// <summary>絶対パス。「読み込み中…」のプレースホルダー子ノードでは null。</summary>
        public string FullPath { get; set; }

        /// <summary>フォルダかどうか。</summary>
        public bool IsDirectory { get; set; }

        private bool m_isExpandedFlg;

        /// <summary>TreeViewの展開状態（バインディング用）。コードから選択・展開する際にも
        /// 画面へ反映されるよう、変更通知付きのプロパティにしている。</summary>
        public bool IsExpanded
        {
            get => m_isExpandedFlg;
            set
            {
                if (m_isExpandedFlg == value)
                {
                    return;
                }
                m_isExpandedFlg = value;
                OnPropertyChanged(nameof(IsExpanded));
            }
        }

        private bool m_isSelectedFlg;

        /// <summary>TreeViewの選択状態（バインディング用）。保存したファイルをフォルダビューで
        /// 選択状態にする、といった用途でコード側から設定する。</summary>
        public bool IsSelected
        {
            get => m_isSelectedFlg;
            set
            {
                if (m_isSelectedFlg == value)
                {
                    return;
                }
                m_isSelectedFlg = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        /// <summary>
        /// 子ノード一覧（フォルダの場合）。展開されるまでは「読み込み中…」の
        /// プレースホルダー1件だけが入っており、実際の子は遅延読み込みされる。
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<FileSystemItem> Children { get; }
            = new System.Collections.ObjectModel.ObservableCollection<FileSystemItem>();

        /// <summary>未保存の変更があるファイルかどうか。変更すると DisplayName も更新される。</summary>
        public bool IsDirty
        {
            get => m_isDirtyFlg;
            set
            {
                if (m_isDirtyFlg == value)
                {
                    return;
                }
                m_isDirtyFlg = value;
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        private bool m_isSearchMatchFlg;

        /// <summary>「すべて検索」（フォルダ全体）の結果、このファイル（または、これを含む
        /// フォルダ）に一致箇所があるかどうか。フォルダツリーペインでの強調表示に使う。</summary>
        public bool IsSearchMatch
        {
            get => m_isSearchMatchFlg;
            set
            {
                if (m_isSearchMatchFlg == value)
                {
                    return;
                }
                m_isSearchMatchFlg = value;
                OnPropertyChanged(nameof(IsSearchMatch));
            }
        }

        /// <summary>画面表示用の名前。未保存の変更があるファイルには "* " を先頭に付ける。</summary>
        public string DisplayName => IsDirty ? "* " + Name : Name;

        private void OnPropertyChanged(string a_propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(a_propertyName));
    }
}
