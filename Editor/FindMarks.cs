using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Leaflet.Editor;

/// <summary>Подсветка совпадений поиска в заметке: все совпадения полупрозрачным цветом акцента, текущее — плотнее и с рамкой.</summary>
public sealed class FindMarks : IBackgroundRenderer
{
    static readonly Pen CurrentPen = new(Skin.Accent, 1);
    List<int> _starts = [];
    int _length, _current = -1;

    public KnownLayer Layer => KnownLayer.Selection;

    public int Count => _starts.Count;

    public void Set(List<int> starts, int length, int current)
    {
        _starts = starts;
        _length = length;
        _current = current;
    }

    public void Clear() => Set([], 0, -1);

    public void Draw(TextView view, DrawingContext dc)
    {
        if (_starts.Count == 0 || _length == 0 || !view.VisualLinesValid || view.VisualLines.Count == 0) return;
        int from = view.VisualLines[0].FirstDocumentLine.Offset, to = view.VisualLines[^1].LastDocumentLine.EndOffset;
        int i = _starts.BinarySearch(from - _length + 1);
        if (i < 0) i = ~i;
        for (; i < _starts.Count && _starts[i] <= to; i++)
        {
            var builder = new BackgroundGeometryBuilder { CornerRadius = 2 };
            builder.AddSegment(view, new TextSegment { StartOffset = _starts[i], Length = _length });
            var geometry = builder.CreateGeometry();
            if (geometry == null) continue;
            bool now = i == _current;
            dc.DrawGeometry(now ? Skin.FoundNow : Skin.Found, now ? CurrentPen : null, geometry);
        }
    }
}
