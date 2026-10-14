using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Windows;

/// <summary>One UAC helper for prepare/commit/rollback. Secrets travel only through a local pipe
/// restricted to the calling Windows identity, never command lines or temporary invitations.
/// Failure modes: UAC denial ends the operation without any change; no connection or no answer within
/// the timeout, a broken pipe and a helper rejection are reported with the helper's own reason; once a
/// commit request has left this process its outcome is decided only by the durable journal — the client
/// never sends an abort after a commit, because the helper would not read it.</summary>
public sealed class GroupSettingsClient : IAsyncDisposable
{
    private const int ErrorCancelled = 1223;
    private readonly NamedPipeServerStream _pipe;
    private readonly IHelperProcess _process;
    private readonly HelperTimeouts _timeouts;
    public Guid Generation { get; private set; }
    /// <summary>Helper acknowledged commit or abort (or refused it after undoing its own attempt).</summary>
    private bool _finished;
    /// <summary>A commit request was attempted: the journal owns the outcome, abort must not be sent.</summary>
    private bool _commitAttempted;
    private GroupSettingsClient(NamedPipeServerStream pipe, IHelperProcess process, HelperTimeouts timeouts)
    { _pipe = pipe; _process = process; _timeouts = timeouts; }
    public static GroupSettingsStore OpenStore() => new(AppPaths.GroupSettingsDir, AppPaths.IsInstalled
        ? DpapiSecretStore.ForMachine(AppPaths.DataDir) : DpapiSecretStore.ForCurrentUser(AppPaths.UserDataDir));

