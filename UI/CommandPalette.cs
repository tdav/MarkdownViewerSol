using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Leaflet.UI;

sealed record PaletteItem(string Title, string? Hint, Action Run, string? Detail = null);

/// <summary>
/// Палитра из макета «Терминал»: команды (Ctrl+P), быстрый переход к заметке (Ctrl+O), имя новой заметки (Ctrl+N).
/// Лежит поверх окна; щелчок мимо или Esc закрывает.
/// </summary>
sealed class CommandPalette : Panel
{
    readonly Border _box;
    readonly TextBlock _prompt;
    readonly TextBox _input;
    readonly ListBox _list;
    Func<string, List<PaletteItem>> _source = _ => [];
    Control? _returnFocus;

    public CommandPalette()
    {
        IsVisible = false;
        Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
        PointerPressed += (_, e) => { if (e.Source == this) Close(); };

        _prompt = new TextBlock { FontWeight = FontWeight.Bold, Foreground = Skin.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        _input = new TextBox
        {
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 14,
            Padding = new Thickness(6, 9), MinHeight = 0,
        };
        _input.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        _input.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        _input.Resources["TextControlBorderBrushFocused"] = Brushes.Transparent;
        _input.Resources["TextControlBorderBrushPointerOver"] = Brushes.Transparent;
        _input.TextChanged += (_, _) => Fill();
        _input.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);

        _list = new ListBox
        {
            Background = Brushes.Transparent, MaxHeight = 360, Classes = { "palette" },
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PaletteItem>((_, _) => new ItemView(), supportsRecycling: true),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ItemView>(true)?.DataContext is PaletteItem item) Run(item);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        var inputRow = new DockPanel { Children = { _prompt, _input } };
        DockPanel.SetDock(_prompt, Dock.Left);
        _box = new Border
        {
            Width = 480, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 44, 0, 0), Background = Skin.Panel, BorderBrush = Skin.Accent,
            BorderThickness = new Thickness(1), BoxShadow = BoxShadows.Parse("0 12 40 0 #99000000"),
            Child = new StackPanel
            {
                Children = { inputRow, new Border { Height = 1, Background = Skin.Line }, _list },
            },
        };
        Children.Add(_box);
    }

    public bool IsOpen => IsVisible;
    public IReadOnlyList<PaletteItem> Items => _list.ItemsSource as List<PaletteItem> ?? [];

    /// <summary>Как Enter: выполнить выделенный пункт (самопроверка).</summary>
    public void RunSelected()
    {
        if (IsVisible && _list.SelectedItem is PaletteItem item) Run(item);
    }

    public void Open(string prompt, string placeholder, Func<string, List<PaletteItem>> source, string text = "")
    {
        _returnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        _box.CornerRadius = new CornerRadius(Skin.IsTerminal ? 0 : 8);
        _prompt.Text = prompt;
        _input.PlaceholderText = placeholder;
        _source = source;
        IsVisible = true;
        _input.Text = text;
        Fill();
        _input.Focus();
        _input.CaretIndex = text.Length;
    }

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        _returnFocus?.Focus();
    }

    void Fill()
    {
        var items = _source(_input.Text ?? "");
        _list.ItemsSource = items;
        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    void Run(PaletteItem item)
    {
        Close();
        item.Run();
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        int count = _list.ItemCount;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Down when count > 0: _list.SelectedIndex = (_list.SelectedIndex + 1) % count; _list.ScrollIntoView(_list.SelectedIndex); break;
            case Key.Up when count > 0: _list.SelectedIndex = (_list.SelectedIndex - 1 + count) % count; _list.ScrollIntoView(_list.SelectedIndex); break;
            case Key.Enter: if (_list.SelectedItem is PaletteItem item) Run(item); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Совпадение по подстроке или по буквам по порядку; меньше — лучше, -1 — нет.</summary>
    public static int Score(string text, string query)
    {
        if (query.Length == 0) return 0;
        int i = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return i == 0 ? 0 : 1 + i;
        int t = 0, gaps = 0;
        foreach (char q in query)
        {
            int found = -1;
            for (; t < text.Length; t++)
                if (char.ToLowerInvariant(text[t]) == char.ToLowerInvariant(q)) { found = t++; break; }
                else gaps++;
            if (found < 0) return -1;
        }
        return 100 + gaps;
    }

    sealed class ItemView : Border
    {
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is not PaletteItem item) { Child = null; return; }
            var row = new DockPanel();
            if (item.Hint != null)
            {
                var kbd = Ui.Kbd(item.Hint);
                DockPanel.SetDock(kbd, Dock.Right);
                row.Children.Add(kbd);
            }
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(Ui.Text(item.Title, 13.5));
            if (item.Detail != null) title.Children.Add(Ui.Text(item.Detail, 12, Skin.Mute));
            row.Children.Add(title);
            Child = row;
        }
    }
}
