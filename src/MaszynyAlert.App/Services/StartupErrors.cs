using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Services;

internal static class StartupErrors
{
    private const uint MbOk = 0x0;
    private const uint MbIconError = 0x10;
    private const uint MbSetForeground = 0x10000;
    private const uint MbTopMost = 0x40000;

    /// <summary>Katalog logu, gdy konfiguracji nie udało się wczytać (domyślne <c>paths.logsDir</c>).</summary>
    internal static string DefaultLogsDir => AppPaths.Resolve(new MaszynyAlert.Core.Config.PathsConfig().LogsDir);

    /// <summary>
    /// Dopisuje wpis w formacie logu programu. Nigdy nie rzuca — brak prawa zapisu do logu nie może zasłonić
    /// właściwego błędu. Zwraca ścieżkę pliku albo <see langword="null"/>, gdy zapis się nie udał.
    /// </summary>
    internal static string? AppendLog(string logsDir, DateTimeOffset now, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            string path = Path.Combine(logsDir, "app-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            var line = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [ERR] MaszynyAlert.App.Program: ")
                .AppendLine(message);
            if (exception is not null) line.AppendLine(exception.ToString());
            File.AppendAllText(path, line.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string MissingDefaultsMessage(string configPath, string rulesPath, Exception exception) =>
        $"Brakuje pliku konfiguracji ('{configPath}' albo '{rulesPath}'), a program nie może go odtworzyć z przykładu, "
        + "bo nie ma prawa zapisu w katalogu danych. Uruchom ponownie instalator VNC Alert (odtworzy brakujące pliki) "
        + $"albo skopiuj plik jako administrator. Szczegóły: {exception.Message}";

    /// <summary>Treść okna: przyczyna + co zrobić + gdzie jest log.</summary>
    internal static string WindowText(string reason, string? logPath) =>
        reason + Environment.NewLine + Environment.NewLine
        + "VNC Alert nie działa. Popraw przyczynę i uruchom program ponownie (skrót „VNC Alert”)."
        + (logPath is null ? string.Empty : Environment.NewLine + "Zapisano w logu: " + logPath);

    /// <summary>
    /// Pokazuje okno błędu na wierzchu i czeka na „OK”. Sygnał komendy <c>vncalert killapp</c> kończy proces
    /// w trakcie czekania (mutex jednej instancji znika razem z procesem).
    /// </summary>
    internal static void Show(string title, string text, StopRequest stopRequest)
    {
        // Wyjątek po starcie WPF: nasłuch już działa (zamyka aplikację przez dyspozytor, który wtedy nie żyje).
        try { stopRequest.Listen(() => Environment.Exit(0)); }
        catch (InvalidOperationException) { }
        _ = MessageBoxW(IntPtr.Zero, text, title, MbOk | MbIconError | MbTopMost | MbSetForeground);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
