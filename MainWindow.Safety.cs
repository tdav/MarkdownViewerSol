using System.Diagnostics;
using Avalonia.Threading;
using Leaflet.Core;
using Leaflet.UI;

namespace Leaflet;

/// <summary>
/// Надёжность данных: правки файла снаружи (тихая перезагрузка или выбор при конфликте), пропавший файл,
/// черновики несохранённых правок и восстановление после сбоя, снимки версий и «История версий…».
/// </summary>
public sealed partial class MainWindow
{
    const int DraftDelayMs = 1500;
    readonly bool _crashTest; // --crashtest: набрать текст, дождаться черновика и упасть (самопроверка)
    History? _history;
    Drafts? _drafts;
    DispatcherTimer _draftTimer = null!;
    List<Draft> _pendingDrafts = [];

    /// <summary>Снимки версий пишутся только для настоящего хранилища (в замерах оно только для чтения).</summary>
    bool HistoryOn => _history != null && _vault is { ReadOnly: false };

    void InitSafety(string? dataDir, bool clean)
    {
        _draftTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DraftDelayMs) };
        _draftTimer.Tick += (_, _) => { _draftTimer.Stop(); WriteDrafts(); };
        if (dataDir == null) return;
        if (clean && Directory.Exists(dataDir))
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        _history = new History(Path.Combine(dataDir, "history"));
        _drafts = new Drafts(Path.Combine(dataDir, "drafts"));
    }

    // ---------- правки снаружи ----------

    void OnExternalChange(Vault vault, string[] paths)
    {
        if (vault != _vault) return;
        var ch = vault.Refresh(paths);
        Images.FilesChanged(paths);
        if (ch.Changed.Count > 0 || ch.ListChanged) Images.NotesChanged();
        var open = _tabs.Concat(_recent).Distinct().ToList();
        bool tabsChanged = false;
        foreach (var (note, oldRel) in ch.Renamed)
        {
            if (HistoryOn) _history!.Rename(vault.Root, oldRel, note.Rel);
            if (open.FirstOrDefault(t => t.Note == note) is not { } tab) continue;
            if (tab.DraftText != null) { _drafts?.Delete(Path.Combine(vault.Root, oldRel)); tab.DraftText = null; RestartDraftTimer(); }
            if (tab == _tab) _settings["last:" + vault.Root] = note.Rel;
            _notice = Loc.T("ext.renamed", oldRel, note.Rel);
            tabsChanged = true;
        }
        foreach (var tab in open)
        {
            if (ch.Removed.Contains(tab.Note))
            {
                tab.Missing = true;
                tab.Conflict = false;
                tabsChanged = true;
                RestartDraftTimer();
            }
            else if (tab.Missing && vault.Adopt(tab.Note)) // файл появился снова
            {
                tab.Missing = false;
                Reconcile(tab);
                tabsChanged = true;
            }
            else if (ch.Changed.Contains(tab.Note))
            {
                Reconcile(tab);
                tabsChanged |= tab.Conflict;
            }
        }
        if (ch.ListChanged) _side.Rebuild();
        if (tabsChanged && _tab != null) ShowNote(_tab);
        else if (ch.ListChanged || ch.Changed.Count > 0) UpdateNoteInfo(full: true);
    }

    /// <summary>Текст файла на диске (<c>Note.Text</c>) сменился: вкладка без правок перечитывается, с правками — ждёт решения.</summary>
    void Reconcile(Tab tab)
    {
        string disk = tab.Note.Text;
        if (tab.Doc.Text == disk) { tab.Dirty = false; tab.Conflict = false; DropDraft(tab); return; }
        if (!tab.Dirty) { Reload(tab, disk); return; }
        tab.Conflict = true;
        RestartDraftTimer();
    }

    /// <summary>Подменяет текст вкладки без отметки «изменено»: меняется только отличающаяся середина, каретка и отмена целы.</summary>
    void Reload(Tab tab, string text)
    {
        _reloading = true;
        ReplaceChanged(tab.Doc, text);
        _reloading = false;
        tab.Dirty = false;
        tab.Conflict = false;
    }

    void UpdateBanner()
    {
        var tab = _tab;
        if (tab == null || (!tab.Conflict && !tab.Missing)) { _banner.Hide(); return; }
        if (tab.Missing)
            _banner.Show(Loc.T("missing.msg", tab.Note.Rel),
                (Loc.T("missing.resave"), () => Resave(tab)), (Loc.T("close.tab"), () => CloseTab(tab)));
        else
            _banner.Show(Loc.T("conflict.msg"),
                (Loc.T("conflict.keepmine"), () => KeepMine(tab)), (Loc.T("conflict.takedisk"), () => TakeDisk(tab)),
                (Loc.T("conflict.copy"), () => SaveCopy(tab)), (Loc.T("conflict.compare"), () => CompareDisk(tab)));
    }

    /// <summary>Пишет свою версию поверх файла; версия с диска уходит в историю.</summary>
    void KeepMine(Tab tab)
    {
        if (_vault == null || !tab.Conflict) return;
        if (HistoryOn) _history!.Add(_vault.Root, tab.Note.Rel, tab.Note.Text, "disk");
        tab.Conflict = false;
        tab.Dirty = true;
        SaveTab(tab);
        _notice = tab.Dirty ? null : Loc.T("conflict.kept");
        AfterResolve(tab);
    }

    /// <summary>Берёт версию с диска; своя уходит в историю, Ctrl+Z тоже вернёт её.</summary>
    void TakeDisk(Tab tab, bool keepMine = true)
    {
        if (_vault == null) return;
        if (keepMine && HistoryOn) _history!.Add(_vault.Root, tab.Note.Rel, tab.Doc.Text, "mine");
        Reload(tab, tab.Note.Text);
        DropDraft(tab);
        if (keepMine) _notice = Loc.T("conflict.taken");
        AfterResolve(tab);
    }

    /// <summary>Своя версия — новой заметкой «Имя (моя версия)» рядом, во вкладке — версия с диска.</summary>
    void SaveCopy(Tab tab)
    {
        if (_vault == null) return;
        try
        {
            var copy = _vault.Create(Loc.T("conflict.copyname", tab.Note.Title), tab.Note.Folder);
            _vault.Save(copy, tab.Doc.Text);
            TakeDisk(tab, keepMine: false);
            _notice = Loc.T("conflict.copied", copy.Title);
            _side.Rebuild();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = Loc.T("conflict.copy.fail") + ex.Message;
        }
        AfterResolve(tab);
    }

    void CompareDisk(Tab tab)
    {
        _historyPanel.Open(Loc.T("conflict.diff.title", tab.Note.Title),
            [new VersionItem(Loc.T("conflict.ondisk"), Loc.T("conflict.modified", tab.Note.Modified), () => tab.Note.Text)],
            () => tab.Doc.Text, Loc.T("conflict.diff.hint"), Loc.T("conflict.takedisk"),
            (_, _) => TakeDisk(tab));
    }

    /// <summary>Пропавший файл: записать текст вкладки по прежнему пути.</summary>
    void Resave(Tab tab)
    {
        if (_vault == null) return;
        try
        {
            _vault.Readd(tab.Note, tab.Doc.Text);
            tab.Missing = false;
            tab.Dirty = false;
            DropDraft(tab);
            _notice = Loc.T("missing.resaved", tab.Note.Rel);
            _side.Rebuild();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = Loc.T("missing.fail") + ex.Message;
        }
        AfterResolve(tab);
    }

    void AfterResolve(Tab tab)
    {
        if (tab == _tab) { UpdateBanner(); UpdateNoteInfo(full: true); }
        RebuildTabs();
    }

    /// <summary>Вкладка закрыта: правленный в этом сеансе текст — снимком в историю (у конфликта — как «ваша версия»).</summary>
    void OnTabClosed(Tab tab)
    {
        if (HistoryOn && (tab.Edited || tab.Missing))
            _history!.Add(_vault!.Root, tab.Note.Rel, tab.Doc.Text, tab.Conflict ? "mine" : "close");
    }

    // ---------- черновики ----------

    void RestartDraftTimer()
    {
        if (_drafts == null) return;
        _draftTimer.Stop();
        _draftTimer.Start();
    }

    /// <summary>Черновик каждой несохранённой вкладки, если текст изменился с прошлого черновика (запись в фоне).</summary>
    void WriteDrafts()
    {
        if (_drafts == null || _vault is not { ReadOnly: false } vault) return;
        foreach (var tab in _tabs.Concat(_recent).Distinct())
        {
            if (!tab.Dirty && !tab.Missing) continue;
            string text = tab.Doc.Text;
            if (text == tab.DraftText) continue;
            _drafts.Write(vault.Root, tab.Note.Path, text, tab.Note.Text);
            tab.DraftText = text;
        }
    }

    void DropDraft(Tab tab)
    {
        if (tab.DraftText == null) return;
        _drafts?.Delete(tab.Note.Path);
        tab.DraftText = null;
    }

    /// <summary>Закрытие окна (после <see cref="SaveAll"/>): снимки правленных заметок, черновики того, что не записалось.</summary>
    void KeepUnsaved()
    {
        _draftTimer.Stop();
        if (_vault is not { ReadOnly: false } vault) return;
        foreach (var tab in _tabs.Concat(_recent).Distinct())
            if (HistoryOn && (tab.Edited || tab.Missing))
                _history!.Add(vault.Root, tab.Note.Rel, tab.Doc.Text, tab.Conflict ? "mine" : "close");
        WriteDrafts();
        _history?.Flush();
        _drafts?.Flush();
    }

    /// <summary>После загрузки хранилища: черновики от упавшего (или закрытого без записи) окна — предложить восстановить.</summary>
    void OfferRecovery()
    {
        if (_drafts == null || _bench || _crashTest || _vault is not { ReadOnly: false } vault) return;
        _pendingDrafts = _drafts.Pending(vault.Root);
        if (_pendingDrafts.Count > 0) ShowRecovery();
    }

    void ShowRecovery()
    {
        if (_vault == null || _pendingDrafts.Count == 0) return;
        var drafts = _pendingDrafts.ToList();
        var items = new List<PaletteItem>
        {
            new(drafts.Count == 1 ? Loc.T("draft.restore") : Loc.T("draft.restore.all", drafts.Count), "Enter", () => RestoreDrafts(drafts), Loc.T("draft.restore.sub")),
        };
        foreach (var d in drafts)
        {
            var note = _vault.Get(d.Path);
            string state = note == null ? Loc.T("draft.nofile") : Drafts.Hash(note.Text) != d.BaseHash ? Loc.T("draft.filechanged") : "";
            items.Add(new($"«{Path.GetFileNameWithoutExtension(d.Path)}»", null, () => RestoreDrafts([d]), $"{When(d.Time)}{state}"));
        }
        items.Add(new(Loc.T("draft.discard"), null, () => DiscardDrafts(drafts), Loc.T("draft.discard.sub")));
        items.Add(new(Loc.T("draft.later"), "Esc", () => { }, Loc.T("draft.later.sub")));
        _palette.Open("⟲", Loc.T("draft.ph"), _ => items);
    }

    void RestoreDrafts(List<Draft> drafts)
    {
        if (_vault == null) return;
        int n = 0;
        foreach (var d in drafts)
        {
            try
            {
                var note = _vault.Get(d.Path) ?? _vault.Recreate(d.Path);
                if (note.Text != d.Text)
                {
                    if (HistoryOn && note.Text.Length > 0) _history!.Add(_vault.Root, note.Rel, note.Text, "draft");
                    Open(note, newTab: _tab != null);
                    if (_tab?.Note == note) ReplaceChanged(_tab.Doc, d.Text); // как правка: «изменено», автосохранение, отмена
                }
                Drafts.Discard(d);
                _pendingDrafts.Remove(d);
                n++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _notice = Loc.T("draft.fail") + ex.Message;
            }
        }
        if (n > 0) _notice = Loc.T("draft.recovered") + Loc.Plural(n, "pl.draft");
        _side.Rebuild();
        UpdateStatus();
    }

    void DiscardDrafts(List<Draft> drafts)
    {
        foreach (var d in drafts) { Drafts.Discard(d); _pendingDrafts.Remove(d); }
        _notice = Loc.T("draft.discarded");
        UpdateStatus();
    }

    // ---------- история версий ----------

    void ShowHistory()
    {
        if (_tab == null || !HistoryOn) return;
        var tab = _tab;
        var items = _history!.List(_vault!.Root, tab.Note.Rel)
            .Select(s => new VersionItem(When(s.Time), $"{s.ReasonText} · {SizeText(s.Size)}", () => History.Read(s))).ToList();
        _historyPanel.Open(Loc.T("hist.title", tab.Note.Title), items, () => tab.Doc.Text,
            Loc.T("hist.hint"), Loc.T("hist.revert"), (item, text) => RestoreVersion(tab, text, item.Title));
    }

    /// <summary>Возвращает версию как правку (отменяется Ctrl+Z); текущий текст перед этим — снимком.</summary>
    void RestoreVersion(Tab tab, string text, string when)
    {
        if (_vault == null) return;
        if (HistoryOn) _history!.Add(_vault.Root, tab.Note.Rel, tab.Doc.Text, "restore");
        if (_tab != tab) Activate(tab);
        ReplaceChanged(tab.Doc, text);
        _notice = Loc.T("hist.reverted", when);
        UpdateStatus();
    }

    static string When(DateTime t) =>
        t.Date == DateTime.Today ? Loc.T("hist.today", t)
        : t.Date == DateTime.Today.AddDays(-1) ? Loc.T("hist.yesterday", t)
        : t.Year == DateTime.Today.Year ? $"{t:dd.MM}, {t:HH:mm}" : $"{t:dd.MM.yyyy}, {t:HH:mm}";

    static string SizeText(long bytes) =>
        bytes < 1024 ? Loc.T("unit.b", bytes) : Loc.Dec((bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)) + Loc.T("unit.kb");

    // ---------- имитация сбоя для самопроверки ----------

    /// <summary>--crashtest: правка без автосохранения, ожидание черновика и немедленное завершение процесса без закрытия окна.</summary>
    async Task CrashTest()
    {
        for (int i = 0; i < 100 && (_vault?.Loaded != true || _tab == null); i++) await Task.Delay(100);
        if (_tab != null && _drafts != null)
        {
            _settings.AutosaveMs = 0;
            ApplyPrefs();
            Editor.Document.Insert(Editor.Document.TextLength, "\nТекст до сбоя\n");
            string file = _drafts.FileFor(_tab.Note.Path);
            for (int i = 0; i < 100 && !File.Exists(file); i++) await Task.Delay(100);
            _drafts.Flush();
        }
        Process.GetCurrentProcess().Kill();
    }
}
