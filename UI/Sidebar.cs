using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Leaflet.Core;

namespace Leaflet.UI;

enum RowKind { Folder, Note, Section, Hit, Empty, Tag }

sealed class Row
{
    public RowKind Kind;
    public string Text = "";
    public int Depth;
    public Note? Note;
    public string Folder = "";
    public bool Open;
    public SearchHit Hit;
    public int MatchLength;
    public int Count; // для тега: число заметок
}

/// <summary>Левая панель: имя хранилища, поиск по заметкам и дерево папок (или результаты поиска).</summary>
sealed class Sidebar : Border
{
    readonly TextBlock _title;
    readonly TextBox _search;
    readonly ListBox _list;
    readonly DispatcherTimer _searchDelay;
    readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    Vault? _vault;
    Note? _current;
    CancellationTokenSource? _searchCts;
    bool _showingSearch;

    public event Action<Note, bool>? OpenNote;           // заметка, в новой вкладке
    public event Action<SearchHit, string>? OpenHit;     // найденное место и строка поиска
    public event Action? NewNote;
    public event Action? OpenFolder;
    public event Action<Note>? RenameNote;
    public event Action<Note>? MoveNote;
    public event Action<Note>? DeleteNote;
    public event Action<Note, bool>? ExportNote; // true — PDF

