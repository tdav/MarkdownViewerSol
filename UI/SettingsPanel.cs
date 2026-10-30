using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Leaflet.Core;

namespace Leaflet.UI;

/// <summary>Переключатель из нескольких вариантов (как «Правка / Чтение» в макете).</summary>
sealed class Segmented : Border
{
    readonly Border[] _items;
    public event Action<int>? Picked;
    public int Selected { get; private set; } = -1;

    public Segmented(params string[] labels)
    {
        BorderBrush = Skin.Line; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(6); ClipToBounds = true;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _items = labels.Select((text, i) =>
        {
            var b = new Border
            {
                Padding = new Thickness(12, 4), Cursor = Ui.Hand, Background = Brushes.Transparent,
                Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = Skin.Mute },
            };
            b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Pick(i); };
            row.Children.Add(b);
            return b;
        }).ToArray();
        Child = row;
    }

    public void Show(int index)
    {
        Selected = index;
        for (int i = 0; i < _items.Length; i++)
        {
            bool on = i == index;
            _items[i].Background = on ? Skin.Sel : Brushes.Transparent;
            ((TextBlock)_items[i].Child!).Foreground = on ? Skin.Fg : Skin.Mute;
        }
    }

    /// <summary>То же, что щелчок по варианту.</summary>
    public void Pick(int index) { Show(index); Picked?.Invoke(index); }
}

/// <summary>«−  значение  +» по списку допустимых значений; текущее значение ставится на ближайшее из списка.</summary>
sealed class Stepper : Border
{
    readonly double[] _values;
    readonly Func<double, string> _format;
    readonly Func<double> _get;
    readonly Action<double> _set;
    readonly TextBlock _text;

    public Stepper(double[] values, Func<double, string> format, Func<double> get, Action<double> set)
    {
        _values = values; _format = format; _get = get; _set = set;
        BorderBrush = Skin.Line; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(6);
        _text = new TextBlock
        {
            Width = 120, TextAlignment = TextAlignment.Center, FontSize = 13, Foreground = Skin.Fg, VerticalAlignment = VerticalAlignment.Center,
        };
        var minus = Ui.Button(Ui.Text("−", 16, Skin.Fg), () => Step(-1), radius: 5, padding: new Thickness(10, 0));
        var plus = Ui.Button(Ui.Text("+", 16, Skin.Fg), () => Step(1), radius: 5, padding: new Thickness(10, 0));
        var row = new DockPanel { Height = 28, Children = { minus, plus, _text } };
        DockPanel.SetDock(minus, Dock.Left);
        DockPanel.SetDock(plus, Dock.Right);
        Child = row;
    }

    public double Value => _get();
    public string Shown => _text.Text ?? "";

    public void Step(int direction)
    {
        double cur = _get(); // значение из файла может лежать между пунктами: шаг идёт к ближайшему пункту в нужную сторону
        int i = direction > 0 ? Array.FindIndex(_values, v => v > cur) : Array.FindLastIndex(_values, v => v < cur);
        if (i < 0) i = direction > 0 ? _values.Length - 1 : 0;
        _set(_values[i]);
        Refresh();
    }

    public void Refresh() => _text.Text = _format(_get());
}

/// <summary>
/// Окно настроек поверх основного окна (как палитра): тема, шрифт и размер текста, ширина текста, задержка автосохранения,
/// папка хранилища. Изменения применяются сразу и пишутся в settings.ini; закрывается Esc, щелчком мимо или «Готово».
/// </summary>
sealed class SettingsPanel : Panel
{
    public const double FullWidth = 1600;
    static readonly double[] Sizes = [10, 11, 12, 13, 14, 14.5, 15, 16, 17, 18, 19, 20, 22, 24, 26, 28, 32];
    static readonly double[] Widths = Enumerable.Range(0, 29).Select(i => 480.0 + 40 * i).ToArray(); // 480…1600; последний — «во всю ширину»
    static readonly double[] Delays = [0, 300, 500, 800, 1200, 2000, 3000, 5000, 10000];
    static readonly double[] Snapshots = [0, 1, 5, 10, 15, 30, 60];

    readonly Settings _settings;
    readonly CommandPalette _palette;
    readonly Border _card;
    TextBlock _fontName = null!, _vaultPath = null!, _attachName = null!;
    Control? _returnFocus;

    public Segmented ThemeChoice { get; private set; } = null!;
    public Segmented LangChoice { get; private set; } = null!;
    public Stepper SizeStepper { get; private set; } = null!;
    public Stepper WidthStepper { get; private set; } = null!;
    public Stepper AutosaveStepper { get; private set; } = null!;
    public Stepper HistoryStepper { get; private set; } = null!;
    public string VaultText => _vaultPath.Text ?? "";
    public string FontText => _fontName.Text ?? "";

