using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.App.Tests;

public sealed class UpdateTaskRegistrationTests : IDisposable
{
    private const string AppDirectory = @"C:\VNC Alert D01";
    private const string DataDirectory = @"C:\D01 Data";
    private const string CurrentVersion = "0.4.22.0";
    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "vnc-d01-" + Guid.NewGuid().ToString("N"));

    private static string Arguments(string version = "0.4.21.0") =>
        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{AppDirectory}\\installer\\Update-Task.ps1\" " +
        $"-ApplicationDirectory \"{AppDirectory}\" -DataDirectory \"{DataDirectory}\" -ExpectedVersion {version}";

    private static string Definition(string? arguments = null, string ns = TaskNamespace)
    {
        XNamespace n = ns;
        return new XElement(n + "Task", new XElement(n + "Actions", new XElement(n + "Exec",
            new XElement(n + "Command", Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")),
            new XElement(n + "Arguments", arguments ?? Arguments()),
            new XElement(n + "WorkingDirectory", AppDirectory)))).ToString();
    }

    private static UpdateTaskRegistration Probe(string xml, int exitCode = 0, string? version = CurrentVersion) =>
        new(AppDirectory, DataDirectory, async (_, ct) =>
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return new UpdateTaskRegistration.QueryResult(exitCode, xml);
        }, () => version);

    [Theory]
    [InlineData("0.4.21.0", false)]
    [InlineData("0.4.23.0", false)]
    [InlineData("0.4.22.0", true)]
    [InlineData("0.04.022.0", false)]
    public void D01_KnownTaskUsesTaskScriptVersionComparison(string registered, bool usable) =>
        Assert.Equal(usable, Probe(Definition(Arguments(registered))).IsUsable());

    [Fact]
    public void D01_KnownTaskAllowsQuotedVersionWhitespaceCaseAndXmlPrefix()
    {
        string arguments = Arguments("\"0.4.21.0\"").ToUpperInvariant().Replace(" -", "   -");
        var xml = XDocument.Parse(Definition(arguments));
        xml.Root!.SetAttributeValue(XNamespace.Xmlns + "t", TaskNamespace);
        XNamespace n = TaskNamespace;
        xml.Root.Element(n + "Actions")!.Element(n + "Exec")!.Element(n + "WorkingDirectory")!.Value = AppDirectory.ToUpperInvariant();
        Assert.False(Probe(xml.ToString()).IsUsable());
    }

    [Theory]
    [InlineData("missing-version")]
    [InlineData("duplicate-version")]
    [InlineData("bad-version")]
    [InlineData("foreign-script")]
    [InlineData("foreign-app")]
    [InlineData("foreign-data")]
    [InlineData("single-quotes")]
    [InlineData("trailing-command")]
    public void D01_UnrecognizedArgumentsRemainRegistered(string scenario)
    {
        string arguments = Arguments();
        arguments = scenario switch
        {
            "missing-version" => arguments.Replace(" -ExpectedVersion 0.4.21.0", ""),
            "duplicate-version" => arguments + " -ExpectedVersion 0.4.22.0",
            "bad-version" => arguments.Replace("0.4.21.0", "nie-wersja"),
            "foreign-script" => arguments.Replace("Update-Task.ps1", "Other-Task.ps1"),
            "foreign-app" => arguments.Replace($"-ApplicationDirectory \"{AppDirectory}\"", "-ApplicationDirectory \"C:\\Inny\""),
            "foreign-data" => arguments.Replace(DataDirectory, @"C:\Inne dane"),
            "single-quotes" => arguments.Replace('"', '\''),
            "trailing-command" => arguments + " -Command anything",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.True(Probe(Definition(arguments)).IsUsable());
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrong-namespace")]
    [InlineData("no-namespace")]
    [InlineData("duplicate-exec")]
    [InlineData("duplicate-arguments")]
    [InlineData("foreign-command")]
    [InlineData("foreign-working-dir")]
    [InlineData("external-dtd")]
    public void D01_UnrecognizedXmlRemainsRegistered(string scenario)
    {
        string definition = Definition();
        if (scenario == "malformed") definition = "<Task";
        else if (scenario == "wrong-namespace") definition = Definition(ns: "urn:other");
        else if (scenario == "no-namespace") definition = Definition(ns: "");
        else if (scenario == "external-dtd") definition = "<!DOCTYPE Task SYSTEM 'file:///never-read'>" + definition;
        else
        {
            XNamespace n = TaskNamespace;
            var xml = XDocument.Parse(definition);
            XElement actions = xml.Root!.Element(n + "Actions")!;
            XElement exec = actions.Element(n + "Exec")!;
            if (scenario == "duplicate-exec") actions.Add(new XElement(exec));
            if (scenario == "duplicate-arguments") exec.Add(new XElement(exec.Element(n + "Arguments")!));
            if (scenario == "foreign-command") exec.Element(n + "Command")!.Value = @"C:\Inny\powershell.exe";
            if (scenario == "foreign-working-dir") exec.Element(n + "WorkingDirectory")!.Value = @"C:\Inny";
            definition = xml.ToString();
        }
        Assert.True(Probe(definition).IsUsable());
    }

    [Fact]
    public void D01_MissingFileVersionDoesNotDowngradeRegisteredTask() =>
        Assert.True(Probe(Definition(), version: null).IsUsable());

    [Fact]
    public void D01_QueryFailureKeepsExistingNoTaskFallback() =>
        Assert.False(Probe("", exitCode: 1).IsUsable());

    [Fact]
    public void D01_QueryIsReadOnlyBoundedAndUncached()
    {
        int calls = 0;
        var probe = new UpdateTaskRegistration(AppDirectory, DataDirectory, async (start, ct) =>
        {
            await Task.Yield();
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), start.FileName);
            Assert.Equal(new[] { "/query", "/tn", UpdateTaskContract.TaskName, "/xml" }, start.ArgumentList);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.True(ct.CanBeCanceled);
            return new UpdateTaskRegistration.QueryResult(0, Definition(Arguments(++calls == 1 ? "0.4.21.0" : CurrentVersion)));
        }, () => CurrentVersion);
        Assert.Equal(TimeSpan.FromSeconds(10), UpdateTaskRegistration.QueryLimit);
        Assert.False(probe.IsUsable());
        Assert.True(probe.IsUsable());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task D01_NonAnsweringQueryTimesOutWithoutExecutingAnyProcess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<UpdateTaskRegistration.QueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new UpdateTaskRegistration(AppDirectory, DataDirectory, (_, _) =>
        {
            entered.SetResult();
            return never.Task;
        }, () => CurrentVersion, queryLimit: TimeSpan.FromMilliseconds(50));
        Task<bool> query = Task.Run(probe.IsUsable);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(await query.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task D01_MismatchedRegisteredTaskUsesUserPendingAndDoesNotRetryCancelledInstallAfterRestart()
    {
        Directory.CreateDirectory(Path.Combine(Admin, UpdateCoordinator.PendingInstallFileName));
        UpdateTaskRegistration registration = Probe(Definition());
        var installer = new CancelledInstaller(registration.IsUsable);
        SharedConfigSync shared = Approve();

        await Coordinator(shared, installer, registration.IsUsable).EvaluateAsync(default);
        await Coordinator(shared, installer, registration.IsUsable).EvaluateAsync(default);

        Assert.Single(installer.UsesTask);
        Assert.False(installer.UsesTask[0]);
        Assert.True(File.Exists(Path.Combine(Downloads, UpdateCoordinator.PendingInstallFileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task D01_MatchingOrUnrecognizedTaskIgnoresForgedUserPending(bool unrecognized)
    {
        Directory.CreateDirectory(Downloads);
        File.WriteAllText(Path.Combine(Downloads, UpdateCoordinator.PendingInstallFileName),
            JsonSerializer.Serialize(new { version = "0.4.26.0", startedAt = DateTimeOffset.UnixEpoch }));
        UpdateTaskRegistration registration = Probe(unrecognized ? "<Task" : Definition(Arguments(CurrentVersion)));
        var installer = new CancelledInstaller(registration.IsUsable);

        await Coordinator(Approve(), installer, registration.IsUsable).EvaluateAsync(default);

        Assert.Single(installer.UsesTask);
        Assert.True(installer.UsesTask[0]);
        Assert.True(File.Exists(Path.Combine(Admin, UpdateCoordinator.PendingInstallFileName)));
    }

    [Fact]
    public void D01_UnquotedPathsWithoutSpacesAreRecognized()
    {
        string xml = Definition().Replace(AppDirectory, @"C:\D01").Replace(DataDirectory, @"C:\D01Data");
        var document = XDocument.Parse(xml);
        XNamespace ns = TaskNamespace;
        XElement arguments = document.Descendants(ns + "Arguments").Single();
        arguments.Value = arguments.Value.Replace("\"", "");
        var probe = new UpdateTaskRegistration(@"C:\D01", @"C:\D01Data", async (_, _) =>
        {
            await Task.Yield();
            return new UpdateTaskRegistration.QueryResult(0, document.ToString());
        }, () => CurrentVersion);
        Assert.False(probe.IsUsable());
    }

    [Fact]
    public void D01_UnreadableFileVersionKeepsProtectedMarker()
    {
        var probe = new UpdateTaskRegistration(AppDirectory, DataDirectory, async (_, _) =>
        {
            await Task.Yield();
            return new UpdateTaskRegistration.QueryResult(0, Definition());
        }, () => throw new UnauthorizedAccessException("Atrapa wersji EXE"));
        Assert.True(probe.IsUsable());
    }

    [Fact]
    public void D01_BlockingQueryWorkDoesNotRunOnUiThread()
    {
        WpfTestHost.Run(() =>
        {
            int uiThread = Environment.CurrentManagedThreadId;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Task action = UpdateService.RunOnWorkerAsync(_ =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test nie zwolnił atrapy.");
                return Task.CompletedTask;
            }, default);
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
                Assert.False(action.IsCompleted);
                WpfTestHost.Pump(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            finally { release.Set(); }
            action.GetAwaiter().GetResult();
        });
    }
    [Theory]
    [InlineData("0.4.22", "0.4.22.0", false)]
    [InlineData("0.4.22.0", "0.4.22", false)]
    [InlineData("0.04.022.0", "0.4.22.0", false)]
    [InlineData("0.4.22", "0.4.22", true)]
    public void D01_TaskScriptDoesNotNormalizeVersionStrings(string expected, string installed, bool usable) =>
        Assert.Equal(usable, Probe(Definition(Arguments(expected)), version: installed).IsUsable());
    private string Admin => UpdateTaskContract.AdminDirectory(_temp);
    private string Downloads => UpdateTaskContract.DownloadDirectory(_temp);
    private SharedConfigSync Approve()
    {
        var shared = new SharedConfigSync(new SharedConfigStore(Path.Combine(_temp, "shared.json")), "TEST-D01");
        shared.Approve(new UpdateApproval
        {
            Version = "0.4.26.0", FileName = FakeSource.Release.FileName, ApprovedBy = "TEST-OTHER", ApprovedAt = DateTimeOffset.UnixEpoch,
        }, DateTimeOffset.UnixEpoch);
        return shared;
    }
    private UpdateCoordinator Coordinator(SharedConfigSync shared, IUpdateInstaller installer, Func<bool> taskRegistered) =>
        new(new UpdatesConfig(), true, Version.Parse(CurrentVersion), new FakeSource(), installer, shared, _ => Task.CompletedTask,
            Downloads, "TEST-D01", new TestClock(), () => true, NullLogger<UpdateCoordinator>.Instance, Admin, taskRegistered);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public DateTimeOffset Now => UtcNow;
        public long MonotonicMs => 0;
    }
    private sealed class FakeSource : IUpdateSource
    {
        private static readonly byte[] Bytes = "D01 inert installer sample"u8.ToArray();
        internal static readonly ReleaseEntry Release = new("VncAlert-Setup-0.4.26.0.exe", new Version(0, 4, 26, 0),
            Bytes.Length, Convert.ToHexString(SHA256.HashData(Bytes)), DateTimeOffset.UnixEpoch);
        public async Task<UpdateCheckResult> CheckAsync(Version installed, CancellationToken ct)
        {
            await Task.Yield();
            return new(UpdateCheckOutcome.Available, Release, null, DateTimeOffset.UnixEpoch) { Releases = [Release] };
        }
        public async Task<string> DownloadAsync(ReleaseEntry release, string targetDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Directory.CreateDirectory(targetDirectory);
            string path = Path.Combine(targetDirectory, release.FileName);
            await File.WriteAllBytesAsync(path, Bytes, ct);
            return path;
        }
    }
    private sealed class CancelledInstaller(Func<bool> usesTask) : IUpdateInstaller
    {
        public List<bool> UsesTask { get; } = [];
        public async Task<(InstallLaunchResult Result, string? Error)> LaunchAsync(string setupPath, Version version, string expectedSha256, CancellationToken ct)
        {
            UsesTask.Add(usesTask());
            await Task.Yield();
            return (InstallLaunchResult.CancelledByUser, "Atrapa: anulowanie, bez procesu i UAC.");
        }
    }
    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }
}