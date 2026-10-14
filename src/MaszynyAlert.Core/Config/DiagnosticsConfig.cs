namespace MaszynyAlert.Core.Config;

public sealed record DiagnosticsConfig
{
    /// <summary>
    /// Czy zapisywać zrzut „przerwa” (pierwsza klatka bez czerwieni w trwającym epizodzie) i pełne,
    /// nieskalowane klatki „ekran”. Domyślnie <see langword="false"/> — po połączeniu epizodów tego samego
    /// komunikatu zrzut „przerwa” służy już tylko badaniu migotania paska statusu i włącza się
    /// go świadomie, na czas diagnozy.
    /// </summary>
    public bool CaptureBreaks { get; init; }
}
