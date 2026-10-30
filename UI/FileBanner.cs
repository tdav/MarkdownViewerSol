using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Leaflet.UI;

/// <summary>Полоса над текстом: файл открытой заметки изменён, удалён или переименован снаружи — что случилось и что можно сделать.</summary>
sealed class FileBanner : Border
{
    readonly TextBlock _text;
    readonly StackPanel _buttons;
    readonly Dictionary<string, Action> _actions = new();

    public string Message => _text.Text ?? "";
    public IReadOnlyCollection<string> ActionTitles => _actions.Keys;

    public FileBanner()
    {
        IsVisible = false;
        Background = Skin.AccentBack;
        BorderBrush = Skin.Line;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(16, 7);
        _text = new TextBlock { FontSize = 13, Foreground = Skin.Fg, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_buttons, Dock.Right);
        Child = new DockPanel { Children = { _buttons, _text } };
    }

    /// <summary>Показывает сообщение; первая кнопка — основная.</summary>
    public void Show(string message, params (string Title, Action Run)[] actions)
    {
        _text.Text = message;
        _actions.Clear();
        _buttons.Children.Clear();
        for (int i = 0; i < actions.Length; i++)
        {
            var (title, run) = actions[i];
            _actions[title] = run;
            _buttons.Children.Add(Button(title, run, i == 0));
        }
        IsVisible = true;
    }

    public void Hide() => IsVisible = false;

    /// <summary>Нажать кнопку по подписи (самопроверка).</summary>
    public bool Press(string title)
    {
        if (!IsVisible || !_actions.TryGetValue(title, out var run)) return false;
        run();
        return true;
    }

    static Border Button(string title, Action click, bool accent)
    {
        var b = new Border
        {
            Child = Ui.Text(title, 12.5, accent ? Skin.AccentFg : Skin.Fg, accent ? FontWeight.Bold : FontWeight.Normal),
            Cursor = Ui.Hand, CornerRadius = new CornerRadius(Skin.IsTerminal ? 0 : 6), Padding = new Thickness(10, 4),
            Background = accent ? Skin.Accent : Skin.Panel, BorderBrush = accent ? Skin.Accent : Skin.Line, BorderThickness = new Thickness(1),
        };
        if (!accent)
        {
            b.PointerEntered += (_, _) => b.Background = Skin.Sel;
            b.PointerExited += (_, _) => b.Background = Skin.Panel;
        }
        b.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && b.IsPointerOver) { e.Handled = true; click(); }
        };
        return b;
    }
}
