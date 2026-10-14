using System.Text.Json;

namespace MaszynyAlert.Core.Config;

public sealed record GroupFirewallRule(string Program, int Port, string RemoteAddresses, bool Enabled,
    string? LocalAddress = null);

public interface IGroupFirewall
{
    GroupFirewallRule? Capture();
    void Apply(ClusterConfig config);
    void Restore(GroupFirewallRule? previous);
}

public sealed class GroupApplySession
{
    private readonly GroupSettingsStore _store;
    private readonly IGroupFirewall _firewall;
    private readonly string _undoPath;
    public GroupApplySession(GroupSettingsStore store, IGroupFirewall firewall, string directory)
    { _store = store; _firewall = firewall; _undoPath = Path.Combine(directory, "system-change.json"); }

    public Guid Prepare(GroupSettingsProposal proposal, Guid? expectedCurrent)
    {
        if (File.Exists(_undoPath)) throw new InvalidOperationException("Najpierw odzyskaj niedokończoną zmianę grupy.");
        var previous = _firewall.Capture();
        Guid? foreignPending = _store.ReadForStartup().InterruptedId;
        Guid? id = null;
        try
        {
            id = _store.Stage(proposal, expectedCurrent);
            SaveUndo(new(id.Value, previous));
            _firewall.Apply(proposal.GetData().Cluster); return id.Value;
        }
        catch
        {
            if (id is { } prepared) Rollback(prepared);
            else
            {
                var recovery = _store.ReadForStartup();
                if (recovery.CommittedId == expectedCurrent && recovery.InterruptedId is { } interrupted
                    && interrupted != foreignPending)
                    _store.Abort(interrupted);
            }
            throw;
        }
    }
    public void Commit(Guid id)
    {
        _store.Commit(id);
        // Commit is the point of no return. Cleanup failure must never turn it into a rollback.
        try { File.Delete(_undoPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Rollback(Guid id)
    {
        var recovery = _store.Read();
        if (recovery.CommittedId == id) return;
        if (File.Exists(_undoPath))
        {
            var undo = ReadUndo();
            if (undo.Generation != id) throw new InvalidDataException("Nieprawidłowa generacja cofania zapory.");
            _firewall.Restore(undo.Previous);
        }
        _store.Abort(id);
        File.Delete(_undoPath);
    }
    public void Recover()
    {
        var recovery = _store.Read();
        if (File.Exists(_undoPath))
        {
            var undo = ReadUndo();
            if (recovery.CommittedId == undo.Generation) File.Delete(_undoPath);
            else if (recovery.InterruptedId == undo.Generation) Rollback(undo.Generation);
            else throw new InvalidDataException("Dzienniki zmiany grupy nie są zgodne.");
        }
        else if (recovery.InterruptedId is { } pending) _store.Abort(pending);
    }
    private Undo ReadUndo()
    {
        var bytes = File.ReadAllBytes(_undoPath);
        if (bytes.Length > 32000) throw new InvalidDataException("Uszkodzony dziennik zapory.");
        var undo = JsonSerializer.Deserialize<Undo>(bytes) ?? throw new InvalidDataException("Uszkodzony dziennik zapory.");
        if (undo.Generation == Guid.Empty) throw new InvalidDataException("Uszkodzony dziennik zapory.");
        return undo;
    }
    private void SaveUndo(Undo undo)
    {
        string temp = _undoPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(JsonSerializer.SerializeToUtf8Bytes(undo)); stream.Flush(true); }
            File.Move(temp, _undoPath);
        }
        finally { File.Delete(temp); }
    }
    private sealed record Undo(Guid Generation, GroupFirewallRule? Previous);
}
