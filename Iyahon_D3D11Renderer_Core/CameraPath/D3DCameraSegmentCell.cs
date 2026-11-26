using System.Numerics;
using System.Windows;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.CameraPath;

internal sealed class D3DCameraSegmentCell : Bindable
{
    public D3DCameraSegmentCell(int index, D3DCameraKeyframe from, D3DCameraKeyframe to)
    {
        Index = index;
        From = from;
        To = to;
    }

    public int Index { get; }
    public D3DCameraKeyframe From { get; }
    public D3DCameraKeyframe To { get; }
    public string NumberText => $"{Index + 1}→{Index + 2}";

    public Geometry? CurveGeometry
    {
        get => curveGeometry;
        set => Set(ref curveGeometry, value);
    }
    private Geometry? curveGeometry;

    public Brush StrokeBrush
    {
        get => strokeBrush;
        set => Set(ref strokeBrush, value);
    }
    private Brush strokeBrush = Brushes.Transparent;

    public bool IsEditing
    {
        get => isEditing;
        set => Set(ref isEditing, value);
    }
    private bool isEditing;
}

internal static class D3DCameraTimeCurveThumbnail
{
    public const double Width = 52;
    public const double Height = 16;

    public static Geometry? Create(BezierAnimation? curve)
    {
        if (curve is null)
            return null;

        var points = curve.Points;
        if (points.Count < 2)
            return null;

        var figure = new PathFigure
        {
            StartPoint = ToPoint(points[0].Point),
            IsClosed = false,
            IsFilled = false,
        };

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];

            figure.Segments.Add(new BezierSegment(
                ToPoint(previous.Point + previous.ControlPoint2),
                ToPoint(current.Point + current.ControlPoint1),
                ToPoint(current.Point),
                true));
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point ToPoint(Vector2 value)
        => new(value.X * Width, (1.0 - value.Y) * Height);
}
