using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MaszynyAlert.Core.History;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace MaszynyAlert.App.Views.Charts;

public abstract class ChartCanvas : Canvas
{
    public static readonly DependencyProperty TextBrushProperty = BrushProperty(nameof(TextBrush), Brushes.Black);
    public static readonly DependencyProperty MutedBrushProperty = BrushProperty(nameof(MutedBrush), Brushes.DimGray);
    public static readonly DependencyProperty GridBrushProperty = BrushProperty(nameof(GridBrush), Brushes.LightGray);
    public static readonly DependencyProperty SurfaceBrushProperty = BrushProperty(nameof(SurfaceBrush), Brushes.White);

    /// <summary>Tekst etykiet i wartości (motyw: <c>TextPrimaryBrush</c>).</summary>
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    /// <summary>Podpisy osi i legendy drugiego planu (motyw: <c>TextSecondaryBrush</c>).</summary>
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

    /// <summary>Linie siatki i osi (motyw: <c>DividerBrush</c>).</summary>
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    /// <summary>Tło karty, na której stoi wykres (motyw: <c>CardBackgroundBrush</c>) — przerwy między polami i wybór kolorów serii.</summary>
    public Brush SurfaceBrush { get => (Brush)GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }

    public const double MinChartHeight = 220;

    protected const double FontSizeNormal = 12.5;
    protected const double FontSizeSmall = 11.5;

    protected ChartCanvas()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        MinHeight = MinChartHeight;
        SizeChanged += (_, e) => { if (e.WidthChanged || e.HeightChanged) Redraw(); };
    }

    private static DependencyProperty BrushProperty(string name, Brush fallback) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(ChartCanvas),
            new FrameworkPropertyMetadata(fallback, (d, _) => ((ChartCanvas)d).Redraw()));

    /// <summary>Czy tło wykresu jest ciemne — wtedy kolory serii w wariancie ciemnym.</summary>
    protected bool IsDark => SurfaceBrush is SolidColorBrush { Color: var c } && (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255 < 0.5;

    /// <summary>Pędzel koloru serii dla bieżącego motywu.</summary>
    protected Brush SeriesBrush(ChartColor color)
    {
        uint argb = color.For(IsDark);
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    /// <summary>Rysuje wykres od nowa (zmiana danych, rozmiaru albo motywu).</summary>
    public void Redraw()
    {
        Children.Clear();
        if (ActualWidth < 40 || ActualHeight < 40) return;
        Draw(ActualWidth, ActualHeight);
    }

    protected abstract void Draw(double width, double height);

    /// <summary>Napis pod wykresem, gdy w zakresie nie ma postojów.</summary>
    protected void DrawEmpty(double width, double height, string text)
    {
        var block = Text(text, MutedBrush, FontSizeNormal);
        block.TextWrapping = TextWrapping.Wrap;
        block.MaxWidth = Math.Max(40, width - 24);
        block.Measure(new System.Windows.Size(block.MaxWidth, double.PositiveInfinity));
        Place(block, (width - block.DesiredSize.Width) / 2, (height - block.DesiredSize.Height) / 2);
    }

    protected TextBlock Text(string text, Brush brush, double size = FontSizeNormal, FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text, Foreground = brush, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        block.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block;
    }

    protected TextBlock Place(TextBlock block, double x, double y)
    {
        SetLeft(block, x);
        SetTop(block, y);
        Children.Add(block);
        return block;
    }

    /// <summary>Napis wyrównany: <paramref name="align"/> względem punktu <paramref name="x"/>, pionowo wyśrodkowany na <paramref name="centerY"/>.</summary>
    protected TextBlock PlaceText(string text, double x, double centerY, Brush brush, HorizontalAlignment align,
        double size = FontSizeNormal, double? maxWidth = null, FontWeight? weight = null)
    {
        var block = Text(text, brush, size, weight);
        double width = block.DesiredSize.Width;
        if (maxWidth is { } max && width > max) { block.Width = Math.Max(10, max); width = block.Width; }
        double left = align switch
        {
            HorizontalAlignment.Right => x - width,
            HorizontalAlignment.Center => x - width / 2,
            _ => x,
        };
        return Place(block, left, centerY - block.DesiredSize.Height / 2);
    }

    protected Line HLine(double x1, double x2, double y, Brush brush, double thickness = 1)
    {
        var line = new Line { X1 = x1, X2 = x2, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = thickness, SnapsToDevicePixels = true };
        Children.Add(line);
        return line;
    }

    protected Line VLine(double x, double y1, double y2, Brush brush, double thickness = 1)
    {
        var line = new Line { X1 = x, X2 = x, Y1 = y1, Y2 = y2, Stroke = brush, StrokeThickness = thickness, SnapsToDevicePixels = true };
        Children.Add(line);
        return line;
    }

    /// <summary>Legenda w jednym wierszu (z zawijaniem): kwadrat koloru + nazwa. Zwraca wysokość zajętą przez legendę.</summary>
    protected double DrawLegend(IEnumerable<(string Name, ChartColor Color)> items, double x, double y, double width)
    {
        double cursorX = x, cursorY = y, rowHeight = 18;
        foreach (var (name, color) in items)
        {
            var label = Text(name, MutedBrush, FontSizeSmall);
            double itemWidth = 14 + 6 + Math.Min(label.DesiredSize.Width, width - 20) + 18;
            if (cursorX > x && cursorX + itemWidth > x + width) { cursorX = x; cursorY += rowHeight + 4; }
            var swatch = new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, RadiusX = 2, RadiusY = 2, Fill = SeriesBrush(color) };
            SetLeft(swatch, cursorX);
            SetTop(swatch, cursorY + 3);
            Children.Add(swatch);
            if (label.DesiredSize.Width > width - 20) label.Width = Math.Max(10, width - 20);
            Place(label, cursorX + 18, cursorY + (rowHeight - label.DesiredSize.Height) / 2);
            cursorX += itemWidth;
        }
        return cursorY + rowHeight - y;
    }

    /// <summary>„Ładne” podziałki osi wartości od zera: krok 1, 2 albo 5 × 10^n, około <paramref name="targetTicks"/> odcinków.</summary>
    protected static (double Max, double Step) NiceAxis(double max, int targetTicks = 5)
    {
        if (!(max > 0) || double.IsInfinity(max)) return (1, 1);
        double raw = max / targetTicks;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double normalized = raw / magnitude;
        double step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
        return (Math.Ceiling(max / step) * step, step);
    }

    protected static string AxisText(double value) =>
        value.ToString(value >= 10 || value == Math.Floor(value) ? "N0" : "N1", CultureInfo.GetCultureInfo("pl-PL"));
}
