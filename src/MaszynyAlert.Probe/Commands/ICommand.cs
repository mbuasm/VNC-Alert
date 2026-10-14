namespace MaszynyAlert.Probe.Commands;

/// <summary>Jedna komenda <c>probe.exe</c>. Nagłówek (wersja/data/folder wyjściowy) i "Raport: ..." na końcu drukuje Program.cs; komenda odpowiada za resztę.</summary>
public interface ICommand
{
    /// <summary>
    /// Wykonuje komendę. <paramref name="args"/> to argumenty specyficzne dla komendy (opcje globalne
    /// już zdjęte przez <see cref="ArgsUtil.ExtractGlobal"/>). <paramref name="session"/> to stan
    /// współdzielony CAŁEGO przebiegu <c>probe.exe</c> (hasła VNC już zapamiętane, kotwice OCR, tabela
    /// podsumowująca — patrz <see cref="ProbeSession"/>): komendy mają łączyć się przez
    /// <c>session.Connections</c>, nie tworzyć własnego <see cref="ConnectionHelper"/>. Zwraca kod wyjścia
    /// (0 = sukces, także gdy część maszyn pominięto; 1 = błąd użycia/awaria).
    /// </summary>
    Task<int> RunAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct);
}
