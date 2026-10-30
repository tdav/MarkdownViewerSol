using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Leaflet.UI;
using Leaflet.Core;

namespace Leaflet;

/// <summary>Вёрстка окна по макетам «Графит» и «Терминал»: вкладки, лента, панели, строка состояния.</summary>
public sealed partial class MainWindow
{
    StackPanel _tabStrip = null!;
    Border _topBar = null!, _railFiles = null!, _railSettings = null!, _railRight = null!, _railRead = null!, _status = null!;
    Sidebar _side = null!;
    FindPanel _find = null!;
    RightPanel _right = null!;
    CommandPalette _palette = null!;
    SettingsPanel _settingsPanel = null!;
    FileBanner _banner = null!;
    HistoryPanel _historyPanel = null!;
    TextBlock _crumb = null!, _stMode = null!, _stFile = null!, _stStats = null!, _stPos = null!, _stSaved = null!, _stEnc = null!;
    TextBlock _helpText = null!, _emptyText = null!, _welcomeKeys = null!, _welcomeOpen = null!, _welcomeHelp = null!, _welcomeBlurb = null!;
    Control _helpIcon = null!;
    Border _segEdit = null!, _segRead = null!, _empty = null!, _welcome = null!;

    Control BuildLayout()
    {
        // --- верх: вкладки в заголовке окна ---
        _tabStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Bottom };
        var plus = Ui.Button(Ui.Icon(Ui.Plus, Skin.Mute, 13), () => PromptNewNote(), padding: new Thickness(7));
        Hinted(plus, "act.new", "new");
        plus.VerticalAlignment = VerticalAlignment.Center;
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Children = { _tabStrip, plus } };
        var tabScroll = new ScrollViewer
        {
            Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var caption = new StackPanel
        {
            Orientation = Orientation.Horizontal, [DockPanel.DockProperty] = Dock.Right,
            Children =
            {
                CaptionButton(Ui.Minimize, "win.min", () => WindowState = WindowState.Minimized, false),
                CaptionButton(Ui.Maximize, "win.max", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized, false),
                CaptionButton(Ui.Close, "common.close", Close, true),
            },
        };
        var top = new DockPanel { Children = { caption, tabScroll } };
        _topBar = new Border
        {
            Height = 36, Background = Skin.Panel2, BorderBrush = Skin.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 0, 0, 0), Child = top,
        };
        _topBar.PointerPressed += (_, e) =>
        {
            if (e.Source is Control c && (c == _topBar || c == top || c == tabs || c == _tabStrip || c is Avalonia.Controls.Presenters.ScrollContentPresenter)
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else BeginMoveDrag(e);
            }
        };

        // --- лента значков ---
        _railFiles = RailButton(Ui.File, "rail.notes", () => ToggleSidebar());
        _railRead = RailButton(Ui.Eye, "rail.read", () => SetReadMode(!Preview.ReadMode), "read");
        _railRight = RailButton(Ui.Panel, "rail.right", () => ToggleRight());
        var rail = new DockPanel { Margin = new Thickness(0, 8) };
        var railTop = new StackPanel
        {
            Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                _railFiles,
                RailButton(Ui.Search, "cmd.search", () => FocusSearch(), "search"),
                RailButton(Ui.Link, "act.open", () => QuickOpen(), "open"),
                RailButton(Ui.Command, "rail.palette", () => ShowCommands(), "palette"),
            },
        };
        _railSettings = RailButton(Ui.Sliders, "rail.settings", () => OpenSettings(), "settings");
        var railBottom = new StackPanel
        {
            Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, [DockPanel.DockProperty] = Dock.Bottom,
            Children =
            {
                _railRead, _railRight,
                RailButton(Ui.Contrast, "rail.theme", () => SetTheme(Skin.Next(Skin.Kind))),
                _railSettings,
                RailButton(Ui.Question, "cmd.manual", () => OpenManual()),
                RailButton(Ui.Heart, "cmd.help", () => OpenHelp()),
            },
        };
        rail.Children.Add(railBottom);
        rail.Children.Add(railTop);
        var railBorder = new Border
        {
            Width = 44, Background = Skin.Panel2, BorderBrush = Skin.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = rail,
        };

        // --- боковые панели ---
        _side = new Sidebar();
        _side.OpenNote += (n, newTab) => Open(n, newTab);
        _side.OpenHit += OpenSearchHit;
        _side.NewNote += () => PromptNewNote();
        _side.OpenFolder += () => _ = PickFolder();
        _side.RenameNote += PromptRename;
        _side.MoveNote += PromptMove;
        _side.DeleteNote += PromptDelete;
        _side.ExportNote += (note, pdf) => _ = PromptExport(note, pdf);
        _right = new RightPanel();
        _right.OpenBacklink += b => { Open(b.From); SelectAt(b.Offset, 0); };
        _right.GoToLine += GoToLine;
        _right.SearchTag += tag => { ShowSidebar(true); _side.FocusSearch(); SetSearch(tag); };

        // --- середина: путь, правка/чтение, редактор ---
        _crumb = Ui.Text("", 13, Skin.Mute);
        _segEdit = Segment(Loc.T("seg.edit"), () => SetReadMode(false));
        _segRead = Segment(Loc.T("seg.read"), () => SetReadMode(true));
        var seg = new Border
        {
            BorderBrush = Skin.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _segEdit, _segRead } },
        };
        var crumb = new Border
        {
            Height = 34, BorderBrush = Skin.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 0),
            Child = new DockPanel { Children = { seg, _crumb } },
        };
        _empty = new Border
        {
            IsVisible = false, Background = Skin.Bg,
            Child = _emptyText = new TextBlock
            {
                Text = EmptyHint(),
                Foreground = Skin.Mute, FontSize = 14, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var editorHost = new Grid { Children = { Editor, _empty, _complete.View } };
        _find = new FindPanel(Editor, _settings);
        Hinted(_find.PrevButton, "find.prev", "findprev");
        Hinted(_find.NextButton, "find.next", "findnext");
        _banner = new FileBanner();
        var main = new DockPanel { Background = Skin.Bg, Children = { crumb, _banner, _find, editorHost } };
        DockPanel.SetDock(crumb, Dock.Top);
        DockPanel.SetDock(_banner, Dock.Top);
        DockPanel.SetDock(_find, Dock.Top);
        _welcome = BuildWelcome();
        var mainHost = new Grid { Children = { main, _welcome } };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        Grid.SetColumn(_side, 1);
        Grid.SetColumn(mainHost, 2);
        Grid.SetColumn(_right, 3);
        body.Children.Add(railBorder);
        body.Children.Add(_side);
        body.Children.Add(mainHost);
        body.Children.Add(_right);

        // --- строка состояния ---
        _stMode = StatusText(FontWeight.Bold);
        _stFile = StatusText();
        _stStats = StatusText();
        _stPos = StatusText();
        _stSaved = StatusText();
        _stEnc = StatusText();
        _stEnc.Text = "UTF-8";
        _helpIcon = Ui.Icon(Ui.Heart, Skin.HelpFg, 12);
        _helpText = new TextBlock { Text = Loc.T("cmd.help"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        var help = new Border
        {
            Background = Skin.HelpBack, BorderBrush = Skin.HelpBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 0), Margin = new Thickness(14, 3, 0, 3), Cursor = Ui.Hand,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { _helpIcon, _helpText } },
            [DockPanel.DockProperty] = Dock.Right,
        };
        ToolTip.SetTip(help, Program.HelpUrl);
        help.PointerReleased += (_, _) => OpenHelp();
        help.PointerEntered += (_, _) => _helpText.TextDecorations = TextDecorations.Underline;
        help.PointerExited += (_, _) => _helpText.TextDecorations = Skin.IsTerminal ? TextDecorations.Underline : null;
        var status = new DockPanel { Margin = new Thickness(12, 0) };
        foreach (var right in new Control[] { help, _stEnc, _stSaved, _stPos })
        {
            DockPanel.SetDock(right, Dock.Right);
            status.Children.Add(right);
        }
        status.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { _stMode, _stFile, _stStats } });
        _status = new Border { Height = 26, Background = Skin.StatusBack, BorderBrush = Skin.Line, Child = status };

        _palette = new CommandPalette();
        _settingsPanel = new SettingsPanel(_settings, _palette, _keys);
        _settingsPanel.Changed += () => ApplyPrefs();
        _settingsPanel.LanguagePicked += SetLanguage;
        _settingsPanel.PickVault += () => _ = PickFolder();
        Editor.SizeChanged += (_, _) => ApplyTextWidth();
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Background = Skin.Bg };
        Grid.SetRow(body, 1);
        Grid.SetRow(_status, 2);
        Grid.SetRowSpan(_settingsPanel, 3);
        Grid.SetRowSpan(_palette, 3);
        _historyPanel = new HistoryPanel();
        Grid.SetRowSpan(_historyPanel, 3);
        root.Children.Add(_topBar);
        root.Children.Add(body);
        root.Children.Add(_status);
        root.Children.Add(_settingsPanel);
        root.Children.Add(_historyPanel);
        root.Children.Add(_palette);
        return root;
    }

    Border CaptionButton(string icon, string tipId, Action click, bool danger)
    {
        var glyph = Ui.Icon(icon, Skin.Mute, 12);
        var b = new Border { Width = 46, Height = 36, Background = Brushes.Transparent, Child = glyph };
        var hover = danger ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)) : (IBrush)Skin.Sel;
        b.PointerEntered += (_, _) => { b.Background = hover; Ui.SetIconBrush(glyph, danger ? Brushes.White : Skin.Fg); };
        b.PointerExited += (_, _) => { b.Background = Brushes.Transparent; Ui.SetIconBrush(glyph, Skin.Mute); };
        b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && b.IsPointerOver) click(); };
        ToolTip.SetTip(b, Loc.T(tipId));
        _tipped.Add((b, tipId));
        return b;
    }

    TextBlock StatusText(FontWeight weight = FontWeight.Normal) => new()
    {
        FontSize = 11.5, Foreground = Skin.StatusFg, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(14, 0, 0, 0),
    };

    Border RailButton(string icon, string tipId, Action click, string? keyId = null)
    {
        var b = Ui.Button(Ui.Icon(icon, Skin.Mute, 16), click, Loc.T(tipId), 6, new Thickness(7));
        ToolTip.SetPlacement(b, PlacementMode.Right);
        if (keyId != null) Hinted(b, tipId, keyId);
        else _tipped.Add((b, tipId));
        return b;
    }

    readonly List<(Control Control, string TitleId, string KeyId)> _hinted = [];
    readonly List<(Control Control, string TitleId)> _tipped = []; // подсказки без сочетаний: при смене языка берутся заново

    /// <summary>Подсказка «Название (сочетание)»; при смене сочетаний или языка обновляется в <see cref="RefreshHints"/>.</summary>
    void Hinted(Control control, string titleId, string keyId)
    {
        _hinted.Add((control, titleId, keyId));
        ToolTip.SetTip(control, _keys.Tip(Loc.T(titleId), keyId));
    }

    /// <summary>Сочетания изменились: подсказки, тексты с клавишами, вкладки и список заметок.</summary>
    void RefreshHints()
    {
        foreach (var (control, titleId, keyId) in _hinted) ToolTip.SetTip(control, _keys.Tip(Loc.T(titleId), keyId));
        _emptyText.Text = EmptyHint();
        _welcomeKeys.Text = WelcomeKeysHint();
        RebuildTabs();
        _side.RefreshHints();
    }

    string EmptyHint() =>
        Loc.T("empty.open") + "\n" + string.Join(", ", new[] { K("open") is { } o ? Loc.T("empty.find", o) : null, K("new") is { } n ? Loc.T("empty.new", n) : null }.OfType<string>());

    string WelcomeKeysHint() =>
        string.Join(", ", new[] { K("folder") is { } f ? Loc.T("welcome.folder", f) : null, K("palette") is { } p ? Loc.T("welcome.cmds", p) : null }.OfType<string>());

    static void SetRailOn(Border button, bool on)
    {
        button.Tag = on;
        if (button.Child != null) Ui.SetIconBrush(button.Child, on ? Skin.Fg : Skin.Mute);
    }

    static Border Segment(string text, Action click)
    {
        var b = new Border
        {
            Padding = new Thickness(9, 2), Cursor = Ui.Hand, Background = Brushes.Transparent,
            Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = Skin.Mute },
        };
        b.PointerReleased += (_, _) => click();
        return b;
    }

    static void SetSegment(Border seg, bool on)
    {
        seg.Background = on ? Skin.Sel : Brushes.Transparent;
        if (seg.Child is TextBlock t) t.Foreground = on ? Skin.Fg : Skin.Mute;
    }

    Border BuildWelcome()
    {
        var open = new Border
        {
            Background = Skin.Accent, CornerRadius = new CornerRadius(6), Padding = new Thickness(18, 9), Cursor = Ui.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _welcomeOpen = new TextBlock { Text = Loc.T("welcome.open"), FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Skin.AccentFg },
        };
        open.PointerReleased += (_, _) => _ = PickFolder();
        _welcomeKeys = new TextBlock { Text = WelcomeKeysHint(), Foreground = Skin.Mute, FontSize = 12 };
        var help = _welcomeHelp = new TextBlock { Text = Loc.T("welcome.help"), Foreground = Skin.Accent, FontSize = 13, Cursor = Ui.Hand };
        help.PointerReleased += (_, _) => OpenHelp();
        _welcomeBlurb = new TextBlock
        {
            Text = Loc.T("welcome.blurb"),
            Foreground = Skin.Mute, FontSize = 14, TextWrapping = TextWrapping.Wrap,
        };
        return new Border
        {
            IsVisible = false, Background = Skin.Bg,
            Child = new StackPanel
            {
                Spacing = 14, Width = 420, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Leaflet", FontSize = 44, FontWeight = FontWeight.Black, Foreground = Skin.Heading },
                    _welcomeBlurb,
                    open,
                    _welcomeKeys,
                    help,
                },
            },
        };
    }

    /// <summary>Язык сменился: подсказки, подписи ленты, панелей, строки состояния и приветствия.</summary>
    void OnLanguageChanged()
    {
        foreach (var (control, titleId) in _tipped) ToolTip.SetTip(control, Loc.T(titleId));
        ((TextBlock)_segEdit.Child!).Text = Loc.T("seg.edit");
        ((TextBlock)_segRead.Child!).Text = Loc.T("seg.read");
        _helpText.Text = Loc.T("cmd.help");
        _welcomeOpen.Text = Loc.T("welcome.open");
        _welcomeHelp.Text = Loc.T("welcome.help");
        _welcomeBlurb.Text = Loc.T("welcome.blurb");
        _right.Relocalize();
        _find.Relocalize();
        _historyPanel.Relocalize();
        _settingsPanel.Relocalize();
        RefreshHints();
        UpdateNoteInfo(full: true);
        UpdateStatus();
        UpdateBanner();
        Editor.TextArea.TextView.Redraw();
    }

    /// <summary>Ставит язык интерфейса (запоминает в settings.ini); все подписи меняются сразу, без перезапуска.</summary>
    void SetLanguage(string lang)
    {
        if (!Loc.Set(lang)) return;
        _settings.Lang = lang;
        _settings.Save();
    }

    /// <summary>Перестраивает то, что зависит от темы, но не следует за кистями сам.</summary>
    void ApplyThemeLayout()
    {
        bool term = Skin.IsTerminal;
        // Шрифт наследуется от окна; шаблоны Fluent (поля ввода, кнопки) берут его из ресурса.
        FontFamily = Skin.Font;
        Editor.FontFamily = Skin.EditorFont;
        Editor.FontSize = Skin.EditorSize;
        if (Application.Current != null) Application.Current.Resources["ContentControlThemeFontFamily"] = Skin.Font;
        Preview.ShowHeadingMarks = term;
        _complete.ApplyTheme();
        Editor.TextArea.TextView.Redraw();
        _status.BorderThickness = new Thickness(0, term ? 0 : 1, 0, 0);
        _stMode.IsVisible = _stFile.IsVisible = _stPos.IsVisible = term;
        _stSaved.IsVisible = !term;
        Ui.SetIconBrush(_helpIcon, Skin.HelpFg);
        _helpText.Foreground = Skin.HelpFg;
        _helpText.FontWeight = term ? FontWeight.Bold : FontWeight.Normal;
        _helpText.TextDecorations = term ? TextDecorations.Underline : null;
        RebuildTabs();
        _side.Rebuild();
        UpdateNoteInfo(full: true);
        UpdateStatus();
    }

    /// <summary>Колонка текста заданной ширины стоит по центру: лишнее место уходит в боковые поля редактора.</summary>
    void ApplyTextWidth()
    {
        const double side = 36;
        int width = _settings.TextWidth;
        double pad = width > 0 && Editor.Bounds.Width > width + 2 * side ? Math.Floor((Editor.Bounds.Width - width) / 2) : side;
        if (Math.Abs(Editor.Padding.Left - pad) > 0.5) Editor.Padding = new Thickness(pad, 22);
    }

    void RebuildTabs()
    {
        _tabStrip.Children.Clear();
        bool term = Skin.IsTerminal;
        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            bool on = tab == _tab;
            var label = new TextBlock
            {
                Text = term ? $"{i + 1} {tab.Note.Title}.md" : tab.Note.Title, FontSize = 12.5,
                Foreground = on ? (term ? Skin.Accent : Skin.Fg) : Skin.Mute, VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (!term) row.Children.Add(Ui.Icon(Ui.File, on ? Skin.Accent : Skin.Mute, 13));
            row.Children.Add(label);
            var t = tab;
            var close = Ui.Button(Ui.Icon(Ui.Close, Skin.Mute, 10), () => CloseTab(t), _keys.Tip(Loc.T("common.close"), "close"), 4, new Thickness(3));
            close.Opacity = on ? 1 : 0.55;
            row.Children.Add(close);
            var view = new Border
            {
                Height = term ? 35 : 30, Padding = new Thickness(term ? 14 : 10, 0, 6, 0), Child = row, Cursor = Ui.Hand,
                Background = on ? Skin.Bg : Brushes.Transparent,
                CornerRadius = term ? default : new CornerRadius(6, 6, 0, 0),
                BorderBrush = term ? (on ? Skin.Accent : Skin.Line) : null,
                BorderThickness = term ? new Thickness(0, 0, 0, on ? 2 : 0) : default,
            };
            ToolTip.SetTip(view, tab.Note.Rel);
            view.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Middle) CloseTab(t);
                else if (e.InitialPressMouseButton == MouseButton.Left) Activate(t);
            };
            if (term) _tabStrip.Children.Add(new Border { BorderBrush = Skin.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = view });
            else _tabStrip.Children.Add(view);
        }
    }

    void ToggleSidebar() => ShowSidebar(!_side.IsVisible);

    void ShowSidebar(bool show)
    {
        _side.IsVisible = show;
        SetRailOn(_railFiles, show);
        _settings.SetFlag("side", show);
    }

    void ToggleRight()
    {
        _right.IsVisible = !_right.IsVisible;
        SetRailOn(_railRight, _right.IsVisible);
        _settings.SetFlag("right", _right.IsVisible);
        if (_right.IsVisible) UpdateNoteInfo(full: true);
    }
}
