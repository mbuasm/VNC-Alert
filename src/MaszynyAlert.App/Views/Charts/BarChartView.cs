using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using MaszynyAlert.Core.History;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace MaszynyAlert.App.Views.Charts;

public sealed class BarChartView : ChartCanvas
{
    private const double RowHeight = 30;
    private const double BarThickness = 18;
    private const double LegendSpace = 30;
    private const double AxisSpace = 40;

    /// <summary>Podpis kreskowania w legendzie (ten sam próg co w tabeli: <see cref="DowntimeReport.MaxEpisode"/>).</summary>
    public const string LongLegendText = "kreskowanie — długie postoje (> 1 h)";

    private BarChartData? _data;

    /// <summary>Dane wykresu; ustawienie rysuje go od nowa i dopasowuje wysokość.</summary>
    public BarChartData? Data
    {
        get => _data;
        set
        {
            _data = value;
            Height = HeightFor(value);
            Redraw();
        }
    }

    /// <summary>Wysokość dla danej liczby maszyn — nie mniej niż <see cref="ChartCanvas.MinChartHeight"/>; druga linia legendy przy wielu seriach.</summary>
    public static double HeightFor(BarChartData? data) =>
        Math.Max(MinChartHeight, LegendSpace + (data?.Series.Count > 3 ? 22 : 0) + AxisSpace + 8 + RowHeight * Math.Max(1, data?.Categories.Count ?? 0));

