using MaszynyAlert.App.Services;
using Serilog.Events;
using Serilog.Parsing;

namespace MaszynyAlert.App.Tests;

public sealed class InMemoryLogSinkTests
{
    private static LogEvent Event(Exception? exception) => new(
        new DateTimeOffset(2026, 10, 4, 3, 11, 44, TimeSpan.Zero),
        LogEventLevel.Warning,
        exception,
        new MessageTemplateParser().Parse("Błąd VNC maszyny {Id}"),
        [new LogEventProperty("Id", new ScalarValue("test01"))]);

    private static Exception Thrown()
    {
        try { throw new System.Net.Sockets.SocketException(10061); }
        catch (Exception ex) { return new InvalidOperationException("Nie można połączyć z test01.\nDruga linia.", ex); }
    }

    [Fact]
    public void WyjatekWPanelu_ZajmujeJednaLinieBezSladuStosu()
    {
        var sink = new InMemoryLogSink();

        sink.Emit(Event(Thrown()));

        string line = Assert.Single(sink.Lines);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain(" at ", line);
        Assert.EndsWith("[WRN] Błąd VNC maszyny test01 — InvalidOperationException: Nie można połączyć z test01.", line);
    }

    [Fact]
    public void WpisBezWyjatku_BezZmian()
    {
        var sink = new InMemoryLogSink();

        sink.Emit(Event(null));

        Assert.EndsWith("[WRN] Błąd VNC maszyny test01", Assert.Single(sink.Lines));
    }
}
