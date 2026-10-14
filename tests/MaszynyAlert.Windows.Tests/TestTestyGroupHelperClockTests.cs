using System.Diagnostics;
using System.IO.Pipes;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Rfb.Tests.Fakes;
using static MaszynyAlert.Windows.Tests.GroupHelperTestKit;

namespace MaszynyAlert.Windows.Tests;

public sealed class TestTestyGroupHelperClockTests
{
    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    [Fact]
    public async Task CzasZyciaNaWstrzyknietymZegarze_NiePlynieZCzasemRzeczywistym_WygasaDokladnieNaGranicy()
    {
        using var dir = new TestDir();
        var store = new GroupSettingsStore(dir.Path, new InMemorySecrets());
        var firewall = new FakeFirewall();
        GroupFirewallRule? original = firewall.Rule;
        string name = PipeName();
        await using NamedPipeServerStream pipe = Server(name);
        var clock = new ManualTimeProvider();

        Task<int> helper = Task.Run(() => GroupSettingsClient.RunHelperAsync(name, dir.Path,
            () => new GroupApplySession(store, firewall, dir.Path), TimeSpan.FromMilliseconds(700), clock));
        try
        {
            using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(5))) await pipe.WaitForConnectionAsync(connect.Token);
            await GroupSettingsClient.Write(pipe, PrepareRequest(Proposal(), null), Token());
            HelperResponse prepared = await GroupSettingsClient.Read<HelperResponse>(pipe, Token());
            Assert.True(prepared.Success, prepared.Error);

            // Odpowiedź czytamy od razu w tle. Sam stan zadania helpera nic nie mówi: po wygaśnięciu helper wysyła powód i STOI
            // w zapisie do pipe (bufor 0 B), dopóki ktoś nie przeczyta — więc „helper jeszcze działa” jest prawdą także wtedy,
            // gdy czas życia już minął. Dowodem jest brak odpowiedzi w pipe.
            using var readLimit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<HelperResponse> pending = GroupSettingsClient.Read<HelperResponse>(pipe, readLimit.Token);

            clock.Advance(TimeSpan.FromMilliseconds(699));
            await Task.Delay(TimeSpan.FromMilliseconds(1200)); // prawdziwy czas > czas życia 700 ms
            Assert.False(pending.IsCompleted, "Helper odpowiedział przed upływem czasu życia na wstrzykniętym zegarze (699 z 700 ms) — "
                + "liczy czas rzeczywisty albo krótszy czas życia.");
            Assert.False(helper.IsCompleted);

            clock.Advance(TimeSpan.FromMilliseconds(1));
            HelperResponse response = await pending;
            Assert.False(response.Success);
            Assert.Contains("limit czasu", response.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await helper.WaitAsync(TimeSpan.FromSeconds(10)));

            var recovery = store.Read();
            Assert.Null(recovery.InterruptedId);
            Assert.Null(recovery.CommittedId);
            Assert.Equal(original, firewall.Rule);
            using FileStream free = Lock(dir.Path);
        }
        finally
        {
            pipe.Dispose();
            try { await helper.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task BezParametruZegara_JakWProgramie_CzasZyciaPlynieZegaremSystemowym()
    {
        using var dir = new TestDir();
        var store = new GroupSettingsStore(dir.Path, new InMemorySecrets());
        var firewall = new FakeFirewall();
        string name = PipeName();
        await using NamedPipeServerStream pipe = Server(name);
        TimeSpan lifetime = TimeSpan.FromSeconds(1);

        var sw = Stopwatch.StartNew();
        Task<int> helper = Task.Run(() => GroupSettingsClient.RunHelperAsync(name, dir.Path,
            () => new GroupApplySession(store, firewall, dir.Path), lifetime));
        try
        {
            using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(5))) await pipe.WaitForConnectionAsync(connect.Token);
            // Nic nie wysyłamy: helper czeka na żądanie prepare, aż upłynie jego czas życia (zegar systemowy, nikt go nie przesuwa).
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            HelperResponse response = await GroupSettingsClient.Read<HelperResponse>(pipe, wait.Token);
            TimeSpan elapsed = sw.Elapsed;

            Assert.False(response.Success);
            Assert.Contains("limit czasu", response.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(elapsed >= lifetime - TimeSpan.FromMilliseconds(50), $"odpowiedź po {elapsed.TotalMilliseconds:0} ms, przed czasem życia");
            Assert.Equal(1, await helper.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, firewall.Applies);
        }
        finally
        {
            pipe.Dispose();
            try { await helper.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        }
    }
}
