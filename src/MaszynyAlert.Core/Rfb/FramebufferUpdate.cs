using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Rfb;

/// <summary>
/// Wynik jednej aktualizacji ekranu (<c>FramebufferUpdate</c>, typ komunikatu 0): lista
/// zdekodowanych prostokątów (bez pseudo-kodowań typu DesktopSize — te tylko zmieniają rozmiar
/// <see cref="Framebuffer"/>) oraz lista numerów kodowań użytych w tej aktualizacji (w kolejności
/// odebrania, może zawierać duplikaty i wpisy pseudo-kodowań — do diagnostyki/logów).
/// </summary>
public sealed record FramebufferUpdate(IReadOnlyList<RectInt> Rectangles, IReadOnlyList<int> EncodingsUsed);
