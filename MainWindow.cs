using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Leaflet.Core;
using Leaflet.Editor;
using Leaflet.UI;

namespace Leaflet;

/// <summary>Открытая заметка: свой документ (и своя история отмены), позиция курсора.</summary>
sealed class Tab(Note note, TextDocument doc)
{
    public Note Note = note;
    public readonly TextDocument Doc = doc;
    public bool Dirty;
    public int Caret;
    public bool Edited;       // правили в этом сеансе: при закрытии — снимок версии
    public bool Conflict;     // файл изменён снаружи при несохранённых правках: ждём решения, не сохраняем
    public bool Missing;      // файл удалён или перенесён снаружи: текст только во вкладке
    public string? DraftText; // последний записанный черновик (null — черновика нет)
    public bool Help;         // справка из ресурсов exe: только чтение, на диск не пишется, в хранилище её нет
    public bool External;     // отдельный файл снаружи текущего хранилища: открывается вкладкой и сохраняется напрямую
}

public sealed partial class MainWindow : Window
{
    public TextEditor Editor { get; }
    public LivePreview Preview { get; }
    public ImagePreview Images { get; }

    readonly Settings _settings;
    readonly Keymap _keys;
    readonly LinkComplete _complete;
    readonly MarkdownKeys _mdKeys;
    readonly bool _bench;
    readonly string? _testOnly; // --only 3c: самопроверка только этой части
    readonly List<Tab> _tabs = [];
    Note? _helpNote;         // справка, создаётся при первом открытии
    bool _readBeforeHelp;    // режим чтения до входа во вкладку справки
    readonly List<Tab> _recent = []; // недавно открытые: при возврате к заметке сохраняется история отмены
    readonly List<Note> _back = [], _forward = [];
    readonly DispatcherTimer _saveTimer, _infoTimer;
    Vault? _vault;
    Tab? _tab;
    bool _reloading;
    DateTime? _savedAt;
    string? _saveError;
    string? _notice; // итог переименования, переноса или удаления; гаснет при следующей правке

    public MainWindow(string[] args)
    {
        string? path = null, fontCheck = null, selfTest = null, data = null;
        var extra = new List<string>(); // остальные файлы из командной строки: вкладками, если лежат в той же папке
        int bench = -1;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bench") bench = ++i;
            else if (args[i] == "--fontcheck") fontCheck = args[++i];
            else if (args[i] == "--selftest") selfTest = args[++i];
            else if (args[i] == "--only") _testOnly = args[++i];
            else if (args[i] == "--data") data = args[++i];
            else if (args[i] == "--lang") i++;
            else if (args[i] == "--crashtest") _crashTest = true;
            else if (!args[i].StartsWith("--") || File.Exists(args[i]) || Directory.Exists(args[i])) { if (path == null) path = args[i]; else extra.Add(args[i]); }
        }
        _bench = bench >= 0 || fontCheck != null;
        _settings = Settings.Load(readOnly: _bench || selfTest != null || _crashTest);
        InitSafety(data ?? (selfTest != null ? Path.Combine(Path.GetFullPath(selfTest), "appdata") : _bench ? null : Settings.DefaultDir), clean: selfTest != null && data == null);
        _keys = Keymap.Current = new Keymap(_settings);

        Skin.SetText(_settings.Font, _settings.FontSize);
        Skin.Apply(Skin.FromName(_settings["theme"]));
        InitNativeBackdrop();

        Title = "Leaflet";
        bool fixedSize = _settings.ReadOnly;
        Width = fixedSize ? 1200 : 1240;
        Height = fixedSize ? 780 : 800;
        Icon = AppIcon();
        if (!fixedSize) RestorePlacement();
        MinWidth = 640;
        MinHeight = 400;
        FontFamily = Skin.Font;
        Background = Skin.Bg;
        // Заголовок окна — полоса вкладок со своими кнопками (системный заголовок Avalonia 12 ложится поверх вкладок).
        WindowDecorations = WindowDecorations.BorderOnly;
        // Без этого Windows 10 рисует над вкладками белую полосу 7 px (верхняя рамка изменения размера); при BorderOnly свой заголовок Avalonia не рисует.
        ExtendClientAreaToDecorationsHint = true;

        Editor = new TextEditor
        {
            Document = new TextDocument(),
            FontFamily = Skin.Font,
            FontSize = 16,
            WordWrap = true,
            ShowLineNumbers = false,
            Padding = new Thickness(36, 22),
            Foreground = Skin.Fg,
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.HighlightCurrentLine = false;
        Editor.TextArea.SelectionBrush = Skin.Selection;
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.Caret.CaretBrush = Skin.Accent;
        Preview = new LivePreview(Editor);
        Images = new ImagePreview(Editor, Preview, ResolveEmbed, EmbedNote, () => _tab == null ? null : Path.GetDirectoryName(_tab.Note.Path));
        Images.OpenNote += target => OpenLink(target, wiki: true);
        Preview.Tables.LinkClicked += OpenLink;
        _complete = new LinkComplete(Editor, Preview, () => _vault, () => _tab?.Note);
        _mdKeys = new MarkdownKeys(Editor, Preview);
        _ = new TypingUndoGroups(Editor);
        new LinkClicks(Editor, Preview).Clicked += OpenLink;
        Editor.TextArea.Caret.PositionChanged += (_, _) => OnCaretMoved();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveTab(_tab); };
        _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _infoTimer.Tick += (_, _) => { _infoTimer.Stop(); UpdateNoteInfo(full: false); };

        Content = BuildLayout();
        _keys.Changed += RefreshHints;
        Loc.Changed += OnLanguageChanged;
        ShowSidebar(_settings.Flag("side", true));
        _right.IsVisible = _settings.Flag("right", true);
        SetRailOn(_railRight, _right.IsVisible);
        ApplyPrefs(force: true);
        SetReadMode(false);

