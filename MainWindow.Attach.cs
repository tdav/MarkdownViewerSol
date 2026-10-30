using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Leaflet.Core;
using Leaflet.Editor;
using Leaflet.UI;

namespace Leaflet;

/// <summary>Вложения: вставка картинки из буфера, файлы из проводника (Ctrl+V и перетаскивание), переименование вложения.</summary>
public sealed partial class MainWindow
{
    /// <summary>Файл вставки из открытой заметки (см. <see cref="Vault.ResolveFile"/>).</summary>
    string? ResolveEmbed(string src, bool wiki) =>
        _vault != null && _tab != null ? _vault.ResolveFile(src, _tab.Note, wiki) : null;

    /// <summary>Встроенная заметка ![[Имя#раздел]] из открытой заметки (адреса ссылок — как для экспорта в .md, нужны только для вида).</summary>
    Block? EmbedNote(string target) =>
        _vault != null && _tab != null ? Export.Embedder(_vault, _tab.Note, ".md")(target) : null;

    void SetupDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        // События перетаскивания только всплывают: ловим и обработанные редактором (он сам принимает лишь перетаскивание текста).
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(DataFormat.File)) return;
            e.DragEffects = _tab != null && !Editor.IsReadOnly ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(DataFormat.File)) return;
            e.Handled = true;
            var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
            if (files.Count == 0 || _tab == null || Editor.IsReadOnly) return;
            // Брошено на текст — туда, иначе — к каретке.
            int offset = Editor.TextArea.Caret.Offset;
            var at = e.GetPosition(Editor);
            if (new Avalonia.Rect(Editor.Bounds.Size).Contains(at) && Editor.GetPositionFromPoint(at) is { } pos)
                offset = Editor.Document.GetOffset(pos.Location);
            AttachFiles(files, offset);
            Editor.TextArea.Focus();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Ctrl+V / Shift+Insert в редакторе: если в буфере нет текста, но есть файлы или картинка — они становятся вложениями.
    /// Текст (даже вместе с картинкой, как из Word или Excel) вставляет сам редактор.
    /// </summary>
    bool TryPasteAttachment()
    {
        if (_tab == null || _vault == null || Editor.IsReadOnly || WinClipboard.HasText) return false;
        if (WinClipboard.HasFiles)
        {
            var files = WinClipboard.Files().Where(File.Exists).ToList();
            if (files.Count == 0) return false;
            AttachFiles(files, Editor.TextArea.Caret.Offset);
            return true;
        }
        if (!WinClipboard.HasImage) return false;
        var png = WinClipboard.ImagePng();
        if (png == null) { _notice = Loc.T("attach.clip.fail"); UpdateStatus(); return true; }
        try
        {
            string path = _vault.AddAttachment(_tab.Note, png, Loc.T("attach.shot", DateTime.Now));
            InsertLinks([path], Editor.TextArea.Caret.Offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = Loc.T("attach.save.fail") + ex.Message;
            UpdateStatus();
        }
        return true;
    }

    /// <summary>Файлы снаружи копируются в папку вложений, из хранилища — только ссылка (заметка .md — [[ссылкой]]).</summary>
    public void AttachFiles(IReadOnlyList<string> files, int offset)
    {
        if (_tab == null || _vault == null) return;
        var paths = new List<string>();
        var failed = new List<string>();
        foreach (var f in files)
        {
            if (Directory.Exists(f)) { failed.Add(Path.GetFileName(f) + Loc.T("attach.folder")); continue; }
            try { paths.Add(_vault.AddAttachment(_tab.Note, f)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(Path.GetFileName(f)); }
        }
        if (paths.Count > 0) InsertLinks(paths, offset);
        if (failed.Count > 0) { _notice = Loc.T("attach.fail") + string.Join(", ", failed); UpdateStatus(); }
    }

    /// <summary>Ссылки на файлы, каждая своей строкой: картинка — ![](путь), заметка — [[имя]], остальное — [имя](путь). Один шаг отмены.</summary>
    void InsertLinks(List<string> paths, int offset)
    {
        var note = _tab!.Note;
        var doc = Editor.Document;
        var links = paths.Select(p =>
        {
            if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && _vault!.Get(p) is { } linked)
                return $"[[{(_vault.Resolve(linked.Title, note) == linked ? linked.Title : linked.Rel[..^3])}]]";
            string rel = Vault.LinkPath(note, p);
            return Vault.IsImage(p) ? $"![]({rel})" : $"[{Path.GetFileName(p).Replace("[", "(").Replace("]", ")")}]({rel})";
        });
        offset = Math.Clamp(offset, 0, doc.TextLength);
        string text = string.Join("\n", links);
        bool before = offset > 0 && doc.GetCharAt(offset - 1) != '\n';
        bool after = offset < doc.TextLength && doc.GetCharAt(offset) is not ('\n' or '\r');
        doc.Insert(offset, (before ? "\n" : "") + text + (after ? "\n" : ""));
        Editor.CaretOffset = offset + (before ? 1 : 0) + text.Length;
        _notice = paths.Count == 1
            ? Loc.T("attach.one") + Path.GetRelativePath(_vault!.Root, paths[0]).Replace('\\', '/')
            : Loc.T("attach.many", paths.Count, Path.GetRelativePath(_vault!.Root, Path.GetDirectoryName(paths[0])!).Replace('\\', '/'));
        UpdateStatus();
    }

    /// <summary>Вложение на строке с кареткой: первая ![](…), [](…) или [[файл.ext]], чей файл есть (кроме заметок).</summary>
    string? AttachmentAtCaret()
    {
        if (_tab == null || _vault == null) return null;
        var line = Editor.Document.GetLineByNumber(Editor.TextArea.Caret.Line);
        string text = Editor.Document.GetText(line);
        foreach (var m in Markdown.Embeds(text))
            if (Markdown.EmbedSource(m) is var (src, wiki) && _vault.ResolveFile(src, _tab.Note, wiki) is { } p) return p;
        foreach (System.Text.RegularExpressions.Match m in Markdown.Link.Matches(text))
            if (_vault.ResolveFile(Markdown.Unescape(m.Groups[2].Value), _tab.Note, false) is { } p && !p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return p;
        foreach (System.Text.RegularExpressions.Match m in Markdown.WikiLink.Matches(text))
            if (_vault.ResolveFile(m.Groups[1].Value, _tab.Note, true) is { } p) return p;
        return null;
    }

    void PromptRenameAttachment(string file)
    {
        if (_vault == null) return;
        string dir = Path.GetDirectoryName(file)!, ext = Path.GetExtension(file);
        _palette.Open("✎", Loc.T("attach.rename.ph"), q =>
        {
            string name = q.Trim();
            if (name.Length == 0) return [new PaletteItem(Loc.T("hint.enter"), null, () => { })];
            if (!Path.HasExtension(name)) name += ext;
            string target = Path.Combine(dir, name);
            if (target == file) return [new PaletteItem(Loc.T("name.same"), null, () => { })];
            if (!target.Equals(file, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                return [new PaletteItem(Loc.T("attach.exists", name), null, () => { })];
            return [new PaletteItem(Loc.T("attach.rename.to", name), "Enter", () => RenameAttachment(file, name), Loc.T("attach.rename.sub"))];
        }, Path.GetFileNameWithoutExtension(file));
    }

    public void RenameAttachment(string file, string name)
    {
        if (_vault == null) return;
        SaveAll();
        try
        {
            var (path, changed) = _vault.MoveAttachment(file, name);
            foreach (var tab in _tabs.Concat(_recent).Distinct())
            {
                if (!changed.Contains(tab.Note) || tab.Doc.Text == tab.Note.Text) continue;
                _reloading = true;
                ReplaceChanged(tab.Doc, tab.Note.Text);
                _reloading = false;
            }
            Images.FilesChanged([file, path]);
            _notice = $"{Path.GetFileName(file)} → {Path.GetFileName(path)}"
                + (changed.Count == 0 ? "" : Loc.T("links.updated") + Loc.Plural(changed.Count, "pl.note.in"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = Loc.T("common.fail") + ex.Message;
        }
        UpdateStatus();
    }

    /// <summary>Вложение или локальный файл по ссылке — в программе Windows по умолчанию. false — это не файл.</summary>
    bool OpenFileLink(string target, bool wiki)
    {
        if (_tab == null || _vault == null) return false;
        bool embed = target.StartsWith('!');
        if (embed) target = target[1..];
        // ![[Заметка]] — открыть саму заметку (Ctrl+щелчок по разметке).
        if (embed && wiki && Markdown.IsNoteEmbed(target)) { OpenLink(target, wiki: true); return true; }
        else if (wiki || target.Contains("://") || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return false;
        int hash = embed ? -1 : target.IndexOf('#');
        string src = embed && wiki ? target : Markdown.Unescape(hash < 0 ? target : target[..hash]);
        if (src.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        if (_vault.ResolveFile(src, _tab.Note, wiki) is not { } file) return embed;
        _ = Launcher.LaunchFileInfoAsync(new FileInfo(file));
        return true;
    }
}