    /// <summary>Настройки изменены: окно применяет их и сохраняет файл.</summary>
    public event Action? Changed;
    public event Action? PickVault;
    /// <summary>Выбран язык интерфейса («ru» / «en»): окно ставит его и перестраивает подписи.</summary>
    public event Action<string>? LanguagePicked;

    readonly Keymap _keys;
    ScrollViewer _body = null!;
    public void ScrollToEnd() => _body.ScrollToEnd();
    readonly Dictionary<string, (Border Button, TextBlock Text)> _keyRows = new();
    TextBlock _keyMessage = null!;
    string? _capturing;

    /// <summary>Идёт назначение: следующее сочетание достаётся выбранной команде (см. <see cref="Capture"/>).</summary>
    public bool Capturing => _capturing != null;
    public string KeyMessage => _keyMessage.Text ?? "";
    public string KeyText(string id) => _keyRows[id].Text.Text ?? "";

    public SettingsPanel(Settings settings, CommandPalette palette, Keymap keys)
    {
        _settings = settings;
        _palette = palette;
        _keys = keys;
        IsVisible = false;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
        PointerPressed += (_, e) => { if (e.Source == this) Close(); };

        _card = new Border
        {
            Width = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 44, 0, 0), Background = Skin.Panel, BorderBrush = Skin.Accent,
            BorderThickness = new Thickness(1), BoxShadow = BoxShadows.Parse("0 12 40 0 #99000000"),
        };
        Children.Add(_card);
        SizeChanged += (_, e) => _card.MaxHeight = Math.Max(240, e.NewSize.Height - 88);
        Build();
    }

    string? _vaultRoot;

    /// <summary>Содержимое окна; зовётся при создании и при смене языка (подписи ставятся при построении).</summary>
    void Build()
    {
        ThemeChoice = new Segmented(Loc.T("set.graphite"), Loc.T("set.terminal"), Loc.T("set.light"));
        ThemeChoice.Picked += i =>
        {
            _settings["theme"] = Skin.Name((ThemeKind)i);
            Apply();
        };

        LangChoice = new Segmented("Русский", "English");
        LangChoice.Picked += i => LanguagePicked?.Invoke(i == 0 ? Loc.Ru : Loc.En);

        _fontName = Ui.Text("", 13);
        var fontButton = Button(_fontName, ChooseFont);
        SizeStepper = new Stepper(Sizes, v => v % 1 == 0 ? $"{v:0} px" : Loc.Dec(v.ToString("0.0", CultureInfo.InvariantCulture)) + " px",
            () => Skin.EditorSize,
            v => { _settings.FontSize = v == Skin.ThemeSize ? 0 : v; Apply(); });
        WidthStepper = new Stepper(Widths, v => v >= FullWidth ? Loc.T("set.fullwidth") : $"{v:0} px",
            () => _settings.TextWidth is var w and > 0 ? w : FullWidth,
            v => { _settings.TextWidth = v >= FullWidth ? 0 : (int)v; Apply(); });
        AutosaveStepper = new Stepper(Delays, v => v <= 0 ? Loc.T("set.off") : Loc.Dec((v / 1000).ToString("0.#", CultureInfo.InvariantCulture)) + Loc.T("set.sec"),
            () => _settings.AutosaveMs,
            v => { _settings.AutosaveMs = (int)v; Apply(); });
        HistoryStepper = new Stepper(Snapshots, v => v <= 0 ? Loc.T("set.onclose") : Loc.T("set.everymin", v),
            () => _settings.HistoryMinutes,
            v => { _settings.HistoryMinutes = (int)v; Apply(); });

        _vaultPath = Ui.Text("", 13);
        _vaultPath.TextTrimming = TextTrimming.PathSegmentEllipsis;
        var pick = Button(Ui.Text(Loc.T("common.choose"), 13), () => PickVault?.Invoke());
        DockPanel.SetDock(pick, Dock.Right);
        pick.Margin = new Thickness(10, 0, 0, 0);
        var vaultRow = new DockPanel { Children = { pick, _vaultPath } };
        _attachName = Ui.Text("", 13);
        var attachButton = Button(_attachName, ChooseAttach);

        var reset = Button(Ui.Text(Loc.T("set.reset"), 12.5, Skin.Mute), Reset);
        var done = Button(Ui.Text(Loc.T("common.done"), 13, Skin.AccentFg, FontWeight.Bold), Close, accent: true);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        var footer = new DockPanel { Margin = new Thickness(20, 12), Children = { done, reset } };
        DockPanel.SetDock(done, Dock.Right);

        var close = Ui.Button(Ui.Icon(Ui.Close, Skin.Mute, 12), Close, Loc.T("common.close.esc"), 4, new Thickness(6));
        var header = new DockPanel
        {
            Margin = new Thickness(20, 12, 12, 8),
            Children = { close, Ui.Text(Loc.T("rail.settings"), 16, Skin.Heading, FontWeight.Bold) },
        };
        DockPanel.SetDock(close, Dock.Right);

        _keyMessage = new TextBlock { FontSize = 11.5, Foreground = Skin.Mute, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var keyList = new StackPanel { Spacing = 6 };
        foreach (var action in Keymap.Actions)
        {
            var label = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            var button = Button(label, () => BeginCapture(action.Id));
            button.Padding = new Thickness(10, 3);
            button.MinWidth = 150;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            _keyRows[action.Id] = (button, label);
            DockPanel.SetDock(button, Dock.Right);
            keyList.Children.Add(new DockPanel { Children = { button, Ui.Text(action.Title, 13) } });
        }
        var resetKeys = Button(Ui.Text(Loc.T("set.resetkeys"), 12.5, Skin.Mute), () => { Cancel(); _keys.ResetAll(); Apply(); });
        resetKeys.HorizontalAlignment = HorizontalAlignment.Left;

        var rows = new StackPanel
        {
            Margin = new Thickness(20, 4, 20, 8), Spacing = 14,
            Children =
            {
                Group(Loc.T("set.g.view")),
                Row(Loc.T("set.lang"), Loc.T("set.lang.hint"), LangChoice),
                Row(Loc.T("set.theme"), Loc.T("set.theme.hint"), ThemeChoice),
                Row(Loc.T("set.font"), Loc.T("set.font.hint"), fontButton),
                Row(Loc.T("set.size"), Loc.T("set.size.hint"), SizeStepper),
                Row(Loc.T("set.width"), Loc.T("set.width.hint"), WidthStepper),
                Group(Loc.T("set.g.work")),
                Row(Loc.T("set.autosave"), Loc.T("set.autosave.hint"), AutosaveStepper),
                Row(Loc.T("set.snap"), Loc.T("set.snap.hint"), HistoryStepper),
                Group(Loc.T("act.keys")),
                new TextBlock { Text = Loc.T("set.keys.hint"), FontSize = 11.5, Foreground = Skin.Mute, TextWrapping = TextWrapping.Wrap },
                keyList,
                _keyMessage,
                resetKeys,
                Group(Loc.T("set.g.vault")),
                new StackPanel { Spacing = 4, Children = { Ui.Text(Loc.T("pick.folder.title"), 13.5), vaultRow } },
                Row(Loc.T("set.attach"), Loc.T("set.attach.hint"), attachButton),
            },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var body = _body = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(footer);

        _card.Child = grid;
    }

    /// <summary>Язык сменился: окно строится заново (прокрутка и открытая папка сохраняются).</summary>
    public void Relocalize()
    {
        double offset = _body.Offset.Y;
        Build();
        _vaultPath.Text = _vaultRoot ?? Loc.T("set.notchosen");
        if (_vaultRoot != null) ToolTip.SetTip(_vaultPath, _vaultRoot);
        Refresh();
        _body.Offset = new Vector(0, offset);
    }

    public bool IsOpen => IsVisible;

    public void Open(string? vaultRoot)
    {
        _returnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        _vaultRoot = vaultRoot;
        _vaultPath.Text = vaultRoot ?? Loc.T("set.notchosen");
        Refresh();
        IsVisible = true;
        Focus();
    }

    /// <summary>Ждёт сочетание для команды; кнопка показывает приглашение.</summary>
    public void BeginCapture(string id)
    {
        _capturing = id;
        _keyMessage.IsVisible = false;
        Refresh();
        Focus(); // фокус не должен остаться в поле, где клавиши печатают текст
    }

    void Cancel()
    {
        if (_capturing == null) return;
        _capturing = null;
        Refresh();
    }

    /// <summary>Нажатие клавиш во время назначения: Esc — отмена, Backspace/Delete — снять, иначе — новое сочетание (или причина отказа).</summary>
    public void Capture(KeyEventArgs e)
    {
        if (_capturing is not { } id) return;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        string? problem = null;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { Cancel(); return; }
        if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None) problem = _keys.Set(id, null);
        else if (Gesture.From(e) is { } g) problem = _keys.Set(id, g);
        else problem = Loc.T("key.bad");
        if (problem != null)
        {
            _keyMessage.Text = problem;
            _keyMessage.Foreground = Skin.Accent;
            _keyMessage.IsVisible = true;
            return;
        }
        _capturing = null;
        _keyMessage.IsVisible = false;
        Apply();
    }

    public void Close()
    {
        if (!IsVisible) return;
        Cancel();
        IsVisible = false;
        _returnFocus?.Focus();
    }

    public void SetVault(string root)
    {
        _vaultRoot = root;
        _vaultPath.Text = root;
        ToolTip.SetTip(_vaultPath, root);
    }

    /// <summary>Показывает текущие значения (после смены темы, сброса, применения настроек).</summary>
    public void Refresh()
    {
        _card.CornerRadius = new CornerRadius(Skin.IsTerminal ? 0 : 8);
        ThemeChoice.Show((int)Skin.Kind);
        LangChoice.Show(Loc.Lang == Loc.Ru ? 0 : 1);
        _fontName.Text = _settings.Font ?? Loc.T("set.themefont", Skin.Font.Name);
        _attachName.Text = _settings.AttachFolder;
        SizeStepper.Refresh();
        WidthStepper.Refresh();
        AutosaveStepper.Refresh();
        HistoryStepper.Refresh();
        foreach (var (id, (button, text)) in _keyRows)
        {
            bool on = id == _capturing;
            string hint = _keys.Hint(id);
            text.Text = on ? Loc.T("key.press") : hint.Length > 0 ? hint : Loc.T("cmd.unassigned");
            text.Foreground = on ? Skin.Accent : hint.Length > 0 ? Skin.Fg : Skin.Mute;
            button.BorderBrush = on ? Skin.Accent : Skin.Line;
        }
    }

    public void SetFont(string? family)
    {
        _settings.Font = family;
        Apply();
    }

    public void SetTextWidth(int px)
    {
        _settings.TextWidth = px;
        Apply();
    }

    public void SetAutosave(int ms)
    {
        _settings.AutosaveMs = ms;
        Apply();
    }

    public void Reset()
    {
        _settings.ResetPrefs();
        Apply();
    }

    void Apply()
    {
        Changed?.Invoke();
        Refresh();
    }

    void ChooseFont()
    {
        var names = FontManager.Current.SystemFonts.Select(f => f.Name).Where(n => !n.StartsWith('@'))
            .Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        _palette.Open("Aa", Loc.T("set.fontph"), q =>
        {
            q = q.Trim();
            var items = new List<PaletteItem>();
            if (q.Length == 0) items.Add(new PaletteItem(Loc.T("set.themedefault"), null, () => SetFont(null), Skin.Font.Name));
            items.AddRange(names.Select(n => (n, s: CommandPalette.Score(n, q))).Where(x => x.s >= 0).OrderBy(x => x.s)
                .Select(x => new PaletteItem(x.n, x.n == _settings.Font ? "✓" : null, () => SetFont(x.n))));
            return items;
        });
    }

    /// <summary>Имя папки вложений: просто имя — рядом с каждой заметкой, «/имя» — одна папка в корне хранилища.</summary>
    public void ChooseAttach() => _palette.Open("▣", Loc.T("set.attachph"), ChooseAttachItems, _settings.AttachFolder);

    public List<PaletteItem> ChooseAttachItems(string q)
    {
        {
            string name = q.Trim().Replace('\\', '/').TrimEnd('/');
            if (name.Trim('/').Length == 0) name = Settings.DefaultAttach;
            if (name.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || name.Split('/').Contains(".."))
                return [new PaletteItem(Loc.T("set.attach.bad"), null, () => { })];
            string bare = name.TrimStart('/');
            var items = new List<PaletteItem>
            {
                new(name.StartsWith('/') ? Loc.T("set.attach.root", bare) : Loc.T("set.attach.each", bare), "Enter", () => SetAttach(name)),
            };
            items.Add(name.StartsWith('/')
                ? new PaletteItem(Loc.T("set.attach.each", bare), null, () => SetAttach(bare))
                : new PaletteItem(Loc.T("set.attach.root", bare), null, () => SetAttach("/" + bare)));
            return items;
        }
    }

    void SetAttach(string name)
    {
        _settings.AttachFolder = name;
        Apply();
    }

    // ---------- строительные блоки ----------

    static Control Group(string title)
    {
        var caps = Ui.Caps(title);
        caps.Margin = new Thickness(0, 6, 0, -4);
        return caps;
    }

    static Control Row(string title, string hint, Control control)
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(control, Dock.Right);
        control.Margin = new Thickness(12, 0, 0, 0);
        var text = new StackPanel
        {
            Spacing = 1, VerticalAlignment = VerticalAlignment.Center,
            Children = { Ui.Text(title, 13.5), new TextBlock { Text = hint, FontSize = 11.5, Foreground = Skin.Mute, TextWrapping = TextWrapping.Wrap } },
        };
        return new DockPanel { Children = { control, text } };
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
            if (e.InitialPressMouseButton == MouseButton.Left && b.IsPointerOver) { e.Handled = true; click(); }
        };
        return b;
    }
}
