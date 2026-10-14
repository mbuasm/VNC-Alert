using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class ObservationWarningTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T12:00:00Z");
    private static MachineStatusSnapshot Snapshot => new()
    {
        MachineId = "aidite3", Name = "Aidite 3", Host = "unused", Profile = "opencnc",
        State = MachineConnectionState.Connected, MainScreenWatchRequired = true,
        MainScreenVisible = true, OcrAvailable = true, LastFrameAt = Now.AddSeconds(-2)
    };

    [Fact]
    public void UnchangedVncScreenDoesNotAlternateBetweenHealthyAndLimited()
    {
        var timing = new TimingConfig();
        foreach (bool cached in new[] { false, true, true, false, true })
            Assert.Null(ObservationWarning.For(Snapshot with { UsingCachedFrame = cached }, Now, timing));
    }

    [Fact]
    public void MissingOrOverdueFramesStillWarn()
    {
        var timing = new TimingConfig { FullFrameIntervalMs = 60000, ReadTimeoutMs = 5000, PollIntervalMs = 1000 };
        Assert.NotNull(ObservationWarning.For(Snapshot with { LastFrameAt = null }, Now, timing));
        Assert.NotNull(ObservationWarning.For(Snapshot with { LastFrameAt = Now.AddSeconds(-67), UsingCachedFrame = true }, Now, timing));
        Assert.Null(ObservationWarning.For(Snapshot with { LastFrameAt = Now.AddSeconds(-60), UsingCachedFrame = true }, Now, timing));
    }

    [Fact]
    public void OcrAndUnrecognizedScreenAreNotHiddenByRecentFrame()
    {
        Assert.Contains("niedostępny", ObservationWarning.For(Snapshot with { OcrAvailable = false }, Now, new())!);
        Assert.Contains("nie potwierdził", ObservationWarning.For(Snapshot with { MainScreenVisible = null }, Now, new())!);
        Assert.Contains("inną zakładkę", ObservationWarning.For(Snapshot with { MainScreenVisible = false }, Now, new())!);
    }
}
