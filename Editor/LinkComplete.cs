using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Leaflet.Core;
using Leaflet.UI;

namespace Leaflet.Editor;

/// <summary>
/// Подсказка при наборе ссылки: после «[[» — заметки по мере набора, после «#» — разделы выбранной заметки.
/// Enter / Tab вставляет, Esc закрывает, стрелки выбирают. Окно не берёт фокус у редактора, обычный набор и отмена не затронуты.
/// </summary>
public sealed class LinkComplete
{
    sealed record Item(string Title, string? Detail, string Insert, Note? Note);

    const int Rows = 8;

    readonly TextEditor _editor;
    readonly TextArea _area;
    readonly TextView _view;
    readonly LivePreview _preview;
    readonly Func<Vault?> _vault;
    readonly Func<Note?> _current;
    readonly Canvas _canvas = new();
    readonly Border _box;
    readonly ListBox _list;
    bool _active, _headingMode, _positionQueued;
    int _start;
    Note? _lastNote; // заметка, выбранная в списке заметок: её разделы покажем, когда набрано «#»

    /// <summary>Слой над редактором: кладётся в ту же ячейку, что и редактор.</summary>
    public Control View => _canvas;
    public bool IsOpen => _box.IsVisible;
    public string[] Titles => _list.ItemsSource is IEnumerable<Item> items ? items.Select(i => i.Title).ToArray() : [];
    public string? Selected => (_list.SelectedItem as Item)?.Title;
    public bool HeadingMode => _headingMode;

    public LinkComplete(TextEditor editor, LivePreview preview, Func<Vault?> vault, Func<Note?> current)
    {
        _editor = editor;
        _area = editor.TextArea;
        _view = _area.TextView;
        _preview = preview;
        _vault = vault;
        _current = current;

        _list = new ListBox
        {
            Background = Brushes.Transparent, MaxHeight = Rows * 30, Classes = { "palette" }, Focusable = false,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Item>((item, _) => Build(item)),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _box = new Border
        {
            IsVisible = false, Width = 340, Background = Skin.Panel, BorderBrush = Skin.Accent, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Skin.IsTerminal ? 0 : 6), BoxShadow = BoxShadows.Parse("0 8 24 0 #88000000"), Child = _list,
        };
        _canvas.Children.Add(_box);

        _area.TextEntered += OnEntered;
        _area.Caret.PositionChanged += (_, _) => { if (_active) Refresh(); };
        _editor.LostFocus += (_, _) => Close();
        _editor.DocumentChanged += (_, _) => Close();
        _area.AddHandler(InputElement.PointerPressedEvent, (_, _) => Close(), RoutingStrategies.Tunnel, handledEventsToo: true);
        _view.ScrollOffsetChanged += (_, _) => { if (IsOpen) QueuePosition(); };
        // Щелчок по списку не должен уводить фокус из редактора: выбираем на отпускании.
        _box.AddHandler(InputElement.PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        _list.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true)?.DataContext is Item item) { _list.SelectedItem = item; Accept(); }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Применить тему к рамке (радиус углов); цвета следуют за <see cref="Skin"/> сами.</summary>
    public void ApplyTheme() => _box.CornerRadius = new CornerRadius(Skin.IsTerminal ? 0 : 6);

    static Control Build(Item? item)
    {
        var row = new DockPanel { LastChildFill = true };
        if (item?.Detail != null)
        {
            var detail = Ui.Text(item.Detail, 12, Skin.Mute);
            detail.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(detail, Dock.Right);
            row.Children.Add(detail);
        }
        row.Children.Add(Ui.Text(item?.Title ?? "", 13.5));
        return row;
    }

    // ---------- открытие и обновление ----------

