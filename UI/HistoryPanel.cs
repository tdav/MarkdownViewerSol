using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Leaflet.Core;

namespace Leaflet.UI;

/// <summary>Версия для сравнения: снимок из истории или текст файла на диске.</summary>
sealed record VersionItem(string Title, string? Detail, Func<string> Load);

/// <summary>
/// Накладка «История версий»: слева версии, справа различия с текущим текстом (только изменённые места, по 3 строки вокруг),
/// внизу — возврат выбранной версии. Её же показывает конфликт с файлом на диске.
/// </summary>
sealed class HistoryPanel : Panel
{
    readonly Border _card;
    readonly TextBlock _title, _legend, _summary, _restoreText;
    readonly ListBox _list;
    readonly TextEditor _diff;
    readonly Border _restore;
    readonly DiffColors _colors = new();
    IReadOnlyList<VersionItem> _items = [];
    Func<string> _current = () => "";
    Action<VersionItem, string>? _onRestore;
    string? _loaded;
    Control? _returnFocus;

    public bool IsOpen => IsVisible;
    public int Count => _items.Count;
    public int SelectedIndex => _list.SelectedIndex;
    public string DiffText => _diff.Text;
    public string Summary => _summary.Text ?? "";
    public bool CanRestore => _restore.IsVisible;

    readonly Border _close;
    readonly TextBlock _doneText;

    /// <summary>Язык сменился: подписи, которые стоят постоянно (остальное ставится при открытии).</summary>
    public void Relocalize()
    {
        ToolTip.SetTip(_close, Loc.T("common.close.esc"));
        _doneText.Text = Loc.T("common.close");
    }

    public HistoryPanel()
    {
        IsVisible = false;
        Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
        PointerPressed += (_, e) => { if (e.Source == this) Close(); };

        _title = Ui.Text("", 16, Skin.Heading, FontWeight.Bold);
        var close = _close = Ui.Button(Ui.Icon(Ui.Close, Skin.Mute, 12), Close, Loc.T("common.close.esc"), 4, new Thickness(6));
        DockPanel.SetDock(close, Dock.Right);
        var header = new DockPanel { Margin = new Thickness(20, 12, 12, 8), Children = { close, _title } };

        _list = new ListBox
        {
            Background = Brushes.Transparent, Classes = { "palette" },
            ItemTemplate = new FuncDataTemplate<VersionItem>((item, _) => item == null ? new Panel() : new StackPanel
            {
                Margin = new Thickness(4, 2), Spacing = 1,
                Children = { Ui.Text(item.Title, 13.5), Ui.Text(item.Detail ?? "", 11.5, Skin.Mute) },
            }),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => ShowSelected();
        var listBox = new Border { Width = 240, BorderBrush = Skin.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = _list };

        _diff = new TextEditor
        {
            IsReadOnly = true, WordWrap = true, ShowLineNumbers = false, FontFamily = Skin.Mono.FontFamily, FontSize = 13,
            Foreground = Skin.Fg, Background = Brushes.Transparent, Padding = new Thickness(10, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        _diff.Options.EnableHyperlinks = false;
        _diff.Options.EnableEmailHyperlinks = false;
        _diff.TextArea.SelectionBrush = Skin.Selection;
        _diff.TextArea.Caret.CaretBrush = Brushes.Transparent;
        _diff.TextArea.TextView.BackgroundRenderers.Add(_colors);
        _diff.TextArea.TextView.LineTransformers.Add(_colors);
        _legend = new TextBlock { FontSize = 11.5, Foreground = Skin.Mute, Margin = new Thickness(12, 6), TextWrapping = TextWrapping.Wrap };
        var diffBox = new DockPanel { Children = { _legend, _diff } };
        DockPanel.SetDock(_legend, Dock.Top);
        var body = new DockPanel { Children = { listBox, diffBox } };
        DockPanel.SetDock(listBox, Dock.Left);

        _summary = new TextBlock { FontSize = 12.5, Foreground = Skin.Mute, VerticalAlignment = VerticalAlignment.Center };
        _restoreText = Ui.Text("", 13, Skin.AccentFg, FontWeight.Bold);
        _restore = Button(_restoreText, Restore, accent: true);
        _doneText = Ui.Text(Loc.T("common.close"), 13);
        var done = Button(_doneText, Close);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { done, _restore } };
        DockPanel.SetDock(buttons, Dock.Right);
        var footer = new DockPanel
        {
            Margin = new Thickness(20, 10), Children = { buttons, _summary },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(new Border { BorderBrush = Skin.Line, BorderThickness = new Thickness(0, 1), Child = body, [Grid.RowProperty] = 1 });
        grid.Children.Add(footer);
        _card = new Border
        {
            Width = 900, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 44, 0, 0), Background = Skin.Panel, BorderBrush = Skin.Accent,
            BorderThickness = new Thickness(1), BoxShadow = BoxShadows.Parse("0 12 40 0 #99000000"), Child = grid,
        };
        Children.Add(_card);
        SizeChanged += (_, e) =>
        {
            _card.Height = Math.Max(240, e.NewSize.Height - 88);
            _card.Width = Math.Min(900, e.NewSize.Width - 48);
        };
    }

    /// <summary>
    /// Открывает список версий. Различия — от текущего текста к версии: «+» вернётся, «−» уйдёт.
    /// <paramref name="restore"/> получает выбранную версию и её текст.
    /// </summary>
    public void Open(string title, IReadOnlyList<VersionItem> items, Func<string> current, string legend, string restoreLabel, Action<VersionItem, string> restore)
    {
        _returnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        _card.CornerRadius = new CornerRadius(Skin.IsTerminal ? 0 : 8);
        _diff.FontFamily = Skin.Mono.FontFamily;
        _title.Text = title;
        _legend.Text = legend;
        _restoreText.Text = restoreLabel;
        _items = items;
        _current = current;
        _onRestore = restore;
        IsVisible = true;
        _list.ItemsSource = items;
        _list.SelectedIndex = -1;
        if (items.Count > 0) _list.SelectedIndex = 0;
        else ShowSelected();
        _list.Focus();
    }

    public void Select(int index) => _list.SelectedIndex = index;

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        _items = [];
        _list.ItemsSource = null;
        _diff.Document = new TextDocument();
        _returnFocus?.Focus();
    }

    public void Restore()
    {
        if (_list.SelectedItem is not VersionItem item || _loaded == null || !_restore.IsVisible) return;
        string text = _loaded;
        var run = _onRestore;
        Close();
        run?.Invoke(item, text);
    }

    void ShowSelected()
    {
        _loaded = null;
        if (_list.SelectedItem is not VersionItem item)
        {
            SetDiff([], _items.Count == 0 ? Loc.T("hist.none") : "");
            return;
        }
        string text;
        try { text = item.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetDiff([], Loc.T("hist.read.fail") + ex.Message);
            return;
        }
        _loaded = text;
        var lines = LineDiff.Compare(_current(), text);
        var (added, removed) = LineDiff.Count(lines);
        SetDiff(LineDiff.Hunks(lines), added + removed == 0 ? Loc.T("hist.same") : Loc.T("hist.delta", added, removed));
        _restore.IsVisible = added + removed > 0;
    }

    void SetDiff(List<DiffLine> lines, string summary)
    {
        _summary.Text = summary;
        _restore.IsVisible = false;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            var l = lines[i];
            sb.Append(l.Kind switch { DiffKind.Add => "+ ", DiffKind.Del => "− ", DiffKind.Same => "  ", _ => "" }).Append(l.Text);
        }
        _colors.Kinds = lines.Select(l => l.Kind).ToArray();
        _diff.Document = new TextDocument(sb.ToString());
        _diff.ScrollToHome();
    }

