using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Leaflet.Core;

namespace Leaflet;

/// <summary>Приложение без XAML: тема и стили редактора подключаются кодом.</summary>
public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Leaflet"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });

        // Цвета Fluent берём из темы «Листа»: кисти общие, смена темы перекрашивает и эти элементы.
        var r = Resources;
        r["ContentControlThemeFontFamily"] = Skin.Font;
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
            r[key] = Skin.Bg;
        r["TextControlBorderBrush"] = Skin.Line;
        r["TextControlBorderBrushPointerOver"] = Skin.Line;
        r["TextControlBorderBrushFocused"] = Skin.Accent;
        r["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            r[key] = Skin.Fg;
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" })
            r[key] = Skin.Mute;
        r["TextControlSelectionHighlightColor"] = Skin.Selection;
        r["ToolTipBackground"] = Skin.Panel2;
        r["ToolTipForeground"] = Skin.Fg;
        r["ToolTipBorderBrush"] = Skin.Line;

        Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 4)),
                new Setter(Layoutable.MinHeightProperty, 0d),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(5)),
            },
        });
        // Подсветка строк списков цветом темы вместо системного синего.
        Styles.Add(ItemBack(":pointerover", Skin.Hover));
        Styles.Add(ItemBack(":pressed", Skin.Sel));
        Styles.Add(ItemBack(":selected", Skin.Sel));
        Styles.Add(ItemBack(":selected", Skin.PickBack, "palette"));
    }

    static Style ItemBack(string pseudo, Avalonia.Media.IBrush brush, string? listClass = null)
    {
        return new Style(x =>
        {
            var item = listClass == null ? x.OfType<ListBoxItem>() : x.OfType<ListBox>().Class(listClass).Descendant().OfType<ListBoxItem>();
            return item.Class(pseudo).Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter");
        })
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, brush) },
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(Program.Args);
            desktop.MainWindow = window;
            if (SingleInstance.Applies(Program.Args))
                SingleInstance.Listen(paths => Avalonia.Threading.Dispatcher.UIThread.Post(() => window.OpenExternal(paths)));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
