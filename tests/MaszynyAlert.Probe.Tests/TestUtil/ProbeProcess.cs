using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MaszynyAlert.Probe.Tests.TestUtil;

internal static class ProbeProcess
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    public static async Task<Result> RunAsync(byte[] stdin, TimeSpan timeout, string workingDirectory, params string[] args)
    {
        string probeDll = Path.Combine(AppContext.BaseDirectory, "probe.dll");
        Assert.True(File.Exists(probeDll), $"Brak {probeDll} — referencja projektu MaszynyAlert.Probe kopiuje go do katalogu testów.");

        var psi = new ProcessStartInfo(DotnetHost())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, // probe ustawia Console.OutputEncoding = UTF-8 (Program.TrySetUtf8Console)
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(probeDll);
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Nie udało się uruchomić probe.");
        var started = Stopwatch.StartNew();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            // Synchronicznie: wejście testów to kilkanaście bajtów — mieści się w buforze potoku, więc zapis nie czeka na proces.
            process.StandardInput.BaseStream.Write(stdin);
            process.StandardInput.BaseStream.Flush();
        }
        catch (IOException)
        {
            // Proces zakończył się, zanim przeczytał wejście — o wyniku powie kod wyjścia i wyjście procesu.
        }
        finally
        {
            process.StandardInput.Close();
        }

        // Czekanie z limitem i zabicie na osobnym wątku (WaitForExit = oczekiwanie jądra): proces ginie na limicie, nawet gdy pula
        // wątków albo wątki xUnit są zajęte innymi testami; spóźnić się może tylko powrót do wątku testu. Chwila zabicia (od startu
        // procesu) trafia do opisu porażki — test limitu sprawdza ją niezależnie od tego, kiedy wróci do swojego wątku.
        TimeSpan remaining = timeout - started.Elapsed;
        TimeSpan? killedAfter = await Task.Factory.StartNew(() =>
        {
            if (process.WaitForExit(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)) return (TimeSpan?)null;
            TimeSpan at = started.Elapsed;
            process.Kill(entireProcessTree: true);
            return at;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (killedAfter is { } killed)
        {
            Assert.Fail($"probe {string.Join(' ', args)} nie zakończył się w {timeout.TotalSeconds:N0} s — zabity "
                + $"({killed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s od startu procesu).");
        }

        return new Result(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Host <c>dotnet</c> środowiska, które wykonuje testy: biblioteka podstawowa leży w
    /// <c>&lt;katalog dotnet&gt;\shared\Microsoft.NETCore.App\&lt;wersja&gt;\</c>.
    /// </summary>
    private static string DotnetHost()
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string root = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        string host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Assert.True(File.Exists(host), $"Nie znaleziono hosta .NET {host} (środowisko testów: {runtimeDir}).");
        return host;
    }
}
