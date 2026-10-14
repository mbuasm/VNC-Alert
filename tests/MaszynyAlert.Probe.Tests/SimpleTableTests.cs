using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe.Tests;

/// <summary>
/// Wypisywanie tabel tekstowych (<see cref="SimpleTable"/>) — wszystkie wyniki (tabela maszyn,
/// tabela podsumowująca) użytkownik czyta w konsoli i w raport.txt, więc równe kolumny są tu funkcją,
/// nie ozdobą.
/// </summary>
public sealed class SimpleTableTests
{
    [Fact]
    public void Render_WyrownujeKolumnyDoNajdluzszejWartosci()
    {
        var table = new SimpleTable("maszyna", "RFB");
        table.AddRow("Aidite 1", "3.8");
        table.AddRow("Bardzo długa nazwa", "3.3");

        List<string> lines = table.Render().ToList();

        Assert.Equal(4, lines.Count);
        Assert.All(lines, l => Assert.Equal(lines[0].Length, l.Length));
        Assert.Equal("maszyna            | RFB", lines[0]);
        Assert.Equal("-------------------+----", lines[1]);
        Assert.Equal("Aidite 1           | 3.8", lines[2]);
        Assert.Equal("Bardzo długa nazwa | 3.3", lines[3]);
    }

    [Fact]
    public void Render_TabeliBezWierszy_DajeSamNaglowekISeparator()
    {
        List<string> lines = new SimpleTable("a", "bb", "ccc").Render().ToList();

        Assert.Equal(["a | bb | ccc", "--+----+----"], lines);
    }

    [Fact]
    public void AddRow_ZeZlaLiczbaKomorek_RzucaArgumentExceptionZObiemaLiczbami()
    {
        // Dlaczego: to błąd programisty (nowa kolumna dodana w jednym miejscu, a nie w drugim) — ma pęknąć
        // od razu i z liczbami w komunikacie, a nie wypisać przesuniętą tabelę, którą ktoś weźmie za wynik.
        var table = new SimpleTable("a", "b");

        ArgumentException ex = Assert.Throws<ArgumentException>(() => table.AddRow("tylko jedna"));

        Assert.Contains("1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => table.AddRow("a", "b", "za dużo"));
    }

    [Fact]
    public void Render_PustychKomorek_NieGubiKolumn()
    {
        var table = new SimpleTable("a", "b");
        table.AddRow("", "x");

        List<string> lines = table.Render().ToList();

        Assert.Equal("  | x", lines[2]);
    }
}
