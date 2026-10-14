using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Rfb.Tests.Fakes;
using Xunit.Abstractions;

namespace MaszynyAlert.Rfb.Tests;

public sealed class QtCursorStaleResponseTests
{
    private const int W = CursorTestKit.W;
    private const int H = CursorTestKit.H;

    private readonly ITestOutputHelper _out;

    public QtCursorStaleResponseTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task ZaleglySamKursorNaPrzyrostowe_PotemPelnaKlatka_Charakterystyka_DwieKlatkiOdSerweraZaJednoPobranie()
    {
        await using var sim = new QtQwsCursorSimServer { Width = W, Height = H };
        sim.Start();
        (MachineConfig machine, TimingConfig timing) = CursorTestKit.MonitorLike(sim.Port);
        await using var source = new VncScreenSource(machine, password: null, timing);
        await source.ConnectAsync(CancellationToken.None);
        var statusBar = new RectInt(0, 0, W, 8);
        await source.CaptureFullAsync(CancellationToken.None).WaitAsync(CursorTestKit.Limit);

        Assert.Null(await source.TryCaptureAsync(statusBar, true, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        await sim.ChangeCursorAsync(8, 8, 0, 0); // „sam kursor” leci jako odpowiedź na czekające żądanie przyrostowe
        Assert.True(await QtQwsCursorSimServer.WaitUntilAsync(() => sim.CursorResponses == 2, TimeSpan.FromSeconds(5)));
        await Task.Delay(100); // bajty są już u klienta, nieodczytane

        int framesBefore = sim.FrameResponses;
        int requestsBefore = sim.RequestCount;
        RgbaImage full = await source.CaptureFullAsync(CancellationToken.None).WaitAsync(CursorTestKit.Limit);
        // Odpowiedź serwera na ponowienie dochodzi po powrocie CaptureFullAsync — czekamy na nią warunkiem (nie stałym
        // opóźnieniem, które pod obciążeniem bywa za krótkie), potem chwilę, czy nie przyjdzie trzecia.
        await QtQwsCursorSimServer.WaitUntilAsync(() => sim.FrameResponses - framesBefore >= 2, TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        int framesForOneCapture = sim.FrameResponses - framesBefore;
        int requestsForOneCapture = sim.RequestCount - requestsBefore;

        // Dwa kolejne cykle przyrostowe na nieruchomym ekranie.
        RgbaImage? next1 = await source.TryCaptureAsync(statusBar, true, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        RgbaImage? next2 = await source.TryCaptureAsync(statusBar, true, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await Task.Delay(100);

        string requests = string.Join(" ", sim.Requests.Select(r => $"#{r.Number}{(r.Incremental ? "p" : "n")}{(r.WantUpdateAtReceipt ? "!" : "")}"));
        _out.WriteLine($"jedno CaptureFullAsync: żądania={requestsForOneCapture}, klatki od serwera={framesForOneCapture}; " +
                       $"cykl+1: {(next1 is null ? "null" : "obraz")}, cykl+2: {(next2 is null ? "null" : "obraz")}; " +
                       $"nakładki (żądanie przy czekającym)={sim.Overlaps}; żądania (n = nieprzyrostowe, p = przyrostowe, ! = nakładka): " +
                       requests);

        // Kontrakt bez zmian: treść klatki poprawna, połączenie zdrowe, atrapa bez zastrzeżeń.
        CursorTestKit.AssertSame(sim.LastFrameScreen!, full, "pełna klatka");
        Assert.True(source.IsConnected);
        Assert.Empty(sim.Problems);

        // żądanie pełne + ponowienie po zaległym „samym kursorze” → 2 pełne klatki od serwera.
        Assert.Equal(2, requestsForOneCapture);
        Assert.Equal(2, framesForOneCapture);
        // Nadmiarowa klatka trafia do cyklu+1 jako „odpowiedź” na jego przyrostowe; cykl+2 wysyła żądanie przy czekającym (#6p!).
        Assert.NotNull(next1);
        Assert.Null(next2);
        Assert.Equal(1, sim.Overlaps);
        Assert.Equal("#0n #1n #2p #3n #4n #5p #6p!", requests);
    }
}
