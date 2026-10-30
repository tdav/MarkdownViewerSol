using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Document;
using Leaflet.Core;
using Leaflet.Editor;

namespace Leaflet.UI;

/// <summary>Правая панель «Графита»: обратные ссылки, структура по заголовкам, теги заметки.</summary>
sealed class RightPanel : Border
{
    readonly StackPanel _backlinks = new(), _outline = new();
    readonly WrapPanel _tags = new();
    readonly TextBlock _backCount = Ui.Text("", 11, Skin.Mute);
    readonly List<(int Line, TextBlock View)> _headings = [];
    int _activeHeading = -1;
    readonly List<(TextBlock View, string Id)> _titles = [];

    public event Action<Backlink>? OpenBacklink;
    public event Action<int>? GoToLine;
    public event Action<string>? SearchTag;

    public int BacklinkCount { get; private set; }
    public IEnumerable<Control> TagChips => _tags.Children;

    public RightPanel()
    {
        Background = Skin.Panel;
        BorderBrush = Skin.Line;
        BorderThickness = new Thickness(1, 0, 0, 0);
        Width = 240;
        var stack = new StackPanel { Spacing = 16, Margin = new Thickness(12) };
        stack.Children.Add(Section("rp.backlinks", _backCount, _backlinks));
        stack.Children.Add(Section("rp.outline", null, _outline));
        stack.Children.Add(Section("rp.tags", null, _tags));
        var scroll = new ScrollViewer { Content = stack };
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Child = scroll;
    }

    Control Section(string titleId, Control? right, Control body)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (right != null) { DockPanel.SetDock(right, Dock.Right); head.Children.Add(right); }
        var title = Ui.Caps(Loc.T(titleId));
        _titles.Add((title, titleId));
        head.Children.Add(title);
        return new StackPanel { Children = { head, body } };
    }

    /// <summary>Язык сменился: заголовки секций (сообщения «нет…» перестраивает окно через <c>UpdateNoteInfo</c>).</summary>
    public void Relocalize()
    {
        foreach (var (view, id) in _titles) view.Text = Loc.T(id).ToUpperInvariant();
    }

    public void ShowBacklinks(List<Backlink> links)
    {
        BacklinkCount = links.Count;
        _backCount.Text = links.Count > 0 ? links.Count.ToString() : "";
        _backlinks.Children.Clear();
        if (links.Count == 0) { _backlinks.Children.Add(Empty(Loc.T("rp.nobacklinks"))); return; }
        foreach (var link in links.Take(100))
        {
            var card = new Border
            {
                Background = Skin.Bg, BorderBrush = Skin.Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Skin.IsTerminal ? 0 : 6), Padding = new Thickness(9, 6),
                Margin = new Thickness(0, 0, 0, 6), Cursor = Ui.Hand,
                Child = new StackPanel
                {
                    Children =
                    {
                        Ui.Text(link.From.Title, 12.5, Skin.Fg, FontWeight.Bold),
                        Ui.Marked(link.Snippet, link.MatchStart, link.MatchLength, 12, Skin.Mute, Skin.Accent),
                    },
                },
            };
            var l = link;
            card.PointerEntered += (_, _) => card.BorderBrush = Skin.Accent;
            card.PointerExited += (_, _) => card.BorderBrush = Skin.Line;
            card.PointerReleased += (_, _) => OpenBacklink?.Invoke(l);
            _backlinks.Children.Add(card);
        }
    }

    /// <summary>Структура и теги по тексту документа (зовётся после паузы в наборе).</summary>
    public void ShowOutline(TextDocument doc, BlockMap blocks)
    {
        _outline.Children.Clear();
        _headings.Clear();
        _activeHeading = -1;
        foreach (var line in doc.Lines)
        {
            if (line.Length < 2 || doc.GetCharAt(line.Offset) != '#' || blocks[line.LineNumber] != BlockMap.Text) continue;
            string text = doc.GetText(line);
            var m = Markdown.Heading.Match(text);
            if (!m.Success) continue;
            int level = m.Groups[1].Length;
            var tb = new TextBlock
            {
                Text = Clean(text[m.Length..]), FontSize = 12.5, Foreground = Skin.Mute,
                TextTrimming = TextTrimming.CharacterEllipsis, Padding = new Thickness((level - 1) * 12, 2, 0, 2),
                Cursor = Ui.Hand, Background = Brushes.Transparent,
            };
            int n = line.LineNumber;
            tb.PointerReleased += (_, _) => GoToLine?.Invoke(n);
            _headings.Add((n, tb));
            _outline.Children.Add(tb);
            if (_headings.Count >= 300) break;
        }
        if (_headings.Count == 0) _outline.Children.Add(Empty(Loc.T("rp.noheadings")));

        _tags.Children.Clear();
        var tags = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        string all = doc.Text;
        foreach (var m in Markdown.TagMatches(all))
            if (tags.Add(m.Value) && tags.Count >= 60) break;
        foreach (var tag in tags)
        {
            var chip = Ui.Chip(tag);
            chip.Cursor = Ui.Hand;
            chip.PointerReleased += (_, _) => SearchTag?.Invoke(tag);
            _tags.Children.Add(chip);
        }
        if (tags.Count == 0) _tags.Children.Add(Empty(Loc.T("rp.notags")));
    }

    /// <summary>Подсвечивает раздел, в котором стоит курсор.</summary>
    public void SetCaretLine(int line)
    {
        int active = -1;
        for (int i = 0; i < _headings.Count && _headings[i].Line <= line; i++) active = i;
        if (active == _activeHeading) return;
        if (_activeHeading >= 0 && _activeHeading < _headings.Count) _headings[_activeHeading].View.Foreground = Skin.Mute;
        if (active >= 0) _headings[active].View.Foreground = Skin.Accent;
        _activeHeading = active;
    }

    static readonly Regex InlineMarks = new(@"\*\*|__|\*|`|==|~~|\[\[|\]\]", RegexOptions.Compiled);
    static string Clean(string s) => InlineMarks.Replace(s, "").Trim();

    static TextBlock Empty(string text) => new() { Text = text, FontSize = 12, Foreground = Skin.Mute };
}
