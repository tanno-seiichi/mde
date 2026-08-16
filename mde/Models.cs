// Models.cs
//
// mde (MarkDown インラインエディタ) の一部。
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
        /// <summary>MarkDown/HTML上に書かれていた元のsrc（相対パス・絶対パス・URLなど）。</summary>
        public string OriginalSrc;

        /// <summary>alt属性（代替テキスト）。</summary>
        public string Alt;

        /// <summary>HTML形式の場合のstyle属性。MarkDown形式では未使用。</summary>
        public string Style;

        /// <summary>元の記法。"html"（&lt;img&gt;タグ）または"md"（![alt](src)）。</summary>
        public string Format;
    }

    /// <summary>
    /// カスタムジャンプ先（&lt;a id="mytag"&gt;&lt;/a&gt; で作られる、見出し以外の任意の場所への
    /// ジャンプ先マーカー）の、目印用の空のRunのTagに設定するメタデータ。
    /// </summary>
    public class AnchorInfo
    {
        /// <summary>アンカーのid（[text](#id) からのジャンプ先として参照される）。</summary>
        public string Id;
    }

    /// <summary>
    /// コードブロックの段落（Paragraph）のTagに設定するメタデータ。言語タグを覚えておく。
    /// </summary>
    public class CodeBlockInfo
    {
        /// <summary>```の直後に書かれた言語名（例: "csharp"）。未指定なら空文字。</summary>
        public string Language = "";
    }

    /// <summary>
    /// リンクのRun要素のTagに設定するメタデータ。リンク先URLと、山括弧形式（自動リンク）
    /// で読み込まれたかどうかを覚えておく。
    /// </summary>
    public class LinkInfo
    {
        /// <summary>リンク先URL。</summary>
        public string Url;

        /// <summary>true の場合、&lt;url&gt; 形式（山括弧の自動リンク）から読み込まれたことを示す。</summary>
        public bool IsAutoLink;
    }

    /// <summary>アウトラインペインの1行分（見出しのテキスト・レベル・対応する段落）。</summary>
    public class OutlineEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>見出しのテキスト。</summary>
        public string Text { get; set; }

        /// <summary>見出しレベル（1〜6）。</summary>
        public int Level { get; set; }

        /// <summary>この見出しが属する段落（クリック時にスクロール先として使う）。</summary>
        public Paragraph Target { get; set; }

        /// <summary>アウトラインの表示上のインデント幅（レベルに応じて広がる。XAML側でMarginに
        /// バインドされる）。</summary>
        public Thickness Indent => new Thickness((Level - 1) * 14, 0, 0, 0);

        /// <summary>アウトラインの表示上のフォントサイズ（レベル1〜2は少し大きめ）。</summary>
        public double FontSizeValue => Level <= 2 ? 13 : 12;

        private bool isSearchMatch;

        /// <summary>「すべて検索」の結果、この見出しの区間に一致箇所があるかどうか。
        /// アウトラインペインでの強調表示に使う。</summary>
        public bool IsSearchMatch
        {
            get => isSearchMatch;
            set
            {
                if (isSearchMatch == value) return;
                isSearchMatch = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSearchMatch)));
            }
        }
    }

    /// <summary>
    /// フォルダツリーペインの1ノード（ファイルまたはフォルダ）。ダーティ（未保存の変更あり）
    /// マーカーの表示をライブ更新するため INotifyPropertyChanged を実装している。
    /// </summary>
    public class FileSystemItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool isDirty;

        /// <summary>ファイル名（拡張子込み）。フォルダの場合はフォルダ名。</summary>
        public string Name { get; set; }

        /// <summary>絶対パス。「読み込み中…」のプレースホルダー子ノードでは null。</summary>
        public string FullPath { get; set; }

        /// <summary>フォルダかどうか。</summary>
        public bool IsDirectory { get; set; }

        private bool isExpanded;

        /// <summary>TreeViewの展開状態（バインディング用）。コードから選択・展開する際にも
        /// 画面へ反映されるよう、変更通知付きのプロパティにしている。</summary>
        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (isExpanded == value) return;
                isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
            }
        }

        private bool isSelected;

        /// <summary>TreeViewの選択状態（バインディング用）。保存したファイルをフォルダビューで
        /// 選択状態にする、といった用途でコード側から設定する。</summary>
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value) return;
                isSelected = value;
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
            get => isDirty;
            set
            {
                if (isDirty == value) return;
                isDirty = value;
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        private bool isSearchMatch;

        /// <summary>「すべて検索」（フォルダ全体）の結果、このファイル（または、これを含む
        /// フォルダ）に一致箇所があるかどうか。フォルダツリーペインでの強調表示に使う。</summary>
        public bool IsSearchMatch
        {
            get => isSearchMatch;
            set
            {
                if (isSearchMatch == value) return;
                isSearchMatch = value;
                OnPropertyChanged(nameof(IsSearchMatch));
            }
        }

        /// <summary>画面表示用の名前。未保存の変更があるファイルには "* " を先頭に付ける。</summary>
        public string DisplayName => IsDirty ? "* " + Name : Name;

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
