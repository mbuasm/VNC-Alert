using System.Diagnostics;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Windows;

public static class UpdateTask
{
    public static bool IsRegistered(string taskName = UpdateTaskContract.TaskName)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/query", "/tn", taskName }) start.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Nie uruchomiono schtasks.exe.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return false;
            }
            Task.WaitAll([output, errors], TimeSpan.FromSeconds(2));
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
