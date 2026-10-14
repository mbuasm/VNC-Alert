using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using MaszynyAlert.Core.History;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;

namespace MaszynyAlert.App.Views.Charts;

/// <summary>
/// Wykres liniowy minut postojów na dzień zmian (<see cref="LineChartData"/>): oś X — kolejne dni robocze zakresu
/// (weekendy pominięte w modelu), oś Y — minuty od zera. Każdy dzień ma przezroczysty pas z podpowiedzią ze
/// wszystkimi seriami; znaczniki punktów — przy najwyżej 31 dniach.
/// </summary>
public sealed class LineChartView : ChartCanvas
{
    private const double AxisSpace = 46;
    private const int MaxMarkerDays = 31;

    private LineChartData? _data;

    public LineChartData? Data
    {
        get => _data;
        set
        {
            _data = value;
            Height = 280;
            Redraw();
        }
    }

    protected override void Draw(double width, double height)
    {
        var data = _data;
        if (data is null) return;
        if (data.Days.Count == 0)
        {
            DrawEmpty(width, height, "Brak dni roboczych w wybranym zakresie.");
            return;
        }
        double legend = DrawLegend(data.Series.Select(s => (s.Name, s.Color)), 0, 0, width);
        double max = data.Series.SelectMany(s => s.Points).Select(p => p.Value).DefaultIfEmpty(0).Max();
        var (axisMax, step) = NiceAxis(max);
        double labelWidth = Text(AxisText(axisMax), MutedBrush, FontSizeSmall).DesiredSize.Width + 10;
        double plotLeft = labelWidth + 6;
        double plotRight = width - 12;
        bool maxLabel = !data.PerMachine && data.Series.Count > 0;
        double plotTop = legend + (maxLabel ? 30 : 14);
        double plotBottom = height - AxisSpace;
        if (plotRight - plotLeft < 40 || plotBottom - plotTop < 40) return;
        int count = data.Days.Count;
        double slot = (plotRight - plotLeft) / count;
        double X(int index) => plotLeft + slot * (index + 0.5);
        double Y(double value) => plotBottom - (plotBottom - plotTop) * Math.Max(0, value) / axisMax;

        for (double tick = 0; tick <= axisMax + step / 2; tick += step)
        {
            double y = Y(tick);
            HLine(plotLeft, plotRight, y, GridBrush);
            PlaceText(AxisText(tick), plotLeft - 6, y, MutedBrush, HorizontalAlignment.Right, FontSizeSmall);
        }
        int every = Math.Max(1, (int)Math.Ceiling(46 / slot));
        double half = Text("00.00", MutedBrush, FontSizeSmall).DesiredSize.Width / 2;
        int firstLabel = 0;
        while (firstLabel < count - 1 && X(firstLabel) - half < plotLeft) firstLabel++;
        for (int i = firstLabel; i < count; i += every)
        {
            if (X(i) + half > width) break;
            PlaceText(data.Days[i].ToString("dd.MM"), X(i), plotBottom + 10, MutedBrush, HorizontalAlignment.Center, FontSizeSmall);
        }
        PlaceText(data.CategoryAxisTitle + " (bez weekendów) · " + data.ValueAxisTitle.ToLowerInvariant() + " na osi pionowej",
            (plotLeft + plotRight) / 2, height - 10, MutedBrush, HorizontalAlignment.Center, FontSizeSmall);

        // Pasy dni z podpowiedzią (pod liniami): jedna podpowiedź na dzień ze wszystkimi seriami.
        for (int i = 0; i < count; i++)
        {
            string tooltip = data.Days[i].ToString("dddd, dd.MM.yyyy", System.Globalization.CultureInfo.GetCultureInfo("pl-PL"))
                + Environment.NewLine + string.Join(Environment.NewLine, data.Series.Select(s => $"{s.Name}: {s.Points[i].ValueText}"));
            var band = new System.Windows.Shapes.Rectangle { Width = Math.Max(1, slot), Height = plotBottom - plotTop, Fill = Brushes.Transparent, ToolTip = tooltip };
            SetLeft(band, plotLeft + slot * i);
            SetTop(band, plotTop);
            Children.Add(band);
        }

        var surface = SurfaceBrush;
        foreach (var series in data.Series)
        {
            var brush = SeriesBrush(series.Color);
            if (count > 1)
            {
                var line = new Polyline { Stroke = brush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
                for (int i = 0; i < count; i++) line.Points.Add(new Point(X(i), Y(series.Points[i].Value)));
                Children.Add(line);
            }
            if (count <= MaxMarkerDays)
                for (int i = 0; i < count; i++)
                {
                    var marker = new Ellipse { Width = 8, Height = 8, Fill = brush, Stroke = surface, StrokeThickness = 1.5, IsHitTestVisible = false };
                    SetLeft(marker, X(i) - 4);
                    SetTop(marker, Y(series.Points[i].Value) - 4);
                    Children.Add(marker);
                }
        }
        // Podpis wybiórczy: tylko maksimum pierwszej serii sumy (druga — „bez długich” — bywa tuż pod nią); przy liniach
        // maszyn żadnego — jeden podpis przy jednej z wielu linii myliłby. Reszta wartości w podpowiedziach dni.
        var first = data.PerMachine ? null : data.Series.FirstOrDefault();
        if (first is not null)
        {
            int best = Enumerable.Range(0, count).OrderByDescending(i => first.Points[i].Value).First();
            if (first.Points[best].Value > 0)
            {
                // Wyśrodkowany nad punktem, ale w poziomie nie poza obszar wykresu (podpisy osi po lewej).
                var label = PlaceText(first.Points[best].ValueText, X(best), Y(first.Points[best].Value) - 14, TextBrush,
                    HorizontalAlignment.Center, FontSizeSmall);
                label.IsHitTestVisible = false;
                double labelWidth2 = label.DesiredSize.Width;
                SetLeft(label, Math.Clamp(X(best) - labelWidth2 / 2, plotLeft, Math.Max(plotLeft, plotRight - labelWidth2)));
            }
        }
        HLine(plotLeft, plotRight, plotBottom, MutedBrush);
    }
}
