using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.App.Services;

/// <summary>Odczyt rejestracji zadania aktualizacji. Wywoływany na wątku roboczym; testy podają wykonawcę query.</summary>
internal sealed class UpdateTaskRegistration
{
    internal sealed record QueryResult(int ExitCode, string Xml);
    internal static readonly TimeSpan QueryLimit = TimeSpan.FromSeconds(10);
    private readonly string _applicationDirectory;
    private readonly string _dataDirectory;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<QueryResult>> _query;
    private readonly Func<string?> _fileVersion;
    private readonly TimeSpan _queryLimit;

    internal UpdateTaskRegistration(string applicationDirectory, string dataDirectory,
        Func<ProcessStartInfo, CancellationToken, Task<QueryResult>>? query = null,
        Func<string?>? fileVersion = null, TimeSpan? queryLimit = null)
    {
        _applicationDirectory = applicationDirectory;
        _dataDirectory = dataDirectory;
        _query = query ?? QueryAsync;
        _fileVersion = fileVersion ?? (() => FileVersionInfo.GetVersionInfo(
            Path.Combine(_applicationDirectory, "MaszynyAlert.App.exe")).FileVersion);
        _queryLimit = queryLimit ?? QueryLimit;
    }

    internal bool IsUsable()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/query", "/tn", UpdateTaskContract.TaskName, "/xml" })
            start.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(_queryLimit);
        try
        {
            QueryResult result = _query(start, timeout.Token).WaitAsync(timeout.Token).GetAwaiter().GetResult();
            return result.ExitCode == 0 && !HasKnownVersionMismatch(result.Xml);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string ArgumentValue(string name) =>
        $"""(?:"(?<{name}>[^"\r\n]*[^"\\\r\n])"|(?<{name}>[^\s"']+))""";
    private static readonly Regex KnownArguments = new(
        @"\A\s*-NoProfile\s+-NonInteractive\s+-ExecutionPolicy\s+Bypass\s+-File\s+" + ArgumentValue("script") +
        @"\s+-ApplicationDirectory\s+" + ArgumentValue("app") + @"\s+-DataDirectory\s+" + ArgumentValue("data") +
        @"\s+-ExpectedVersion\s+" + ArgumentValue("version") + @"\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private bool HasKnownVersionMismatch(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 64 * 1024,
            });
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            XElement? task = XDocument.Load(reader).Root;
            if (task?.Name != ns + "Task") return false;
            XElement? actions = task.Elements(ns + "Actions").SingleOrDefault();
            XElement? exec = actions?.Elements().SingleOrDefault();
            if (exec?.Name != ns + "Exec") return false;
            string? command = exec.Elements(ns + "Command").SingleOrDefault()?.Value;
            string? working = exec.Elements(ns + "WorkingDirectory").SingleOrDefault()?.Value;
            string? arguments = exec.Elements(ns + "Arguments").SingleOrDefault()?.Value;
            if (!SamePath(command, Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
                || !SamePath(working, _applicationDirectory) || arguments is null) return false;
            Match match = KnownArguments.Match(arguments);
            if (!match.Success
                || !SamePath(match.Groups["script"].Value, Path.Combine(_applicationDirectory, @"installer\Update-Task.ps1"))
                || !SamePath(match.Groups["app"].Value, _applicationDirectory)
                || !SamePath(match.Groups["data"].Value, _dataDirectory)) return false;
            string expected = match.Groups["version"].Value;
            string? installed = _fileVersion();
            // Update-Task.ps1 porównuje FileVersion i ExpectedVersion jako tekst przez -ne.
            // 0.4.22 i 0.4.22.0 (także wiodące zera) muszą dać mismatch, bez normalizacji Version.
            return IsVersionText(expected) && IsVersionText(installed)
                && !string.Equals(expected, installed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
            // Query potwierdziło rejestrację. Nieczytelna wersja lub niejednoznaczny XML nie mogą pozwolić
            // na zaufanie znacznikowi operatora, tak samo jak cudzy skrypt/katalog albo dodatkowa akcja.
            return false;
        }
    }

    private static bool IsVersionText(string? text) =>
        text is not null && text.All(c => char.IsAsciiDigit(c) || c == '.')
        && Version.TryParse(text, out var version) && version.Build >= 0;
    private static bool SamePath(string? actual, string expected) =>
        !string.IsNullOrWhiteSpace(actual) && Path.IsPathFullyQualified(actual)
        && string.Equals(Path.GetFullPath(actual).TrimEnd('\\', '/'), Path.GetFullPath(expected).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static async Task<QueryResult> QueryAsync(ProcessStartInfo start, CancellationToken ct)
    {
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Nie uruchomiono schtasks.exe.");
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            return new QueryResult(process.ExitCode, output.Result);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        }
    }
}