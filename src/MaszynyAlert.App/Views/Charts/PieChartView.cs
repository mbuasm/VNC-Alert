using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using MaszynyAlert.Core.History;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MaszynyAlert.App.Views.Charts;

/// <summary>
/// Wykres kołowy udziału czasu postojów według nazwy błędu albo rodzaju postoju (<see cref="PieChartData"/>): koło po lewej,
/// legenda z wartością i procentem każdej pozycji po prawej (tożsamość pozycji nigdy nie wynika z samego koloru).
/// </summary>
public sealed class PieChartView : ChartCanvas
{
    private PieChartData? _data;

    public PieChartData? Data
    {
        get => _data;
        set
        {
            _data = value;
            Height = HeightFor(value);
            Redraw();
        }
    }

    /// <summary>Wysokość: koło albo legenda (wiersz 24 px na pozycję), co większe.</summary>
    public static double HeightFor(PieChartData? data) =>
        Math.Max(MinChartHeight, 24 * (data?.Slices.Count ?? 0) + 40);

    protected override void Draw(double width, double height)
    {
        var data = _data;
        if (data is null) return;
        if (data.IsEmpty)
        {
            // Napis z modelu: wykres błędów mówi „nie ma błędów maszyn”, wykres rodzajów — „nie ma postojów”.
            DrawEmpty(width, height, data.EmptyText);
            return;
        }
        double diameter = Math.Min(height - 16, Math.Min(240, width * 0.38));
        double radius = diameter / 2;
        var center = new Point(radius + 4, height / 2);
        double angle = -90;
        var surface = SurfaceBrush;
        foreach (var slice in data.Slices)
        {
            double sweep = slice.Share * 360;
            string tooltip = $"{slice.Label}{Environment.NewLine}{slice.ValueText} ({slice.ShareText})";
            Shape shape = sweep >= 359.99
                ? new Ellipse { Width = diameter, Height = diameter }
                : new Path { Data = Wedge(center, radius, angle, sweep) };
            shape.Fill = SeriesBrush(slice.Color);
            shape.Stroke = surface;
            shape.StrokeThickness = 2;
            shape.ToolTip = tooltip;
            if (shape is Ellipse)
            {
                SetLeft(shape, center.X - radius);
                SetTop(shape, center.Y - radius);
            }
            Children.Add(shape);
            angle += sweep;
        }

        // Legenda: kolor, nazwa, minuty i procent — w kolejności wycinków (od największego, potem „Nierozpoznany”, „Pozostałe”).
        double legendLeft = center.X + radius + 24;
        double legendWidth = width - legendLeft;
        if (legendWidth < 120) return;
        double rowHeight = 24;
        double top = Math.Max(4, (height - rowHeight * (data.Slices.Count + 1)) / 2);
        double valueColumn = Math.Min(190, legendWidth * 0.42);
        double nameWidth = legendWidth - valueColumn - 22;
        for (int i = 0; i < data.Slices.Count; i++)
        {
            var slice = data.Slices[i];
            double centerY = top + rowHeight * (i + 0.5);
            var swatch = new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, RadiusX = 2, RadiusY = 2, Fill = SeriesBrush(slice.Color) };
            SetLeft(swatch, legendLeft);
            SetTop(swatch, centerY - 6);
            Children.Add(swatch);
            PlaceText(slice.Label, legendLeft + 20, centerY, TextBrush, HorizontalAlignment.Left, FontSizeNormal, nameWidth)
                .ToolTip = slice.Label;
            PlaceText($"{slice.ValueText} · {slice.ShareText}", width - 4, centerY, MutedBrush, HorizontalAlignment.Right, FontSizeSmall, valueColumn);
        }
        double totalY = top + rowHeight * (data.Slices.Count + 0.5);
        HLine(legendLeft, width - 4, totalY - rowHeight / 2 + 2, GridBrush);
        PlaceText("Razem", legendLeft + 20, totalY, TextBrush, HorizontalAlignment.Left, FontSizeNormal, weight: FontWeights.SemiBold);
        PlaceText(data.TotalText, width - 4, totalY, TextBrush, HorizontalAlignment.Right, FontSizeSmall, weight: FontWeights.SemiBold);
    }

    private static Geometry Wedge(Point center, double radius, double startDeg, double sweepDeg)
    {
        static Point On(Point c, double r, double deg)
        {
            double rad = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }
        var figure = new PathFigure { StartPoint = center, IsClosed = true };
        figure.Segments.Add(new LineSegment(On(center, radius, startDeg), true));
        figure.Segments.Add(new ArcSegment(On(center, radius, startDeg + sweepDeg), new Size(radius, radius), 0,
            sweepDeg > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