    protected override void Draw(double width, double height)
    {
        var data = _data;
        if (data is null) return;
        if (data.Categories.Count == 0 || data.IsEmpty)
        {
            DrawEmpty(width, height, data.EmptyText);
            return;
        }
        double legend = DrawLegend(data.Series.Select(s => (s.Name, s.Color)), 0, 0, width);
        if (data.HasLong) legend = DrawHatchLegend(0, legend + 2, width);
        double labelWidth = Math.Min(Math.Max(width * 0.22, 80), 190);
        labelWidth = Math.Min(labelWidth, data.Categories.Max(c => Text(c, TextBrush).DesiredSize.Width) + 12);
        double valueWidth = data.TotalTexts.Max(t => Text(t, TextBrush, FontSizeSmall, FontWeights.SemiBold).DesiredSize.Width) + 12;
        double plotLeft = labelWidth;
        double plotRight = width - valueWidth;
        double plotTop = legend + 12;
        double plotBottom = height - AxisSpace;
        if (plotRight - plotLeft < 40) return;
        double rowHeight = Math.Min(RowHeight * 1.4, (plotBottom - plotTop) / data.Categories.Count);
        var (axisMax, step) = NiceAxis(data.Totals.Max());
        double Scale(double value) => plotLeft + (plotRight - plotLeft) * Math.Max(0, value) / axisMax;

        for (double tick = 0; tick <= axisMax + step / 2; tick += step)
        {
            double x = Scale(tick);
            VLine(x, plotTop, plotBottom, GridBrush);
            PlaceText(AxisText(tick), x, plotBottom + 10, MutedBrush, HorizontalAlignment.Center, FontSizeSmall);
        }
        PlaceText(data.ValueAxisTitle, (plotLeft + plotRight) / 2, height - 10, MutedBrush, HorizontalAlignment.Center, FontSizeSmall);

        var hatch = HatchBrush(SurfaceBrush);
        for (int row = 0; row < data.Categories.Count; row++)
        {
            double centerY = plotTop + rowHeight * (row + 0.5);
            PlaceText(data.Categories[row], plotLeft - 8, centerY, TextBrush, HorizontalAlignment.Right, FontSizeNormal, labelWidth - 8);
            double cursor = 0;
            // Podpowiedź: każda część (pominięte zera przy wielu nazwach błędów), „w tym długie” tam, gdzie są, i suma.
            string tooltip = data.Categories[row] + Environment.NewLine
                + string.Join(Environment.NewLine, data.Series
                    .Where(s => s.Points[row].Value > 0 || data.Series.Count <= 3)
                    .Select(s => $"{s.Name}: {s.Points[row].ValueText}"
                        + (s.Points[row].LongValue > 0 ? $" (w tym długie > 1 h: {DowntimeCharts.MinutesText(s.Points[row].LongValue)})" : string.Empty)))
                + Environment.NewLine + "Razem: " + data.TotalTexts[row];
            foreach (var series in data.Series)
            {
                var point = series.Points[row];
                double value = point.Value;
                if (value <= 0) continue;
                // Przerwa 2 px (tło karty) przed każdą kolejną częścią słupka — rozróżnienie nie tylko kolorem.
                double x1 = Scale(cursor) + (cursor > 0 ? 2 : 0), x2 = Scale(cursor + value);
                var segment = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(1, x2 - x1), Height = BarThickness, Fill = SeriesBrush(series.Color),
                    RadiusX = 3, RadiusY = 3, ToolTip = tooltip,
                };
                SetLeft(segment, x1);
                SetTop(segment, centerY - BarThickness / 2);
                Children.Add(segment);
                // Długie postoje tej części: kreskowanie na jej końcu (proporcjonalnie do udziału długich w części).
                double longValue = Math.Min(point.LongValue, value);
                if (longValue > 0)
                {
                    double longWidth = Math.Max(1, (x2 - x1) * longValue / value);
                    var stripes = new System.Windows.Shapes.Rectangle
                    {
                        Width = longWidth, Height = BarThickness, Fill = hatch, RadiusX = 3, RadiusY = 3, ToolTip = tooltip,
                    };
                    SetLeft(stripes, x2 - longWidth);
                    SetTop(stripes, centerY - BarThickness / 2);
                    Children.Add(stripes);
                }
                cursor += value;
            }
            PlaceText(data.TotalTexts[row], Scale(cursor) + 6, centerY, TextBrush, HorizontalAlignment.Left, FontSizeSmall,
                weight: FontWeights.SemiBold).ToolTip = tooltip;
        }
        HLine(plotLeft, plotRight, plotBottom, MutedBrush);
        VLine(plotLeft, plotTop, plotBottom, MutedBrush);
    }

    /// <summary>Wiersz legendy dla kreskowania: próbka z ukośnymi liniami na kolorze serii i podpis. Zwraca dolną krawędź.</summary>
    private double DrawHatchLegend(double x, double y, double width)
    {
        const double rowHeight = 18;
        var sample = new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, RadiusX = 2, RadiusY = 2, Fill = SeriesBrush(ChartPalette.Errors) };
        SetLeft(sample, x);
        SetTop(sample, y + 3);
        Children.Add(sample);
        var stripes = new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, RadiusX = 2, RadiusY = 2, Fill = HatchBrush(SurfaceBrush) };
        SetLeft(stripes, x);
        SetTop(stripes, y + 3);
        Children.Add(stripes);
        var label = Text(LongLegendText, MutedBrush, FontSizeSmall);
        if (label.DesiredSize.Width > width - 20) label.Width = Math.Max(10, width - 20);
        Place(label, x + 18, y + (rowHeight - label.DesiredSize.Height) / 2);
        return y + rowHeight;
    }

    /// <summary>
    /// Ukośne linie w kolorze tła karty (przezroczyste poza liniami) — nakładane na część słupka, więc widać kolor serii,
    /// a wzór odróżnia długie postoje także przy daltonizmie i w druku.
    /// </summary>
    private static Brush HatchBrush(Brush surface)
    {
        var pen = new Pen(surface, 1.6);
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(0, 6), new Point(6, 0)));
        lines.Children.Add(new LineGeometry(new Point(-1, 1), new Point(1, -1)));
        lines.Children.Add(new LineGeometry(new Point(5, 7), new Point(7, 5)));
        var brush = new DrawingBrush(new GeometryDrawing(null, pen, lines))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}