    void OnEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text != "[" || _vault() == null || _editor.IsReadOnly) return;
        int caret = _area.Caret.Offset;
        var doc = _editor.Document;
        if (caret < 2 || doc.GetCharAt(caret - 1) != '[' || doc.GetCharAt(caret - 2) != '[') return;
        if (_preview.Blocks.IsCode(_area.Caret.Line)) return;
        _active = true;
        _start = caret;
        _lastNote = null;
        Refresh();
    }

    public void Close()
    {
        _active = false;
        if (_box.IsVisible) _box.IsVisible = false;
    }

    void Refresh()
    {
        var vault = _vault();
        var doc = _editor.Document;
        int caret = _area.Caret.Offset;
        if (vault == null || caret < _start || caret - _start > 80 || _start < 2 || _start > doc.TextLength
            || doc.GetText(_start - 2, 2) != "[[") { Close(); return; }
        string q = doc.GetText(_start, caret - _start);
        if (q.IndexOfAny(['\n', '\r', '[', ']', '|']) >= 0) { Close(); return; }
        int hash = q.IndexOf('#');
        _headingMode = hash >= 0;
        Show(hash < 0 ? NoteItems(vault, q.Trim()) : HeadingItems(vault, q[..hash].Trim(), q[(hash + 1)..].Trim()));
    }

    /// <summary>Как заметка записывается в ссылку: по имени, а если имя встречается дважды — с путём.</summary>
    static string NameFor(Vault vault, Note note, Note? from) =>
        vault.Resolve(note.Title, from) == note ? note.Title : note.Rel[..^3];

    List<Item> NoteItems(Vault vault, string q)
    {
        var cur = _current();
        var notes = vault.Notes.Where(n => n != cur);
        IEnumerable<Note> found = q.Length == 0
            ? notes.OrderByDescending(n => n.Modified)
            : notes.Select(n => (n, s: BestScore(n, q))).Where(x => x.s >= 0)
                .OrderBy(x => x.s).ThenBy(x => x.n.Title.Length).ThenBy(x => x.n.Title, StringComparer.CurrentCultureIgnoreCase).Select(x => x.n);
        var items = found.Take(50)
            .Select(n => new Item(n.Title, n.Folder.Length > 0 ? n.Folder : null, NameFor(vault, n, cur), n)).ToList();
        _lastNote = items.FirstOrDefault()?.Note;
        return items;
    }

    static int BestScore(Note n, string q)
    {
        int a = CommandPalette.Score(n.Title, q), b = q.Contains('/') ? CommandPalette.Score(n.Rel[..^3], q) : -1;
        return a < 0 ? b : b < 0 ? a : Math.Min(a, b);
    }

    List<Item> HeadingItems(Vault vault, string name, string filter)
    {
        var cur = _current();
        var target = name.Length == 0 ? cur : vault.Resolve(name, cur) ?? _lastNote;
        if (target == null) return [];
        string text = target == cur ? _editor.Document.Text : target.Text;
        string link = name.Length == 0 && target == cur ? "" : NameFor(vault, target, cur);
        return Markdown.Headings(text)
            .Select((h, i) => (h, i, s: CommandPalette.Score(h.Text, filter)))
            .Where(x => x.s >= 0).OrderBy(x => x.s).ThenBy(x => x.i)
            .Select(x => new Item(x.h.Text, new string('#', x.h.Level), link + "#" + x.h.Text, target)).Take(50).ToList();
    }

    void Show(List<Item> items)
    {
        _list.ItemsSource = items;
        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
        if (items.Count == 0) { _box.IsVisible = false; return; }
        _box.IsVisible = true;
        QueuePosition();
    }

    void QueuePosition()
    {
        if (_positionQueued) return;
        _positionQueued = true;
        Dispatcher.UIThread.Post(Position, DispatcherPriority.Loaded);
    }

    /// <summary>Под началом набираемой ссылки; если снизу не хватает места — над строкой.</summary>
    void Position()
    {
        _positionQueued = false;
        if (!IsOpen) return;
        var loc = _editor.Document.GetLocation(Math.Clamp(_start, 0, _editor.Document.TextLength));
        Point At(VisualYPosition y)
        {
            var p = _view.GetVisualPosition(new TextViewPosition(loc), y);
            var local = new Point(p.X - _view.ScrollOffset.X, p.Y - _view.ScrollOffset.Y);
            return _view.TranslatePoint(local, _canvas) ?? local;
        }
        _box.Measure(new Size(_box.Width, double.PositiveInfinity));
        double h = _box.DesiredSize.Height;
        var below = At(VisualYPosition.LineBottom);
        double y = below.Y + 2;
        if (y + h > _canvas.Bounds.Height) y = Math.Max(0, At(VisualYPosition.LineTop).Y - h - 2);
        Canvas.SetLeft(_box, Math.Clamp(below.X, 4, Math.Max(4, _canvas.Bounds.Width - _box.Width - 4)));
        Canvas.SetTop(_box, y);
    }

    // ---------- клавиши и вставка ----------

    /// <summary>Клавиши, пока подсказка открыта. true — нажатие обработано.</summary>
    public bool OnKey(KeyEventArgs e)
    {
        if (!IsOpen || e.KeyModifiers != KeyModifiers.None) return false;
        int count = _list.ItemCount;
        switch (e.Key)
        {
            case Key.Down: Move(1, count); break;
            case Key.Up: Move(-1, count); break;
            case Key.Enter or Key.Tab: Accept(); break;
            case Key.Escape: Close(); break;
            default: return false;
        }
        e.Handled = true;
        return true;
    }

    void Move(int step, int count)
    {
        if (count == 0) return;
        _list.SelectedIndex = (_list.SelectedIndex + step + count) % count;
        _list.ScrollIntoView(_list.SelectedIndex);
        if (!_headingMode && _list.SelectedItem is Item { Note: { } note }) _lastNote = note;
    }

    /// <summary>Заменяет набранное выбранным пунктом и ставит курсор за «]]».</summary>
    void Accept()
    {
        if (_list.SelectedItem is not Item item) return;
        var doc = _editor.Document;
        int caret = _area.Caret.Offset, start = _start;
        bool closed = caret + 2 <= doc.TextLength && doc.GetText(caret, 2) == "]]";
        Close();
        doc.Replace(start, caret - start, closed ? item.Insert : item.Insert + "]]");
        _area.Caret.Offset = start + item.Insert.Length + 2;
    }
}
