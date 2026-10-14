using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace MaszynyAlert.App.Views;

internal static class WindowEffects
{
    private static readonly ConditionalWeakTable<Window, StrongBox<double>> IdleOpacity = new();

    public static void Fade(Window window, double opacity) => window.BeginAnimation(Window.OpacityProperty,
        new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    public static void Attach(Window window, double idleOpacity)
    {
        if (IdleOpacity.TryGetValue(window, out var existing))
        {
            // Okno jest już podpięte — zmieniamy samą wartość, nigdy nie dokładamy drugiego kompletu handlerów.
            SetIdleOpacity(window, idleOpacity);
            return;
        }

        var box = new StrongBox<double>(idleOpacity);
        IdleOpacity.Add(window, box);
        window.Opacity = idleOpacity;
        window.MouseEnter += (_, _) => Fade(window, 1);
        window.MouseLeave += (_, _) => Fade(window, box.Value);
    }

    /// <summary>
    /// Zmienia przezroczystość „bez kursora” podpiętego okna i od razu ją pokazuje, o ile kursor nad oknem
    /// nie jest (wtedy okno i tak jest w pełni widoczne i nową wartość weźmie przy zjechaniu myszą).
    /// Okno niepodpięte przez <see cref="Attach"/> zostaje bez zmian.
    /// </summary>
    public static void SetIdleOpacity(Window window, double idleOpacity)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!IdleOpacity.TryGetValue(window, out var box)) return;
        box.Value = idleOpacity;
        if (!window.IsMouseOver) Fade(window, idleOpacity);
    }
}
