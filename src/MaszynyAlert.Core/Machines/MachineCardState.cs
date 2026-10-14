using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Machines;

/// <summary>Kolor karty maszyny: zielony (wszystko w porządku), bursztynowy (czynność przy maszynie), czerwony (błąd).</summary>
public enum MachineCardTone
{
    /// <summary>Zielony — „Pod obserwacją”.</summary>
    Ok,

    /// <summary>Bursztynowy — operator ma coś zrobić przy maszynie albo obserwacja jest ograniczona.</summary>
    Warning,

    /// <summary>Czerwony — trwa niepotwierdzony błąd maszyny.</summary>
    Error,
}

public sealed record MachineCardState(string State, string ShortState, string Detail, MachineCardTone Tone, string Symbol)
{
    /// <summary>Stan „wszystko w porządku” — po nim liczone są maszyny wymagające uwagi.</summary>
    public const string WatchedState = "Pod obserwacją";

    /// <summary>Tekst pokazywany zamiast procentu pracy, gdy postęp nie jest znany.</summary>
    public const string NoProgress = "—";

    /// <summary>
    /// Postęp pracy z ostatniej migawki („42 %”) albo <see cref="NoProgress"/>. Osobne pole, bo nakładka
    /// statusu pokazuje procent przy KAŻDYM stanie kafelka, a <see cref="Detail"/> przy błędzie mówi o błędzie.
    /// </summary>
    public string Progress { get; init; } = NoProgress;

    /// <summary>
    /// Wylicza kartę maszyny <paramref name="machineId"/>. <paramref name="alerts"/> to alerty widoczne na tym
    /// komputerze (bez zamkniętych) — dla dowolnych maszyn; funkcja sama wybiera te dotyczące tej maszyny.
    /// <paramref name="snapshot"/> jest <see langword="null"/>, gdy maszyna jest w konfiguracji, ale nie ma
    /// jeszcze o niej żadnej migawki.
    /// </summary>
    public static MachineCardState From(
        string machineId,
        MachineStatusSnapshot? snapshot,
        IReadOnlyList<Alert> alerts,
        TimingConfig timing,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(timing);

        return Classify(machineId, snapshot, alerts, timing, now) with
        {
            Progress = snapshot?.LastProgressPercent is { } percent ? $"{percent} %" : NoProgress,
        };
    }

    private static MachineCardState Classify(
        string machineId,
        MachineStatusSnapshot? snapshot,
        IReadOnlyList<Alert> alerts,
        TimingConfig timing,
        DateTimeOffset now)
    {
        Alert? error = alerts.FirstOrDefault(a => a.MachineId == machineId && a.Kind == AlertKind.Error);

        // po „Idę sprawdzić” karta NIE może dalej krzyczeć „Błąd maszyny” —
        // operator już przy maszynie stoi. Eskalacja (Escalated) wraca do czerwieni, bo błąd trwa mimo ACK.
        if (error is { Status: AlertStatus.Acknowledged })
        {
            DateTimeOffset since = error.AcknowledgedAt ?? error.StartedAt;
            return new MachineCardState(
                "Sprawdzane",
                "Sprawdzane",
                $"Zgłoszono, ktoś sprawdza — od {since.ToLocalTime():HH:mm}. {error.ShortText}",
                MachineCardTone.Warning,
                "…");
        }

        if (error is not null)
        {
            return new MachineCardState("Błąd maszyny", "Błąd", error.ShortText, MachineCardTone.Error, "!");
        }

        if (snapshot is null || snapshot.State != MachineConnectionState.Connected)
        {
            return new MachineCardState(
                "Brak obserwacji",
                "Brak podglądu",
                snapshot?.State == MachineConnectionState.Connecting
                    ? "Łączę się z maszyną…"
                    : "Nie można teraz sprawdzić stanu maszyny.",
                MachineCardTone.Warning,
                "?");
        }

        if (snapshot.Source == "plik")
        {
            return new MachineCardState(
                "Tryb testowy",
                "Test",
                "Obraz pochodzi z pliku. Maszyna nie jest obserwowana.",
                MachineCardTone.Warning,
                "?");
        }

        if (snapshot.MainScreenVisible == false)
        {
            return new MachineCardState(
                "Przełącz ekran maszyny",
                "Zły ekran",
                "Wróć do widoku z komunikatami i procentem pracy.",
                MachineCardTone.Warning,
                "!");
        }

        // postój PRZED „Ograniczoną obserwacją”. Alert postoju potwierdzony przyciskiem
        // „✕ Skasuj” (Acknowledged) świadomie znika z karty, tak jak znika z pop-upu i z sekcji
        // „Maszyny bez pracy” (AppOrchestrator.IdleAlerts).
        Alert? idle = alerts.FirstOrDefault(a =>
            a.MachineId == machineId
            && a.Kind == AlertKind.MachineIdle
            && a.Status is AlertStatus.Active or AlertStatus.Escalated);
        if (idle is not null)
        {
            bool atZero = idle.Category == AlertStateMachine.IdleAtZeroCategory;
            DateTimeOffset since = (atZero ? snapshot.ProgressZeroSince : snapshot.ProgressFullSince) ?? idle.StartedAt;
            return atZero
                ? new MachineCardState(
                    "Stoi na 0 %",
                    "Stoi 0 %",
                    $"Od {since.ToLocalTime():HH:mm} — wciśnij Start. Praca jest wczytana, ale maszyna jej nie zaczęła.",
                    MachineCardTone.Warning,
                    "▶")
                : new MachineCardState(
                    "Stoi bez pracy (100 %)",
                    "Stoi 100 %",
                    $"Od {since.ToLocalTime():HH:mm} — dołóż prace. Maszyna czeka na kolejne zadanie.",
                    MachineCardTone.Warning,
                    "＋");
        }

        if (ObservationWarning.For(snapshot, now, timing) is { } warning)
        {
            return new MachineCardState("Ograniczona obserwacja", "Ograniczona", warning, MachineCardTone.Warning, "?");
        }

        return new MachineCardState(
            WatchedState,
            WatchedState,
            snapshot.LastProgressPercent is { } percent
                ? $"Postęp pracy: {percent}%"
                : "Brak wykrytego błędu. Postęp nie jest dostępny.",
            MachineCardTone.Ok,
            "✓");
    }
}