    static Border Button(Control content, Action click, bool accent = false)
    {
        var b = new Border
        {
            Child = content, Cursor = Ui.Hand, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 5),
            Background = accent ? Skin.Accent : Brushes.Transparent, BorderBrush = accent ? Skin.Accent : Skin.Line, BorderThickness = new Thickness(1),
        };
        if (!accent)
        {
            b.PointerEntered += (_, _) => b.Background = Skin.Sel;
            b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        }
        b.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left && b.IsPointerOver) { e.Handled = true; click(); }
        };
        return b;
    }

    /// <summary>Фон строк «+»/«−» во всю ширину, цвет знака, приглушённые строки пропусков.</summary>
    sealed class DiffColors : DocumentColorizingTransformer, IBackgroundRenderer
    {
        public DiffKind[] Kinds = [];
        public KnownLayer Layer => KnownLayer.Background;

        DiffKind KindOf(int lineNumber) => lineNumber - 1 < Kinds.Length ? Kinds[lineNumber - 1] : DiffKind.Same;

        public void Draw(TextView textView, DrawingContext dc)
        {
            foreach (var line in textView.VisualLines)
            {
                var brush = KindOf(line.FirstDocumentLine.LineNumber) switch { DiffKind.Add => Skin.DiffAdd, DiffKind.Del => Skin.DiffDel, _ => null };
                if (brush == null) continue;
                dc.FillRectangle(brush, new Rect(0, line.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, line.Height));
            }
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            switch (KindOf(line.LineNumber))
            {
                case DiffKind.Gap:
                    ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(Skin.Mute));
                    break;
                case DiffKind.Add when line.Length > 0:
                    ChangeLinePart(line.Offset, line.Offset + 1, e => e.TextRunProperties.SetForegroundBrush(Skin.DiffAddFg));
                    break;
                case DiffKind.Del when line.Length > 0:
                    ChangeLinePart(line.Offset, line.Offset + 1, e => e.TextRunProperties.SetForegroundBrush(Skin.DiffDelFg));
                    break;
            }
        }
    }
}
