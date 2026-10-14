using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaszynyAlert.App.Services;

namespace MaszynyAlert.App.Views;

public partial class ToastWindow : Window
{
    /// <summary>Znak w kółku: „✓”, „!” (kreska i kropka), „✕” — w siatce 28 × 28 px.</summary>
    private static readonly Geometry CheckGlyph = Geometry.Parse("M 8.5,14.5 L 12.5,18.5 L 19.5,10");
    private static readonly Geometry WarningGlyph = Geometry.Parse("M 14,7.5 L 14,15.5 M 14,20 L 14,20.1");
    private static readonly Geometry ErrorGlyph = Geometry.Parse("M 9.5,9.5 L 18.5,18.5 M 18.5,9.5 L 9.5,18.5");

    private static readonly SolidColorBrush DarkGlyphBrush = Frozen(System.Windows.Media.Color.FromRgb(0x3A, 0x2C, 0x00));

    /// <summary>Znak na zielonym i czerwonym kółku.</summary>
    private static readonly SolidColorBrush LightGlyphBrush = Frozen(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));

    internal static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(320);

    internal static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(550);

    private readonly DispatcherTimer _timer;
    private bool _dismissing;

    public ToastWindow(NotificationKind kind, string title, string? detail, string? revealPath, TimeSpan duration)
    {
        InitializeComponent();

        Kind = kind;
        RevealPath = revealPath;
        Duration = duration;
        TitleText.Text = title;
        DetailText.Text = detail ?? string.Empty;
        DetailText.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        ApplyKind(kind);
        if (revealPath is not null)
        {
            Cursor = System.Windows.Input.Cursors.Hand;
            ToolTip = "Kliknij, aby pokazać plik w folderze";
        }

        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (!_dismissing) _timer.Start(); };
        MouseLeftButtonUp += OnClicked;
        SourceInitialized += (_, _) => MakeNonActivating();
        Loaded += (_, _) => Appear();
    }

    /// <summary>Rodzaj (kolor kółka i znak).</summary>
    public NotificationKind Kind { get; }

    /// <summary>Plik pokazywany w Eksploratorze po kliknięciu; <see langword="null"/> = kliknięcie tylko zamyka.</summary>
    public string? RevealPath { get; }

    /// <summary>Jak długo dymek stoi (bez kursora nad nim), zanim zacznie znikać.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Pierwszy wiersz (<see cref="Window.Title"/> zostaje „VNC Alert” — tak dymek widzą czytniki ekranu).</summary>
    public string Headline => TitleText.Text;

    /// <summary>Drugi wiersz (pusty, gdy go nie ma).</summary>
    public string Detail => DetailText.Text;

    /// <summary>
    /// Zaczyna znikanie (albo zamyka od razu przy <paramref name="immediately"/> — następny dymek zastępuje poprzedni bez czekania).
    /// Ponowne wywołanie nie ponawia animacji; natychmiastowe zamknięcie przerywa także rozpoczęte znikanie.
    /// </summary>
    public void Dismiss(bool immediately = false)
    {
        if (_dismissing && !immediately) return;
        _dismissing = true;
        _timer.Stop();
        if (immediately || !IsLoaded)
        {
            Close();
            return;
        }

        var sink = new DoubleAnimation(HiddenOffset, HideDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        sink.Completed += (_, _) => Close();
        Slide.BeginAnimation(TranslateTransform.YProperty, sink);
        Card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, HideDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    /// <summary>
    /// Przesunięcie, przy którym karta jest w całości pod dolną krawędzią okna (poza <c>Stage</c>): jej wysokość i dolny margines.
    /// </summary>
    internal double HiddenOffset => Card.ActualHeight + Card.Margin.Bottom;

    private void ApplyKind(NotificationKind kind)
    {
        (string circle, string border, Geometry glyph) = kind switch
        {
            NotificationKind.Warning => ("WarningYellowBrush", "WarningSoftBorderBrush", WarningGlyph),
            NotificationKind.Error => ("AlertRedBrush", "ErrorSoftBorderBrush", ErrorGlyph),
            _ => ("OkGreenBrush", "OkSoftBorderBrush", CheckGlyph),
        };
        // Odwołania do zasobów, nie kopie pędzli: przełączenie motywu w trakcie pokazywania od razu zmienia kolory.
        IconCircle.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, circle);
        Card.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, border);
        IconGlyph.Data = glyph;
        IconGlyph.Stroke = kind == NotificationKind.Warning ? DarkGlyphBrush : LightGlyphBrush;
    }

    private void Appear()
    {
        Slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(HiddenOffset, 0, ShowDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ShowDuration * 0.6));
        if (!_dismissing && !IsMouseOver) _timer.Start();
    }

    private void OnClicked(object sender, MouseButtonEventArgs e)
    {
        if (RevealPath is { } path && File.Exists(path))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                // Eksplorator się nie otworzył — plik i tak jest zapisany; dymek po prostu znika.
            }
        }

        Dismiss();
    }

    /// <summary>
    /// <c>ShowActivated=False</c> chroni fokus tylko przy pokazaniu; <c>WS_EX_NOACTIVATE</c> także przy kliknięciu (operator pisze dalej
    /// w Panelu), a <c>WS_EX_TOOLWINDOW</c> chowa dymek z Alt+Tab.
    /// </summary>
    private void MakeNonActivating()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        int style = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    private static SolidColorBrush Frozen(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
}
