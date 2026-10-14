using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class MachinePreviewRfbTests
{
    [Fact]
    public async Task CaptureAsync_OnDemandVnc_IsSharedReadOnlyAndClosesAfterOneFrame()
    {
        var screen = TestScreens.Gradient(48, 32);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = screen });
        var machine = Machine(server.Port);
        var config = Config(machine);
        var service = new MachinePreviewService(() => config, _ => null,
            (cfg, requested) => new VncScreenSource(requested, null, cfg.Timing), new SystemClock());

        MachinePreviewFrame result = await service.CaptureAsync(machine.Id, CancellationToken.None);
        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        await session.WaitForCloseAsync(TestSupport.ShortWait);

        ImageAssert.Equal(screen, result.Image);
        Assert.Equal("vnc-na-zadanie", result.Source);
        Assert.True(session.Shared);
        Assert.Single(session.UpdateRequests);
        Assert.False(session.UpdateRequests[0].Incremental);
        Assert.All(session.ReceivedMessageTypes, type => Assert.True(type is 2 or 3));
        Assert.DoesNotContain((byte)0, session.ReceivedMessageTypes);
        Assert.DoesNotContain((byte)4, session.ReceivedMessageTypes);
        Assert.DoesNotContain((byte)5, session.ReceivedMessageTypes);
        Assert.DoesNotContain((byte)6, session.ReceivedMessageTypes);
    }

    [Fact]
    public async Task CaptureAsync_TimeoutDuringHandshake_ClosesSocket()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            SilentUntilClientCloses = true,
        });
        var machine = Machine(server.Port);
        var config = Config(machine, sourceTimeoutMs: 60_000);
        var time = new ManualTimeProvider();
        var service = Service(config, TimeSpan.FromSeconds(1), time);

        Task<MachinePreviewFrame> capture = service.CaptureAsync(machine.Id, CancellationToken.None);
        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.False(capture.IsCompleted);
        Assert.Null(session.ClientProtocolVersion);

        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => capture);
        await session.WaitForCloseAsync(TestSupport.ShortWait);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public async Task CaptureAsync_CancelDuringFrameRead_ClosesSocket()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = TestScreens.Gradient(48, 32),
            DelayBeforeUpdate = TimeSpan.FromMilliseconds(500),
        });
        var machine = Machine(server.Port);
        var config = Config(machine);
        var service = Service(config);
        using var cancellation = new CancellationTokenSource();

        Task<MachinePreviewFrame> capture = service.CaptureAsync(machine.Id, cancellation.Token);
        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        await session.WaitForEncodingsAsync(TestSupport.ShortWait);
        using var requestDeadline = new CancellationTokenSource(TestSupport.ShortWait);
        while (session.UpdateRequests.Count == 0)
        {
            await Task.Delay(10, requestDeadline.Token);
        }
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        await session.WaitForCloseAsync(TestSupport.ShortWait);
        Assert.True(session.IsClosed);
        Assert.All(session.ReceivedMessageTypes, type => Assert.True(type is 2 or 3));
    }

    private static MachinePreviewService Service(AppConfig config, TimeSpan? timeout = null, TimeProvider? time = null) => new(
        () => config, _ => null,
        (cfg, requested) => new VncScreenSource(requested, null, cfg.Timing), new SystemClock())
        { Timeout = timeout ?? TimeSpan.FromSeconds(12), TimeProvider = time ?? TimeProvider.System };

    private static MachineConfig Machine(int port) => new()
    {
        Id = "aidite1", Name = "Aidite 1", Host = "127.0.0.1", Port = port,
        Profile = "opencnc", Mode = ConnectionMode.Polling,
    };

    private static AppConfig Config(MachineConfig machine, int sourceTimeoutMs = 5_000) => new()
    {
        Machines = [machine],
        Timing = new TimingConfig { ConnectTimeoutMs = sourceTimeoutMs, ReadTimeoutMs = sourceTimeoutMs },
    };
}
