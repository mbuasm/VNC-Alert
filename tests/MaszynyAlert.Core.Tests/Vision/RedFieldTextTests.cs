using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class RedFieldTextTests
{
    private static readonly Rgba BarTeal = new(180, 202, 213);
    private static readonly Rgba FieldRed = new(255, 48, 0);
    private static readonly Rgba FieldYellow = new(255, 255, 0);
    private static readonly Rgba White = new(255, 255, 255);
    private static readonly Rgba Black = new(0, 0, 0);
    private static readonly Rgba HmiBlue = new(0, 48, 205);

    private static RedThresholds Aidite5xRed => TestPaths.Profile("aidite5x").Red;

    private static RgbaImage Bar(int width = 400, int height = 48)
    {
        var img = new RgbaImage(width, height);
        img.Fill(BarTeal);
        return img;
    }

    private static void FillRect(RgbaImage img, int x, int y, int w, int h, Rgba c)
    {
        for (int yy = y; yy < y + h; yy++)
        {
            for (int xx = x; xx < x + w; xx++)
            {
                img.SetPixel(xx, yy, c);
            }
        }
    }

    /// <summary>Pseudo-litery: pionowe kreski 1 px co 4 px, 12 px wysokości, 4 px od górnej krawędzi pola.</summary>
    private static void Glyphs(RgbaImage img, RectInt field, Rgba ink)
    {
        for (int x = field.X + 4; x < field.Right - 4; x += 4)
        {
            FillRect(img, x, field.Y + 4, 1, 12, ink);
        }
    }

    private static RgbaImage OneRedFieldWithWhiteText(out RectInt field)
    {
        RgbaImage img = Bar();
        field = new RectInt(100, 3, 200, 20);
        FillRect(img, field.X, field.Y, field.Width, field.Height, FieldRed);
        Glyphs(img, field, White);
        // Reszta paska: niebieskie napisy na turkusie (EDIT/AUTO) — poza oknem barwy.
        FillRect(img, 10, 15, 30, 14, HmiBlue);
        FillRect(img, 330, 10, 40, 10, HmiBlue);
        return img;
    }

    [Fact]
    public void Find_CzerwonePoleZBialymTekstem_ZwracaJegoObwiednieIKolor()
    {
        RgbaImage img = OneRedFieldWithWhiteText(out RectInt expected);

        RedField field = Assert.Single(RedFieldLocator.Find(img, Aidite5xRed));

        Assert.Equal(expected, field.Bounds);
        Assert.Equal(FieldRed, field.Color);
        Assert.True(field.HasText);
        // Biały na RGB(255,48,0): sqrt(0² + 207² + 255²) ≈ 328,4.
        Assert.InRange(field.TextContrast, 328, 329);
    }

    [Fact]
    public void PrepareRedFieldTextForOcr_BialeLiteryNaCzerwieni_SaCiemneNaBialymTle()
    {
        RgbaImage img = OneRedFieldWithWhiteText(out RectInt field);

        Gray8Image? prepared = ImageOps.PrepareRedFieldTextForOcr(img, Aidite5xRed, marginPx: 5, scale: 1);

        Assert.NotNull(prepared);
        Assert.Equal(field.Width + 10, prepared.Width);
        Assert.Equal(field.Height + 10, prepared.Height);
        // Kreska litery (x = 104, y = 7..18 w oryginale) → czarna; tło pola → białe; margines → biały.
        Assert.Equal(0, prepared.Get(5 + 4, 5 + 8));
        Assert.Equal(255, prepared.Get(5 + 2, 5 + 8));
        Assert.Equal(255, prepared.Get(0, 0));
        // Niebieskich napisów reszty paska (EDIT/AUTO) nie ma w obrazie — tylko pole.
        Assert.DoesNotContain(prepared.Pixels, p => p > 0 && p < 255);
    }

    [Fact]
    public void PrepareForOcr_NaCzerwonymPolu_ZalewaCienkieBialeLitery()
    {
        RgbaImage img = OneRedFieldWithWhiteText(out RectInt field);

        Gray8Image masked = ImageOps.PrepareForOcr(img, Aidite5xRed, marginPx: 0, scale: 1);

        Assert.Equal(field.Width, masked.Width);
        Assert.Equal(field.Height, masked.Height);
        Assert.All(masked.Pixels, p => Assert.Equal(0, p));
    }

    [Fact]
    public void PrepareRedFieldTextForOcr_DwaPolaObokSiebie_ZostajaWJednymWierszu()
    {
        RgbaImage img = Bar(500);
        var stopped = new RectInt(100, 3, 60, 20);
        var message = new RectInt(170, 3, 300, 20);
        FillRect(img, stopped.X, stopped.Y, stopped.Width, stopped.Height, FieldRed);
        FillRect(img, message.X, message.Y, message.Width, message.Height, FieldRed);
        Glyphs(img, stopped, White);
        Glyphs(img, message, White);

        IReadOnlyList<RedField> fields = RedFieldLocator.Find(img, Aidite5xRed);
        Gray8Image? prepared = ImageOps.PrepareRedFieldTextForOcr(img, Aidite5xRed, marginPx: 5, scale: 1);

        Assert.Equal([stopped, message], fields.Select(f => f.Bounds));
        Assert.Single(RedFieldLocator.GroupIntoLines(fields));
        Assert.NotNull(prepared);
        Assert.Equal(message.Right - stopped.X + 10, prepared.Width);
        Assert.Equal(20 + 10, prepared.Height);
        // Odstęp między polami (x 160..169 w oryginale) — biały, nie ciemny pasek z turkusowego tła.
        Assert.Equal(255, prepared.Get(5 + 165 - 100, 5 + 10));
    }

    [Fact]
    public void PrepareRedFieldTextForOcr_PolaWDwochWierszachIPlakietka_IdaPodSobaWKolejnosci()
    {
        // żółta plakietka „EDIT” przez oba wiersze paska (niebieski tekst), czerwone pole
        // u góry (biały tekst), żółty komunikat pod nim (czarny tekst).
        RgbaImage img = Bar(700);
        var edit = new RectInt(0, 0, 60, 48);
        var tip = new RectInt(150, 3, 200, 20);
        var message = new RectInt(80, 25, 500, 20);
        FillRect(img, edit.X, edit.Y, edit.Width, edit.Height, FieldYellow);
        FillRect(img, 10, 15, 40, 18, HmiBlue);
        FillRect(img, tip.X, tip.Y, tip.Width, tip.Height, FieldRed);
        Glyphs(img, tip, White);
        FillRect(img, message.X, message.Y, message.Width, message.Height, FieldYellow);
        Glyphs(img, message, Black);

        IReadOnlyList<IReadOnlyList<RedField>> lines = RedFieldLocator.GroupIntoLines(RedFieldLocator.Find(img, Aidite5xRed));
        Gray8Image? prepared = ImageOps.PrepareRedFieldTextForOcr(img, Aidite5xRed, marginPx: 4, scale: 1);

        Assert.Equal([edit, tip, message], lines.Select(l => Assert.Single(l).Bounds));
        Assert.NotNull(prepared);
        Assert.Equal(500 + 8, prepared.Width);
        Assert.Equal(48 + 20 + 20 + (4 * 4), prepared.Height);
        // Wiersz 1 (EDIT): niebieski napis na żółci → czarny.
        Assert.Equal(0, prepared.Get(4 + 20, 4 + 20));
        // Wiersz 2 (czerwone pole, top = 4 + 48 + 4): biała kreska → czarna, tło pola → białe.
        int tipTop = 4 + 48 + 4;
        Assert.Equal(0, prepared.Get(4 + 4, tipTop + 8));
        Assert.Equal(255, prepared.Get(4 + 2, tipTop + 8));
        // Wiersz 3 (żółty komunikat): czarna kreska → czarna, tło → białe.
        int messageTop = tipTop + 20 + 4;
        Assert.Equal(0, prepared.Get(4 + 4, messageTop + 8));
        Assert.Equal(255, prepared.Get(4 + 2, messageTop + 8));
    }

    [Fact]
    public void Find_CzerwoneLiteryNaJasnymTle_NieSaPolem()
    {
        RgbaImage img = Bar(300);
        img.Fill(new Rgba(235, 235, 235));
        var red = new Rgba(220, 30, 30);
        for (int x = 20; x < 80; x += 9)
        {
            FillRect(img, x, 10, 4, 12, red);
        }

        FillRect(img, 20, 10, 60, 2, red);
        FillRect(img, 100, 10, 3, 14, red);
        FillRect(img, 110, 10, 12, 14, red);

        Assert.Empty(RedFieldLocator.Find(img, new RedThresholds()));
        Assert.Null(ImageOps.PrepareRedFieldTextForOcr(img, new RedThresholds(), marginPx: 20, scale: 2));
    }

    [Fact]
    public void PrepareRedFieldTextForOcr_PoleBezNapisu_JestPomijane()
    {
        RgbaImage img = Bar();
        FillRect(img, 50, 5, 40, 20, FieldRed);

        RedField lamp = Assert.Single(RedFieldLocator.Find(img, Aidite5xRed));

        Assert.False(lamp.HasText);
        Assert.Null(ImageOps.PrepareRedFieldTextForOcr(img, Aidite5xRed, marginPx: 20, scale: 2));
    }

    [Fact]
    public void PrepareRedFieldTextForOcr_PustyWycinek_ZwracaNull()
    {
        Assert.Null(ImageOps.PrepareRedFieldTextForOcr(new RgbaImage(0, 0), Aidite5xRed, marginPx: 20, scale: 2));
        Assert.Empty(RedFieldLocator.Find(new RgbaImage(0, 0), Aidite5xRed));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PrepareRedFieldTextForOcr_Skala_PowiekszaObraz(int scale)
    {
        RgbaImage img = OneRedFieldWithWhiteText(out RectInt field);

        Gray8Image? prepared = ImageOps.PrepareRedFieldTextForOcr(img, Aidite5xRed, marginPx: 20, scale: scale);

        Assert.NotNull(prepared);
        Assert.Equal((field.Width + 40) * scale, prepared.Width);
        Assert.Equal((field.Height + 40) * scale, prepared.Height);
    }


    private const string NativeFolder = "pasek-ocr-2026-09-25";

    private static string NativePath(string file) =>
        Path.Combine(TestPaths.SamplesOutput, "production-aidite5x", NativeFolder, file);

    private static readonly RectInt TipField = new(202, 3, 372, 20);

    private static RectInt ToRect(int[] b) => new(b[0], b[1], b[2], b[3]);

    public static TheoryData<string> NativeFiles()
    {
        var data = new TheoryData<string>();
        foreach (string time in new[] { "093850", "093917", "093930", "093951", "095746", "100508", "100552", "101439", "101955", "102008" })
        {
            data.Add($"520-akrony-{time}-blad.png");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NativeFiles))]
    public void NatywnyWycinek_MaPoleKomunikatuWStalymMiejscu(string file)
    {
        RgbaImage bar = PngCodec.DecodeFile(NativePath(file));

        IReadOnlyList<RedField> fields = RedFieldLocator.Find(bar, Aidite5xRed);

        Assert.Equal(954, bar.Width);
        Assert.Equal(48, bar.Height);
        RedField tip = Assert.Single(fields, f => f.Bounds == TipField);
        Assert.True(tip.HasText);
        // 09:38:50 to złocistożółte RGB(255,214,32) „TIP:ORIGIN NOT REINSTATED” (inny odcień niż żółty wiersz
        // komunikatu RGB(255,255,0)), reszta — czerwone „TIP:M024.01 …”.
        Assert.Equal(file.Contains("093850", StringComparison.Ordinal) ? new Rgba(255, 214, 32) : FieldRed, tip.Color);
    }

    [Fact]
    public void NatywneWycinki_PolaZgodneZManifestem()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(NativePath("manifest.json")));
        JsonElement samples = manifest.RootElement.GetProperty("samples");
        var files = Directory.EnumerateFiles(Path.GetDirectoryName(NativePath("x"))!, "*.png")
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(files, samples.EnumerateArray().Select(s => s.GetProperty("file").GetString()).Order(StringComparer.Ordinal));
        foreach (JsonElement sample in samples.EnumerateArray())
        {
            string file = sample.GetProperty("file").GetString()!;
            var expected = sample.GetProperty("fields").EnumerateArray()
                .Select(f => (
                    Bounds: ToRect(f.GetProperty("bounds").EnumerateArray().Select(v => v.GetInt32()).ToArray()),
                    Color: f.GetProperty("color").GetString()))
                .ToList();

            IReadOnlyList<RedField> found = RedFieldLocator.Find(PngCodec.DecodeFile(NativePath(file)), Aidite5xRed);
            var actual = found.Select(f => (f.Bounds, Color: (string?)$"RGB({f.Color.R},{f.Color.G},{f.Color.B})")).ToList();

            Assert.True(expected.SequenceEqual(actual),
                $"{file}: manifest {string.Join(", ", expected)}, znalezione {string.Join(", ", actual)}.");
            Assert.All(found, f => Assert.True(f.HasText, $"{file}: pole {f.Bounds} bez napisu."));
        }
    }

    [Theory]
    [InlineData("095746")]
    [InlineData("100508")]
    [InlineData("102008")]
    public void NatywnyWycinek_BialeLiteryStajaSieCiemneATloPolaJasne(string time)
    {
        RgbaImage bar = PngCodec.DecodeFile(NativePath($"520-akrony-{time}-blad.png"));
        const int margin = 20;

        Gray8Image? prepared = ImageOps.PrepareRedFieldTextForOcr(bar, Aidite5xRed, margin, scale: 1);

        Assert.NotNull(prepared);
        Assert.Equal(TipField.Width + (2 * margin), prepared.Width);
        Assert.Equal(TipField.Height + (2 * margin), prepared.Height);
        int white = 0, fieldColor = 0, dark = 0;
        for (int y = TipField.Y; y < TipField.Bottom; y++)
        {
            for (int x = TipField.X; x < TipField.Right; x++)
            {
                Rgba c = bar.GetPixel(x, y);
                byte v = prepared.Get(margin + x - TipField.X, margin + y - TipField.Y);
                if (c == White)
                {
                    white++;
                    Assert.True(v <= 10, $"Biały piksel litery ({x},{y}) dał jasność {v} — litera nie jest ciemna.");
                }
                else if (c == FieldRed)
                {
                    fieldColor++;
                    Assert.Equal(255, v);
                }

                if (v < 128)
                {
                    dark++;
                }
            }
        }

        // Zmierzone: 254 białe piksele liter, 5522 piksele czystej czerwieni pola (372×20 = 7440).
        Assert.InRange(white, 200, 400);
        Assert.InRange(fieldColor, 5000, 6000);
        // Litery to nie blok: ciemne jest kilka–kilkanaście procent pola.
        Assert.InRange(dark, 7440 / 50, 7440 / 4);
    }

    [Fact]
    public void NatywnyWycinek_ObecnaMaskaCzerwieni_ZalewaBialeLitery()
    {
        RgbaImage bar = PngCodec.DecodeFile(NativePath("520-akrony-095746-blad.png"));

        Gray8Image masked = ImageOps.PrepareForOcr(bar, Aidite5xRed, marginPx: 20, scale: 1);

        // Wycinek maski: obwiednia czerwieni (pole) + 20 px, przycięty do paska → początek (182, 0).
        int whiteInField = 0;
        for (int y = TipField.Y; y < TipField.Bottom; y++)
        {
            for (int x = TipField.X; x < TipField.Right; x++)
            {
                if (masked.Get(x - 182, y) == 255)
                {
                    whiteInField++;
                }
            }
        }

        Assert.True(whiteInField < 10, $"W polu zostało {whiteInField} białych pikseli — maska przestała zalewać litery?");
    }

    [Fact]
    public void NatywnyWycinek_DwaWierszePaskaIPlakietka_TrzyWierszeObrazu()
    {
        RgbaImage bar = PngCodec.DecodeFile(NativePath("520-akrony-093917-blad.png"));
        const int margin = 20;

        IReadOnlyList<IReadOnlyList<RedField>> lines = RedFieldLocator.GroupIntoLines(RedFieldLocator.Find(bar, Aidite5xRed));
        Gray8Image? prepared = ImageOps.PrepareRedFieldTextForOcr(bar, Aidite5xRed, margin, scale: 2);

        Assert.Equal(
            [new RectInt(0, 0, 104, 48), TipField, new RectInt(109, 25, 658, 20)],
            lines.Select(l => Assert.Single(l).Bounds));
        Assert.NotNull(prepared);
        Assert.Equal((658 + (2 * margin)) * 2, prepared.Width);
        Assert.Equal((48 + 20 + 20 + (4 * margin)) * 2, prepared.Height);
        // Większość obrazu jasna (tło), ale są ciemne litery.
        int darkPixels = prepared.Pixels.Count(p => p < 128);
        Assert.InRange(darkPixels, prepared.Pixels.Length / 100, prepared.Pixels.Length / 5);
    }
}