    public string Query => _search.Text?.Trim() ?? "";
    public IEnumerable<Row> Rows => _list.Items.OfType<Row>();
    public void ActivateRow(Row row) => Activate(row, false);
    /// <summary>Строка поиска — запрос по тегу: «#» и буквы, цифры, «_», «/», «-».</summary>
    public static bool IsTagQuery(string query) => TagQuery.IsMatch(query);
    static readonly System.Text.RegularExpressions.Regex TagQuery = new(@"^#[\p{L}\p{N}_/-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    readonly Border _addButton, _folderButton, _searchKeys;

    /// <summary>Подсказки и значок сочетания в поле поиска по текущим сочетаниям.</summary>
    public void RefreshHints()
    {
        var keys = Keymap.Current;
        ToolTip.SetTip(_addButton, keys.Tip(Loc.T("cmd.new"), "new"));
        ToolTip.SetTip(_folderButton, keys.Tip(Loc.T("act.folder"), "folder"));
        _search.PlaceholderText = Loc.T("sb.search");
        _searchKeys.Child = keys.Hint("search") is { Length: > 0 } h ? Ui.Kbd(h) : null;
        if (_vault != null) Rebuild();
    }

    public Sidebar()
    {
        Background = Skin.Panel;
        BorderBrush = Skin.Line;
        BorderThickness = new Thickness(0, 0, 1, 0);
        Width = 220;
        Padding = new Thickness(6, 10, 6, 6);

        _title = Ui.Text("", 13, Skin.Fg, FontWeight.Bold);
        var head = new DockPanel { Margin = new Thickness(6, 0, 0, 8), Height = 26 };
        var add = _addButton = Ui.Button(Ui.Icon(Ui.Plus, Skin.Mute), () => NewNote?.Invoke(), padding: new Thickness(5));
        var folder = _folderButton = Ui.Button(Ui.Icon(Ui.OpenFolder, Skin.Mute), () => OpenFolder?.Invoke(), padding: new Thickness(5));
        DockPanel.SetDock(add, Dock.Right);
        DockPanel.SetDock(folder, Dock.Right);
        head.Children.Add(add);
        head.Children.Add(folder);
        head.Children.Add(_title);

        _search = new TextBox
        {
            PlaceholderText = Loc.T("sb.search"),
            FontSize = 13,
            Margin = new Thickness(4, 0, 4, 8),
            Padding = new Thickness(4, 4),
            MinHeight = 28,
            CornerRadius = new CornerRadius(6),
            InnerLeftContent = new Border { Padding = new Thickness(8, 0, 0, 0), Child = Ui.Icon(Ui.Search, Skin.Mute, 14) },
            InnerRightContent = _searchKeys = new Border { Padding = new Thickness(0, 0, 6, 0) },
        };
        RefreshHints();
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Rebuild(); };
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _search.Text = ""; Rebuild(); e.Handled = true; }
            else if (e.Key == Key.Down && _list.ItemCount > 0) { _list.SelectedIndex = FirstClickable(); _list.ContainerFromIndex(_list.SelectedIndex)?.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter) { _searchDelay.Stop(); Rebuild(); Activate(_list.Items.OfType<Row>().FirstOrDefault(r => r.Kind is RowKind.Note or RowKind.Hit or RowKind.Tag), false); e.Handled = true; }
        };

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Row>((_, _) => new RowView(), supportsRecycling: true),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton is not (MouseButton.Left or MouseButton.Middle)) return;
            var row = (e.Source as Visual)?.FindAncestorOfType<RowView>(includeSelf: true)?.DataContext as Row;
            if (row != null) Activate(row, e.InitialPressMouseButton == MouseButton.Middle || e.KeyModifiers.HasFlag(KeyModifiers.Control));
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _list.ContextRequested += (_, e) =>
        {
            var view = (e.Source as Visual)?.FindAncestorOfType<RowView>(includeSelf: true);
            if (view?.DataContext is not Row { Note: { } note }) return;
            ShowMenu(view, note);
            e.Handled = true;
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is Row r) { Activate(r, false); e.Handled = true; }
            else if (Keymap.Current.Match(e) == "rename" && _list.SelectedItem is Row { Note: { } rn }) { RenameNote?.Invoke(rn); e.Handled = true; }
            else if (e.Key == Key.Delete && _list.SelectedItem is Row { Note: { } dn }) { DeleteNote?.Invoke(dn); e.Handled = true; }
            else if (e.Key is Key.Left or Key.Right && _list.SelectedItem is Row { Kind: RowKind.Folder } f && f.Open != (e.Key == Key.Right))
            { Activate(f, false); e.Handled = true; }
        };

        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(_search);
        dock.Children.Add(_list);
        Child = dock;
    }

    void ShowMenu(Control target, Note note)
    {
        MenuItem Item(string header, string? key, Action run)
        {
            var item = new MenuItem { Header = header, InputGesture = key == null ? null : KeyGesture.Parse(key) };
            item.Click += (_, _) => run();
            return item;
        }
        var menu = new ContextMenu
        {
            Items =
            {
                Item(Loc.T("sb.opentab"), null, () => OpenNote?.Invoke(note, true)),
                Item(Loc.T("sb.rename"), "F2", () => RenameNote?.Invoke(note)),
                Item(Loc.T("sb.move"), null, () => MoveNote?.Invoke(note)),
                Item(Loc.T("sb.exphtml"), null, () => ExportNote?.Invoke(note, false)),
                Item(Loc.T("sb.exppdf"), null, () => ExportNote?.Invoke(note, true)),
                new Separator(),
                Item(Loc.T("sb.trash"), "Delete", () => DeleteNote?.Invoke(note)),
            },
        };
        menu.Open(target);
    }

    int FirstClickable()
    {
        int i = 0;
        foreach (var r in _list.Items.OfType<Row>()) { if (r.Kind is not (RowKind.Section or RowKind.Empty)) return i; i++; }
        return 0;
    }

    public void SetQuery(string text)
    {
        _search.Text = text;
        _search.CaretIndex = text.Length;
    }

    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    public void SetVault(Vault? vault)
    {
        _vault = vault;
        _title.Text = vault?.Name ?? "Leaflet";
        _open.Clear();
        Rebuild();
    }

    /// <summary>Отмечает открытую заметку и раскрывает её папки.</summary>
    public void SetCurrent(Note? note)
    {
        _current = note;
        if (note != null && note.Folder.Length > 0)
        {
            var parts = note.Folder.Split('/');
            bool added = false;
            for (int i = 1; i <= parts.Length; i++) added |= _open.Add(string.Join('/', parts[..i]));
            if (added && Query.Length == 0) { Rebuild(); return; }
        }
        SyncSelection();
    }

    void SyncSelection()
    {
        if (Query.Length > 0) return;
        var row = _list.Items.OfType<Row>().FirstOrDefault(r => r.Kind == RowKind.Note && r.Note == _current);
        _list.SelectedItem = row;
        if (row != null) _list.ScrollIntoView(row);
    }

    void Activate(Row? row, bool newTab)
    {
        if (row == null) return;
        switch (row.Kind)
        {
            case RowKind.Folder:
                if (!_open.Remove(row.Folder)) _open.Add(row.Folder);
                int index = _list.SelectedIndex;
                Rebuild();
                _list.SelectedIndex = index;
                break;
            case RowKind.Note:
                OpenNote?.Invoke(row.Note!, newTab);
                break;
            case RowKind.Hit:
                OpenHit?.Invoke(row.Hit, Query);
                break;
            case RowKind.Tag:
                SetQuery(row.Text);
                break;
        }
    }

    public void Rebuild()
    {
        if (Query.Length > 0) { _ = RebuildSearch(Query); return; }
        _searchCts?.Cancel();
        var rows = new List<Row>();
        if (_vault != null)
        {
            if (_vault.Notes.Count == 0)
                rows.Add(new Row { Kind = RowKind.Empty, Text = _vault.Loaded ? Loc.T("sb.empty") + (Keymap.Current.Hint("new") is { Length: > 0 } nk ? Loc.T("sb.empty.new", nk) : "") : Loc.T("sb.loading") });
            else AddFolder(rows, _vault.Notes.ToLookup(n => n.Folder, StringComparer.OrdinalIgnoreCase), "", 0);
        }
        SetRows(rows, search: false);
        SyncSelection();
    }

    static readonly StringComparer ByName = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, true);

    void AddFolder(List<Row> rows, ILookup<string, Note> byFolder, string folder, int depth)
    {
        string prefix = folder.Length == 0 ? "" : folder + "/";
        var subs = byFolder.Select(g => g.Key)
            .Where(k => k.Length > prefix.Length && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, ByName);
        foreach (var sub in subs)
        {
            string path = prefix + sub;
            bool open = _open.Contains(path);
            rows.Add(new Row { Kind = RowKind.Folder, Text = sub, Depth = depth, Folder = path, Open = open });
            if (open) AddFolder(rows, byFolder, path, depth + 1);
        }
        foreach (var n in byFolder[folder].OrderBy(n => n.Title, ByName))
            rows.Add(new Row { Kind = RowKind.Note, Text = n.Title, Depth = depth, Note = n });
    }

    async Task RebuildSearch(string query)
    {
        if (_vault == null) return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var notes = _vault.Notes.ToArray();
        if (IsTagQuery(query)) { await RebuildTags(query, notes, cts); return; }
        var titles = notes.Where(n => n.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n.Title.Length).Take(30).ToList();
        var hits = await Task.Run(() => Vault.SearchText(notes, query, 3, 300, cts.Token));
        if (cts.IsCancellationRequested) return;

        var rows = new List<Row>();
        if (titles.Count > 0)
        {
            rows.Add(new Row { Kind = RowKind.Section, Text = Loc.T("sb.notes", titles.Count) });
            rows.AddRange(titles.Select(n => new Row { Kind = RowKind.Note, Text = n.Title, Note = n }));
        }
        rows.Add(new Row { Kind = RowKind.Section, Text = hits.Count >= 300 ? Loc.T("sb.intext.many") : Loc.T("sb.intext", hits.Count) });
        if (hits.Count == 0) rows.Add(new Row { Kind = RowKind.Empty, Text = Loc.T("sb.nothing") });
        rows.AddRange(hits.Select(h => new Row { Kind = RowKind.Hit, Text = h.Note.Title, Note = h.Note, Hit = h, MatchLength = query.Length }));
        SetRows(rows, search: true);
    }

    /// <summary>«#тег» даёт заметки с этим тегом (и вложенными), «#» и начало имени — список подходящих тегов.</summary>
    async Task RebuildTags(string query, Note[] notes, CancellationTokenSource cts)
    {
        string tag = query[1..];
        var (counts, hits) = await Task.Run(() =>
        {
            var counts = Vault.TagCounts(notes);
            bool exact = tag.Length > 0 && notes.Any(n => Vault.HasTag(n, tag));
            return (counts, exact ? Vault.TagHits(notes, tag, cts.Token) : null);
        });
        if (cts.IsCancellationRequested) return;
        var rows = new List<Row>();
        if (hits != null)
        {
            rows.Add(new Row { Kind = RowKind.Section, Text = Loc.T("sb.tagged", query, hits.Count) });
            rows.AddRange(hits.Select(h => new Row { Kind = RowKind.Hit, Text = h.Note.Title, Note = h.Note, Hit = h, MatchLength = query.Length }));
        }
        else
        {
            var shown = counts.Where(c => c.Tag.StartsWith(tag, StringComparison.OrdinalIgnoreCase)).ToList();
            rows.Add(new Row { Kind = RowKind.Section, Text = tag.Length == 0 ? Loc.T("sb.tags", shown.Count) : Loc.T("sb.tagsstart", query, shown.Count) });
            if (shown.Count == 0) rows.Add(new Row { Kind = RowKind.Empty, Text = counts.Count == 0 ? Loc.T("sb.notags") : Loc.T("sb.notag") });
            rows.AddRange(shown.Take(300).Select(c => new Row { Kind = RowKind.Tag, Text = "#" + c.Tag, Count = c.Notes }));
        }
        SetRows(rows, search: true);
    }

    void SetRows(List<Row> rows, bool search)
    {
        bool reset = search || search != _showingSearch;
        _showingSearch = search;
        _list.ItemsSource = rows;
        if (reset && rows.Count > 0) _list.ScrollIntoView(0); // иначе остаётся прокрутка прошлого списка
    }

    /// <summary>Строка списка; переиспользуется при прокрутке, поэтому строит содержимое заново при смене данных.</summary>
    sealed class RowView : Border
    {
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            Child = DataContext is Row row ? Build(row) : null;
        }

        static Control Build(Row row)
        {
            bool term = Skin.IsTerminal;
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Margin = new Thickness(row.Depth * 14, 0, 0, 0),
            };
            switch (row.Kind)
            {
                case RowKind.Folder:
                    var chev = Ui.Icon(Ui.Chevron, Skin.Mute, 12);
                    if (row.Open) chev.RenderTransform = new RotateTransform(90);
                    panel.Children.Add(chev);
                    if (!term) panel.Children.Add(Ui.Icon(Ui.Folder, Skin.Mute, 15));
                    panel.Children.Add(Ui.Text(term ? row.Text + "/" : row.Text));
                    return panel;
                case RowKind.Note:
                    panel.Children.Add(new Control { Width = 12 });
                    if (!term) panel.Children.Add(Ui.Icon(Ui.File, Skin.Mute, 15));
                    panel.Children.Add(Ui.Text(term ? row.Text + ".md" : row.Text));
                    return panel;
                case RowKind.Section:
                    return new Border { Padding = new Thickness(0, 6, 0, 2), Child = Ui.Caps(row.Text) };
                case RowKind.Tag:
                    var count = Ui.Text(row.Count.ToString(), 12, Skin.Mute);
                    DockPanel.SetDock(count, Dock.Right);
                    return new DockPanel { Children = { count, Ui.Text(row.Text, 13, Skin.Accent) } };
                case RowKind.Empty:
                    return new TextBlock { Text = row.Text, Foreground = Skin.Mute, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                default:
                    var h = row.Hit;
                    return new StackPanel
                    {
                        Children =
                        {
                            Ui.Text(Loc.T("sb.hit", h.Note.Title, h.Line), 12.5, Skin.Fg, FontWeight.Bold),
                            Ui.Marked(h.Snippet, h.MatchStart, row.MatchLength, 12, Skin.Mute, Skin.Accent).With(t => t.MaxLines = 2),
                        },
                    };
            }
        }
    }
}

static class ControlExtensions
{
    public static T With<T>(this T control, Action<T> setup) { setup(control); return control; }
}