    /// <summary>The operator process is not elevated, the helper is. <see cref="PipeOptions.CurrentUserOnly"/> cannot
    /// bridge that gap: an elevated token's owner is the Administrators group, not the user, so the helper's own
    /// "same user" check rejects the pipe and the connection breaks before the request is read. Instead the operator
    /// process ACLs the pipe to exactly its own user SID (which the elevated helper's token still carries), so only a
    /// process of the same user — elevated or not — can open it. The name is an unpredictable GUID created before the
    /// helper launches and the single instance cannot be pre-empted, so no other process can occupy it.</summary>
    private static NamedPipeServerStream CreateServerPipe(string name)
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, security);
    }

    /// <summary>Elevated side. The server ACL already restricts the pipe to this user's SID, so the client no longer
    /// needs <see cref="PipeOptions.CurrentUserOnly"/> (which would fail the same way from the elevated side).</summary>
    private static NamedPipeClientStream CreateClientPipe(string pipeName)
        => OperatingSystem.IsWindows()
            ? new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)
            : new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public static Task<GroupSettingsClient> PrepareAsync(GroupSettingsProposal proposal, Guid? expectedCurrent, CancellationToken ct)
        => PrepareAsync(proposal, expectedCurrent, ProcessHelperLauncher.Instance, HelperTimeouts.Default, ct);

    internal static async Task<GroupSettingsClient> PrepareAsync(GroupSettingsProposal proposal, Guid? expectedCurrent,
        IHelperLauncher launcher, HelperTimeouts timeouts, CancellationToken ct)
    {
        string name = "MaszynyAlert.Group." + Guid.NewGuid().ToString("N");
        var pipe = CreateServerPipe(name);
        IHelperProcess? process = null;
        try
        {
            try { process = launcher.Launch(name); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { throw new GroupSettingsDeniedException(ex); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(timeouts.Prepare);
            HelperResponse response;
            try
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                var data = proposal.GetData();
                await Write(pipe, new HelperRequest("prepare", Convert.ToBase64String(GroupFileCodec.Serialize(proposal.ExportInvitation())),
                    data.Cluster, expectedCurrent, null), timeout.Token);
                response = await Read<HelperResponse>(pipe, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new GroupSettingsHelperException(GroupSettingsHelperOutcome.NoResponse,
                    $"Proces ustawień nie odpowiedział w ciągu {timeouts.Prepare.TotalSeconds:0} s. Ustawienia grupy nie zostały zatwierdzone; "
                    + "przygotowaną zmianę proces wycofuje sam.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw new GroupSettingsHelperException(GroupSettingsHelperOutcome.Disconnected,
                    "Proces ustawień przerwał połączenie przed przygotowaniem zmiany. Ustawienia grupy nie zostały zatwierdzone.", inner: ex);
            }
            if (!response.Success || response.Generation is not { } id)
                throw new GroupSettingsHelperException(GroupSettingsHelperOutcome.Rejected,
                    response.Error ?? "Zmiana nie została przygotowana. Sprawdź zapis ustawień i zaporę.");
            return new GroupSettingsClient(pipe, process, timeouts) { Generation = id };
        }
        catch { pipe.Dispose(); process?.Dispose(); throw; }
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        if (_finished || _commitAttempted) throw new InvalidOperationException("Zatwierdzenie ustawień grupy już wykonano.");
        _commitAttempted = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(_timeouts.Commit);
        HelperResponse response;
        try
        {
            await Write(_pipe, new HelperRequest("commit", null, null, null, Generation), timeout.Token);
            response = await Read<HelperResponse>(_pipe, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            // The request may have reached the helper. Close our end so a helper still waiting exits, then let it
            // finish (bounded) so the journal the caller consults is final.
            _pipe.Dispose();
            await WaitForHelperExitAsync();
            throw new GroupSettingsHelperException(GroupSettingsHelperOutcome.Uncertain,
                "Nie potwierdzono zatwierdzenia ustawień grupy. Wynik zostanie ustalony z dziennika ustawień.", Generation, ex);
        }
        _finished = true;
        if (!response.Success)
            throw new GroupSettingsHelperException(GroupSettingsHelperOutcome.Rejected,
                response.Error ?? "Nie zatwierdzono ustawień grupy.", Generation);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_finished || _commitAttempted) return;
            using var timeout = new CancellationTokenSource(_timeouts.Abort);
            HelperResponse response;
            try
            {
                if (!_pipe.IsConnected) throw new EndOfStreamException("Proces ustawień zakończył połączenie.");
                await Write(_pipe, new HelperRequest("abort", null, null, null, Generation), timeout.Token);
                response = await Read<HelperResponse>(_pipe, timeout.Token);
            }
            catch (OperationCanceledException ex)
            {
                throw new GroupSettingsHelperException(GroupSettingsHelperOutcome.NoResponse,
                    "Proces ustawień nie potwierdził cofnięcia zmiany w wyznaczonym czasie. Sprawdź niedokończoną generację.", Generation, ex);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw new GroupSettingsHelperException(GroupSettingsHelperOutcome.Disconnected,
                    "Proces ustawień zakończył się bez potwierdzenia cofnięcia zmiany. Sprawdź niedokończoną generację.", Generation, ex);
            }
            if (!response.Success)
                throw new GroupSettingsHelperException(GroupSettingsHelperOutcome.Rejected,
                    response.Error ?? "Cofnięcie ustawień wymaga odzyskania niedokończonej generacji.", Generation);
        }
        finally { _pipe.Dispose(); _process.Dispose(); }
    }

    private async Task WaitForHelperExitAsync()
    {
        using var exit = new CancellationTokenSource(_timeouts.Exit);
        try { await _process.WaitForExitAsync(exit.Token); }
        catch (Exception) { /* still running or handle unavailable: the journal stays authoritative */ }
    }

    public static int RunHelper(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            || !pipeName.StartsWith("MaszynyAlert.Group.", StringComparison.Ordinal)
            || !Guid.TryParseExact(pipeName["MaszynyAlert.Group.".Length..], "N", out _)) return 1;
        return RunHelperAsync(pipeName, AppPaths.GroupSettingsDir,
            () => new GroupApplySession(OpenStore(), new GroupFirewall(Environment.ProcessPath!, AppPaths.GroupSettingsDir), AppPaths.GroupSettingsDir),
            TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
    }

    /// <summary>Elevated side. Any failure first undoes this helper's own attempt (never a committed generation),
    /// releases <c>apply.lock</c> and only then reports the reason to the operator process; exit code 1.
    /// <paramref name="time"/> only for tests (manual clock for the lifetime); <c>null</c> = system clock, as in the program.</summary>
    internal static async Task<int> RunHelperAsync(string pipeName, string directory, Func<GroupApplySession> openWriter, TimeSpan lifetime,
        TimeProvider? time = null)
    {
        using var lifetimeCts = new CancellationTokenSource(lifetime, time ?? TimeProvider.System);
        using var pipe = CreateClientPipe(pipeName);
        try { await pipe.ConnectAsync(30000, lifetimeCts.Token); } catch (Exception) { return 1; }
        string? failure;
        try { failure = await ServeAsync(pipe, directory, openWriter, lifetimeCts.Token); }
        catch (OperationCanceledException) { failure = "Przekroczono limit czasu zmiany ustawień grupy; własna próba została wycofana."; }
        catch (Exception ex) { failure = ex.Message; }
        if (failure is null) return 0;
        using var reply = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await Write(pipe, new HelperResponse(false, null, failure), reply.Token); } catch (Exception) { /* operator process gone */ }
        return 1;
    }

    /// <summary>Returns null on success, otherwise the reason; throws are reported by the caller the same way.
    /// The pending generation of this attempt is rolled back in <c>finally</c> before any reason is sent.</summary>
    private static async Task<string?> ServeAsync(Stream pipe, string directory, Func<GroupApplySession> openWriter, CancellationToken ct)
    {
        // Read the request before any refusal so the reason always reaches the operator process
        // (it would otherwise write into a pipe this side already closed).
        var request = await Read<HelperRequest>(pipe, ct);
        if (request.Operation != "prepare" || request.Invitation is null || request.Cluster is null)
            return "Nieprawidłowe żądanie przygotowania zmiany grupy.";
        Directory.CreateDirectory(directory);
        FileStream applyLock;
        // Held until the rollback in finally completes, so no other writer interleaves with it.
        try
        {
            applyLock = new FileStream(Path.Combine(directory, "apply.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return "Trwa inna zmiana ustawień grupy. Poczekaj na jej zakończenie i spróbuj ponownie."; }
        Guid? pending = null;
        GroupApplySession? writer = null;
        try
        {
            writer = openWriter();
            writer.Recover();
            var invitation = GroupFileCodec.Parse(Convert.FromBase64String(request.Invitation));
            var proposal = new GroupSettingsProposal(invitation, request.Cluster, request.Cluster.Enabled);
            pending = writer.Prepare(proposal, request.ExpectedCurrent);
            await Write(pipe, new HelperResponse(true, pending), ct);
            var finish = await Read<HelperRequest>(pipe, ct);
            if (finish.Generation != pending) return "Żądanie zakończenia dotyczy innej generacji; własna próba została wycofana.";
            if (finish.Operation == "commit") writer.Commit(pending.Value);
            else if (finish.Operation == "abort") writer.Rollback(pending.Value);
            else return "Nieznana operacja zakończenia zmiany grupy; własna próba została wycofana.";
            pending = null;
            await Write(pipe, new HelperResponse(true, finish.Generation), ct);
            return null;
        }
        finally
        {
            if (pending is { } id && writer is not null)
                try { writer.Rollback(id); } catch (Exception) { /* Durable undo record remains for the next writer. */ }
            applyLock.Dispose();
        }
    }

    internal static async Task Write<T>(Stream stream, T value, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 2 * GroupFileCodec.MaxFileBytes) throw new InvalidDataException("Zbyt duży komunikat ustawień.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    internal static async Task<T> Read<T>(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > 2 * GroupFileCodec.MaxFileBytes) throw new InvalidDataException("Nieprawidłowy komunikat ustawień.");
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Brak komunikatu ustawień.");
    }
}
