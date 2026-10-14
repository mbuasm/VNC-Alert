using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MaszynyAlert.Windows;

public static class WindowClickThrough
{
    private const int GwlExStyle = -20;

    /// <summary>Okno ma warstwę alfa — WPF ustawia to samo dla okna z <c>AllowsTransparency</c>.</summary>
    internal const int WsExLayered = 0x0008_0000;

    /// <summary>Okno nie łapie myszy — kliknięcia trafiają do okna pod spodem.</summary>
    internal const int WsExTransparent = 0x0000_0020;

    /// <summary>
    /// Włącza albo wyłącza przepuszczanie kliknięć dla okna <paramref name="windowHandle"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Uchwyt okna jest pusty.</exception>
    /// <exception cref="Win32Exception">Systemowi nie udało się odczytać albo zapisać stylu okna.</exception>
    public static void Apply(nint windowHandle, bool clickThrough)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("Okno nie ma jeszcze uchwytu — ustaw styl po zdarzeniu Loaded.", nameof(windowHandle));
        }

        Marshal.SetLastSystemError(0);
        int current = GetWindowLong(windowHandle, GwlExStyle);
        if (current == 0 && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        int wanted = NextExStyle(current, clickThrough);
        if (wanted == current)
        {
            return;
        }

        Marshal.SetLastSystemError(0);
        if (SetWindowLong(windowHandle, GwlExStyle, wanted) == 0 && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// Nowa wartość rozszerzonych stylów okna. Włączenie dokłada <c>WS_EX_TRANSPARENT</c> razem z
    /// <c>WS_EX_LAYERED</c> (bez warstwy alfa system nie przepuszcza kliknięć); wyłączenie zdejmuje TYLKO
    /// <c>WS_EX_TRANSPARENT</c> — warstwa zostaje, bo trzyma ją także przezroczyste tło okna WPF, a jej
    /// zdjęcie zepsułoby wygląd nakładki. Czysta funkcja — sprawdzana testem bez tworzenia okna.
    /// </summary>
    internal static int NextExStyle(int currentExStyle, bool clickThrough) => clickThrough
        ? currentExStyle | WsExLayered | WsExTransparent
        : currentExStyle & ~WsExTransparent;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint handle, int index, int value);
}
