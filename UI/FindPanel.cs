using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Leaflet.Core;
using Leaflet.Editor;

namespace Leaflet.UI;

/// <summary>
/// Поиск и замена в открытой заметке: панель над редактором. Совпадения подсвечены все, текущее выделено в тексте.
/// Enter / Shift+Enter — дальше / назад, Esc закрывает (его обрабатывает окно).
/// </summary>
sealed class FindPanel : Border
{
    readonly TextEditor _editor;
    readonly Settings _settings;
    readonly FindMarks _marks = new();
    readonly TextBox _find, _repl;
    readonly TextBlock _count;
    readonly Grid _grid;
    readonly Border _expander, _case, _close;
    readonly Control _expanderIcon;
    TextDocument? _doc;
    List<int> _starts = [];
    int _len, _cur = -1, _anchor;
    bool _setting, _replaceMode;

    public Border PrevButton { get; }
    public Border NextButton { get; }
    public Border ReplaceButton { get; }
    public Border ReplaceAllButton { get; }

    public bool IsOpen => IsVisible;
    public bool ReplaceMode => _replaceMode;
    public int MarkCount => _marks.Count;
    public string CountText => _count.Text ?? "";
    public TextBox FindBox => _find;
    public TextBox ReplaceBox => _repl;
    public int Current => _cur;
    public int Total => _starts.Count;

    public bool MatchCase
    {
        get => _settings.Flag("find.case", false);
        set
        {
            _settings.SetFlag("find.case", value);
            ShowCase();
            if (IsOpen) Refresh(jump: true);
        }
    }

    public FindPanel(TextEditor editor, Settings settings)
    {
        _editor = editor;
        _settings = settings;
        IsVisible = false;
        Background = Skin.Panel;
        BorderBrush = Skin.Line;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(12, 6);

        _find = Field(Loc.T("fp.find"));
        _repl = Field(Loc.T("fp.replacewith"));
        _find.TextChanged += (_, _) =>
        {
            if (_setting) return;
            if (IndexOfSelection() < 0) _anchor = _editor.SelectionStart;
            Refresh(jump: true);
        };

        _count = new TextBlock
        {
            FontSize = 12, Foreground = Skin.Mute, MinWidth = 96, Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var up = Ui.Icon(Ui.Chevron, Skin.Mute, 14);
        up.RenderTransform = new RotateTransform(-90);
        var down = Ui.Icon(Ui.Chevron, Skin.Mute, 14);
        down.RenderTransform = new RotateTransform(90);
        PrevButton = Ui.Button(up, () => Step(false), Loc.T("find.prev"), padding: new Thickness(6));
        NextButton = Ui.Button(down, () => Step(true), Loc.T("find.next"), padding: new Thickness(6));
        _case = new Border
        {
            Padding = new Thickness(7, 3), CornerRadius = new CornerRadius(5), Cursor = Ui.Hand, Margin = new Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            Child = new TextBlock { Text = "Aa", FontSize = 12.5, FontWeight = FontWeight.Bold },
        };
        ToolTip.SetTip(_case, Loc.T("fp.case"));
        _case.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) MatchCase = !MatchCase; };
        ShowCase();
        var close = _close = Ui.Button(Ui.Icon(Ui.Close, Skin.Mute, 12), () => Close(), Loc.T("common.close.esc"), padding: new Thickness(6));

        _expanderIcon = Ui.Icon(Ui.Chevron, Skin.Mute, 13);
        _expander = Ui.Button(_expanderIcon, () => SetReplace(!_replaceMode), Loc.T("fp.replace"), padding: new Thickness(5));
        _expander.VerticalAlignment = VerticalAlignment.Center;
        _expander.Margin = new Thickness(0, 0, 6, 0);

        ReplaceButton = TextButton(Loc.T("fp.replace"), () => ReplaceCurrent(), Loc.T("fp.replace.one"));
        ReplaceAllButton = TextButton(Loc.T("fp.all"), () => ReplaceAll(), Loc.T("fp.all.tip"));

