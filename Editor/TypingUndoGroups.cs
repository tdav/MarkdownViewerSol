using System.Diagnostics;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Leaflet.Editor;

/// <summary>
/// Склеивает набор подряд в один шаг отмены: слово вместе с пробелами после него.
/// Новый шаг начинается после паузы, перевода строки, перемещения курсора или с началом нового слова.
/// Без этого AvaloniaEdit отменяет по одному символу.
/// </summary>
public sealed class TypingUndoGroups
{
    static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.5);
    readonly TextArea _area;
    int _nextOffset = -1;
    long _lastTicks;
    bool _afterSpace, _open;

    public TypingUndoGroups(TextEditor editor)
    {
        _area = editor.TextArea;
        _area.TextEntering += OnEntering;
        _area.TextEntered += OnEntered;
        // Правка не набором (щелчок по задаче, Tab в таблице) — свой шаг: следующий набор к нему не приклеивается.
        editor.DocumentChanged += (_, e) =>
        {
            if (e.OldDocument != null) e.OldDocument.Changing -= OnChanging;
            if (e.NewDocument != null) e.NewDocument.Changing += OnChanging;
        };
        if (editor.Document != null) editor.Document.Changing += OnChanging;
    }

    void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (!_open) _nextOffset = -1;
    }

    void OnEntering(object? sender, TextInputEventArgs e)
    {
        var undo = _area.Document.UndoStack;
        if (_open) { undo.EndUndoGroup(); _open = false; }

        string text = e.Text ?? "";
        bool newline = text.Contains('\n') || text.Contains('\r');
        bool space = text.Length > 0 && text.All(char.IsWhiteSpace);
        bool continues = !newline
            && _area.Selection.IsEmpty
            && _area.Caret.Offset == _nextOffset
            && Stopwatch.GetElapsedTime(_lastTicks) < Pause
            && !(_afterSpace && !space);

        if (continues) undo.StartContinuedUndoGroup();
        else undo.StartUndoGroup();
        _open = true;
        _afterSpace = space || newline;
    }

    void OnEntered(object? sender, TextInputEventArgs e)
    {
        if (!_open) return;
        _area.Document.UndoStack.EndUndoGroup();
        _open = false;
        _nextOffset = _area.Caret.Offset;
        _lastTicks = Stopwatch.GetTimestamp();
    }
}
