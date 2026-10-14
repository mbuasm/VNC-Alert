using System.Diagnostics;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;
using Xunit.Abstractions;

namespace MaszynyAlert.Rfb.Tests;

public sealed class QtCursorRequestTests
{
    private const int W = CursorTestKit.W;
    private const int H = CursorTestKit.H;
    private static readonly RectInt Full = CursorTestKit.Full;

    private readonly ITestOutputHelper _out;

    public QtCursorRequestTests(ITestOutputHelper output) => _out = output;

    // ---- liczba ponowień -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NieprzyrostoweZadanie_SamKursor_DokladnieJednoPonowienie_TenSamProstokatNieprzyrostowo_PoOdpowiedzi()
    {
        await using var sim = new QtQwsCursorSimServer { Width = W, Height = H };
        sim.Start();
        await using var client = new RfbClient(CursorTestKit.Options(sim.Port));
        await client.ConnectAsync(CancellationToken.None);
        var roi = new RectInt(5, 3, 20, 10);

        RgbaImage image = await client.CaptureAsync(roi, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit);
        await Task.Delay(300);

        IReadOnlyList<SimRequest> requests = sim.Requests;
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r =>
        {
            Assert.False(r.Incremental);
            Assert.Equal((5, 3, 20, 10), (r.X, r.Y, r.Width, r.Height));
            Assert.False(r.WantUpdateAtReceipt);
        });
        Assert.Equal(1, requests[1].ResponsesBefore); // ponowienie dopiero po odebraniu odpowiedzi „sam kursor”
        Assert.Equal(1, sim.CursorResponses);
        Assert.Equal(1, sim.FrameResponses);
        CursorTestKit.AssertSame(sim.LastFrameScreen!.Crop(roi), image, "ROI");
    }

    [Fact]
    public async Task PrzyrostoweZadanie_SamKursor_ZeroPonowien_UpdateAsyncITryCaptureAsync()
    {
        await using var sim = new QtQwsCursorSimServer { Width = W, Height = H };
        sim.Start();
        await using var client = new RfbClient(CursorTestKit.Options(sim.Port));
        await client.ConnectAsync(CancellationToken.None);
        await client.CaptureAsync(Full, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit);
        byte[] before = [.. client.Framebuffer.Image.Pixels];

        await sim.ChangeCursorAsync(5, 7, 2, 2);
        FramebufferUpdate update = await client.UpdateAsync(Full, incremental: true, CancellationToken.None).WaitAsync(CursorTestKit.Limit);
        await sim.ChangeCursorAsync(9, 9, 0, 0);
        RgbaImage? image = await client.TryCaptureAsync(Full, incremental: true, TimeSpan.FromSeconds(2), CancellationToken.None)
            .WaitAsync(CursorTestKit.Limit);
        await Task.Delay(300);

        Assert.Empty(update.Rectangles);
        Assert.Equal(new[] { -239 }, update.EncodingsUsed);
        Assert.NotNull(image);
        Assert.Equal(before, client.Framebuffer.Image.Pixels); // kursor nie zmienia bufora (bajt w bajt)
        Assert.Equal(4, sim.RequestCount); // 2 (pierwsza klatka) + 1 + 1, bez ponowień
        Assert.True(sim.Requests[2].Incremental && sim.Requests[3].Incremental);
        Assert.Equal(3, sim.CursorResponses);
        Assert.Equal(0, sim.Overlaps);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(2026)]
    [InlineData(156239)]
    public async Task NOdpowiedziSamKursorPodRzad_DokladnieNPonowien_KazdePoOdpowiedzi_PotemKlatka(int seed)
    {
        int n = new Random(seed).Next(1, 31);
        _out.WriteLine($"ziarno={seed} N={n}");
        await using var sim = new QtQwsCursorSimServer
        {
            Width = W, Height = H, DirtyCursorBeforeFirstRequests = n, Writes = WriteMode.RandomChunks, WriteSeed = seed,
        };
        sim.Start();
        await using var client = new RfbClient(CursorTestKit.Options(sim.Port));
        await client.ConnectAsync(CancellationToken.None);

        RgbaImage image = await client.CaptureAsync(Full, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit);
        await Task.Delay(300);

        IReadOnlyList<SimRequest> requests = sim.Requests;
        Assert.Equal(n + 1, requests.Count);
        Assert.Equal(n, sim.CursorResponses);
        Assert.Equal(1, sim.FrameResponses);
        Assert.Equal(0, sim.Overlaps);
        for (int k = 0; k < requests.Count; k++)
        {
            Assert.False(requests[k].Incremental);
            Assert.Equal(k, requests[k].ResponsesBefore); // żądanie k wysłane dopiero po k odpowiedziach — nigdy dwa naraz
        }

        CursorTestKit.AssertSame(sim.LastFrameScreen!, image, $"ziarno={seed}");
    }

    [Fact]
    public async Task KazdaOdpowiedzSamKursor_KoniecPoReadTimeout_PonowienTyleCoOdpowiedzi_NigdyDwaNaraz()
    {
        await using var sim = new QtQwsCursorSimServer { Width = W, Height = H, DirtyCursorBeforeEveryRequest = true };
        sim.Start();
        await using var client = new RfbClient(CursorTestKit.Options(sim.Port, TimeSpan.FromSeconds(1)));
        await client.ConnectAsync(CancellationToken.None);

        var watch = Stopwatch.StartNew();
        Exception? ex = await Record.ExceptionAsync(() => client.CaptureAsync(Full, incremental: false, CancellationToken.None)
            .WaitAsync(CursorTestKit.Limit));
        watch.Stop();
        await Task.Delay(300);

        IReadOnlyList<SimRequest> requests = sim.Requests;
        _out.WriteLine($"żądania={requests.Count}, „sam kursor”={sim.CursorResponses}, czas={watch.ElapsedMilliseconds} ms, " +
                       $"tempo={requests.Count / watch.Elapsed.TotalSeconds:0} żądań/s, wyjątek={ex?.GetType().Name}: {ex?.Message}");
        RfbException rfb = Assert.IsAssignableFrom<RfbException>(ex);
        Assert.IsNotType<RfbProtocolException>(rfb);
        Assert.Contains("pełną aktualizację", rfb.Message);
        Assert.InRange(watch.ElapsedMilliseconds, 900, 10_000);
        Assert.False(client.IsConnected);
        Assert.InRange(requests.Count - sim.CursorResponses, 0, 1);
        Assert.Equal(0, sim.FrameResponses);
        Assert.Equal(0, sim.Overlaps);
        for (int k = 0; k < requests.Count; k++)
        {
            Assert.Equal(k, requests[k].ResponsesBefore);
        }
    }

    // ---- nieruchomy ekran — wywołania monitora (VncScreenSource: pełna klatka + przyrostowe z limitem bezczynności) ----------

    [Fact]
    public async Task NieruchomyEkran_PrzyrostoweMilczy_BezRozlaczeniaIBezNowychZadan_PelnaKlatkaCoCyklDostajeOdpowiedz()
    {
        await using var sim = new QtQwsCursorSimServer { Width = W, Height = H };
        sim.Start();
        (MachineConfig machine, TimingConfig timing) = CursorTestKit.MonitorLike(sim.Port);
        await using var source = new VncScreenSource(machine, password: null, timing);
        await source.ConnectAsync(CancellationToken.None);
        var statusBar = new RectInt(0, 0, W, 8);
        const int cycles = 4;

        RgbaImage first = await source.CaptureFullAsync(CancellationToken.None).WaitAsync(CursorTestKit.Limit);
        CursorTestKit.AssertSame(sim.LastFrameScreen!, first, "pierwsza klatka");
        Assert.Equal(2, sim.RequestCount); // „sam kursor” + ponowienie
        var silence = Stopwatch.StartNew();
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            int before = sim.RequestCount;
            for (int i = 0; i < 5; i++)
            {
                RgbaImage? none = await source.TryCaptureAsync(statusBar, incremental: true, TimeSpan.FromMilliseconds(200), CancellationToken.None)
                    .WaitAsync(CursorTestKit.Limit);
                Assert.Null(none);
                Assert.True(source.IsConnected, $"cykl {cycle}, próba {i}");
            }

            Assert.Equal(before + 1, sim.RequestCount); // tylko pierwsze wywołanie wysłało żądanie, reszta czeka na to samo
            await Task.Delay(300);
            Assert.Equal(before + 1, sim.RequestCount); // między cyklami nic

            RgbaImage full = await source.CaptureFullAsync(CancellationToken.None).WaitAsync(CursorTestKit.Limit);
            CursorTestKit.AssertSame(sim.LastFrameScreen!, full, $"pełna klatka cyklu {cycle}");
            Assert.Equal(before + 2, sim.RequestCount);
        }

        silence.Stop();
        _out.WriteLine($"cisza łącznie {silence.ElapsedMilliseconds} ms przy ReadTimeout {timing.ReadTimeoutMs} ms; żądania={sim.RequestCount}, " +
                       $"klatki={sim.FrameResponses}, nakładki={sim.Overlaps}");
        Assert.True(source.IsConnected);
        Assert.True(silence.ElapsedMilliseconds > timing.ReadTimeoutMs, "cisza krótsza niż ReadTimeout — test nic by nie dowodził");
        Assert.Equal(1 + cycles, sim.FrameResponses);
        Assert.Equal(1, sim.CursorResponses);
        Assert.Equal(cycles, sim.Overlaps);
        Assert.All(sim.Requests.Where(r => r.WantUpdateAtReceipt), r => Assert.False(r.Incremental));
        Assert.Empty(sim.Problems);
    }

    [Fact]
    public async Task NieruchomyEkran_ZmianaKursoraPodczasCzekania_SamKursorOdebranyBezPonowienia_PotemZnowuCisza()
    {
        await using var sim = new QtQwsCursorSimServer { Width = W, Height = H };
        sim.Start();
        (MachineConfig machine, TimingConfig timing) = CursorTestKit.MonitorLike(sim.Port);
        await using var source = new VncScreenSource(machine, password: null, timing);
        await source.ConnectAsync(CancellationToken.None);
        var statusBar = new RectInt(0, 0, W, 8);
        RgbaImage first = await source.CaptureFullAsync(CancellationToken.None).WaitAsync(CursorTestKit.Limit);

        Assert.Null(await source.TryCaptureAsync(statusBar, true, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.Equal(3, sim.RequestCount);
        await sim.ChangeCursorAsync(6, 6, 1, 1);
        RgbaImage? stale = await source.TryCaptureAsync(statusBar, true, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        int afterCursor = sim.RequestCount;
        RgbaImage? silent = await source.TryCaptureAsync(statusBar, true, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.NotNull(stale);
        CursorTestKit.AssertSame(first.Crop(statusBar), stale!, "ROI po samym kursorze");
        Assert.Equal(3, afterCursor); // bez ponowienia po „sam kursor” na żądanie przyrostowe
        Assert.Null(silent);
        Assert.Equal(4, sim.RequestCount); // następny cykl wysyła własne żądanie przyrostowe
        Assert.Equal(0, sim.Overlaps);
        Assert.True(source.IsConnected);
    }

    [Fact]
    public async Task ZmianaRoiPrzyCzekajacymPrzyrostowym_WymuszoneNieprzyrostowe_SamKursor_PonowienieTegoRoi_NastepnyCyklBierzeKlatke()
    {
        await using var sim = new QtQwsCursorSimServer { Width = W, Height = H, DirtyCursorBeforeRequest = n => n == 3 };
        sim.Start();
        (MachineConfig machine, TimingConfig timing) = CursorTestKit.MonitorLike(sim.Port);
        await using var source = new VncScreenSource(machine, password: null, timing);
        await source.ConnectAsync(CancellationToken.None);
        var statusBar = new RectInt(0, 0, W, 8);
        var statusAndProgress = new RectInt(0, 0, W, 24);
        await source.CaptureFullAsync(CancellationToken.None).WaitAsync(CursorTestKit.Limit);

        Assert.Null(await source.TryCaptureAsync(statusBar, true, TimeSpan.FromMilliseconds(200), CancellationToken.None)); // #2 czeka
        RgbaImage? afterCursor = await source.TryCaptureAsync(statusAndProgress, true, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        bool reRequested = await QtQwsCursorSimServer.WaitUntilAsync(() => sim.RequestCount == 5, TimeSpan.FromSeconds(5));
        RgbaImage? frame = await source.TryCaptureAsync(statusAndProgress, true, TimeSpan.FromSeconds(2), CancellationToken.None);
        await Task.Delay(200);

        IReadOnlyList<SimRequest> requests = sim.Requests;
        _out.WriteLine(string.Join(" ", requests.Select(r => $"#{r.Number}{(r.Incremental ? "p" : "n")}{(r.WantUpdateAtReceipt ? "!" : "")}")));
        Assert.Null(afterCursor); // ponowione nieprzyrostowe jeszcze czeka
        Assert.True(reRequested);
        Assert.NotNull(frame);
        CursorTestKit.AssertSame(sim.LastFrameScreen!.Crop(statusAndProgress), frame!, "ROI po ponowieniu");
        Assert.Equal(5, requests.Count); // trzeci cykl nic nie wysłał
        Assert.False(requests[3].Incremental);
        Assert.False(requests[4].Incremental);
        Assert.Equal((0, 0, W, 24), (requests[4].X, requests[4].Y, requests[4].Width, requests[4].Height));
        Assert.Equal(1, sim.Overlaps);
        Assert.True(source.IsConnected);
    }


    private static async Task<(string Outcome, RgbaImage? Image, bool AskedCursor, bool SentPixelFormat, int Cursors)> RunPaletteQt(
        RfbPixelFormat? leftover, bool clientCursor)
    {
        await using var sim = new QtQwsCursorSimServer
        {
            Width = W, Height = H, ServerInitFormat = CursorTestKit.QtPalette8, LeftoverClientFormat = leftover,
            ClientCursorSupported = clientCursor, HextileSupported = false, InitialColour = new Rgba(255, 0, 0),
        };
        sim.Start();
        await using var client = new RfbClient(CursorTestKit.Options(sim.Port, TimeSpan.FromSeconds(1)));
        await client.ConnectAsync(CancellationToken.None);
        RgbaImage? image = null;
        Exception? ex = await Record.ExceptionAsync(async () =>
            image = await client.CaptureAsync(Full, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit));
        await Task.Delay(100);
        string outcome = ex is null ? "klatka" : $"{ex.GetType().Name}: {ex.Message}";
        return (outcome, image, sim.ReceivedEncodings.Contains(-239), sim.ClientMessageTypes.Contains((byte)0), sim.CursorResponses);
    }

    [Theory]
    [InlineData(0)] // pole pixelFormat niezainicjowane (pierwszy klient od startu HMI)
    [InlineData(8)] // po kliencie 8 bpp true-colour
    [InlineData(16)]
    [InlineData(32)]
    public async Task SerwerQtPaletowy_KlientProsiOKursorBezSetPixelFormat_KursorNieDokladaRozjazduPonadKlatki(int leftoverBpp)
    {
        RfbPixelFormat? leftover = leftoverBpp == 0 ? null : CursorTestKit.FormatFor(leftoverBpp);

        var withCursor = await RunPaletteQt(leftover, clientCursor: true);
        var withoutCursor = await RunPaletteQt(leftover, clientCursor: false);

        _out.WriteLine($"format zastany {leftoverBpp} bpp: z kursorem ({withCursor.Cursors} odp.) → {withCursor.Outcome}; bez kursora → {withoutCursor.Outcome}");
        if (withCursor.Image is { } a)
        {
            _out.WriteLine($"  piksel (0,0) z kursorem: {a.GetPixel(0, 0)} (ekran atrapy: czerwony)");
        }

        Assert.True(withCursor.AskedCursor);
        Assert.False(withCursor.SentPixelFormat);
        Assert.Equal(withoutCursor.Image is null, withCursor.Image is null); // ten sam wynik: kursor nie dodaje nowego rozjazdu
        if (withCursor.Image is { } x && withoutCursor.Image is { } y)
        {
            CursorTestKit.AssertSame(y, x, $"format zastany {leftoverBpp} bpp");
        }
    }
}
