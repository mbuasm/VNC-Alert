namespace MaszynyAlert.App.Views;

public sealed class AdminLoginThrottle
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    public TimeSpan Remaining(DateTimeOffset now) => now >= _retryAfter ? TimeSpan.Zero : _retryAfter - now;

    public void RegisterFailure(DateTimeOffset now) => _retryAfter = now + Delay;

    public static string WaitMessage(TimeSpan remaining) =>
        $"Poczekaj jeszcze {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} s — hasło nie zostało sprawdzone.";
}
