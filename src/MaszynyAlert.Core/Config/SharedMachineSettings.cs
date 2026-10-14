namespace MaszynyAlert.Core.Config;

public sealed record SharedMachineSettings
{
    /// <summary>Czy maszyna ma być obserwowana i pokazywana (nadpisuje <see cref="MachineConfig.Enabled"/>).</summary>
    public bool Enabled { get; init; } = true;
}
