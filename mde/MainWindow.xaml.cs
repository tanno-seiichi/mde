using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
    public partial class MainWindow : Window
    {
        private bool isSourceMode = false;
        private bool isProgrammaticChange = false;

        private string currentFilePath = null;
        private string currentFileDirectory = null;
        private string loadedFolderRootPath = null;

        // Files with changes (typically from a folder-wide replace, or switching away from an
        // edited file) that have NOT been written to disk yet. Key = absolute file path.
        private readonly Dictionary<string, string> pendingFileEdits =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Whether the currently open file (currentFilePath) has unsaved changes.
        private bool currentFileIsDirty = false;

        // Each file's original line-ending style ("\r\n" or "\n"), detected when first read from
        // disk, so saving doesn't silently convert CRLF files to LF (our internal markdown string
        // building always uses bare "\n").
        private readonly Dictionary<string, string> fileLineEndings =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // For each top-level Block still exactly as it was when the file was loaded, the original
        // markdown source text that produced it. On save, blocks still present here are written
        // back verbatim (preserving exact original formatting); any block that's been edited is
        // removed from this table (see InvalidateOriginalText) so it gets freshly regenerated
        // instead. Keyed by object identity, so the entry is automatically discarded once the
        // Block itself is no longer reachable.
        private readonly ConditionalWeakTable<Block, OriginalTextHolder> originalBlockText =
            new ConditionalWeakTable<Block, OriginalTextHolder>();

        private class OriginalTextHolder
        {
            public string Text;
        }

        private Paragraph ctxParagraph;
        private TableCell ctxCell;
        private Image ctxImage;

        private double zoomLevel = 1.0;

        private readonly ObservableCollection<OutlineEntry> outlineItems = new ObservableCollection<OutlineEntry>();
        private readonly ObservableCollection<FileSystemItem> folderRoots = new ObservableCollection<FileSystemItem>();

        private static readonly Brush HeaderBackground = new SolidColorBrush(Color.FromRgb(0xE5, 0xEF, 0xEC));
        private static readonly Brush CellBorder = new SolidColorBrush(Color.FromRgb(0xE3, 0xDD, 0xCC));
        private static readonly Brush CodeBlockBackground = new SolidColorBrush(Color.FromRgb(0xEC, 0xE8, 0xDC));

        private static readonly Regex InlineImageRegex = new Regex(
            "(<img\\s+[^>]*?/?>)|(!\\[([^\\]]*)\\]\\(([^)\\s]+)(?:\\s+\"[^\"]*\")?\\))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Unique per open document/window, so simultaneously edited files never collide with each
        // other's dropped-image filenames while staged in the OS temp folder.
        private readonly string instanceTempId = Guid.NewGuid().ToString("N");

        public MainWindow()
        {
            InitializeComponent();
            this.Title = Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version;
            OutlineList.ItemsSource = outlineItems;
            FolderTree.ItemsSource = folderRoots;
            DataObject.AddCopyingHandler(Editor, Editor_Copying);
            DataObject.AddPastingHandler(Editor, Editor_Pasting);
            LoadIntroContent();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "mde", instanceTempId);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort cleanup only
            }
        }

        public class ImageInfo
        {
            public string OriginalSrc;
            public string Alt;
            public string Style;
            public string Format; // "html" or "md"
        }

        public class CodeBlockInfo
        {
            public string Language = "";
        }

        public class OutlineEntry
        {
            public int Level { get; set; }
            public string Text { get; set; }
            public Paragraph TargetParagraph { get; set; }
            public Thickness Indent => new Thickness((Level - 1) * 14, 0, 0, 0);
            public double FontSizeValue => Level <= 2 ? 13 : 12;
        }

        public class FileSystemItem : INotifyPropertyChanged
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public bool IsDirectory { get; set; }
            public bool IsExpanded { get; set; }
            public ObservableCollection<FileSystemItem> Children { get; } = new ObservableCollection<FileSystemItem>();

            private bool isDirty;
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

            public string DisplayName => IsDirty ? "* " + Name : Name;

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ======================================================================
        //  Initial content
        // ======================================================================
        private void LoadIntroContent()
        {
#if false
            string intro = string.Join("\n", new[]
            {
                "# MarkDown インラインエディタ（デスクトップ版）",
                "",
                "このエディタは、編集ペインとプレビューペインが分かれていません。この画面に直接書き込むと、そのままMarkDownとして整形されます。",
                "",
                "* 「* 」と入力すると箇条書きに変わります",
                "* Enterキーで次の項目に進みます",
                "* Shift+Enterで項目内改行、Tabで字下げ（Shift+Tabで戻す）ができます",
                "* 空の項目でEnterキーを押すと箇条書きを抜けます",
                "",
                "「# 」〜「###### 」と入力すると見出しに変わります。右クリックでも見出しレベルを選べます。",
                "",
                "右クリックで表の挿入、行・列の削除、ソースモードへの切り替えもできます。",
                "",
                "「```」または「```言語名」と入力してEnterを押すとコードブロックになります。抜けるときはコードブロックの上下の行をクリックしてください。",
                "",
                "「開く…」でMarkDownファイルを選ぶと、同じフォルダにある画像も自動的に表示されます。"
            });
            MarkdownToDocument(intro, Editor.Document);
#endif
            RefreshOutline();
            currentFileIsDirty = false;
        }

        // ======================================================================
        //  Auto-convert "* " / "# ".."###### " while typing
        // ======================================================================
        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isSourceMode) return;
            RefreshOutline();

            currentFileIsDirty = true;
            RefreshFolderTreeDirtyMarkers();
            InvalidateOriginalText(Editor.CaretPosition);

            if (isProgrammaticChange) return;

            var para = Editor.CaretPosition?.Paragraph;
            if (para == null) return;
            if (!(para.Parent is FlowDocument)) return; // only top-level paragraphs auto-convert
            if (para.Tag is CodeBlockInfo) return; // no auto-formatting inside code blocks

            string text = new TextRange(para.ContentStart, para.ContentEnd).Text;
            text = text.TrimEnd('\r', '\n');

            if (Regex.IsMatch(text, "^\\*[ \u00A0]$"))
            {
                ConvertParagraphToListItem(para);
                return;
            }
            var m = Regex.Match(text, "^(#{1,6})[ \u00A0]$");
            if (m.Success)
            {
                ConvertParagraphToHeading(para, m.Groups[1].Value.Length);
            }
        }

        /// <summary>Walks up from a position to the top-level Block that's a direct child of the
        /// FlowDocument (the Paragraph itself if it's already top-level, or the containing List/
        /// Table if the position is inside a list item or table cell).</summary>
        private Block GetTopLevelBlock(TextPointer position)
        {
            var para = position?.Paragraph;
            if (para == null) return null;

            DependencyObject node = para;
            while (node != null)
            {
                if (node is Block block && ReferenceEquals(block.Parent, Editor.Document))
                    return block;

                node = (node as TextElement)?.Parent;
            }
            return null;
        }

        /// <summary>Marks the block at (or containing) a position as edited, so it will be freshly
        /// regenerated on save instead of reusing its original source text verbatim.</summary>
        private void InvalidateOriginalText(TextPointer position)
        {
            var block = GetTopLevelBlock(position);
            if (block != null) originalBlockText.Remove(block);
        }

        /// <summary>Same as InvalidateOriginalText, but starting from a Block reference directly
        /// rather than a TextPointer.</summary>
        private void InvalidateOriginalTextForBlock(Block block)
        {
            DependencyObject node = block;
            while (node != null)
            {
                if (node is Block b && ReferenceEquals(b.Parent, Editor.Document))
                {
                    originalBlockText.Remove(b);
                    return;
                }
                node = (node as TextElement)?.Parent;
            }
        }

        private void ConvertParagraphToListItem(Paragraph p)
        {
            isProgrammaticChange = true;
            try
            {
                Block prev = p.PreviousBlock;
                var newLiPara = new Paragraph();
                var newLi = new ListItem(newLiPara);

                if (prev is List prevList)
                {
                    prevList.ListItems.Add(newLi);
                    Editor.Document.Blocks.Remove(p);
                }
                else
                {
                    var list = new List { MarkerStyle = TextMarkerStyle.Disc };
                    list.ListItems.Add(newLi);
                    Editor.Document.Blocks.InsertBefore(p, list);
                    Editor.Document.Blocks.Remove(p);
                }
                Editor.CaretPosition = newLiPara.ContentStart;
            }
            finally
            {
                isProgrammaticChange = false;
            }
            Editor.Focus();
        }

        private void ConvertParagraphToHeading(Paragraph p, int level)
        {
            isProgrammaticChange = true;
            try
            {
                p.Inlines.Clear();
                ApplyHeadingStyle(p, level);
                Editor.CaretPosition = p.ContentStart;
            }
            finally
            {
                isProgrammaticChange = false;
            }
        }

        private void ClearParagraphSpecialStyling(Paragraph p)
        {
            p.Background = null;
            p.Padding = new Thickness(0);
            p.BorderThickness = new Thickness(0);
            p.BorderBrush = null;
            p.ClearValue(TextElement.FontFamilyProperty);
        }

        private void ApplyHeadingStyle(Paragraph p, int level)
        {
            ClearParagraphSpecialStyling(p);
            p.Tag = level == 0 ? null : (object)level;
            if (level == 0)
            {
                p.FontSize = 16;
                p.FontWeight = FontWeights.Normal;
                p.Margin = new Thickness(0, 0, 0, 14);
            }
            else
            {
                double[] sizes = { 0, 30, 24, 20, 18, 16.5, 15.5 };
                p.FontSize = sizes[level];
                p.FontWeight = FontWeights.Bold;
                p.Margin = new Thickness(0, level <= 2 ? 20 : 14, 0, 10);
                if (level <= 2)
                {
                    p.BorderBrush = CellBorder;
                    p.BorderThickness = new Thickness(0, 0, 0, level == 1 ? 2 : 1);
                    p.Padding = new Thickness(0, 0, 0, 4);
                }
                else
                {
                    p.BorderThickness = new Thickness(0);
                }
            }
        }

        // ---------------- code block ("```language") ----------------
        private void ConvertParagraphToCodeBlock(Paragraph p, string language = "")
        {
            isProgrammaticChange = true;
            try
            {
                p.Inlines.Clear();
                ApplyCodeBlockStyle(p, language);

                var trailingPara = new Paragraph();
                Editor.Document.Blocks.InsertAfter(p, trailingPara);

                Editor.CaretPosition = p.ContentStart;
            }
            finally
            {
                isProgrammaticChange = false;
            }
            Editor.Focus();
        }

        private void ApplyCodeBlockStyle(Paragraph p, string language = "")
        {
            ClearParagraphSpecialStyling(p);
            p.Tag = new CodeBlockInfo { Language = language ?? "" };
            p.FontFamily = new FontFamily("Consolas");
            p.FontSize = 13.5;
            p.FontWeight = FontWeights.Normal;
            p.Background = CodeBlockBackground;
            p.Padding = new Thickness(14, 10, 14, 10);
            p.Margin = new Thickness(0, 4, 0, 14);
            p.BorderBrush = CellBorder;
            p.BorderThickness = new Thickness(1);
            ToolTipService.SetToolTip(p, string.IsNullOrEmpty(language) ? "コードブロック" : "コードブロック (" + language + ")");
        }

        /// <summary>
        /// Removes up to one indent level (a leading tab, or up to 4 leading spaces) from the start
        /// of the current line within a code block paragraph. Bounded so it never reads or deletes
        /// past the current line or past the paragraph's own content.
        /// </summary>
        private void OutdentCodeLine(Paragraph p)
        {
            InvalidateOriginalTextForBlock(p);
            var caret = Editor.CaretPosition;
            var lineStart = caret.GetLineStartPosition(0) ?? p.ContentStart;

            TextPointer upperBound = p.ContentEnd;
            var nextLineStart = caret.GetLineStartPosition(1);
            if (nextLineStart != null && nextLineStart.CompareTo(upperBound) < 0)
                upperBound = nextLineStart;

            var probe = lineStart.GetPositionAtOffset(4);
            if (probe == null || probe.CompareTo(upperBound) > 0) probe = upperBound;
            if (probe.CompareTo(lineStart) < 0) probe = lineStart;

            string prefix = new TextRange(lineStart, probe).Text;

            int removeCount = 0;
            if (prefix.StartsWith("\t"))
            {
                removeCount = 1;
            }
            else
            {
                while (removeCount < prefix.Length && removeCount < 4 && prefix[removeCount] == ' ')
                    removeCount++;
            }
            if (removeCount == 0) return;

            var removeEnd = lineStart.GetPositionAtOffset(removeCount);
            if (removeEnd == null) return;

            isProgrammaticChange = true;
            try
            {
                // caret is a live TextPointer: it automatically re-anchors as content before it is
                // removed, so no manual offset math is needed after the deletion below.
                new TextRange(lineStart, removeEnd).Text = "";
            }
            finally
            {
                isProgrammaticChange = false;
            }

            Editor.CaretPosition = caret;
            Editor.Focus();
        }

        // ======================================================================
        //  Key handling: Enter / Shift+Enter / Tab / arrow keys
        // ======================================================================
        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (isSourceMode) return;
            var para = Editor.CaretPosition?.Paragraph;
            if (para == null) return;

            if (e.Key == Key.Enter)
            {
                if (IsInListItem(para, out ListItem li, out List parentList))
                {
                    e.Handled = true;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        InsertLineBreakAtCaret();
                    else
                        HandleListEnter(li, parentList);
                    return;
                }
                if (para.Tag is int level && level > 0)
                {
                    e.Handled = true;
                    HandleHeadingEnter(para);
                    return;
                }
                if (para.Tag is CodeBlockInfo)
                {
                    e.Handled = true;
                    InsertLineBreakAtCaret();
                    return;
                }
                if (para.Parent is TableCell)
                {
                    e.Handled = true;
                    InsertLineBreakAtCaret();
                    return;
                }
                if (para.Parent is FlowDocument)
                {
                    string plainText = new TextRange(para.ContentStart, para.ContentEnd).Text.TrimEnd('\r', '\n');
                    var fenceMatch = Regex.Match(plainText, "^```(\\S*)$");
                    if (fenceMatch.Success)
                    {
                        e.Handled = true;
                        ConvertParagraphToCodeBlock(para, fenceMatch.Groups[1].Value);
                        return;
                    }
                }
                return; // plain paragraph: default WPF behaviour creates a new Paragraph
            }

            if (e.Key == Key.Tab && IsInListItem(para, out ListItem tabLi, out List tabList))
            {
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    OutdentListItem(tabLi, tabList);
                else
                    IndentListItem(tabLi, tabList);
                return;
            }

            if (e.Key == Key.Tab && para.Tag is CodeBlockInfo)
            {
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    OutdentCodeLine(para);
                }
                else
                {
                    Editor.Selection.Text = "\t";
                    Editor.CaretPosition = Editor.Selection.End;
                    Editor.Selection.Select(Editor.CaretPosition, Editor.CaretPosition);
                }
                return;
            }

            if (para.Parent is TableCell cell)
            {
                if (e.Key == Key.Up || e.Key == Key.Down)
                {
                    e.Handled = true;
                    MoveVertical(cell, e.Key == Key.Up ? -1 : 1);
                }
                else if (e.Key == Key.Left && IsCaretAtStart(cell))
                {
                    e.Handled = true;
                    MoveHorizontal(cell, -1);
                }
                else if (e.Key == Key.Right && IsCaretAtEnd(cell))
                {
                    e.Handled = true;
                    MoveHorizontal(cell, 1);
                }
            }
        }

        private bool IsInListItem(Paragraph para, out ListItem li, out List parentList)
        {
            li = para.Parent as ListItem;
            if (li != null)
            {
                parentList = li.Parent as List;
                return parentList != null;
            }
            parentList = null;
            return false;
        }

        private void InsertLineBreakAtCaret()
        {
            Editor.CaretPosition = Editor.CaretPosition.InsertLineBreak();
        }

        private void HandleHeadingEnter(Paragraph headingPara)
        {
            isProgrammaticChange = true;
            try
            {
                var newPara = new Paragraph();
                Editor.Document.Blocks.InsertAfter(headingPara, newPara);
                Editor.CaretPosition = newPara.ContentStart;
            }
            finally
            {
                isProgrammaticChange = false;
            }
        }

        // ---------------- list Enter / indent / outdent ----------------
        private string GetOwnListItemText(ListItem li)
        {
            var ownPara = li.Blocks.FirstBlock as Paragraph;
            if (ownPara == null) return "";
            var sb = new StringBuilder();
            AppendPlainInlineText(ownPara.Inlines, sb);
            string t = sb.ToString().Trim();
            if (t.Length == 0 && HasDescendantImage(ownPara)) return "\u200B";
            return t;
        }

        /// <summary>Concatenates only the literal Run text within an Inlines collection (recursing
        /// into Spans, treating LineBreak as a newline). Unlike TextRange.Text, this never picks up
        /// a list item's marker glyph as if it were part of the text content.</summary>
        private void AppendPlainInlineText(InlineCollection inlines, StringBuilder sb)
        {
            foreach (Inline inline in inlines)
            {
                if (inline is Run run) sb.Append(run.Text);
                else if (inline is Span span) AppendPlainInlineText(span.Inlines, sb);
                else if (inline is LineBreak) sb.Append('\n');
            }
        }

        private bool HasDescendantImage(Paragraph p)
        {
            foreach (Inline inline in p.Inlines)
                if (InlineContainsImage(inline)) return true;
            return false;
        }

        private bool InlineContainsImage(Inline inline)
        {
            if (inline is InlineUIContainer iuc && iuc.Child is Image) return true;
            if (inline is Span span)
                foreach (Inline child in span.Inlines)
                    if (InlineContainsImage(child)) return true;
            return false;
        }

        private void HandleListEnter(ListItem li, List parentList)
        {
            InvalidateOriginalTextForBlock(parentList);
            isProgrammaticChange = true;
            try
            {
                bool hasNestedList = li.Blocks.Count > 1;
                bool isEmpty = !hasNestedList && GetOwnListItemText(li).Length == 0;

                if (isEmpty)
                {
                    List topList = parentList;
                    DependencyObject cursor = parentList.Parent;
                    while (cursor is ListItem ownerLi)
                    {
                        topList = ownerLi.Parent as List;
                        cursor = topList?.Parent;
                    }

                    var newPara = new Paragraph();
                    Editor.Document.Blocks.InsertAfter(topList, newPara);

                    parentList.ListItems.Remove(li);
                    if (parentList.ListItems.Count == 0)
                    {
                        RemoveEmptyList(parentList);
                    }
                    Editor.CaretPosition = newPara.ContentStart;
                }
                else
                {
                    var newLi = new ListItem(new Paragraph());
                    var items = parentList.ListItems.Cast<ListItem>().ToList();
                    int idx = items.IndexOf(li);
                    parentList.ListItems.Clear();
                    for (int k = 0; k < items.Count; k++)
                    {
                        parentList.ListItems.Add(items[k]);
                        if (k == idx) parentList.ListItems.Add(newLi);
                    }
                    Editor.CaretPosition = ((Paragraph)newLi.Blocks.FirstBlock).ContentStart;
                }
            }
            finally
            {
                isProgrammaticChange = false;
            }
            Editor.Focus();
        }

        private void RemoveEmptyList(List list)
        {
            if (list.Parent is ListItem ownerLi)
                ownerLi.Blocks.Remove(list);
            else if (list.Parent is FlowDocument doc)
                doc.Blocks.Remove(list);
        }

        private void IndentListItem(ListItem li, List parentList)
        {
            InvalidateOriginalTextForBlock(parentList);
            ListItem prevLi = null;
            foreach (ListItem item in parentList.ListItems)
            {
                if (item == li) break;
                prevLi = item;
            }
            if (prevLi == null) return; // first item cannot be indented

            isProgrammaticChange = true;
            try
            {
                List nestedList = prevLi.Blocks.Count > 1 ? prevLi.Blocks.LastBlock as List : null;
                if (nestedList == null)
                {
                    nestedList = new List { MarkerStyle = TextMarkerStyle.Circle };
                    prevLi.Blocks.Add(nestedList);
                }
                parentList.ListItems.Remove(li);
                nestedList.ListItems.Add(li);

                if (li.Blocks.FirstBlock is Paragraph fp) Editor.CaretPosition = fp.ContentEnd;
            }
            finally
            {
                isProgrammaticChange = false;
            }
            Editor.Focus();
        }

        private void OutdentListItem(ListItem li, List parentList)
        {
            InvalidateOriginalTextForBlock(parentList);
            if (!(parentList.Parent is ListItem parentLi)) return; // already top level
            if (!(parentLi.Parent is List grandList)) return;

            isProgrammaticChange = true;
            try
            {
                var siblings = parentList.ListItems.Cast<ListItem>().ToList();
                int idx = siblings.IndexOf(li);
                var before = siblings.Take(idx).ToList();
                var after = siblings.Skip(idx + 1).ToList();

                parentList.ListItems.Clear();
                foreach (var b in before) parentList.ListItems.Add(b);

                if (after.Count > 0)
                {
                    List ownNested = new List { MarkerStyle = TextMarkerStyle.Circle };
                    foreach (var a in after) ownNested.ListItems.Add(a);
                    li.Blocks.Add(ownNested);
                }

                var grandItems = grandList.ListItems.Cast<ListItem>().ToList();
                int gIdx = grandItems.IndexOf(parentLi);
                grandList.ListItems.Clear();
                for (int k = 0; k < grandItems.Count; k++)
                {
                    grandList.ListItems.Add(grandItems[k]);
                    if (k == gIdx) grandList.ListItems.Add(li);
                }

                if (parentList.ListItems.Count == 0)
                {
                    parentLi.Blocks.Remove(parentList);
                }

                if (li.Blocks.FirstBlock is Paragraph fp) Editor.CaretPosition = fp.ContentEnd;
            }
            finally
            {
                isProgrammaticChange = false;
            }
            Editor.Focus();
        }

        // ---------------- table cell navigation ----------------
        private bool IsCaretAtStart(TableCell cell)
        {
            var firstPara = cell.Blocks.FirstBlock as Paragraph;
            if (firstPara == null) return true;
            return Editor.CaretPosition.CompareTo(firstPara.ContentStart) <= 0;
        }

        private bool IsCaretAtEnd(TableCell cell)
        {
            var lastPara = cell.Blocks.LastBlock as Paragraph;
            if (lastPara == null) return true;
            return Editor.CaretPosition.CompareTo(lastPara.ContentEnd) >= 0;
        }

        private void MoveVertical(TableCell cell, int dir)
        {
            if (!(cell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;
            int rIdx = rg.Rows.IndexOf(row);
            int cIdx = row.Cells.IndexOf(cell);
            int targetIdx = rIdx + dir;
            if (targetIdx < 0 || targetIdx >= rg.Rows.Count) return;
            var targetRow = rg.Rows[targetIdx];
            if (cIdx < targetRow.Cells.Count && targetRow.Cells[cIdx].Blocks.LastBlock is Paragraph tp)
                Editor.CaretPosition = tp.ContentEnd;
        }

        private void MoveHorizontal(TableCell cell, int dir)
        {
            if (!(cell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;
            int rIdx = rg.Rows.IndexOf(row);
            int cIdx = row.Cells.IndexOf(cell);

            if (dir == 1)
            {
                if (cIdx + 1 < row.Cells.Count)
                {
                    if (row.Cells[cIdx + 1].Blocks.FirstBlock is Paragraph np) Editor.CaretPosition = np.ContentStart;
                    return;
                }
                if (rIdx + 1 < rg.Rows.Count)
                {
                    var nr = rg.Rows[rIdx + 1];
                    if (nr.Cells.Count > 0 && nr.Cells[0].Blocks.FirstBlock is Paragraph np2) Editor.CaretPosition = np2.ContentStart;
                }
            }
            else
            {
                if (cIdx - 1 >= 0)
                {
                    if (row.Cells[cIdx - 1].Blocks.LastBlock is Paragraph pp) Editor.CaretPosition = pp.ContentEnd;
                    return;
                }
                if (rIdx - 1 >= 0)
                {
                    var pr = rg.Rows[rIdx - 1];
                    if (pr.Cells.Count > 0 && pr.Cells[pr.Cells.Count - 1].Blocks.LastBlock is Paragraph pp2)
                        Editor.CaretPosition = pp2.ContentEnd;
                }
            }
        }

        // ======================================================================
        //  Context menu
        // ======================================================================
        private void Editor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (isSourceMode) { e.Handled = true; return; }

            Point pos = Mouse.GetPosition(Editor);
            TextPointer tp = Editor.GetPositionFromPoint(pos, true);
            ctxParagraph = tp?.Paragraph;
            ctxCell = ctxParagraph?.Parent as TableCell;

            var hit = VisualTreeHelper.HitTest(Editor, pos);
            ctxImage = FindVisualAncestorOrSelf<Image>(hit?.VisualHit);

            bool inTable = ctxCell != null;
            bool inCodeBlock = ctxParagraph?.Tag is CodeBlockInfo;
            HeadingMenuItem.Visibility = inTable ? Visibility.Collapsed : Visibility.Visible;
            InsertTableMenuItem.Visibility = inTable ? Visibility.Collapsed : Visibility.Visible;
            InsertRowAboveMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            InsertRowBelowMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            InsertColumnLeftMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            InsertColumnRightMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            DeleteRowMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            DeleteColumnMenuItem.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
            CopyCodeBlockMenuItem.Visibility = inCodeBlock ? Visibility.Visible : Visibility.Collapsed;
            SaveImageMenuItem.Visibility = ctxImage != null ? Visibility.Visible : Visibility.Collapsed;
            ToggleModeMenuItem.Header = isSourceMode ? "MarkDownモードに切り替え" : "ソースモードに切り替え";
        }

        /// <summary>
        /// Copies the entire code block as ready-to-use MarkDown, including the ``` fences and
        /// language tag - distinct from a normal Ctrl+C of selected text, which copies just the
        /// raw code content.
        /// </summary>
        private void CopyCodeBlockItem_Click(object sender, RoutedEventArgs e)
        {
            if (ctxParagraph == null || !(ctxParagraph.Tag is CodeBlockInfo)) return;
            string md = BlockToMarkdown(ctxParagraph);
            if (!string.IsNullOrEmpty(md)) Clipboard.SetText(md);
        }

        private void SourceEditor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // The TextBox uses its own static ContextMenu (defined in XAML); nothing extra to prepare here.
        }

        private void HeadingItem_Click(object sender, RoutedEventArgs e)
        {
            if (ctxParagraph == null) return;
            int level = int.Parse((string)((MenuItem)sender).Tag);
            InvalidateOriginalTextForBlock(ctxParagraph);
            isProgrammaticChange = true;
            try { ApplyHeadingStyle(ctxParagraph, level); }
            finally { isProgrammaticChange = false; }
            RefreshOutline();
        }

        private void InsertTableItem_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new TableSizeDialog { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                InsertTable(dlg.Rows, dlg.Columns);
            }
        }

        private void InsertTable(int rows, int cols)
        {
            var table = new Table();
            for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn());
            var rg = new TableRowGroup();
            table.RowGroups.Add(rg);

            var headerRow = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                var cell = new TableCell(new Paragraph())
                {
                    FontWeight = FontWeights.Bold,
                    Background = HeaderBackground,
                    BorderBrush = CellBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                headerRow.Cells.Add(cell);
            }
            rg.Rows.Add(headerRow);

            for (int r = 0; r < rows - 1; r++)
            {
                var row = new TableRow();
                for (int c = 0; c < cols; c++)
                {
                    var cell = new TableCell(new Paragraph())
                    {
                        BorderBrush = CellBorder,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 6, 8, 6)
                    };
                    row.Cells.Add(cell);
                }
                rg.Rows.Add(row);
            }

            var trailingPara = new Paragraph();

            isProgrammaticChange = true;
            try
            {
                if (ctxParagraph != null && ctxParagraph.Parent is FlowDocument)
                {
                    Editor.Document.Blocks.InsertAfter(ctxParagraph, table);
                    Editor.Document.Blocks.InsertAfter(table, trailingPara);
                }
                else
                {
                    Editor.Document.Blocks.Add(table);
                    Editor.Document.Blocks.Add(trailingPara);
                }
            }
            finally
            {
                isProgrammaticChange = false;
            }

            if (headerRow.Cells[0].Blocks.FirstBlock is Paragraph hp) Editor.CaretPosition = hp.ContentStart;
            Editor.Focus();
        }

        private void InsertRowAboveItem_Click(object sender, RoutedEventArgs e) => InsertRow(true);
        private void InsertRowBelowItem_Click(object sender, RoutedEventArgs e) => InsertRow(false);
        private void InsertColumnLeftItem_Click(object sender, RoutedEventArgs e) => InsertColumn(true);
        private void InsertColumnRightItem_Click(object sender, RoutedEventArgs e) => InsertColumn(false);

        private void InsertRow(bool above)
        {
            if (ctxCell == null) return;
            InvalidateOriginalText(ctxCell.ContentStart);
            if (!(ctxCell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;

            int colCount = row.Cells.Count;
            var newRow = new TableRow();
            for (int c = 0; c < colCount; c++)
            {
                var cell = new TableCell(new Paragraph())
                {
                    BorderBrush = CellBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                newRow.Cells.Add(cell);
            }

            int idx = rg.Rows.IndexOf(row);
            int insertIdx = above ? idx : idx + 1;
            rg.Rows.Insert(insertIdx, newRow);

            if (newRow.Cells.Count > 0 && newRow.Cells[0].Blocks.FirstBlock is Paragraph np)
                Editor.CaretPosition = np.ContentStart;
            Editor.Focus();
        }

        private void InsertColumn(bool left)
        {
            if (ctxCell == null) return;
            InvalidateOriginalText(ctxCell.ContentStart);
            if (!(ctxCell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;
            if (!(rg.Parent is Table table)) return;

            int colIdx = row.Cells.IndexOf(ctxCell);
            int insertIdx = left ? colIdx : colIdx + 1;
            var rows = rg.Rows.Cast<TableRow>().ToList();

            TableCell firstNewCell = null;
            for (int r = 0; r < rows.Count; r++)
            {
                var targetRow = rows[r];
                var cell = new TableCell(new Paragraph())
                {
                    BorderBrush = CellBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                if (r == 0)
                {
                    cell.FontWeight = FontWeights.Bold;
                    cell.Background = HeaderBackground;
                }

                int idxInRow = Math.Min(insertIdx, targetRow.Cells.Count);
                targetRow.Cells.Insert(idxInRow, cell);
                if (targetRow == row) firstNewCell = cell;
            }

            var newColumn = new TableColumn();
            int colInsertIdx = Math.Min(insertIdx, table.Columns.Count);
            table.Columns.Insert(colInsertIdx, newColumn);

            if (firstNewCell?.Blocks.FirstBlock is Paragraph np) Editor.CaretPosition = np.ContentStart;
            Editor.Focus();
        }

        private void DeleteRowItem_Click(object sender, RoutedEventArgs e)
        {
            if (ctxCell == null) return;
            InvalidateOriginalText(ctxCell.ContentStart);
            if (!(ctxCell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;

            if (rg.Rows.Count <= 1)
            {
                if (rg.Parent is Table table) Editor.Document.Blocks.Remove(table);
                return;
            }
            rg.Rows.Remove(row);
        }

        private void DeleteColumnItem_Click(object sender, RoutedEventArgs e)
        {
            if (ctxCell == null) return;
            InvalidateOriginalText(ctxCell.ContentStart);
            if (!(ctxCell.Parent is TableRow row)) return;
            if (!(row.Parent is TableRowGroup rg)) return;
            if (!(rg.Parent is Table table)) return;

            int colIndex = row.Cells.IndexOf(ctxCell);

            if (row.Cells.Count <= 1)
            {
                Editor.Document.Blocks.Remove(table);
                return;
            }

            foreach (TableRow r in rg.Rows)
            {
                if (colIndex < r.Cells.Count) r.Cells.RemoveAt(colIndex);
            }
            if (table.Columns.Count > colIndex) table.Columns.RemoveAt(colIndex);
        }

        // ======================================================================
        //  Copy / paste: tables round-trip with Excel and other MarkDown apps,
        //  code blocks copy either as raw content or as a full fenced block.
        // ======================================================================
        private List<TableRow> GetTableRows(Table table)
        {
            var rows = new List<TableRow>();
            foreach (TableRowGroup rg in table.RowGroups)
                foreach (TableRow r in rg.Rows) rows.Add(r);
            return rows;
        }

        private Table FindEnclosingTable(TableCell cell)
        {
            if (!(cell.Parent is TableRow row)) return null;
            if (!(row.Parent is TableRowGroup rg)) return null;
            return rg.Parent as Table;
        }

        private class CellRange
        {
            public int MinRow, MaxRow, MinCol, MaxCol;
        }

        /// <summary>
        /// Locates startCell and endCell within the table's row/cell grid and returns the smallest
        /// rectangular range spanning both. Returns null if either cell can't be located.
        /// </summary>
        private CellRange GetSelectedCellRange(List<TableRow> rows, TableCell startCell, TableCell endCell)
        {
            int startRow = -1, startCol = -1, endRow = -1, endCol = -1;
            for (int r = 0; r < rows.Count; r++)
            {
                int c = rows[r].Cells.IndexOf(startCell);
                if (c >= 0) { startRow = r; startCol = c; }
                c = rows[r].Cells.IndexOf(endCell);
                if (c >= 0) { endRow = r; endCol = c; }
            }
            if (startRow < 0 || endRow < 0) return null;

            return new CellRange
            {
                MinRow = Math.Min(startRow, endRow),
                MaxRow = Math.Max(startRow, endRow),
                MinCol = Math.Min(startCol, endCol),
                MaxCol = Math.Max(startCol, endCol)
            };
        }

        private string RangeToTsv(List<TableRow> rows, CellRange range)
        {
            var lines = new List<string>();
            for (int r = range.MinRow; r <= range.MaxRow; r++)
            {
                var cells = rows[r].Cells.Cast<TableCell>().ToList();
                var rowTexts = new List<string>();
                for (int c = range.MinCol; c <= range.MaxCol && c < cells.Count; c++)
                {
                    rowTexts.Add(CellPlainText(cells[c]).Replace('\t', ' ').Replace("\r", " ").Replace("\n", " "));
                }
                lines.Add(string.Join("\t", rowTexts));
            }
            return string.Join("\r\n", lines);
        }

        private string RangeToHtmlFragment(List<TableRow> rows, CellRange range)
        {
            var sb = new StringBuilder();
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;\">");
            for (int r = range.MinRow; r <= range.MaxRow; r++)
            {
                sb.Append("<tr>");
                var cells = rows[r].Cells.Cast<TableCell>().ToList();
                for (int c = range.MinCol; c <= range.MaxCol && c < cells.Count; c++)
                {
                    // Only style as a header if the selection's first row IS the table's own header row.
                    string tag = r == 0 ? "th" : "td";
                    string text = WebUtility.HtmlEncode(CellPlainText(cells[c])).Replace("\n", "<br>");
                    sb.Append('<').Append(tag).Append(" style=\"border:1px solid #999999;padding:4px 8px;\">")
                      .Append(text).Append("</").Append(tag).Append('>');
                }
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        private string CellPlainText(TableCell cell)
        {
            var sb = new StringBuilder();
            foreach (Block b in cell.Blocks)
                if (b is Paragraph p) sb.Append(new TextRange(p.ContentStart, p.ContentEnd).Text);
            return sb.ToString().Trim();
        }

        private string TableToTsv(Table table)
        {
            var lines = new List<string>();
            foreach (var row in GetTableRows(table))
            {
                var cellTexts = row.Cells.Cast<TableCell>()
                    .Select(c => CellPlainText(c).Replace('\t', ' ').Replace("\r", " ").Replace("\n", " "));
                lines.Add(string.Join("\t", cellTexts));
            }
            return string.Join("\r\n", lines);
        }

        private string TableToHtmlFragment(Table table)
        {
            var sb = new StringBuilder();
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;\">");
            var rows = GetTableRows(table);
            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append("<tr>");
                foreach (TableCell cell in rows[r].Cells)
                {
                    string tag = r == 0 ? "th" : "td";
                    string text = WebUtility.HtmlEncode(CellPlainText(cell)).Replace("\n", "<br>");
                    sb.Append('<').Append(tag).Append(" style=\"border:1px solid #999999;padding:4px 8px;\">")
                      .Append(text).Append("</").Append(tag).Append('>');
                }
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        /// <summary>
        /// Wraps an HTML fragment in the CF_HTML clipboard envelope Windows expects (Version/
        /// StartHTML/EndHTML/StartFragment/EndFragment byte offsets), so pasting into Excel, Word,
        /// browsers, etc. is recognized as real HTML rather than plain text. Offsets are computed
        /// in UTF-8 bytes so this is correct for Japanese/other non-ASCII cell content too.
        /// </summary>
        private string BuildHtmlClipboardFragment(string htmlBodyFragment)
        {
            const string htmlPrefix = "<html><body><!--StartFragment-->";
            const string htmlSuffix = "<!--EndFragment--></body></html>";
            const string headerTemplate =
                "Version:0.9\r\n" +
                "StartHTML:{0:0000000000}\r\n" +
                "EndHTML:{1:0000000000}\r\n" +
                "StartFragment:{2:0000000000}\r\n" +
                "EndFragment:{3:0000000000}\r\n";

            int headerByteLength = Encoding.UTF8.GetByteCount(string.Format(headerTemplate, 0, 0, 0, 0));
            int startHtml = headerByteLength;
            int startFragment = startHtml + Encoding.UTF8.GetByteCount(htmlPrefix);
            int endFragment = startFragment + Encoding.UTF8.GetByteCount(htmlBodyFragment);
            int endHtml = endFragment + Encoding.UTF8.GetByteCount(htmlSuffix);

            string header = string.Format(headerTemplate, startHtml, endHtml, startFragment, endFragment);
            return header + htmlPrefix + htmlBodyFragment + htmlSuffix;
        }

        private void Editor_Copying(object sender, DataObjectCopyingEventArgs e)
        {
            if (isSourceMode || e.IsDragDrop) return;

            var selection = Editor.Selection;
            if (selection == null || selection.IsEmpty) return;

            var startCell = selection.Start?.Paragraph?.Parent as TableCell;
            var endCell = selection.End?.Paragraph?.Parent as TableCell;
            var anyCell = startCell ?? endCell;
            if (anyCell == null) return;

            var table = FindEnclosingTable(anyCell);
            if (table == null) return;

            var rows = GetTableRows(table);
            CellRange range = null;
            if (startCell != null && endCell != null &&
                FindEnclosingTable(startCell) == table && FindEnclosingTable(endCell) == table)
            {
                range = GetSelectedCellRange(rows, startCell, endCell);
            }

            // If a precise cell range could be determined, copy only that range; otherwise (e.g.
            // the selection extends outside the table) fall back to exporting the whole table.
            string tsv = range != null ? RangeToTsv(rows, range) : TableToTsv(table);
            string htmlFragment = range != null ? RangeToHtmlFragment(rows, range) : TableToHtmlFragment(table);
            e.DataObject.SetData(DataFormats.Text, tsv);
            e.DataObject.SetData(DataFormats.Html, BuildHtmlClipboardFragment(htmlFragment));
        }

        private List<List<string>> TryParseHtmlTable(string html)
        {
            var tableMatch = Regex.Match(html, "<table[^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!tableMatch.Success) return null;

            var rows = new List<List<string>>();
            foreach (Match rowMatch in Regex.Matches(tableMatch.Groups[1].Value, "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var cells = new List<string>();
                foreach (Match cellMatch in Regex.Matches(rowMatch.Groups[1].Value, "<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    cells.Add(StripHtmlToText(cellMatch.Groups[1].Value));
                }
                if (cells.Count > 0) rows.Add(cells);
            }
            return rows.Count > 0 ? rows : null;
        }

        private string StripHtmlToText(string html)
        {
            string text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", "");
            text = WebUtility.HtmlDecode(text);
            return text.Trim();
        }

        private bool LooksLikeTsv(string text)
        {
            return !string.IsNullOrEmpty(text) && text.Contains('\t');
        }

        private List<List<string>> ParseTsv(string text)
        {
            var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            return lines.Select(line => line.Split('\t').ToList()).ToList();
        }

        private void InsertParsedTable(List<List<string>> rows)
        {
            if (rows == null || rows.Count == 0) return;
            int colCount = rows.Max(r => r.Count);
            if (colCount == 0) return;

            var table = new Table();
            for (int c = 0; c < colCount; c++) table.Columns.Add(new TableColumn());
            var rg = new TableRowGroup();
            table.RowGroups.Add(rg);

            for (int r = 0; r < rows.Count; r++)
            {
                var row = new TableRow();
                for (int c = 0; c < colCount; c++)
                {
                    string text = c < rows[r].Count ? rows[r][c] : "";
                    var cell = new TableCell(new Paragraph(new Run(text)))
                    {
                        BorderBrush = CellBorder,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 6, 8, 6)
                    };
                    if (r == 0)
                    {
                        cell.FontWeight = FontWeights.Bold;
                        cell.Background = HeaderBackground;
                    }
                    row.Cells.Add(cell);
                }
                rg.Rows.Add(row);
            }

            isProgrammaticChange = true;
            try
            {
                var para = Editor.CaretPosition?.Paragraph;
                var trailingPara = new Paragraph();
                if (para != null && para.Parent is FlowDocument)
                {
                    Editor.Document.Blocks.InsertAfter(para, table);
                    Editor.Document.Blocks.InsertAfter(table, trailingPara);
                    if (string.IsNullOrWhiteSpace(new TextRange(para.ContentStart, para.ContentEnd).Text))
                        Editor.Document.Blocks.Remove(para);
                }
                else
                {
                    Editor.Document.Blocks.Add(table);
                    Editor.Document.Blocks.Add(trailingPara);
                }

                if (rg.Rows.Count > 0 && rg.Rows[0].Cells.Count > 0 && rg.Rows[0].Cells[0].Blocks.FirstBlock is Paragraph fp)
                    Editor.CaretPosition = fp.ContentStart;
            }
            finally
            {
                isProgrammaticChange = false;
            }

            RefreshOutline();
            Editor.Focus();
        }

        private void InsertPlainTextWithLineBreaks(string text)
        {
            InvalidateOriginalText(Editor.CaretPosition);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            isProgrammaticChange = true;
            try
            {
                Editor.Selection.Text = lines[0];
                Editor.CaretPosition = Editor.Selection.End;
                for (int i = 1; i < lines.Length; i++)
                {
                    Editor.CaretPosition = Editor.CaretPosition.InsertLineBreak();
                    Editor.Selection.Select(Editor.CaretPosition, Editor.CaretPosition);
                    Editor.Selection.Text = lines[i];
                    Editor.CaretPosition = Editor.Selection.End;
                }
            }
            finally
            {
                isProgrammaticChange = false;
            }
        }

        private void Editor_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (isSourceMode) return;

            // Pasting into a code block: always insert as literal text, keeping everything inside
            // the same fenced block (default paste would otherwise split it into new paragraphs).
            var currentPara = Editor.CaretPosition?.Paragraph;
            if (currentPara != null && currentPara.Tag is CodeBlockInfo && e.SourceDataObject.GetDataPresent(DataFormats.Text))
            {
                string codeText = (string)e.SourceDataObject.GetData(DataFormats.Text);
                e.CancelCommand();
                InsertPlainTextWithLineBreaks(codeText);
                return;
            }

            // Excel (and most rich sources) put an HTML table on the clipboard - this is the most
            // reliable way to detect "the user copied a table".
            if (e.SourceDataObject.GetDataPresent(DataFormats.Html))
            {
                string html = (string)e.SourceDataObject.GetData(DataFormats.Html);
                var tableData = TryParseHtmlTable(html);
                if (tableData != null)
                {
                    e.CancelCommand();
                    InsertParsedTable(tableData);
                    return;
                }
            }

            // Fallback: plain tab-separated text (e.g. from apps that don't emit HTML on copy).
            if (e.SourceDataObject.GetDataPresent(DataFormats.Text))
            {
                string text = (string)e.SourceDataObject.GetData(DataFormats.Text);
                if (LooksLikeTsv(text))
                {
                    var tableData = ParseTsv(text);
                    if (tableData != null && tableData.Any(r => r.Count > 1))
                    {
                        e.CancelCommand();
                        InsertParsedTable(tableData);
                    }
                }
            }
        }

        // ======================================================================
        //  Mode toggle
        // ======================================================================
        private void ToggleModeBtn_Click(object sender, RoutedEventArgs e)
        {
            bool wasDirty = currentFileIsDirty;

            if (!isSourceMode)
            {
                SourceEditor.Text = DocumentToMarkdown(Editor.Document);
                Editor.Visibility = Visibility.Collapsed;
                SourceEditor.Visibility = Visibility.Visible;
                isSourceMode = true;
                ModeIndicator.Text = "ソースモード";
                SourceEditor.Focus();
            }
            else
            {
                MarkdownToDocument(SourceEditor.Text, Editor.Document);
                SourceEditor.Visibility = Visibility.Collapsed;
                Editor.Visibility = Visibility.Visible;
                isSourceMode = false;
                ModeIndicator.Text = "MarkDownモード";
                RefreshOutline();
                Editor.Focus();
            }

            // Switching view mode just re-displays the same content; it must not by itself change
            // whether the file is considered dirty.
            currentFileIsDirty = wasDirty;
            RefreshFolderTreeDirtyMarkers();
        }

        // ======================================================================
        //  New / Open / Save / Save As
        // ======================================================================
        private void NewBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "現在の内容を破棄して新規作成します。保存されていない変更は失われますが、よろしいですか？",
                "新規作成", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;

            DiscardCurrentDocumentSilently();
            Editor.Focus();
        }

        private void OpenBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Markdownファイル (*.md;*.markdown)|*.md;*.markdown|すべてのファイル (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                LoadFile(dlg.FileName);
            }
        }

        private void LoadFile(string path)
        {
            // Special case: re-opening the file that's already the active one. Without this check,
            // GetCurrentContentForFile() below would just return the live (possibly edited) content
            // right back, making the "open" appear to do nothing.
            if (!string.IsNullOrEmpty(currentFilePath) && PathsReferToSameFile(path, currentFilePath))
            {
                if (!currentFileIsDirty) return; // no edits since load/save; nothing to do

                var result = MessageBox.Show(
                    "このファイルには保存されていない変更があります。破棄して、保存済みの内容で開き直しますか？",
                    "ファイルを開き直す", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (result != MessageBoxResult.OK) return;

                pendingFileEdits.Remove(path); // discard any pending in-memory edits for this file too

                string onDiskContent = SafeReadFile(path);
                if (onDiskContent == null)
                {
                    MessageBox.Show("ファイルを開けませんでした。", "ファイルを開く", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (isSourceMode) SourceEditor.Text = onDiskContent;
                else { MarkdownToDocument(onDiskContent, Editor.Document); RefreshOutline(); }

                currentFileIsDirty = false;
                RefreshFolderTreeDirtyMarkers();
                return;
            }

            SnapshotCurrentFileIfDirty();

            string md = GetCurrentContentForFile(path);
            if (md == null)
            {
                md = SafeReadFile(path);
                if (md == null)
                {
                    MessageBox.Show("ファイルを開けませんでした。", "ファイルを開く",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            currentFilePath = path;
            currentFileDirectory = Path.GetDirectoryName(path);
            this.Title = Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version + " - " + Path.GetFileName( path );
            pendingFileEdits.Remove(path); // this file's content now lives in the editor itself

            if (isSourceMode)
            {
                SourceEditor.Text = md;
            }
            else
            {
                MarkdownToDocument(md, Editor.Document);
                RefreshOutline();
            }

            currentFileIsDirty = false;

            if (!string.IsNullOrEmpty(currentFileDirectory) && !IsWithinLoadedFolder(currentFileDirectory))
                LoadFolderTree(currentFileDirectory);
            else
                RefreshFolderTreeDirtyMarkers();
        }

        /// <summary>
        /// If a file is currently open and its live content differs from what's on disk, stash a
        /// copy in pendingFileEdits so it isn't lost when a different file is loaded into the
        /// (single, shared) editor. Skipped in source mode to keep the model simple.
        /// </summary>
        private void SnapshotCurrentFileIfDirty()
        {
            if (string.IsNullOrEmpty(currentFilePath) || isSourceMode) return;

            if (!currentFileIsDirty)
            {
                pendingFileEdits.Remove(currentFilePath);
                return;
            }

            try
            {
                pendingFileEdits[currentFilePath] = DocumentToMarkdown(Editor.Document);
            }
            catch
            {
                // best effort only; never block navigation because of this
            }
        }

        /// <summary>
        /// Returns the "current truth" for a file's content: the live editor content if it's the
        /// active file, an in-memory pending edit if one exists (from a folder-wide replace or a
        /// previous unsaved edit), or null if neither applies (caller should read from disk).
        /// </summary>
        private string GetCurrentContentForFile(string path)
        {
            if (!string.IsNullOrEmpty(currentFilePath) && PathsReferToSameFile(path, currentFilePath))
                return isSourceMode ? SourceEditor.Text : DocumentToMarkdown(Editor.Document);

            foreach (var kv in pendingFileEdits)
                if (PathsReferToSameFile(kv.Key, path)) return kv.Value;

            return null;
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentFilePath))
            {
                SaveAs();
                return;
            }
            if (!isSourceMode) RelocatePendingTempImages();
            string md = isSourceMode ? SourceEditor.Text : DocumentToMarkdown(Editor.Document);
            File.WriteAllText(currentFilePath, ApplyLineEnding(md, GetLineEndingForFile(currentFilePath)), new UTF8Encoding(false));
            currentFileIsDirty = false;
            RefreshFolderTreeDirtyMarkers();
        }

        private void SaveAsBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveAs();
        }

        private void SaveAs()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdownファイル (*.md)|*.md|すべてのファイル (*.*)|*.*",
                FileName = currentFilePath != null ? Path.GetFileName(currentFilePath) : "document.md"
            };
            if (currentFileDirectory != null) dlg.InitialDirectory = currentFileDirectory;

            if (dlg.ShowDialog() == true)
            {
                // Save As carries over the line-ending style of the file being saved from (if any),
                // rather than losing that preference just because the name/location changed.
                string lineEnding = !string.IsNullOrEmpty(currentFilePath) ? GetLineEndingForFile(currentFilePath) : "\r\n";
                fileLineEndings[dlg.FileName] = lineEnding;

                currentFilePath = dlg.FileName;
                currentFileDirectory = Path.GetDirectoryName(dlg.FileName);
                this.Title = Assembly.GetExecutingAssembly().GetName().Name + " v" + Assembly.GetExecutingAssembly().GetName().Version + " - " + Path.GetFileName(dlg.FileName);

                if (!isSourceMode) RelocatePendingTempImages();

                string md = isSourceMode ? SourceEditor.Text : DocumentToMarkdown(Editor.Document);
                File.WriteAllText(dlg.FileName, ApplyLineEnding(md, lineEnding), new UTF8Encoding(false));
                currentFileIsDirty = false;

                if (!string.IsNullOrEmpty(currentFileDirectory) && !IsWithinLoadedFolder(currentFileDirectory))
                    LoadFolderTree(currentFileDirectory);
                else
                    RefreshFolderTreeDirtyMarkers();
            }
        }

        private void SaveAllBtn_Click(object sender, RoutedEventArgs e)
        {
            int savedCount = 0;
            var failures = new List<string>();

            if (!string.IsNullOrEmpty(currentFilePath))
            {
                try
                {
                    if (!isSourceMode) RelocatePendingTempImages();
                    string md = isSourceMode ? SourceEditor.Text : DocumentToMarkdown(Editor.Document);
                    File.WriteAllText(currentFilePath, ApplyLineEnding(md, GetLineEndingForFile(currentFilePath)), new UTF8Encoding(false));
                    pendingFileEdits.Remove(currentFilePath);
                    currentFileIsDirty = false;
                    savedCount++;
                }
                catch (Exception ex)
                {
                    failures.Add(currentFilePath + " (" + ex.Message + ")");
                }
            }

            foreach (var kv in pendingFileEdits.ToList())
            {
                try
                {
                    File.WriteAllText(kv.Key, ApplyLineEnding(kv.Value, GetLineEndingForFile(kv.Key)), new UTF8Encoding(false));
                    pendingFileEdits.Remove(kv.Key);
                    savedCount++;
                }
                catch (Exception ex)
                {
                    failures.Add(kv.Key + " (" + ex.Message + ")");
                }
            }

            RefreshFolderTreeDirtyMarkers();

            string message = savedCount + " 個のファイルを保存しました。";
            if (failures.Count > 0)
                message += "\n\n保存に失敗したファイル:\n" + string.Join("\n", failures);

            MessageBox.Show(message, "すべて保存", MessageBoxButton.OK,
                failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        // ======================================================================
        //  Find & Replace
        // ======================================================================
        // ======================================================================
        //  Show/hide the folder and outline side panes
        // ======================================================================
        private double lastFolderColumnWidth = 190;
        private double lastOutlineColumnWidth = 210;
        private bool folderPaneVisible = true;
        private bool outlinePaneVisible = true;

        private void ToggleFolderPaneBtn_Click(object sender, RoutedEventArgs e)
        {
            folderPaneVisible = !folderPaneVisible;
            if (folderPaneVisible)
            {
                FolderColumnDef.Width = new GridLength(lastFolderColumnWidth);
                FolderSplitterColumnDef.Width = new GridLength(2);
                FolderPaneBorder.Visibility = Visibility.Visible;
                FolderSplitter.Visibility = Visibility.Visible;
                ToggleFolderPaneBtn.Content = "フォルダを隠す";
            }
            else
            {
                if (FolderColumnDef.Width.Value > 0) lastFolderColumnWidth = FolderColumnDef.Width.Value;
                FolderColumnDef.Width = new GridLength(0);
                FolderSplitterColumnDef.Width = new GridLength(0);
                FolderPaneBorder.Visibility = Visibility.Collapsed;
                FolderSplitter.Visibility = Visibility.Collapsed;
                ToggleFolderPaneBtn.Content = "フォルダを表示";
            }
        }

        private void ToggleOutlinePaneBtn_Click(object sender, RoutedEventArgs e)
        {
            outlinePaneVisible = !outlinePaneVisible;
            if (outlinePaneVisible)
            {
                OutlineColumnDef.Width = new GridLength(lastOutlineColumnWidth);
                OutlineSplitterColumnDef.Width = new GridLength(2);
                OutlinePaneBorder.Visibility = Visibility.Visible;
                OutlineSplitter.Visibility = Visibility.Visible;
                ToggleOutlinePaneBtn.Content = "アウトラインを隠す";
            }
            else
            {
                if (OutlineColumnDef.Width.Value > 0) lastOutlineColumnWidth = OutlineColumnDef.Width.Value;
                OutlineColumnDef.Width = new GridLength(0);
                OutlineSplitterColumnDef.Width = new GridLength(0);
                OutlinePaneBorder.Visibility = Visibility.Collapsed;
                OutlineSplitter.Visibility = Visibility.Collapsed;
                ToggleOutlinePaneBtn.Content = "アウトラインを表示";
            }
        }

        private void FindReplaceBtn_Click(object sender, RoutedEventArgs e)
        {
            var win = new FindReplaceWindow(this) { Owner = this };
            win.Show();
        }

        private int CountOccurrences(string text, string term, bool caseSensitive, bool useRegex)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return 0;
            if (useRegex)
            {
                try
                {
                    var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    return Regex.Matches(text, term, options).Count;
                }
                catch (ArgumentException)
                {
                    return 0;
                }
            }
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(term, idx, comparison)) >= 0)
            {
                count++;
                idx += term.Length;
            }
            return count;
        }

        private string ReplaceAllText(string text, string term, string replacement, bool caseSensitive, bool useRegex)
        {
            if (string.IsNullOrEmpty(term)) return text;
            replacement = replacement ?? "";
            if (useRegex)
            {
                try
                {
                    var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    return Regex.Replace(text, term, replacement, options);
                }
                catch (ArgumentException)
                {
                    return text;
                }
            }
            if (caseSensitive) return text.Replace(term, replacement);

            var sb = new StringBuilder();
            int idx = 0;
            while (true)
            {
                int found = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0) { sb.Append(text, idx, text.Length - idx); break; }
                sb.Append(text, idx, found - idx);
                sb.Append(replacement);
                idx = found + term.Length;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Finds the next match at or after fromIndex within text. Used by the step-by-step
        /// replace session (both current-file and folder scope) since it works uniformly whether
        /// or not regex is enabled.
        /// </summary>
        public (int index, int length)? FindNextMatchInText(string text, string term, bool caseSensitive, bool useRegex, int fromIndex)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term) || fromIndex > text.Length) return null;
            if (useRegex)
            {
                try
                {
                    var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var regex = new Regex(term, options);
                    var m = regex.Match(text, fromIndex);
                    if (!m.Success) return null;
                    return (m.Index, m.Length);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int idx = text.IndexOf(term, fromIndex, comparison);
            return idx < 0 ? ((int, int)?)null : (idx, term.Length);
        }

        /// <summary>Applies exactly one replacement at the given match position and returns the new text.</summary>
        public string ReplaceOneMatch(string text, string term, string replacement, bool caseSensitive, bool useRegex, int index, int length)
        {
            replacement = replacement ?? "";
            if (useRegex)
            {
                try
                {
                    var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var regex = new Regex(term, options);
                    return regex.Replace(text, replacement, 1, index);
                }
                catch (ArgumentException)
                {
                    return text;
                }
            }
            return text.Substring(0, index) + replacement + text.Substring(index + length);
        }

        /// <summary>
        /// Selects and scrolls to the next occurrence of term after the caret, wrapping around to
        /// the start of the document if nothing is found after the caret. Operates directly on the
        /// live document via TextPointers, so the match is actually highlighted on screen (works
        /// for both plain text and regex, as long as a match doesn't span a Run boundary such as
        /// crossing into a heading, list item, or embedded image - an accepted edge-case limit).
        /// </summary>
        public bool FindNextInCurrentFile(string term, bool caseSensitive, bool useRegex)
        {
            if (isSourceMode || string.IsNullOrEmpty(term)) return false;

            TextRange found = FindTextFrom(Editor.CaretPosition, term, caseSensitive, useRegex)
                            ?? FindTextFrom(Editor.Document.ContentStart, term, caseSensitive, useRegex);
            if (found == null) return false;

            Editor.Selection.Select(found.Start, found.End);
            ScrollParagraphToTop(found.Start.Paragraph ?? Editor.Document.Blocks.FirstBlock as Paragraph);
            Editor.CaretPosition = found.End;
            Editor.Focus();
            return true;
        }

        private TextRange FindTextFrom(TextPointer start, string term, bool caseSensitive, bool useRegex)
        {
            Regex regex = null;
            if (useRegex)
            {
                try { regex = new Regex(term, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch (ArgumentException) { return null; }
            }
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            TextPointer navigator = start;
            while (navigator != null)
            {
                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    string runText = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (!string.IsNullOrEmpty(runText))
                    {
                        int idx, len;
                        if (useRegex)
                        {
                            var m = regex.Match(runText);
                            if (m.Success) { idx = m.Index; len = m.Length; }
                            else { idx = -1; len = 0; }
                        }
                        else
                        {
                            idx = runText.IndexOf(term, comparison);
                            len = term.Length;
                        }

                        if (idx >= 0)
                        {
                            TextPointer matchStart = navigator.GetPositionAtOffset(idx);
                            TextPointer matchEnd = matchStart?.GetPositionAtOffset(len);
                            if (matchStart != null && matchEnd != null)
                                return new TextRange(matchStart, matchEnd);
                        }
                    }
                }
                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }
            return null;
        }

        // ---- step-by-step (one match at a time) replace for the CURRENT FILE, with live
        // highlighting - the currently selected text in the editor IS the "pending match". ----

        /// <summary>Finds and highlights the next match. If fromSelectionEnd, resumes searching
        /// right after whatever is currently selected (used mid-session); otherwise starts from
        /// the caret (used when starting a fresh session).</summary>
        public bool StepFindNext(string term, bool caseSensitive, bool useRegex, bool fromSelectionEnd)
        {
            TextPointer from = (fromSelectionEnd && !Editor.Selection.IsEmpty) ? Editor.Selection.End : Editor.CaretPosition;
            TextRange found = FindTextFrom(from, term, caseSensitive, useRegex);
            if (found == null) return false;

            Editor.Selection.Select(found.Start, found.End);
            ScrollParagraphToTop(found.Start.Paragraph ?? Editor.Document.Blocks.FirstBlock as Paragraph);
            Editor.Focus();
            return true;
        }

        /// <summary>Replaces the currently highlighted match, then finds and highlights the next one.</summary>
        public bool StepReplaceAndFindNext(string term, string replacement, bool caseSensitive, bool useRegex)
        {
            if (!Editor.Selection.IsEmpty)
            {
                InvalidateOriginalText(Editor.Selection.Start);
                isProgrammaticChange = true;
                try { Editor.Selection.Text = replacement ?? ""; }
                finally { isProgrammaticChange = false; }
                RefreshOutline();
            }
            return StepFindNext(term, caseSensitive, useRegex, fromSelectionEnd: true);
        }

        /// <summary>Skips the currently highlighted match (no replace), finds and highlights the next one.</summary>
        public bool StepSkipAndFindNext(string term, bool caseSensitive, bool useRegex)
        {
            return StepFindNext(term, caseSensitive, useRegex, fromSelectionEnd: true);
        }

        /// <summary>Replaces the currently highlighted match plus every remaining match, with no further prompting.</summary>
        public int StepReplaceAllRemaining(string term, string replacement, bool caseSensitive, bool useRegex)
        {
            int count = 0;
            isProgrammaticChange = true;
            try
            {
                if (!Editor.Selection.IsEmpty)
                {
                    InvalidateOriginalText(Editor.Selection.Start);
                    Editor.Selection.Text = replacement ?? "";
                    count++;
                }
                while (StepFindNext(term, caseSensitive, useRegex, fromSelectionEnd: true))
                {
                    InvalidateOriginalText(Editor.Selection.Start);
                    Editor.Selection.Text = replacement ?? "";
                    count++;
                }
            }
            finally
            {
                isProgrammaticChange = false;
            }
            RefreshOutline();
            return count;
        }

        public int ReplaceAllInCurrentFile(string term, string replacement, bool caseSensitive, bool useRegex)
        {
            if (string.IsNullOrEmpty(term)) return 0;

            if (isSourceMode)
            {
                int srcCount = CountOccurrences(SourceEditor.Text, term, caseSensitive, useRegex);
                if (srcCount > 0) SourceEditor.Text = ReplaceAllText(SourceEditor.Text, term, replacement, caseSensitive, useRegex);
                return srcCount;
            }

            string md = DocumentToMarkdown(Editor.Document);
            int count = CountOccurrences(md, term, caseSensitive, useRegex);
            if (count == 0) return 0;

            MarkdownToDocument(ReplaceAllText(md, term, replacement, caseSensitive, useRegex), Editor.Document);
            RefreshOutline();
            return count;
        }

        private List<string> GetAllMarkdownFilesInRoot()
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(loadedFolderRootPath) || !Directory.Exists(loadedFolderRootPath)) return result;
            try
            {
                result.AddRange(Directory.GetFiles(loadedFolderRootPath, "*.md", SearchOption.AllDirectories));
                result.AddRange(Directory.GetFiles(loadedFolderRootPath, "*.markdown", SearchOption.AllDirectories));
            }
            catch
            {
                // ignore folders we can't enumerate (permissions, etc.)
            }
            return result;
        }

        public List<(string, int)> FindAllInFolder(string term, bool caseSensitive, bool useRegex)
        {
            var results = new List<(string, int)>();
            if (string.IsNullOrEmpty(term)) return results;

            foreach (var file in GetAllMarkdownFilesInRoot())
            {
                string content = GetCurrentContentForFile(file) ?? SafeReadFile(file);
                if (content == null) continue;
                int count = CountOccurrences(content, term, caseSensitive, useRegex);
                if (count > 0) results.Add((file, count));
            }
            return results;
        }

        public List<(string, int)> ReplaceAllInFolder(string term, string replacement, bool caseSensitive, bool useRegex)
        {
            var results = new List<(string, int)>();
            if (string.IsNullOrEmpty(term)) return results;

            foreach (var file in GetAllMarkdownFilesInRoot())
            {
                string content = GetCurrentContentForFile(file) ?? SafeReadFile(file);
                if (content == null) continue;

                int count = CountOccurrences(content, term, caseSensitive, useRegex);
                if (count == 0) continue;

                string replaced = ReplaceAllText(content, term, replacement, caseSensitive, useRegex);
                SetFileContentForReplace(file, replaced);
                results.Add((file, count));
            }
            return results;
        }

        private string SafeReadFile(string path)
        {
            try
            {
                string content = File.ReadAllText(path, Encoding.UTF8);
                fileLineEndings[path] = DetectLineEnding(content);
                return content;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Guesses whether a file predominantly uses CRLF or LF line endings by counting
        /// both kinds. Defaults to CRLF (the common Windows convention) for empty/ambiguous content.</summary>
        private string DetectLineEnding(string content)
        {
            if (string.IsNullOrEmpty(content)) return "\r\n";
            int crlfCount = 0, lfOnlyCount = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] != '\n') continue;
                if (i > 0 && content[i - 1] == '\r') crlfCount++;
                else lfOnlyCount++;
            }
            if (crlfCount == 0 && lfOnlyCount == 0) return "\r\n";
            return crlfCount >= lfOnlyCount ? "\r\n" : "\n";
        }

        /// <summary>The line-ending style previously detected for this file, or "\r\n" if unknown
        /// (e.g. a brand-new, never-loaded-from-disk file).</summary>
        private string GetLineEndingForFile(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var kv in fileLineEndings)
                    if (PathsReferToSameFile(kv.Key, path)) return kv.Value;
            }
            return "\r\n";
        }

        /// <summary>Converts a markdown string (which always uses bare "\n" internally) to use the
        /// given line-ending style before writing it to disk.</summary>
        private string ApplyLineEnding(string text, string lineEnding)
        {
            string normalized = text.Replace("\r\n", "\n");
            return lineEnding == "\n" ? normalized : normalized.Replace("\n", lineEnding);
        }

        /// <summary>Opens a file (from a folder-wide find/replace result), preferring any pending
        /// in-memory edit over the on-disk version.</summary>
        public void OpenFileForFindReplace(string path)
        {
            LoadFile(path);
        }

        // ---- primitives used by the step-by-step (one match at a time) replace session ----

        public string GetCurrentFileContent()
        {
            return isSourceMode ? SourceEditor.Text : DocumentToMarkdown(Editor.Document);
        }

        public void SetCurrentFileContent(string newContent)
        {
            if (isSourceMode)
            {
                SourceEditor.Text = newContent;
            }
            else
            {
                MarkdownToDocument(newContent, Editor.Document);
                RefreshOutline();
            }
        }

        public List<string> GetFolderFiles()
        {
            return GetAllMarkdownFilesInRoot();
        }

        public string GetFileContentForReplace(string path)
        {
            return GetCurrentContentForFile(path) ?? SafeReadFile(path);
        }

        /// <summary>Applies new content for a file: directly to the live editor if it's the
        /// currently open file, otherwise staged in pendingFileEdits (not written to disk).</summary>
        public void SetFileContentForReplace(string path, string newContent)
        {
            if (!string.IsNullOrEmpty(currentFilePath) && PathsReferToSameFile(path, currentFilePath))
            {
                SetCurrentFileContent(newContent);
            }
            else
            {
                pendingFileEdits[path] = newContent;
                RefreshFolderTreeDirtyMarkers();
            }
        }

        // ======================================================================
        private void RefreshOutline()
        {
            outlineItems.Clear();
            foreach (Block block in Editor.Document.Blocks)
            {
                if (block is Paragraph p && p.Tag is int level && level > 0)
                {
                    string text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                    if (text.Length == 0) text = "(無題)";
                    outlineItems.Add(new OutlineEntry { Level = level, Text = text, TargetParagraph = p });
                }
            }
        }

        private void OutlineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (OutlineList.SelectedItem is OutlineEntry entry && entry.TargetParagraph != null)
            {
                Editor.CaretPosition = entry.TargetParagraph.ContentStart;
                ScrollParagraphToTop(entry.TargetParagraph);
                Editor.Focus();
            }
        }

        private void ScrollParagraphToTop(Paragraph p)
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(Editor);
            if (scrollViewer == null)
            {
                p.BringIntoView();
                return;
            }

            Editor.UpdateLayout();
            Rect rect = p.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            double targetOffset = scrollViewer.VerticalOffset + rect.Top;
            scrollViewer.ScrollToVerticalOffset(Math.Max(0, targetOffset));
        }

        private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static T FindVisualAncestorOrSelf<T>(DependencyObject start) where T : DependencyObject
        {
            var current = start;
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ======================================================================
        //  Folder pane
        // ======================================================================
        private void OpenFolderTreeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (currentFileIsDirty || pendingFileEdits.Count > 0)
            {
                var confirmResult = MessageBox.Show(
                    "保存されていない変更があります。破棄して別のフォルダを開きますか？",
                    "フォルダを開く", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (confirmResult != MessageBoxResult.OK) return;
            }

            string previousRelativePath = GetCurrentFileRelativePathInLoadedFolder();

            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (dlg.ShowDialog() != true) return;

            DiscardCurrentDocumentSilently();
            LoadFolderTree(dlg.FolderName);
            OpenMatchingOrFirstFile(dlg.FolderName, previousRelativePath);
        }

        /// <summary>The currently open file's path relative to the currently loaded folder root
        /// (e.g. "sub\file.md"), or null if there's no open file or it isn't inside that folder.</summary>
        private string GetCurrentFileRelativePathInLoadedFolder()
        {
            if (string.IsNullOrEmpty(currentFilePath) || string.IsNullOrEmpty(loadedFolderRootPath)) return null;
            try
            {
                string root = Path.GetFullPath(loadedFolderRootPath).TrimEnd(Path.DirectorySeparatorChar);
                string file = Path.GetFullPath(currentFilePath);
                if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                return file.Substring(root.Length + 1);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Opens the file at the same relative path in the newly loaded folder, if one
        /// exists there; otherwise falls back to opening the folder's first file.</summary>
        private void OpenMatchingOrFirstFile(string newFolderPath, string relativePath)
        {
            if (!string.IsNullOrEmpty(relativePath))
            {
                try
                {
                    string candidate = Path.Combine(newFolderPath, relativePath);
                    if (File.Exists(candidate))
                    {
                        LoadFile(candidate);
                        return;
                    }
                }
                catch
                {
                    // fall through to opening the first file instead
                }
            }
            OpenFirstFileInLoadedFolder();
        }

        /// <summary>Resets the editor to a blank, untitled document and clears all unsaved-change
        /// tracking, without any confirmation prompt (the caller is expected to have already
        /// confirmed with the user, if needed).</summary>
        private void DiscardCurrentDocumentSilently()
        {
            currentFilePath = null;
            currentFileDirectory = null;
            this.Title = Assembly.GetExecutingAssembly().GetName().Name;

            pendingFileEdits.Clear();
            currentFileIsDirty = false;

            isProgrammaticChange = true;
            try
            {
                if (isSourceMode)
                {
                    SourceEditor.Text = "";
                }
                else
                {
                    Editor.Document.Blocks.Clear();
                    Editor.Document.Blocks.Add(new Paragraph());
                }
            }
            finally
            {
                isProgrammaticChange = false;
            }
            RefreshOutline();
            RefreshFolderTreeDirtyMarkers();
        }

        /// <summary>Opens the first file (skipping subfolders) found at the root of the currently
        /// loaded folder tree, if any.</summary>
        private void OpenFirstFileInLoadedFolder()
        {
            if (folderRoots.Count == 0) return;
            var firstFile = folderRoots[0].Children.FirstOrDefault(c => !c.IsDirectory);
            if (firstFile != null) LoadFile(firstFile.FullPath);
        }

        private void LoadFolderTree(string folderPath)
        {
            loadedFolderRootPath = folderPath;
            folderRoots.Clear();
            try
            {
                var root = BuildFileSystemNode(folderPath, true);
                root.Children.Clear();
                PopulateChildren(root);
                root.IsExpanded = true;
                folderRoots.Add(root);
                RefreshFolderTreeDirtyMarkers();
            }
            catch
            {
                // folder not accessible; leave the tree empty
            }
        }

        /// <summary>
        /// Walks the folder tree and marks each file item dirty if it's the currently open file
        /// with unsaved changes, or if it has a pending (unsaved) in-memory edit from find/replace.
        /// </summary>
        private void RefreshFolderTreeDirtyMarkers()
        {
            foreach (var root in folderRoots)
                RefreshDirtyMarkerRecursive(root);
        }

        private void RefreshDirtyMarkerRecursive(FileSystemItem node)
        {
            if (!node.IsDirectory && node.FullPath != null)
            {
                bool isCurrent = !string.IsNullOrEmpty(currentFilePath) && PathsReferToSameFile(node.FullPath, currentFilePath);
                node.IsDirty = isCurrent
                    ? currentFileIsDirty
                    : pendingFileEdits.Keys.Any(k => PathsReferToSameFile(k, node.FullPath));
            }
            foreach (var child in node.Children)
                RefreshDirtyMarkerRecursive(child);
        }

        /// <summary>
        /// True if 'dir' is the folder currently shown in the folder pane, or a subfolder of it -
        /// in which case there is no need to rebuild the tree (and collapse whatever the user had
        /// expanded) just because a file in that same area was opened.
        /// </summary>
        private bool IsWithinLoadedFolder(string dir)
        {
            if (string.IsNullOrEmpty(loadedFolderRootPath) || string.IsNullOrEmpty(dir)) return false;
            try
            {
                string root = Path.GetFullPath(loadedFolderRootPath).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
                string target = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
                return target == root || target.StartsWith(root + Path.DirectorySeparatorChar);
            }
            catch
            {
                return false;
            }
        }

        private FileSystemItem BuildFileSystemNode(string path, bool isDirectory)
        {
            var item = new FileSystemItem
            {
                Name = string.IsNullOrEmpty(Path.GetFileName(path)) ? path : Path.GetFileName(path),
                FullPath = path,
                IsDirectory = isDirectory
            };
            if (isDirectory)
            {
                // placeholder child so the expand arrow shows before we lazily populate it
                item.Children.Add(new FileSystemItem { Name = "読み込み中…", IsDirectory = false, FullPath = null });
            }
            return item;
        }

        private void PopulateChildren(FileSystemItem node)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(node.FullPath).OrderBy(d => d))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(".")) continue;
                    node.Children.Add(BuildFileSystemNode(dir, true));
                }
                foreach (var file in Directory.GetFiles(node.FullPath)
                             .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f))
                {
                    node.Children.Add(BuildFileSystemNode(file, false));
                }
            }
            catch
            {
                // access denied etc.; leave whatever was already added
            }
        }

        private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem tvi && tvi.DataContext is FileSystemItem node && node.IsDirectory)
            {
                if (node.Children.Count == 1 && node.Children[0].FullPath == null)
                {
                    node.Children.Clear();
                    PopulateChildren(node);
                    RefreshFolderTreeDirtyMarkers();
                }
            }
        }

        private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FileSystemItem item && !item.IsDirectory && item.FullPath != null)
            {
                LoadFile(item.FullPath);
            }
        }

        // ======================================================================
        //  Zoom
        // ======================================================================
        private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(zoomLevel + 0.1);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(zoomLevel - 0.1);
        private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

        private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                SetZoom(zoomLevel + (e.Delta > 0 ? 0.1 : -0.1));
            }
        }

        private void SetZoom(double value)
        {
            zoomLevel = Math.Max(0.5, Math.Min(2.5, Math.Round(value, 2)));
            Editor.LayoutTransform = new ScaleTransform(zoomLevel, zoomLevel);
            SourceEditor.FontSize = 13.5 * zoomLevel;
            ZoomLabelBtn.Content = Math.Round(zoomLevel * 100) + "%";
        }


        // ======================================================================
        //  DOM(FlowDocument) -> Markdown
        // ======================================================================
        private string DocumentToMarkdown(FlowDocument doc)
        {
            var lines = new List<string>();
            foreach (Block block in doc.Blocks)
            {
                string s = originalBlockText.TryGetValue(block, out var holder) ? holder.Text : BlockToMarkdown(block);
                if (!string.IsNullOrWhiteSpace(s)) lines.Add(s);
            }
            return string.Join("\n\n", lines);
        }

        private string BlockToMarkdown(Block block)
        {
            if (block is Paragraph p)
            {
                if (p.Tag is CodeBlockInfo codeInfo)
                {
                    var sb = new StringBuilder();
                    AppendInlinesMarkdown(p.Inlines, sb);
                    string codeText = sb.ToString().Trim('\r', '\n');
                    return "```" + codeInfo.Language + "\n" + codeText + "\n```";
                }
                int level = p.Tag is int lv ? lv : 0;
                string text = ParagraphInlineToMarkdown(p);
                return level > 0 ? new string('#', level) + " " + text : text;
            }
            if (block is List list) return ListToMarkdown(list, 0);
            if (block is Table table) return TableToMarkdown(table);
            return "";
        }

        private string ListToMarkdown(List list, int level)
        {
            string indent = new string(' ', level * 3);
            var lines = new List<string>();
            foreach (ListItem li in list.ListItems)
            {
                var ownPara = li.Blocks.FirstBlock as Paragraph;
                string ownText = ownPara != null ? ParagraphInlineToMarkdown(ownPara) : "";
                var parts = ownText.Split('\n');
                lines.Add(indent + "* " + parts[0]);
                for (int k = 1; k < parts.Length; k++) lines.Add(indent + "  " + parts[k]);

                foreach (Block b in li.Blocks)
                {
                    if (b is List nested) lines.Add(ListToMarkdown(nested, level + 1));
                }
            }
            return string.Join("\n", lines);
        }

        private string TableToMarkdown(Table table)
        {
            var rows = new List<TableRow>();
            foreach (TableRowGroup rg in table.RowGroups)
                foreach (TableRow r in rg.Rows) rows.Add(r);
            if (rows.Count == 0) return "";

            var mdRows = new List<string>();
            foreach (var row in rows)
            {
                var cells = new List<string>();
                foreach (TableCell cell in row.Cells)
                {
                    var sb = new StringBuilder();
                    foreach (Block b in cell.Blocks)
                        if (b is Paragraph cp) sb.Append(ParagraphInlineToMarkdown(cp));
                    cells.Add(sb.ToString().Replace("|", "\\|"));
                }
                mdRows.Add("| " + string.Join(" | ", cells) + " |");
            }
            int colCount = rows[0].Cells.Count;
            string sep = "| " + string.Join(" | ", Enumerable.Repeat("---", colCount)) + " |";

            var result = new List<string> { mdRows[0], sep };
            result.AddRange(mdRows.Skip(1));
            return string.Join("\n", result);
        }

        private string ParagraphInlineToMarkdown(Paragraph p)
        {
            var sb = new StringBuilder();
            AppendInlinesMarkdown(p.Inlines, sb);
            return sb.ToString().Trim();
        }

        private void AppendInlinesMarkdown(InlineCollection inlines, StringBuilder sb)
        {
            foreach (Inline inline in inlines)
            {
                if (inline is LineBreak)
                {
                    sb.Append('\n');
                }
                else if (inline is Run run)
                {
                    sb.Append(run.Text.Replace("\u200B", ""));
                }
                else if (inline is InlineUIContainer iuc && iuc.Child is Image img)
                {
                    sb.Append(ImageToMarkdownString(img));
                }
                else if (inline is Span span)
                {
                    AppendInlinesMarkdown(span.Inlines, sb);
                }
            }
        }

        private string ImageToMarkdownString(Image img)
        {
            var info = img.Tag as ImageInfo;
            string src = info?.OriginalSrc ?? "";
            string alt = info?.Alt ?? "";
            if (info?.Format == "md") return "![" + alt + "](" + src + ")";

            string tag = "<img src=\"" + src + "\" alt=\"" + alt + "\"";
            if (!string.IsNullOrEmpty(info?.Style)) tag += " style=\"" + info.Style + "\"";
            tag += " />";
            return tag;
        }

        // ======================================================================
        //  Markdown -> DOM(FlowDocument)
        // ======================================================================
        /// <summary>Stores the exact original source lines that produced a freshly-parsed block, so
        /// it can be written back verbatim later if it's never edited.</summary>
        private void RecordOriginalText(Block block, string[] lines, int start, int end)
        {
            originalBlockText.AddOrUpdate(block, new OriginalTextHolder { Text = string.Join("\n", lines, start, end - start) });
        }

        private void MarkdownToDocument(string md, FlowDocument doc)
        {
            isProgrammaticChange = true;
            try
            {
                doc.Blocks.Clear();
                originalBlockText.Clear();
                var lines = md.Replace("\r\n", "\n").Split('\n');
                int i = 0;
                while (i < lines.Length)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

                    int blockStart = i;

                    if (line.TrimStart().StartsWith("```"))
                    {
                        string language = Regex.Match(line.TrimStart(), "^```(\\S*)").Groups[1].Value;
                        i++;
                        var codeLines = new List<string>();
                        while (i < lines.Length && lines[i].Trim() != "```")
                        {
                            codeLines.Add(lines[i]);
                            i++;
                        }
                        if (i < lines.Length) i++; // skip closing fence

                        var codePara = new Paragraph();
                        ApplyCodeBlockStyle(codePara, language);
                        for (int k = 0; k < codeLines.Count; k++)
                        {
                            if (k > 0) codePara.Inlines.Add(new LineBreak());
                            codePara.Inlines.Add(new Run(codeLines[k]));
                        }
                        doc.Blocks.Add(codePara);
                        RecordOriginalText(codePara, lines, blockStart, i);
                        continue;
                    }

                    var hMatch = Regex.Match(line, "^(#{1,6})\\s+(.*)$");
                    if (hMatch.Success)
                    {
                        var p = new Paragraph();
                        ApplyHeadingStyle(p, hMatch.Groups[1].Value.Length);
                        AppendInlineMarkdownToParagraph(p, hMatch.Groups[2].Value, false);
                        doc.Blocks.Add(p);
                        i++;
                        RecordOriginalText(p, lines, blockStart, i);
                        continue;
                    }

                    if (Regex.IsMatch(line, "^\\s*\\*\\s+"))
                    {
                        var listLines = new List<string>();
                        while (i < lines.Length && (
                            Regex.IsMatch(lines[i], "^\\s*\\*\\s+") ||
                            (listLines.Count > 0 && !string.IsNullOrWhiteSpace(lines[i]) &&
                             Regex.IsMatch(lines[i], "^\\s+\\S") &&
                             !lines[i].TrimStart().StartsWith("|") &&
                             !Regex.IsMatch(lines[i], "^\\s*#{1,6}\\s"))))
                        {
                            listLines.Add(lines[i]);
                            i++;
                        }
                        var list = BuildNestedList(listLines);
                        doc.Blocks.Add(list);
                        RecordOriginalText(list, lines, blockStart, i);
                        continue;
                    }

                    if (line.TrimStart().StartsWith("|") && i + 1 < lines.Length &&
                        Regex.IsMatch(lines[i + 1], "^[\\s|:-]+$") && lines[i + 1].Contains("-"))
                    {
                        var headerCells = ParseTableRow(line);
                        i += 2;
                        var table = new Table();
                        foreach (var _ in headerCells) table.Columns.Add(new TableColumn());
                        var rg = new TableRowGroup();
                        table.RowGroups.Add(rg);

                        var headerRow = new TableRow();
                        foreach (var txt in headerCells)
                        {
                            var hp = new Paragraph();
                            AppendInlineMarkdownToParagraph(hp, txt, false);
                            var cell = new TableCell(hp)
                            {
                                FontWeight = FontWeights.Bold,
                                Background = HeaderBackground,
                                BorderBrush = CellBorder,
                                BorderThickness = new Thickness(1),
                                Padding = new Thickness(8, 6, 8, 6)
                            };
                            headerRow.Cells.Add(cell);
                        }
                        rg.Rows.Add(headerRow);

                        while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                        {
                            var cellTexts = ParseTableRow(lines[i]);
                            var row = new TableRow();
                            foreach (var txt in cellTexts)
                            {
                                var cp = new Paragraph();
                                AppendInlineMarkdownToParagraph(cp, txt, false);
                                var cell = new TableCell(cp)
                                {
                                    BorderBrush = CellBorder,
                                    BorderThickness = new Thickness(1),
                                    Padding = new Thickness(8, 6, 8, 6)
                                };
                                row.Cells.Add(cell);
                            }
                            rg.Rows.Add(row);
                            i++;
                        }
                        doc.Blocks.Add(table);
                        RecordOriginalText(table, lines, blockStart, i);
                        continue;
                    }

                    var para = new Paragraph();
                    AppendInlineMarkdownToParagraph(para, line, false);
                    doc.Blocks.Add(para);
                    i++;
                    RecordOriginalText(para, lines, blockStart, i);
                }

                if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());

                ResolveImages(doc);
            }
            finally
            {
                isProgrammaticChange = false;
            }
        }

        private List<string> ParseTableRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            return t.Split('|').Select(s => s.Trim()).ToList();
        }

        private List BuildNestedList(List<string> listLines)
        {
            var rootList = new List { MarkerStyle = TextMarkerStyle.Disc };
            var stack = new List<(List list, int level)> { (rootList, 0) };

            foreach (var line in listLines)
            {
                var m = Regex.Match(line, "^(\\s*)\\*\\s+(.*)$");
                if (m.Success)
                {
                    int indent = m.Groups[1].Value.Length;
                    int level = Math.Max(0, (int)Math.Round(indent / 3.0));
                    string text = m.Groups[2].Value;

                    while (stack.Count > 1 && stack[stack.Count - 1].level > level)
                        stack.RemoveAt(stack.Count - 1);

                    var top = stack[stack.Count - 1];
                    if (top.level < level && top.list.ListItems.Count > 0)
                    {
                        var lastLi = top.list.ListItems.Cast<ListItem>().Last();
                        List nestedList = lastLi.Blocks.Count > 1 ? lastLi.Blocks.LastBlock as List : null;
                        if (nestedList == null)
                        {
                            nestedList = new List { MarkerStyle = TextMarkerStyle.Circle };
                            lastLi.Blocks.Add(nestedList);
                        }
                        stack.Add((nestedList, level));
                        top = stack[stack.Count - 1];
                    }

                    var para = new Paragraph();
                    AppendInlineMarkdownToParagraph(para, text, false);
                    top.list.ListItems.Add(new ListItem(para));
                }
                else
                {
                    var top = stack[stack.Count - 1];
                    if (top.list.ListItems.Count > 0)
                    {
                        var lastLi = top.list.ListItems.Cast<ListItem>().Last();
                        if (lastLi.Blocks.FirstBlock is Paragraph lastPara)
                        {
                            lastPara.Inlines.Add(new LineBreak());
                            AppendInlineMarkdownToParagraph(lastPara, line.Trim(), true);
                        }
                    }
                }
            }
            return rootList;
        }

        private void AppendInlineMarkdownToParagraph(Paragraph p, string text, bool append)
        {
            if (!append) p.Inlines.Clear();
            int lastIndex = 0;
            foreach (Match m in InlineImageRegex.Matches(text))
            {
                if (m.Index > lastIndex) p.Inlines.Add(new Run(text.Substring(lastIndex, m.Index - lastIndex)));

                if (m.Groups[1].Success)
                    p.Inlines.Add(new InlineUIContainer(BuildImageFromHtmlTag(m.Groups[1].Value)));
                else
                    p.Inlines.Add(new InlineUIContainer(BuildImageFromMarkdown(m.Groups[3].Value, m.Groups[4].Value)));

                lastIndex = m.Index + m.Length;
            }
            if (lastIndex < text.Length) p.Inlines.Add(new Run(text.Substring(lastIndex)));
        }

        private Image BuildImageFromHtmlTag(string tagStr)
        {
            string src = FirstGroupOrEmpty(Regex.Match(tagStr, "src\\s*=\\s*\"([^\"]*)\""), Regex.Match(tagStr, "src\\s*=\\s*'([^']*)'"));
            string alt = FirstGroupOrEmpty(Regex.Match(tagStr, "alt\\s*=\\s*\"([^\"]*)\""), Regex.Match(tagStr, "alt\\s*=\\s*'([^']*)'"));
            string style = FirstGroupOrEmpty(Regex.Match(tagStr, "style\\s*=\\s*\"([^\"]*)\""), Regex.Match(tagStr, "style\\s*=\\s*'([^']*)'"));

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

        private Image BuildImageFromMarkdown(string alt, string src)
        {
            var img = new Image
            {
                Tag = new ImageInfo { OriginalSrc = src, Alt = alt, Format = "md" },
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 4, 0, 4)
            };
            AutomationProperties.SetName(img, alt ?? "");
            img.ToolTip = src;
            SetImageSource(img, src);
            AttachImageDragHandlers(img);
            return img;
        }

        // ======================================================================
        //  Drag an embedded image OUT to Explorer / Desktop / other apps
        // ======================================================================
        private Point? imageDragStartPoint = null;

        private void AttachImageDragHandlers(Image img)
        {
            img.Cursor = Cursors.Hand;
            img.PreviewMouseLeftButtonDown += Image_PreviewMouseLeftButtonDown;
            img.PreviewMouseMove += Image_PreviewMouseMove;
        }

        private void Image_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            imageDragStartPoint = e.GetPosition(null);
        }

        private void Image_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || imageDragStartPoint == null) return;
            if (!(sender is Image img)) return;

            Point current = e.GetPosition(null);
            Vector diff = imageDragStartPoint.Value - current;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            imageDragStartPoint = null;

            string filePath = GetExportableFilePath(img);
            if (filePath == null) return;

            var data = new DataObject(DataFormats.FileDrop, new[] { filePath });
            DragDrop.DoDragDrop(img, data, DragDropEffects.Copy);
        }

        /// <summary>
        /// Resolves an embedded image's current, real on-disk path (in the "images" folder if the
        /// document has been saved, or the temp staging folder otherwise). Returns null for remote
        /// (http/https/data) images or if the underlying file can't be found.
        /// </summary>
        private string GetExportableFilePath(Image img)
        {
            if (!(img.Tag is ImageInfo info) || string.IsNullOrEmpty(info.OriginalSrc)) return null;
            string src = info.OriginalSrc;

            if (Uri.TryCreate(src, UriKind.Absolute, out Uri u) &&
                (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "data"))
                return null;

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
        /// Right-click "画像を保存…" - unlike a native drag to Explorer (where any same-name
        /// conflict at the destination is resolved by Explorer's own prompt), this fully controls
        /// the file write itself, so a same-named existing file is never overwritten or prompted
        /// about - a numeric suffix is appended automatically instead.
        /// </summary>
        private void SaveImageItem_Click(object sender, RoutedEventArgs e)
        {
            if (ctxImage == null) return;
            string sourcePath = GetExportableFilePath(ctxImage);
            if (sourcePath == null)
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
            if (dlg.ShowDialog() != true) return;

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

        private string FirstGroupOrEmpty(Match a, Match b)
        {
            if (a.Success) return a.Groups[1].Value;
            if (b.Success) return b.Groups[1].Value;
            return "";
        }

        private void SetImageSource(Image img, string src)
        {
            if (string.IsNullOrWhiteSpace(src)) return;
            try
            {
                Uri uri;
                if (Uri.TryCreate(src, UriKind.Absolute, out Uri absoluteUri) &&
                    (absoluteUri.Scheme == "http" || absoluteUri.Scheme == "https" || absoluteUri.Scheme == "data"))
                {
                    uri = absoluteUri;
                }
                else if (Path.IsPathRooted(src) && File.Exists(src))
                {
                    uri = new Uri(src, UriKind.Absolute);
                }
                else if (!string.IsNullOrEmpty(currentFileDirectory))
                {
                    string combined = Path.GetFullPath(Path.Combine(currentFileDirectory, src.Replace('/', Path.DirectorySeparatorChar)));
                    if (!File.Exists(combined)) return;
                    uri = new Uri(combined, UriKind.Absolute);
                }
                else
                {
                    return;
                }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = uri;
                bmp.EndInit();

                if (bmp.IsDownloading)
                {
                    bmp.DownloadCompleted += (s, e) => ApplyImageSizing(img);
                }

                img.Source = bmp;
                ApplyImageSizing(img);
            }
            catch
            {
                // Leave unresolved; the image area will simply appear blank.
            }
        }

        /// <summary>
        /// Sizes an image at up to 100% of its original pixel dimensions, shrinking it only if its
        /// natural width would overflow the available editor width (tall images are never shrunk just
        /// for height). The result scales automatically with the editor's zoom level, since zoom is
        /// applied as a uniform LayoutTransform over the whole editor - an image capped at 100% of the
        /// available width at 100% zoom remains fully visible (not cut off) at any zoom level, because
        /// the proportion of the viewport it occupies never changes.
        /// </summary>
        private void ApplyImageSizing(Image img)
        {
            if (!(img.Source is BitmapSource bmp)) return;
            double naturalWidth = bmp.PixelWidth;
            double naturalHeight = bmp.PixelHeight;
            if (naturalWidth <= 0 || naturalHeight <= 0) return;

            double availableWidth = GetAvailableImageWidth();
            double targetWidth = naturalWidth;
            if (availableWidth > 0 && naturalWidth > availableWidth)
            {
                targetWidth = availableWidth;
            }

            double scale = targetWidth / naturalWidth;
            img.Width = targetWidth;
            img.Height = naturalHeight * scale;
        }

        private double GetAvailableImageWidth()
        {
            double w = Editor.ActualWidth;
            if (w <= 0) return 560; // reasonable fallback before the first layout pass
            w -= Editor.Padding.Left + Editor.Padding.Right;
            w -= 24; // scrollbar + small safety margin so the right edge is never flush
            return Math.Max(100, w);
        }

        private void SourceEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            currentFileIsDirty = true;
            RefreshFolderTreeDirtyMarkers();
        }

        private void Editor_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (isSourceMode) return;
            foreach (var img in FindAllImages(Editor.Document))
            {
                ApplyImageSizing(img);
            }
        }

        // ======================================================================
        //  Drag & drop image files into the editor
        // ======================================================================
        private static readonly string[] ImageDropExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

        private bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && ImageDropExtensions.Contains(ext.ToLowerInvariant());
        }

        private void Editor_DragEnter(object sender, DragEventArgs e)
        {
            Editor_DragOver(sender, e);
        }

        private void Editor_DragOver(object sender, DragEventArgs e)
        {
            bool accept = !isSourceMode &&
                          e.Data.GetDataPresent(DataFormats.FileDrop) &&
                          e.Data.GetData(DataFormats.FileDrop) is string[] dragFiles &&
                          dragFiles.Any(IsImageFile);
            e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Editor_Drop(object sender, DragEventArgs e)
        {
            if (isSourceMode || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;

            var imageFiles = files.Where(IsImageFile).ToList();
            if (imageFiles.Count == 0) return;

            e.Handled = true;

            Point dropPoint = e.GetPosition(Editor);
            TextPointer insertAt = Editor.GetPositionFromPoint(dropPoint, true);
            if (insertAt == null) return;

            InvalidateOriginalText(insertAt);

            isProgrammaticChange = true;
            try
            {
                foreach (var file in imageFiles)
                {
                    // Always stage in the OS temp folder first, even for an already-saved document.
                    // This way, images added since the last save don't touch the real "images"
                    // folder until the user explicitly saves - so discarding unsaved changes truly
                    // leaves the on-disk file and folder untouched. Relocation into the real
                    // "images" folder happens in RelocatePendingTempImages on every Save/Save As.
                    string tempPath = CopyFileWithDedup(file, GetOrCreateTempImageFolder());
                    if (tempPath == null) continue;

                    var img = BuildImageFromMarkdown(Path.GetFileNameWithoutExtension(file), tempPath);
                    var container = new InlineUIContainer(img, insertAt);
                    insertAt = container.ElementEnd;
                }
                Editor.CaretPosition = insertAt;
            }
            finally
            {
                isProgrammaticChange = false;
            }

            RefreshOutline();
            Editor.Focus();
        }

        private string GetOrCreateTempImageFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mde", instanceTempId);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Copies sourcePath into destDir, appending a numeric suffix ("_1", "_2", ...) if a file
        /// with the same name already exists there. Returns the full destination path, or null on
        /// failure. If sourcePath already IS the same file as the (would-be) destination, no copy
        /// is made and the existing path is reused as-is.
        /// </summary>
        private string CopyFileWithDedup(string sourcePath, string destDir)
        {
            try
            {
                Directory.CreateDirectory(destDir);

                string fileName = Path.GetFileName(sourcePath);
                string destPath = Path.Combine(destDir, fileName);

                if (File.Exists(destPath) && !PathsReferToSameFile(sourcePath, destPath))
                {
                    string baseName = Path.GetFileNameWithoutExtension(fileName);
                    string ext = Path.GetExtension(fileName);
                    int counter = 1;
                    do
                    {
                        fileName = baseName + "_" + counter + ext;
                        destPath = Path.Combine(destDir, fileName);
                        counter++;
                    } while (File.Exists(destPath));
                }

                if (!PathsReferToSameFile(sourcePath, destPath))
                {
                    File.Copy(sourcePath, destPath, false);
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
        /// Called right after the document's folder becomes known (first Save/Save As). Moves any
        /// images that were staged in the OS temp folder into the real "images" folder next to the
        /// saved file, and updates each image's stored path so MarkDown serialization uses the
        /// final relative path.
        /// </summary>
        private void RelocatePendingTempImages()
        {
            if (string.IsNullOrEmpty(currentFileDirectory)) return;

            string tempDir;
            try { tempDir = Path.GetFullPath(GetOrCreateTempImageFolder()); }
            catch { return; }

            foreach (var img in FindAllImages(Editor.Document))
            {
                if (!(img.Tag is ImageInfo info) || string.IsNullOrEmpty(info.OriginalSrc)) continue;
                if (!Path.IsPathRooted(info.OriginalSrc)) continue; // already a relative path; nothing to do

                string fullSrc;
                try { fullSrc = Path.GetFullPath(info.OriginalSrc); } catch { continue; }
                if (!fullSrc.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase)) continue; // not ours

                string destPath = CopyFileWithDedup(fullSrc, Path.Combine(currentFileDirectory, "images"));
                if (destPath == null) continue;

                info.OriginalSrc = "images/" + Path.GetFileName(destPath);
                SetImageSource(img, info.OriginalSrc);

                try { File.Delete(fullSrc); } catch { /* best-effort cleanup */ }
            }
        }

        private bool PathsReferToSameFile(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void ResolveImages(FlowDocument doc)
        {
            foreach (var img in FindAllImages(doc))
            {
                if (img.Tag is ImageInfo info) SetImageSource(img, info.OriginalSrc);
            }
        }

        private IEnumerable<Image> FindAllImages(FlowDocument doc)
        {
            foreach (Block block in doc.Blocks)
                foreach (var img in FindImagesInBlock(block))
                    yield return img;
        }

        private IEnumerable<Image> FindImagesInBlock(Block block)
        {
            if (block is Paragraph p)
            {
                foreach (var img in FindImagesInInlines(p.Inlines)) yield return img;
            }
            else if (block is List list)
            {
                foreach (ListItem li in list.ListItems)
                    foreach (Block b in li.Blocks)
                        foreach (var img in FindImagesInBlock(b)) yield return img;
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup rg in table.RowGroups)
                    foreach (TableRow row in rg.Rows)
                        foreach (TableCell cell in row.Cells)
                            foreach (Block b in cell.Blocks)
                                foreach (var img in FindImagesInBlock(b)) yield return img;
            }
        }

        private IEnumerable<Image> FindImagesInInlines(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                if (inline is InlineUIContainer iuc && iuc.Child is Image im) yield return im;
                else if (inline is Span span)
                    foreach (var img in FindImagesInInlines(span.Inlines)) yield return img;
            }
        }

        private void VersionInfo_Click( object sender, RoutedEventArgs e )
        {
            //AboutWindowを表示する
            var aboutWindow = new AboutWindow();
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
        }
    }
}