        var tools = new DockPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _count, PrevButton, NextButton, _case } },
            },
        };
        DockPanel.SetDock(close, Dock.Right);
        tools.Children.Insert(0, close);
        var replaceTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 0, 0, 0), Children = { ReplaceButton, ReplaceAllButton } };

        _grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        Place(_expander, 0, 0);
        Place(_find, 0, 1);
        Place(tools, 0, 2);
        Place(_repl, 1, 1);
        Place(replaceTools, 1, 2);
        _repl.Margin = new Thickness(0, 6, 0, 0);
        replaceTools.Margin = new Thickness(10, 6, 0, 0);
        Child = _grid;
        SetReplace(false);

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        editor.TextArea.TextView.BackgroundRenderers.Add(_marks);
        editor.DocumentChanged += (_, _) => Attach(editor.Document);
        Attach(editor.Document);
    }

    /// <summary>Язык сменился: подписи полей и кнопок, число совпадений.</summary>
    public void Relocalize()
    {
        _find.PlaceholderText = Loc.T("fp.find");
        _repl.PlaceholderText = Loc.T("fp.replacewith");
        ToolTip.SetTip(_case, Loc.T("fp.case"));
        ToolTip.SetTip(_close, Loc.T("common.close.esc"));
        ToolTip.SetTip(_expander, Loc.T("fp.replace"));
        ((TextBlock)ReplaceButton.Child!).Text = Loc.T("fp.replace");
        ToolTip.SetTip(ReplaceButton, Loc.T("fp.replace.one"));
        ((TextBlock)ReplaceAllButton.Child!).Text = Loc.T("fp.all");
        ToolTip.SetTip(ReplaceAllButton, Loc.T("fp.all.tip"));
        if (IsOpen) Refresh(jump: false);
        else _count.Text = CountLabel(); // закрытая панель: только подпись, подсветку не трогаем
    }

    TextBox Field(string placeholder) => new()
    {
        PlaceholderText = placeholder, FontSize = 13, Width = 300, MinHeight = 28, Padding = new Thickness(6, 4),
        CornerRadius = new CornerRadius(6),
    };

    void Place(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        _grid.Children.Add(control);
    }

    static Border TextButton(string text, Action click, string tip)
    {
        var b = Ui.Button(Ui.Text(text, 12.5), click, tip, 5, new Thickness(10, 4));
        b.BorderBrush = Skin.Line;
        b.BorderThickness = new Thickness(1);
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    void ShowCase()
    {
        bool on = MatchCase;
        _case.Background = on ? Skin.AccentBack : Brushes.Transparent;
        if (_case.Child is TextBlock t) t.Foreground = on ? Skin.Accent : Skin.Mute;
    }

    void SetReplace(bool on)
    {
        _replaceMode = on && !_editor.IsReadOnly;
        _repl.IsVisible = ReplaceButton.IsVisible = ReplaceAllButton.IsVisible = _replaceMode;
        _grid.RowDefinitions[1].Height = _replaceMode ? GridLength.Auto : new GridLength(0);
        _expanderIcon.RenderTransform = _replaceMode ? new RotateTransform(90) : null;
        _expander.IsVisible = !_editor.IsReadOnly;
    }

    /// <summary>Режим «только чтение» включён или выключен: строка замены в чтении не нужна.</summary>
    public void SyncReadOnly() => SetReplace(_replaceMode);

    // ---------- открыть и закрыть ----------

    /// <summary>Открывает панель. Выделенный в одну строку текст становится запросом; <paramref name="replace"/> — показать строку замены.</summary>
    public void Open(bool replace)
    {
        SetReplace(replace);
        if (!_editor.TextArea.Selection.IsEmpty)
        {
            string sel = _editor.SelectedText;
            if (sel.Length is > 0 and <= 200 && !sel.Contains('\n') && !sel.Contains('\r'))
            {
                _setting = true;
                _find.Text = sel;
                _setting = false;
            }
        }
        _anchor = _editor.SelectionStart;
        IsVisible = true;
        Refresh(jump: true);
        var target = _replaceMode && !string.IsNullOrEmpty(_find.Text) ? _repl : _find;
        target.Focus();
        target.SelectAll();
    }

    public void Close(bool focusEditor = true)
    {
        if (!IsVisible) return;
        // Фокус — в редактор до того, как поле поиска скроется: иначе скрытие сбросит и его.
        if (focusEditor) _editor.TextArea.Focus(); // TextEditor сам фокус не принимает
        IsVisible = false;
        _marks.Clear();
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    public void SetQuery(string text)
    {
        _setting = true;
        _find.Text = text;
        _setting = false;
        Refresh(jump: true);
    }

    // ---------- поиск ----------

    void Attach(TextDocument? doc)
    {
        if (_doc != null) _doc.Changed -= OnDocumentChanged;
        _doc = doc;
        if (doc != null) doc.Changed += OnDocumentChanged;
        if (IsOpen) { _anchor = _editor.SelectionStart; Refresh(jump: false); }
    }

    void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (IsOpen) Refresh(jump: false);
    }

    /// <summary>Номер совпадения, которое сейчас выделено в тексте, или -1.</summary>
    int IndexOfSelection()
    {
        if (_starts.Count == 0 || _editor.TextArea.Selection.IsEmpty || _editor.SelectionLength != _len) return -1;
        int i = _starts.BinarySearch(_editor.SelectionStart);
        return i >= 0 ? i : -1;
    }

    /// <summary>Пересчитывает совпадения; <paramref name="jump"/> — перейти к первому от точки отсчёта.</summary>
    void Refresh(bool jump)
    {
        string q = _find.Text ?? "";
        _len = q.Length;
        _starts = q.Length == 0 ? [] : TextFind.Matches(_editor.Document.Text, q, MatchCase);
        _cur = IndexOfSelection();
        if (jump && _starts.Count > 0)
        {
            int i = _starts.FindIndex(s => s >= _anchor);
            Select(i < 0 ? 0 : i);
        }
        else Show();
    }

    void Select(int i)
    {
        _cur = i;
        int start = _starts[i];
        _editor.Select(start, _len);
        _editor.ScrollToLine(_editor.Document.GetLineByOffset(start).LineNumber);
        Show();
    }

    void Show()
    {
        _marks.Set(_starts, _len, _cur);
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        _count.Text = CountLabel();
    }

    string CountLabel() => _len == 0 ? "" : _starts.Count == 0 ? Loc.T("fp.none")
        : _cur >= 0 ? Loc.T("fp.of", _cur + 1, _starts.Count) : Loc.T("fp.count", _starts.Count);

    /// <summary>Следующее или предыдущее совпадение от текущего места; в конце заметки переходит по кругу.</summary>
    public void Step(bool forward)
    {
        if (!IsOpen) { Open(false); return; }
        if (_starts.Count == 0) return;
        int at = IndexOfSelection(), i;
        if (at >= 0) i = (at + (forward ? 1 : -1) + _starts.Count) % _starts.Count;
        else if (forward)
        {
            i = _starts.FindIndex(s => s >= _editor.SelectionStart);
            if (i < 0) i = 0;
        }
        else
        {
            i = _starts.FindLastIndex(s => s < _editor.SelectionStart);
            if (i < 0) i = _starts.Count - 1;
        }
        Select(i);
    }

    // ---------- замена ----------

    /// <summary>Заменяет выделенное совпадение и переходит к следующему; если совпадение ещё не выбрано — только выбирает его.</summary>
    public void ReplaceCurrent()
    {
        if (_editor.IsReadOnly || _starts.Count == 0) return;
        int i = IndexOfSelection();
        if (i < 0) { Step(true); return; }
        string repl = _repl.Text ?? "";
        int start = _starts[i];
        _anchor = start + repl.Length;
        _editor.Document.Replace(start, _len, repl);
        Refresh(jump: true);
    }

    /// <summary>Заменяет все совпадения за один шаг отмены.</summary>
    public void ReplaceAll()
    {
        if (_editor.IsReadOnly || _starts.Count == 0) return;
        var (text, n) = TextFind.Replace(_editor.Document.Text, _find.Text ?? "", _repl.Text ?? "", MatchCase);
        if (n == 0) return;
        _anchor = _editor.SelectionStart;
        MainWindow.ReplaceChanged(_editor.Document, text);
        Refresh(jump: false);
        _count.Text = Loc.T("fp.replaced", n);
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None) return;
        if (e.Source == _repl) ReplaceCurrent();
        else Step(!e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }
}
