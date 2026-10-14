using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Tests.Ocr;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("Failed to grab a tray", "FAILED TO GRAB A TRAY")]
    [InlineData("Fai1ed t0 grab", "FAIIED TO GRAB")]
    [InlineData("ZŁAMANE NARZĘDZIE", "ZLAMANE NARZEDZIE")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  a   b  ", "A B")]
    [InlineData("5 min", "5 MIN")]
    [InlineData("tray!", "TRAY")]
    public void Normalize_DlaTypowychWejsc_ZwracaOczekiwanyTekst(string? input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_ZamieniaZeroNaLitereOAJedynkeNaI()
    {
        Assert.Equal("TOOL BROKEN", TextNormalizer.Normalize("T00l br0ken"));
        Assert.Equal("AIR", TextNormalizer.Normalize("A1r"));
    }

    [Fact]
    public void Normalize_ZostawiaCyfrePiecBezZmian()
    {
        Assert.Equal("ERROR 55", TextNormalizer.Normalize("Error 55"));
    }

    [Fact]
    public void Normalize_KreskaIZnakiInterpunkcyjne_ZamienianeNaSpacje()
    {
        Assert.Equal("E STOP", TextNormalizer.Normalize("E-STOP"));
        Assert.Equal("ERROR CODE X", TextNormalizer.Normalize("Error: code #X."));
    }

    [Fact]
    public void Normalize_CyfryZeroIJeden_ZawszeStajaSieLiteramiTakzeWLiczbach()
    {
        // Zachowanie utrwalone: podmiana 0→O i 1→I jest bezwarunkowa, więc liczby z tymi cyframi
        // ulegają zniekształceniu (dla dopasowania reguł to nieszkodliwe — obie strony są normalizowane).
        Assert.Equal("ERROR I2", TextNormalizer.Normalize("Error 12"));
        Assert.Equal("CODE IOO", TextNormalizer.Normalize("code 100"));
    }

    [Fact]
    public void Normalize_PionowaKreskaMiedzyLiterami_StajeSieLiteraI()
    {
        Assert.Equal("SPINDLE", TextNormalizer.Normalize("SP|NDLE"));
    }

    [Fact]
    public void Normalize_PionowaKreskaNaKoncu_StajeSieSpacja()
    {
        Assert.Equal("TRAY", TextNormalizer.Normalize("TRAY|"));
        Assert.Equal("TRAY", TextNormalizer.Normalize("|TRAY"));
    }

    [Fact]
    public void Normalize_WieleSpacjiITabulatorow_ZwijaDoPojedynczychSpacji()
    {
        Assert.Equal("A B C", TextNormalizer.Normalize("A\t\tB\n\nC"));
    }

    [Fact]
    public void Normalize_WszystkiePolskieZnakiDiakrytyczne_ZamienianeNaAscii()
    {
        Assert.Equal("ACELNOSZZ", TextNormalizer.Normalize("ąćęłńóśźż"));
    }

    [Theory]
    [InlineData("No.705 [N]Axis 6 servo alarm", "NO 705 N AXIS 6 SERVO ALARM")]
    [InlineData("No.1200 [p]Request home alarm", "NO 1200 P REQUEST HOME ALARM")]
    [InlineData("No.725 [N]Axis 6 servo isn't enabled", "NO 725 N AXIS 6 SERVO ISN T ENABLED")]
    // Treść po chińsku ginie (nie jest [A-Z0-9]), ale NUMER alarmu zostaje — i tylko on da się dopasować.
    [InlineData("No.520 [N]宏程序执行错误", "NO 520 N")]
    [InlineData("No.2502 [N]<00>启动执行暂停宏超时,宏文件号320", "NO 2502 N 00 320")]
    [InlineData("BŁĄD 100 – ŻÓŁTY", "BLAD 100 ZOLTY")]
    [InlineData("  a\t\tb  ", "A B")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void NormalizeKeepDigits_KomunikatowZHali_ZachowujeCyfryNumeruAlarmu(string? input, string expected)
    {
        // Dlaczego: wzorce reguł dopasowują NUMER komunikatu („No.705”, „No.1200”), bo część treści jest
        // po chińsku i OCR nic z niej nie wyciągnie. Zwykła normalizacja zamienia cyfry 0→O i 1→I, więc
        // „NO 705” stałoby się „NO 7O5” i regex na cyfrach nigdy by nie trafił — ten wariant istnieje
        // wyłącznie po to, żeby numery alarmów przetrwały normalizację.
        Assert.Equal(expected, TextNormalizer.NormalizeKeepDigits(input));
    }

    [Fact]
    public void NormalizeKeepDigits_PionowejKreski_ZamieniaJaNaSpacjeZamiastNaLitereI()
    {
        // Dlaczego: utrwalenie jedynej różnicy względem Normalize — w wariancie „KeepDigits” NIE ma żadnych
        // podstawień znaków podobnych do liter, więc „SP|NDLE” zostaje rozdzielone, a nie sklejone w „SPINDLE”.
        Assert.Equal("SP NDLE", TextNormalizer.NormalizeKeepDigits("SP|NDLE"));
        Assert.Equal("SPINDLE", TextNormalizer.Normalize("SP|NDLE"));
    }

    [Fact]
    public void Normalize_NumeruKomunikatuAidite_NADAL_ZamieniaCyfryNaLitery_Regresja()
    {
        // Dlaczego (regresja): dodanie NormalizeKeepDigits nie może zmienić zachowania zwykłego Normalize —
        // na nim stoi dopasowanie fraz słownych („servo alarm”, „tool broken”), gdzie podstawienie 0→O i
        // 1→I ratuje typowe pomyłki OCR. Gdyby ktoś „przy okazji” wyłączył je w Normalize, frazy z cyframi
        // udającymi litery przestałyby się dopasowywać, a testy numerów alarmów nadal by przechodziły.
        Assert.Equal("NO 7O5 N AXIS 6 SERVO ALARM", TextNormalizer.Normalize("No.705 [N]Axis 6 servo alarm"));
        Assert.Equal("NO I2OO P REQUEST HOME ALARM", TextNormalizer.Normalize("No.1200 [p]Request home alarm"));
    }
}
