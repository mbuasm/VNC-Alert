namespace MaszynyAlert.Core.Config;

/// <summary>Allowlisted shared data. No paths, UI preferences, global enable switch or secrets.</summary>
public sealed record GroupProfile
{
    public required List<MachineConfig> Machines { get; init; }
    public required Dictionary<string, ScreenProfile> Profiles { get; init; }
    public required TimingConfig Timing { get; init; }
    public required OcrConfig Ocr { get; init; }
    public required RuleSet Rules { get; init; }
    public required Dictionary<string, string> Instructions { get; init; }

    public static GroupProfile Capture(AppConfig config, RuleSet rules, IReadOnlyDictionary<string, string> instructions) =>
        GroupFileCodec.Clone(new GroupProfile
        {
            Machines = config.Machines, Profiles = config.Profiles, Timing = config.Timing,
            Ocr = config.Ocr, Rules = rules, Instructions = new(instructions, StringComparer.Ordinal)
        });

    public AppConfig ApplyTo(AppConfig local)
    {
        var copy = GroupFileCodec.Clone(this);
        return local with { Machines = copy.Machines, Profiles = copy.Profiles, Timing = copy.Timing, Ocr = copy.Ocr };
    }
}