        // Папка: из командной строки (файл — его папка или охватывающее хранилище) или последняя открытая.
        string? openFile = null, root = null;
        bool rememberVault = false;
        if (path != null && File.Exists(path))
        {
            openFile = Path.GetFullPath(path);
            root = _settings["vault"] is { } saved && Directory.Exists(saved) && IsUnder(saved, openFile)
                ? saved
                : Path.GetDirectoryName(openFile);
        }
        else if (path != null && Directory.Exists(path)) { root = path; rememberVault = true; }
        else if (_settings["vault"] is { } last && Directory.Exists(last)) root = last;
        if (root != null) SetVault(root, openFile, rememberVault);
        else ShowWelcome(true);
        foreach (var more in extra) OpenExternal([Path.GetFullPath(more)], raise: false);

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        SetupDrop();
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            var b = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (b == PointerUpdateKind.XButton1Pressed) { GoBack(); e.Handled = true; }
            else if (b == PointerUpdateKind.XButton2Pressed) { GoForward(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => { if (!_crashTest) SaveAll(); };
        Closing += (_, _) => OnClosing();

        //if (bench >= 0)
        //    Opened += async (_, _) => await new Bench.Bench(this, args[bench]).Run();
        //else if (selfTest != null)
        //    Opened += async (_, _) => { await SelfTest(selfTest); Close(); };
        //else if (fontCheck != null)
        //    Opened += async (_, _) => { await Bench.FontCheck.Run(this, fontCheck); Close(); };
        //else if (_crashTest)
        //    Opened += async (_, _) => await CrashTest();
        //else
        Opened += (_, _) =>
        {
            if (_tab != null) Editor.Focus();
            if (data == null && !_settings.ReadOnly)
            {
                DesktopShortcut.EnsureFirstRun(_settings, FileAssoc.ExePath);
                if (FileAssoc.EnsureFirstRun(_settings, FileAssoc.ExePath) && !FileAssoc.IsDefault())
                    ShowAssocBanner();
            }
        };
        Activated += (_, _) =>
        {
            if (_assocBanner.IsVisible && FileAssoc.IsDefault())
            {
                _assocBanner.Hide();
                _settingsPanel.Refresh();
                _notice = Loc.T("shell.default.ok");
                UpdateStatus();
            }
        };
    }

    // ---------- хранилище ----------

    void SetVault(string root, string? openFile = null, bool rememberVault = true)
    {
        SaveAll();
        _vault?.StopWatching();
        _tabs.Clear();
        _recent.Clear();
        _tab = null;
        _back.Clear();
        _forward.Clear();
        var vault = _vault = new Vault(root) { ReadOnly = _bench, AttachFolder = _settings.AttachFolder };
        if (rememberVault) _settings["vault"] = vault.Root;
        _settingsPanel.SetVault(vault.Root);
        ShowWelcome(false);
        _side.SetVault(vault);

        var first = openFile != null ? vault.Add(openFile) : null;
        if (first != null) Open(first);
        else ShowNote(null);
        Dispatcher.UIThread.Post(() => _ = LoadVault(vault), DispatcherPriority.Background);
    }

    async Task LoadVault(Vault vault)
    {
        var notes = await Task.Run(vault.Scan);
        if (vault != _vault) return;
        vault.SetAll(notes);
        _side.Rebuild();
        if (_tab == null)
        {
            var last = _settings["last:" + vault.Root] is { } rel ? vault.Get(Path.Combine(vault.Root, rel)) : null;
            last ??= vault.Notes.OrderByDescending(n => n.Modified).FirstOrDefault();
            if (last != null) Open(last);
        }
        else UpdateNoteInfo(full: true);
        vault.Watch(batch => Dispatcher.UIThread.Post(() => OnExternalChange(vault, batch)));
        OfferRecovery();
    }

    async Task PickFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("pick.folder.title"), AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } local) SetVault(local, rememberVault: true);
    }

    async Task PickFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("pick.file.title"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown", "*.mdown", "*.mkd"] },
            ],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) OpenExternal(paths, raise: false);
    }

    /// <summary>Открывает отдельный Markdown-файл снаружи текущего хранилища в новой вкладке, не закрывая открытые заметки.</summary>
    void OpenStandaloneFile(string fullPath)
    {
        SaveTab(_tab);
        var tab = _tabs.Find(t => SamePath(t.Note.Path, fullPath)) ?? _recent.Find(t => SamePath(t.Note.Path, fullPath));
        try
        {
            if (tab == null)
            {
                string text = File.ReadAllText(fullPath);
                var note = new Note
                {
                    Path = fullPath,
                    Rel = Path.GetFileName(fullPath),
                    Title = Path.GetFileNameWithoutExtension(fullPath),
                    Folder = Path.GetDirectoryName(fullPath) ?? "",
                    Text = text,
                    Links = Markdown.LinkKeys(text),
                    Tags = Markdown.TagKeys(text),
                    Modified = File.GetLastWriteTime(fullPath),
                };
                tab = new Tab(note, new TextDocument(text)) { External = true };
                var t = tab;
                tab.Doc.Changed += (_, _) => OnDocChanged(t);
            }
            else if (!tab.Dirty)
            {
                string disk = File.ReadAllText(fullPath);
                tab.Note.Text = disk;
                tab.Note.Modified = File.GetLastWriteTime(fullPath);
                if (tab.Doc.Text != disk) Reload(tab, disk);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = Loc.T("common.fail") + ex.Message;
            UpdateStatus();
            return;
        }
        if (!_tabs.Contains(tab))
        {
            _recent.Remove(tab);
            _recent.Add(tab);
            if (_recent.Count > 30) _recent.RemoveAt(0);
            int at = _tab == null ? _tabs.Count : _tabs.IndexOf(_tab);
            _tabs.Insert(Math.Min(at + 1, _tabs.Count), tab);
        }
        ShowWelcome(false);
        Activate(tab);
    }

    void ShowWelcome(bool show)
    {
        _welcome.IsVisible = show;
        if (show) { _side.SetVault(null); ShowNote(null); }
    }

    // ---------- вкладки и переходы ----------

    void Open(Note note, bool newTab = false, bool remember = true)
    {
        if (_tab?.Note == note) { Editor.Focus(); return; }
        if (remember && _tab != null) { _back.Add(_tab.Note); _forward.Clear(); }
        SaveTab(_tab);
        var tab = _tabs.Find(t => t.Note == note);
        if (tab == null)
        {
            tab = _recent.Find(t => t.Note == note);
            if (tab == null)
            {
                tab = new Tab(note, new TextDocument(note.Text)) { Help = note == _helpNote };
                var t = tab;
                tab.Doc.Changed += (_, _) => OnDocChanged(t);
            }
            else if (!tab.Dirty && tab.Doc.Text != note.Text) Reload(tab, note.Text);
            _recent.Remove(tab);
            _recent.Add(tab);
            if (_recent.Count > 30) _recent.RemoveAt(0);
            int at = _tab == null ? _tabs.Count : _tabs.IndexOf(_tab);
            if (newTab || _tab == null) _tabs.Insert(Math.Min(at + 1, _tabs.Count), tab);
            else _tabs[at] = tab; // как в Obsidian: переход открывается в текущей вкладке
        }
        Activate(tab);
    }

    void Activate(Tab tab)
    {
        if (_tab != null && _tab != tab) { _tab.Caret = Editor.CaretOffset; SaveTab(_tab); }
        bool wasHelp = _tab?.Help == true;
        _tab = tab;
        if (tab.Help && !wasHelp) _readBeforeHelp = Preview.ReadMode;
        if (tab.Help != wasHelp) SetReadMode(tab.Help || _readBeforeHelp);
        Editor.Document = tab.Doc;
        Editor.CaretOffset = Math.Min(tab.Caret, tab.Doc.TextLength);
        int line = Editor.TextArea.Caret.Line;
        if (line == 1) Editor.ScrollToHome();
        else Dispatcher.UIThread.Post(() => { if (_tab == tab) Editor.ScrollToLine(line); }, DispatcherPriority.Loaded);
        if (_vault != null && !tab.Help && !tab.External) _settings["last:" + _vault.Root] = tab.Note.Rel;
        ShowNote(tab);
        Editor.Focus();
    }

    void ShowNote(Tab? tab)
    {
        _empty.IsVisible = tab == null;
        Editor.IsEnabled = tab != null;
        if (tab == null) { _find.Close(focusEditor: false); _complete.Close(); }
        if (tab == null) Editor.Document = new TextDocument();
        Title = tab == null ? "Leaflet" : $"{tab.Note.Title} — Leaflet";
        _side.SetCurrent(tab?.Note);
        RebuildTabs();
        UpdateCrumb();
        UpdateBanner();
        UpdateNoteInfo(full: true);
    }

    void CloseTab(Tab tab)
    {
        if (tab == _tab) tab.Caret = Editor.CaretOffset;
        SaveTab(tab);
        OnTabClosed(tab);
        int i = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (tab != _tab) { RebuildTabs(); return; }
        _tab = null;
        if (tab.Help) SetReadMode(_readBeforeHelp);
        if (_tabs.Count > 0) Activate(_tabs[Math.Min(i, _tabs.Count - 1)]);
        else ShowNote(null);
    }

    void CycleTab(int step)
    {
        if (_tabs.Count < 2 || _tab == null) return;
        Activate(_tabs[(_tabs.IndexOf(_tab) + step + _tabs.Count) % _tabs.Count]);
    }

    void GoBack() => Step(_back, _forward);
    void GoForward() => Step(_forward, _back);

    void Step(List<Note> from, List<Note> to)
    {
        while (from.Count > 0)
        {
            var note = from[^1];
            from.RemoveAt(from.Count - 1);
            if (_vault?.Get(note.Path) == null) continue;
            if (_tab != null) to.Add(_tab.Note);
            Open(note, remember: false);
            return;
        }
    }

    void OpenLink(string target, bool wiki)
    {
        if (OpenFileLink(target, wiki)) return;
        if (!wiki)
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                _ = Launcher.LaunchUriAsync(uri);
            else if (_tab != null && FileAssoc.IsMarkdown(Uri.UnescapeDataString(target)))
            {
                string file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_tab.Note.Path)!, Uri.UnescapeDataString(target)));
                if (_vault != null && !_tab.External && _vault.Get(file) is { } linked) Open(linked);
                else if (File.Exists(file)) OpenExternal([file], raise: false);
            }
            return;
        }
        if (_vault == null || _tab?.External == true) return;
        SaveTab(_tab); // чтобы новая заметка сразу видела обратную ссылку
        var note = _vault.Resolve(target, _tab?.Note);
        if (note == null)
        {
            note = _vault.Create(target);
            _side.Rebuild();
        }
        Open(note);
        int hash = target.IndexOf('#');
        if (hash >= 0) GoToHeading(target[(hash + 1)..]);
    }

    void GoToHeading(string heading)
    {
        heading = heading.Trim();
        foreach (var line in Editor.Document.Lines)
        {
            string text = Editor.Document.GetText(line);
            var m = Markdown.Heading.Match(text);
            if (m.Success && text[m.Length..].Trim().Equals(heading, StringComparison.OrdinalIgnoreCase)) { GoToLine(line.LineNumber); return; }
        }
    }

    void GoToLine(int line)
    {
        if (_tab == null || line < 1 || line > Editor.Document.LineCount) return;
        Editor.TextArea.Caret.Location = new TextLocation(line, 1);
        Editor.ScrollToLine(line);
        Editor.Focus();
    }

    void SelectAt(int offset, int length)
    {
        if (_tab == null) return;
        offset = Math.Clamp(offset, 0, Editor.Document.TextLength);
        length = Math.Clamp(length, 0, Editor.Document.TextLength - offset);
        Editor.Select(offset, length);
        Editor.ScrollToLine(Editor.Document.GetLineByOffset(offset).LineNumber);
        Editor.Focus();
    }

    void OpenSearchHit(SearchHit hit, string query)
    {
        Open(hit.Note);
        // Текст в редакторе мог уйти вперёд сохранённого: ищем совпадение рядом с найденным местом.
        string text = Editor.Document.Text;
        int at = text.IndexOf(query, Math.Max(0, Math.Min(hit.Offset, text.Length) - 200), StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) SelectAt(at, query.Length);
    }

    void SetSearch(string text) => _side.SetQuery(text);

    void FocusSearch()
    {
        ShowSidebar(true);
        _side.FocusSearch();
    }

    // ---------- правка и сохранение ----------

    void OnDocChanged(Tab tab)
    {
        if (_reloading) return;
        tab.Edited = true;
        if (!tab.Dirty) { tab.Dirty = true; _notice = null; UpdateStatus(); }
        _saveTimer.Stop();
        if (_settings.AutosaveMs > 0) _saveTimer.Start();
        if (_drafts != null) { _draftTimer.Stop(); _draftTimer.Start(); }
        _infoTimer.Stop();
        _infoTimer.Start();
    }

    void SaveTab(Tab? tab)
    {
        if (tab == null || !tab.Dirty || tab.Help || tab.Conflict || tab.Missing) return; // конфликт и пропавший файл ждут решения
        if (tab.External)
        {
            if (_bench) return;
            try
            {
                string text = tab.Doc.Text;
                string tmp = tab.Note.Path + ".leaflet-tmp";
                File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
                File.Move(tmp, tab.Note.Path, overwrite: true);
                tab.Note.Text = text;
                tab.Note.Links = Markdown.LinkKeys(text);
                tab.Note.Tags = Markdown.TagKeys(text);
                tab.Note.Modified = File.GetLastWriteTime(tab.Note.Path);
                tab.Dirty = false;
                _savedAt = DateTime.Now;
                _saveError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _saveError = ex.Message;
            }
            if (tab == _tab) UpdateStatus();
            return;
        }
        if (_vault == null) return;
        // Файл поменяли снаружи, а слежение ещё не успело: не затираем, а разбираем как правку снаружи.
        if (_vault.ChangedOnDisk(tab.Note))
        {
            OnExternalChange(_vault, [tab.Note.Path]);
            if (!tab.Dirty || tab.Conflict || tab.Missing) return;
        }
        try
        {
            string before = tab.Note.Text;
            _vault.Save(tab.Note, tab.Doc.Text);
            tab.Dirty = false;
            _savedAt = DateTime.Now;
            _saveError = null;
            DropDraft(tab);
            if (HistoryOn && _settings.HistoryMinutes > 0)
                _history!.Add(_vault.Root, tab.Note.Rel, tab.Note.Text, "save", TimeSpan.FromMinutes(_settings.HistoryMinutes), before);
            Images.NotesChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saveError = ex.Message;
        }
        if (tab == _tab) UpdateStatus();
    }

    void SaveAll()
    {
        _saveTimer.Stop();
        foreach (var tab in _tabs) SaveTab(tab);
        _settings.Save();
    }

    void OnClosing()
    {
        SaveAll();
        KeepUnsaved();
        _vault?.StopWatching();
        if (!_bench)
        {
            SavePlacement();
            _settings.Save();
        }
    }

    void PromptNewNote()
    {
        if (_vault == null) { _ = PickFolder(); return; }
        _palette.Open("+", Loc.T("new.ph"), q =>
        {
            string name = q.Trim();
            if (name.Length == 0) return [new PaletteItem(Loc.T("hint.enter"), null, () => { })];
            var items = new List<PaletteItem>();
            if (_vault.Resolve(name) is { } existing)
                items.Add(new PaletteItem(Loc.T("open.existing", existing.Title), "Enter", () => Open(existing), existing.Folder));
            items.Add(new PaletteItem(Loc.T("create.named", name), items.Count == 0 ? "Enter" : null, () =>
            {
                var note = _vault.Create(name);
                _side.Rebuild();
                Open(note, newTab: true);
            }));
            return items;
        });
    }

    // ---------- переименование, перенос, удаление ----------

    void PromptRename(Note note)
    {
        if (_vault == null) return;
        int linked = _vault.Backlinks(note).Count;
        _palette.Open("✎", Loc.T("rename.ph"), q =>
        {
            var (path, title) = _vault.Target(note.Folder, q);
            if (title.Length == 0) return [new PaletteItem(Loc.T("hint.enter"), null, () => { })];
            if (path == note.Path) return [new PaletteItem(Loc.T("name.same"), null, () => { })];
            if (!path.Equals(note.Path, StringComparison.OrdinalIgnoreCase) && (File.Exists(path) || _vault.Get(path) != null))
                return [new PaletteItem(Loc.T("note.exists", title), null, () => { }, note.Folder.Length > 0 ? note.Folder : null)];
            return [new PaletteItem(Loc.T("attach.rename.to", title), "Enter", () => MoveNote(note, note.Folder, title),
                linked == 0 ? null : Loc.T("links.willupdate") + Loc.Plural(linked, "pl.note.in"))];
        }, note.Title);
    }

    void PromptMove(Note note)
    {
        if (_vault == null) return;
        var folders = _vault.Folders().Where(f => !f.Equals(note.Folder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
        _palette.Open("→", Loc.T("move.ph", note.Title), q =>
        {
            q = q.Trim().Trim('/');
            var items = folders.Select(f => (f, s: CommandPalette.Score(f.Length == 0 ? "/" : f, q)))
                .Where(x => x.s >= 0).OrderBy(x => x.s).Take(50)
                .Select(x => new PaletteItem(x.f.Length == 0 ? "/" : x.f, null, () => MoveNote(note, x.f, note.Title),
                    x.f.Length == 0 ? Loc.T("move.root") : null)).ToList();
            if (q.Length > 0 && !folders.Any(f => f.Equals(q, StringComparison.OrdinalIgnoreCase))
                && !q.Equals(note.Folder, StringComparison.OrdinalIgnoreCase))
                items.Add(new PaletteItem(Loc.T("move.newfolder", q), null, () => MoveNote(note, q, note.Title)));
            if (items.Count > 0) items[0] = items[0] with { Hint = "Enter" };
            return items;
        });
    }

    void PromptDelete(Note note)
    {
        if (_vault == null) return;
        int linked = _vault.Backlinks(note).Count;
        List<PaletteItem> items =
        [
            new(Loc.T("del.title", note.Title), "Enter", () => DeleteNote(note),
                linked == 0 ? null : Loc.T("del.refs") + Loc.Plural(linked, "pl.note")),
            new(Loc.T("common.cancel"), "Esc", () => { }),
        ];
        _palette.Open("✕", Loc.T("del.ph"), _ => items);
    }

    void MoveNote(Note note, string folder, string title)
    {
        if (_vault == null) return;
        SaveAll();
        string oldRel = note.Rel;
        try
        {
            var changed = _vault.Move(note, folder, title);
            if (HistoryOn) _history!.Rename(_vault.Root, oldRel, note.Rel);
            foreach (var tab in _tabs.Concat(_recent).Distinct())
            {
                if (!changed.Contains(tab.Note) || tab.Doc.Text == tab.Note.Text) continue;
                _reloading = true;
                ReplaceChanged(tab.Doc, tab.Note.Text);
                _reloading = false;
            }
            _notice = $"{oldRel} → {note.Rel}"
                + (changed.Count == 0 ? "" : Loc.T("links.updated") + Loc.Plural(changed.Count, "pl.note.in"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = Loc.T("common.fail") + ex.Message;
        }
        if (_tab?.Note == note) _settings["last:" + _vault.Root] = note.Rel;
        _side.Rebuild();
        ShowNote(_tab);
    }

    /// <summary>Заменяет в документе только отличающуюся середину: каретка и остальная история отмены не сбиваются.</summary>
    internal static void ReplaceChanged(TextDocument doc, string text)
    {
        string old = doc.Text;
        int max = Math.Min(old.Length, text.Length), start = 0, end = 0;
        while (start < max && old[start] == text[start]) start++;
        while (end < max - start && old[^(end + 1)] == text[^(end + 1)]) end++;
        doc.Replace(start, old.Length - start - end, text[start..^end]);
    }

    void DeleteNote(Note note)
    {
        if (_vault == null) return;
        SaveAll();
        try { _vault.Trash(note); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = Loc.T("del.fail") + ex.Message;
            UpdateStatus();
            return;
        }
        _notice = Loc.T("del.done", note.Title);
        _recent.RemoveAll(t => t.Note == note);
        _back.RemoveAll(n => n == note);
        _forward.RemoveAll(n => n == note);
        foreach (var tab in _tabs.Where(t => t.Note == note).ToList())
        {
            tab.Dirty = false; // иначе закрытие вкладки запишет файл заново
            DropDraft(tab);
            CloseTab(tab);
        }
        _side.Rebuild();
        UpdateNoteInfo(full: true);
    }

    // ---------- экспорт ----------

    /// <summary>Выбор в палитре: эта заметка в HTML или PDF, все заметки в папку.</summary>
    void PromptExport()
    {
        if (_vault == null && _tab == null) return;
        var items = new List<PaletteItem>();
        if (_tab?.Note is { } note)
        {
            items.Add(new(Loc.T("exp.html.item", note.Title), "Enter", () => _ = PromptExport(note, false), Loc.T("exp.html.sub")));
            items.Add(new(Loc.T("exp.pdf.item", note.Title), null, () => _ = PromptExport(note, true), Loc.T("exp.pdf.sub")));
        }
        if (_vault != null)
        {
            items.Add(new(Loc.T("exp.all.html"), items.Count == 0 ? "Enter" : null, () => _ = PromptExportAll(".html"), Loc.T("exp.all.html.sub")));
            items.Add(new(Loc.T("exp.all.pdf"), null, () => _ = PromptExportAll(".pdf"), Loc.T("exp.all.pdf.sub")));
        }
        _palette.Open("⇪", Loc.T("exp.ph"), q => items
            .Select(i => (i, s: CommandPalette.Score(i.Title, q.Trim()))).Where(x => x.s >= 0).OrderBy(x => x.s).Select(x => x.i).ToList());
    }

    async Task<IStorageFolder?> ExportFolder() =>
        _settings["export.dir"] is { } dir && Directory.Exists(dir) ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;

    async Task PromptExport(Note note, bool pdf)
    {
        SaveAll();
        string ext = pdf ? ".pdf" : ".html";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = pdf ? Loc.T("exp.title.pdf") : Loc.T("exp.title.html"),
            SuggestedFileName = note.Title + ext,
            SuggestedStartLocation = await ExportFolder(),
            DefaultExtension = ext[1..],
            FileTypeChoices = [new FilePickerFileType(pdf ? Loc.T("exp.type.pdf") : Loc.T("exp.type.html")) { Patterns = ["*" + ext] }],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) path += ext;
        ExportNote(note, path);
    }

    async Task PromptExportAll(string ext)
    {
        if (_vault == null) return;
        SaveAll();
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = ext == ".pdf" ? Loc.T("exp.dir.pdf") : Loc.T("exp.dir.html"), AllowMultiple = false,
            SuggestedStartLocation = await ExportFolder(),
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } dir) ExportAll(dir, ext);
    }

    void ExportNote(Note note, string path)
    {
        try
        {
            Export.ToFile(_vault, note, note.Text, path, Skin.ExportStyle(_settings.TextWidth));
            _notice = Loc.T("exp.done") + path;
            _settings["export.dir"] = Path.GetDirectoryName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = Loc.T("exp.fail") + ex.Message;
        }
        UpdateStatus();
    }

    void ExportAll(string dir, string ext)
    {
        if (_vault == null) return;
        try
        {
            int n = Export.AllToFolder(_vault, dir, ext, Skin.ExportStyle(_settings.TextWidth));
            _notice = Loc.T("exp.all.done", Loc.Plural(n, "pl.note"), dir);
            _settings["export.dir"] = dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = Loc.T("exp.fail") + ex.Message;
        }
        UpdateStatus();
    }

    // ---------- замена по всему хранилищу ----------

    /// <summary>Шаг 1: что заменить. Шаг 2 (<see cref="PromptReplaceWith"/>): чем заменить, с предпросмотром заметок и числа замен.</summary>
    void PromptReplaceAll(string find = "")
    {
        if (_vault == null) return;
        SaveAll();
        _palette.Open("↔", Loc.T("repl.ph"), ReplaceFindItems, find);
    }

    List<PaletteItem> ReplaceFindItems(string find)
    {
        if (_vault == null || find.Length == 0) return [new PaletteItem(Loc.T("repl.hint"), null, () => { })];
        bool matchCase = _find.MatchCase;
        var plan = _vault.ReplacePlan(find, matchCase);
        int total = plan.Sum(p => p.Count);
        return
        [
            total == 0
                ? new PaletteItem(Loc.T("repl.none"), null, () => { })
                : new PaletteItem(Loc.T("repl.next", Cut(find)), "Enter", () => PromptReplaceWith(find, plan),
                    Loc.T("in.fmt", Loc.Plural(total, "pl.match"), Loc.Plural(plan.Count, "pl.note.in"))),
            new PaletteItem(matchCase ? Loc.T("repl.case.on") : new string(Loc.T("repl.case.off")), null, () => { _find.MatchCase = !matchCase; PromptReplaceAll(find); }),
        ];
    }

    void PromptReplaceWith(string find, List<(Note Note, int Count)> plan, string repl = "")
    {
        _palette.Open("↔", Loc.T("repl.with.ph", Cut(find)), r => ReplaceItems(find, r, plan), repl);
    }

    List<PaletteItem> ReplaceItems(string find, string repl, List<(Note Note, int Count)> plan)
    {
        bool matchCase = _find.MatchCase;
        int total = plan.Sum(p => p.Count);
        string summary = Loc.T("in.fmt", Loc.Plural(total, "pl.repl"), Loc.Plural(plan.Count, "pl.note.in"));
        var items = new List<PaletteItem>
        {
            new(repl.Length == 0 ? Loc.T("repl.delete", Cut(find)) : Loc.T("repl.replace", Cut(find), Cut(repl)), "Enter",
                () => ReplaceInVault(find, repl, matchCase), summary),
        };
        foreach (var (note, count) in plan.Take(100))
            items.Add(new PaletteItem(note.Title, null, () => ShowFirstMatch(note, find, matchCase),
                (note.Folder.Length > 0 ? note.Folder + " · " : "") + Loc.Plural(count, "pl.repl")));
        if (plan.Count > 100) items.Add(new PaletteItem(Loc.T("repl.more", Loc.Plural(plan.Count - 100, "pl.note")), null, () => { }));
        return items;
    }

    static string Cut(string s) => s.Length <= 28 ? s : s[..27] + "…";

    void ShowFirstMatch(Note note, string find, bool matchCase)
    {
        Open(note);
        if (TextFind.Matches(Editor.Document.Text, find, matchCase, 1) is [var at]) SelectAt(at, find.Length);
    }

    /// <summary>Заменяет во всех заметках: сначала сохраняет открытые, потом правит файлы; открытые документы получают только отличающуюся часть.</summary>
    void ReplaceInVault(string find, string repl, bool matchCase)
    {
        if (_vault == null || find.Length == 0) return;
        SaveAll();
        var (changed, failed) = _vault.ReplaceAll(find, repl, matchCase);
        var notes = changed.Select(c => c.Note).ToHashSet();
        foreach (var tab in _tabs.Concat(_recent).Distinct())
        {
            if (!notes.Contains(tab.Note) || tab.Doc.Text == tab.Note.Text) continue;
            _reloading = true;
            ReplaceChanged(tab.Doc, tab.Note.Text);
            _reloading = false;
        }
        int n = changed.Sum(c => c.Count);
        _notice = n == 0 ? Loc.T("repl.nomatch")
            : Loc.T("repl.done", Loc.Plural(n, "pl.match"), Loc.Plural(changed.Count, "pl.note.in"))
            + (failed.Count > 0 ? Loc.T("repl.unsaved") + string.Join(", ", failed) : "");
        _side.Rebuild();
        UpdateNoteInfo(full: true);
    }

    void OpenFind(bool replace)
    {
        if (_tab != null) _find.Open(replace);
    }

    void SetReadMode(bool read)
    {
        read |= _tab?.Help == true; // справку править нельзя
        Preview.ReadMode = read;
        Editor.IsReadOnly = read;
        _find.SyncReadOnly();
        SetSegment(_segEdit, !read);
        SetSegment(_segRead, read);
        SetRailOn(_railRead, read);
        UpdateStatus();
    }

    /// <summary>Строка команды «Тема: …» — для темы, на которую переключит следующий щелчок.</summary>
    static string ThemeCommand(ThemeKind current) => Skin.Next(current) switch
    {
        ThemeKind.Terminal => "cmd.theme.terminal",
        ThemeKind.Light => "cmd.theme.light",
        _ => "cmd.theme.graphite",
    };

    void SetTheme(ThemeKind kind)
    {
        _settings["theme"] = Skin.Name(kind);
        ApplyPrefs();
        _settingsPanel.Refresh();
    }

    /// <summary>Применяет тему, шрифт, размер, ширину текста и автосохранение из настроек; тяжёлая перестройка — только если менялись вид или шрифт.</summary>
    void ApplyPrefs(bool force = false)
    {
        var kind = Skin.FromName(_settings["theme"]);
        var font = _settings.Font;
        double size = _settings.FontSize;
        if (force || kind != Skin.Kind || font != Skin.UserFont || size != Skin.UserSize)
        {
            Skin.SetText(font, size);
            Skin.Apply(kind);
            ApplyThemeLayout();
            UpdateNativeBackdrop();
        }
        ApplyTextWidth();
        if (_vault != null) _vault.AttachFolder = _settings.AttachFolder;
        _saveTimer.Interval = TimeSpan.FromMilliseconds(_settings.AutosaveMs > 0 ? _settings.AutosaveMs : Settings.DefaultAutosaveMs);
        if (_settings.AutosaveMs == 0) _saveTimer.Stop();
        _settings.Save();
    }

    void OpenSettings() => _settingsPanel.Open(_vault?.Root);

    /// <summary>Справка: Help.md из ресурсов exe открывается вкладкой только для чтения; в хранилище не попадает и на диск не пишется.</summary>
    void OpenManual()
    {
        if (_helpNote == null)
        {
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("Help.md");
            if (s == null) { _notice = Loc.T("manual.fail"); UpdateStatus(); return; }
            string text = new StreamReader(s, System.Text.Encoding.UTF8).ReadToEnd();
            _helpNote = new Note { Path = Path.Combine(AppContext.BaseDirectory, "Help.md"), Rel = "Help.md", Title = "Help", Text = text };
        }
        if (_vault == null) ShowWelcome(false);
        Open(_helpNote, newTab: true);
    }

    void OpenHelp() => _ = Launcher.LaunchUriAsync(new Uri(Program.HelpUrl));

    // ---------- сведения о заметке ----------

    void OnCaretMoved()
    {
        if (_tab == null) return;
        var caret = Editor.TextArea.Caret;
        _stPos.Text = Loc.T("st.pos", caret.Line, caret.Column);
        _right.SetCaretLine(caret.Line);
    }

    void UpdateCrumb()
    {
        if (_tab == null) { _crumb.Text = _vault?.Name ?? ""; return; }
        var n = _tab.Note;
        if (_tab.External)
        {
            _crumb.Text = n.Path;
            return;
        }
        _crumb.Text = Skin.IsTerminal
            ? $"~/{_vault?.Name}/{n.Rel}"
            : string.Join("  ›  ", n.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Append(n.Title));
    }

    int _words, _chars;

    /// <summary>Обратные ссылки (при смене заметки), структура, теги и счётчики (после паузы в наборе).</summary>
    void UpdateNoteInfo(bool full)
    {
        if (_tab == null)
        {
            _right.ShowBacklinks([]);
            _right.ShowOutline(new TextDocument(), Preview.Blocks);
            _words = _chars = 0;
            UpdateStatus();
            return;
        }
        if (full && _right.IsVisible)
            _right.ShowBacklinks(_vault != null && !_tab.External && !_tab.Help ? _vault.Backlinks(_tab.Note) : []);
        if (_right.IsVisible) { _right.ShowOutline(_tab.Doc, Preview.Blocks); _right.SetCaretLine(Editor.TextArea.Caret.Line); }
        string text = _tab.Doc.Text;
        _words = Markdown.CountWords(text);
        _chars = text.Length;
        UpdateCrumb();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        bool term = Skin.IsTerminal;
        _stMode.Text = Preview.ReadMode ? Loc.T("st.read") : Loc.T("st.edit");
        _stFile.Text = _tab == null ? "" : _tab.Note.Rel + (_tab.Dirty ? " [+]" : "");
        if (_tab == null) _stStats.Text = "";
        else if (term) _stStats.Text = Loc.Plural(_words, "pl.word");
        else
            _stStats.Text = $"{Loc.Plural(_words, "pl.word")} · {Loc.Plural(_chars, "pl.char")}"
                + (_right.IsVisible ? $" · {Loc.Plural(_right.BacklinkCount, "pl.backlink")}" : "");
        if (term && _notice != null) _stFile.Text = _notice;
        _stSaved.Text = _saveError != null ? Loc.T("st.save.fail") + _saveError
            : _notice != null ? _notice
            : _tab?.Dirty == true ? Loc.T("st.unsaved")
            : _savedAt is { } t ? Loc.T("st.saved", t) : "";
        ToolTip.SetTip(_stSaved, _saveError);
        OnCaretMoved();
    }

    // ---------- палитра и клавиши ----------

    /// <summary>Что делает каждое сочетание; ключи — <see cref="Keymap.Actions"/>.</summary>
    Dictionary<string, Action> KeyHandlers() => _keyHandlers ??= new()
    {
        ["palette"] = ShowCommands,
        ["open"] = QuickOpen,
        ["new"] = PromptNewNote,
        ["search"] = FocusSearch,
        ["find"] = () => OpenFind(false),
        ["replace"] = () => OpenFind(true),
        ["findnext"] = () => { if (_tab != null) _find.Step(true); },
        ["findprev"] = () => { if (_tab != null) _find.Step(false); },
        ["replaceall"] = () => PromptReplaceAll(),
        ["read"] = () => SetReadMode(!Preview.ReadMode),
        ["folder"] = () => _ = PickFolder(),
        ["rename"] = () => { if (_tab != null && !_tab.External && !_tab.Help) PromptRename(_tab.Note); },
        ["export"] = PromptExport,
        ["save"] = SaveAll,
        ["close"] = () => { if (_tab != null) CloseTab(_tab); },
        ["next"] = () => CycleTab(1),
        ["prev"] = () => CycleTab(-1),
        ["back"] = GoBack,
        ["forward"] = GoForward,
        ["settings"] = OpenSettings,
        ["keys"] = ShowKeys,
        ["sidebar"] = ToggleSidebar,
        ["right"] = ToggleRight,
        ["theme"] = () => SetTheme(Skin.Next(Skin.Kind)),
    };
    Dictionary<string, Action>? _keyHandlers;

    string? K(string id) => _keys.Hint(id) is { Length: > 0 } h ? h : null;

    List<PaletteItem> Commands()
    {
        List<PaletteItem> all =
        [
            new(Loc.T("cmd.open"), K("open"), QuickOpen),
            new(Loc.T("cmd.new"), K("new"), PromptNewNote),
            new(Loc.T("cmd.search"), K("search"), FocusSearch),
            new(Loc.T("cmd.replaceall"), K("replaceall"), () => PromptReplaceAll(), Loc.T("cmd.replaceall.sub")),
            new(Loc.T("cmd.toggleread"), K("read"), () => SetReadMode(!Preview.ReadMode)),
            new(Loc.T("cmd.openfile"), null, () => _ = PickFile()),
            new(Loc.T("cmd.folder"), K("folder"), () => _ = PickFolder()),
            new(Loc.T(ThemeCommand(Skin.Kind)), K("theme"), () => SetTheme(Skin.Next(Skin.Kind))),
            new(_side.IsVisible ? Loc.T("cmd.side.hide") : Loc.T("cmd.side.show"), K("sidebar"), ToggleSidebar),
            new(_right.IsVisible ? Loc.T("cmd.right.hide") : Loc.T("cmd.right.show"), K("right"), ToggleRight),
            new(Loc.T("common.back"), K("back"), GoBack),
            new(Loc.T("common.forward"), K("forward"), GoForward),
            new(Loc.T("close.tab"), K("close"), () => { if (_tab != null) CloseTab(_tab); }),
            new(Loc.T("common.save"), K("save"), SaveAll),
            new(Loc.T("cmd.export"), K("export"), PromptExport, Loc.T("cmd.export.sub")),
            new(Loc.T("cmd.settings"), K("settings"), OpenSettings),
            new(Loc.T("cmd.keys"), K("keys"), ShowKeys),
            new(Loc.T("cmd.lang"), null, () => SetLanguage(Loc.Other)),
            .. (FileAssoc.IsRegistered() && !FileAssoc.IsDefault()
                ? new PaletteItem[] { new(Loc.T("cmd.setdefault"), null, () => FileAssoc.OpenDefaultSettings(), Loc.T("cmd.setdefault.sub")) }
                : []),
            FileAssoc.IsRegistered()
                ? new(Loc.T("cmd.unregister"), null, () => Association(register: false), Loc.T("cmd.assoc.sub"))
                : new(Loc.T("cmd.register"), null, () => Association(register: true), Loc.T("cmd.assoc.sub")),
            new(Loc.T("cmd.manual"), null, OpenManual, Loc.T("cmd.manual.sub")),
            new(Loc.T("cmd.help"), null, OpenHelp, "dalink.to/olegche"),
        ];
        if (_pendingDrafts.Count > 0)
            all.Insert(0, new(Loc.T("cmd.recover"), null, ShowRecovery, Loc.Plural(_pendingDrafts.Count, "pl.draft")));
        if (_tab?.Note is { } note)
        {
            bool inVault = _vault != null && !_tab.External && !_tab.Help;
            all.InsertRange(2,
            [
                .. (inVault && HistoryOn ? new PaletteItem[] { new(Loc.T("cmd.history"), null, ShowHistory, Loc.T("cmd.history.sub")) } : []),
                new(Loc.T("cmd.find"), K("find"), () => OpenFind(false)),
                new(Loc.T("cmd.replace"), K("replace"), () => OpenFind(true)),
                .. (inVault ? new PaletteItem[]
                {
                    new(Loc.T("cmd.rename"), K("rename"), () => PromptRename(note)),
                    new(Loc.T("cmd.move"), null, () => PromptMove(note)),
                    new(Loc.T("cmd.delete"), null, () => PromptDelete(note)),
                } : []),
                new(Loc.T("cmd.export.html"), null, () => _ = PromptExport(note, false)),
                new(Loc.T("cmd.export.pdf"), null, () => _ = PromptExport(note, true)),
            ]);
        }
        if (AttachmentAtCaret() is { } file)
            all.Insert(2, new(Loc.T("cmd.rename.attach"), null, () => PromptRenameAttachment(file), Path.GetFileName(file)));
        if (_tab != null && Preview.Blocks.IsTable(Editor.TextArea.Caret.Line))
            all.Insert(2, new(Loc.T("cmd.align"), "Tab", () => _mdKeys.AlignTable()));
        all.Insert(all.FindIndex(i => i.Title == Loc.T("cmd.settings")) + 1, new(Loc.T("cmd.attachfolder"), null, _settingsPanel.ChooseAttach, _settings.AttachFolder));
        return all;
    }

    void ShowCommands()
    {
        var all = Commands();
        _palette.Open(">", Loc.T("cmd.ph"), q => all
            .Select(i => (i, s: CommandPalette.Score(i.Title, q.Trim())))
            .Where(x => x.s >= 0).OrderBy(x => x.s).Select(x => x.i).ToList());
    }

    void QuickOpen()
    {
        if (_vault == null) { _ = PickFolder(); return; }
        var recent = _back.AsEnumerable().Reverse().Concat(_tabs.Select(t => t.Note)).Distinct().ToList();
        _palette.Open("›", Loc.T("cmd.ph.note"), q =>
        {
            q = q.Trim();
            IEnumerable<Note> found = q.Length == 0
                ? recent.Concat(_vault.Notes.OrderByDescending(n => n.Modified)).Distinct()
                : _vault.Notes.Select(n => (n, s: CommandPalette.Score(n.Title, q)))
                    .Where(x => x.s >= 0).OrderBy(x => x.s).ThenBy(x => x.n.Title.Length).Select(x => x.n);
            var items = found.Take(50)
                .Select(n => new PaletteItem(n.Title, null, () => Open(n), n.Folder.Length > 0 ? n.Folder : null)).ToList();
            if (q.Length > 0 && !_vault.Notes.Any(n => n.Title.Equals(q, StringComparison.OrdinalIgnoreCase)))
                items.Add(new PaletteItem(Loc.T("create.named", q), items.Count == 0 ? "Enter" : null, () =>
                {
                    var note = _vault.Create(q);
                    _side.Rebuild();
                    Open(note);
                }));
            return items;
        });
    }

    /// <summary>Справка по клавишам: все команды с их сочетаниями; Enter выполняет команду. Поиск идёт и по названию, и по сочетанию.</summary>
    void ShowKeys() => _palette.Open("?", Loc.T("cmd.ph.keys"), KeyHelp());

    Func<string, List<PaletteItem>> KeyHelp()
    {
        var items = Keymap.Actions.Select(a =>
        {
            string? hint = K(a.Id);
            return new PaletteItem(a.Title, hint, () => KeyHandlers()[a.Id](), hint == null ? Loc.T("cmd.unassigned") : null);
        }).ToList();
        items.AddRange(Keymap.Fixed.Select(f => new PaletteItem(f.Title, f.Keys, () => { })));
        items.Add(new PaletteItem(Loc.T("cmd.changekeys"), null, OpenSettings));
        static int Rank(PaletteItem i, string q)
        {
            int byTitle = CommandPalette.Score(i.Title, q), byKeys = i.Hint == null ? -1 : CommandPalette.Score(i.Hint, q);
            if (byKeys >= 0) byKeys += 200;
            return byTitle < 0 ? byKeys : byKeys < 0 ? byTitle : Math.Min(byTitle, byKeys);
        }
        return q =>
        {
            q = q.Trim();
            if (q.Length == 0) return items;
            return items.Select(i => (i, s: Rank(i, q))).Where(x => x.s >= 0).OrderBy(x => x.s).Select(x => x.i).ToList();
        };
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_settingsPanel.Capturing) { _settingsPanel.Capture(e); e.Handled = true; return; }
        if (_palette.IsOpen) return;
        if (_historyPanel.IsOpen)
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { _historyPanel.Close(); e.Handled = true; }
            return;
        }
        if (_settingsPanel.IsOpen && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { _settingsPanel.Close(); e.Handled = true; return; }
        if (_complete.OnKey(e)) return;
        if (_mdKeys.OnKey(e)) return;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _find.IsOpen && !_side.IsKeyboardFocusWithin)
        {
            _find.Close();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.D1 and <= Key.D9)
        {
            int i = e.Key - Key.D1;
            if (i < _tabs.Count) Activate(_tabs[i]);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control || e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift)
            && Editor.TextArea.IsKeyboardFocusWithin && TryPasteAttachment())
        {
            e.Handled = true;
            return;
        }
        if (_keys.Match(e) is not { } id) return;
        // F2 в списке заметок переименовывает строку списка, а не открытую заметку.
        if (id == "rename" && (_tab == null || _side.IsKeyboardFocusWithin)) return;
        KeyHandlers()[id]();
        e.Handled = true;
    }
}
