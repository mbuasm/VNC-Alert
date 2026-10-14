using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Export;

public static class ReportPresentationNames
{
    /// <summary>Nazwa błędu w tabeli PDF i w Excelu dla wiersza bez zapisanego tekstu.</summary>
    public const string MissingReason = "Błąd (brak opisu)";

    /// <summary>
    /// Nazwa błędu do raportu: zapisany tekst postoju (<see cref="DowntimeSlice.Reason"/>) z „Tacka” → „Ramka” — mapowanie
    /// <see cref="DowntimePresentation.ErrorName"/> (jedno dla Panelu, wykresów, PDF i Excela). Pusty tekst w tabeli
    /// eksportu → <see cref="MissingReason"/> (na wykresie kołowym taki postój idzie do „Nierozpoznany”).
    /// </summary>
    public static string ErrorName(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? MissingReason : DowntimePresentation.ErrorName(reason);

    /// <summary>Nazwa grupy (rodzaju) błędu do raportu — <see cref="DowntimePresentation.CategoryName"/>.</summary>
    public static string CategoryName(string? category) => DowntimePresentation.CategoryName(category);

    public static string StateText(DowntimeSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        string state = slice.Interrupted ? "Przerwana obserwacja" : slice.Ongoing ? "Otwarty epizod" : "Zakończony";
        return slice.IsLong ? state + ", " + LongDowntimeMark : state;
    }

    public const string LongDowntimeMark = "długi postój (> 1 h)";
}
