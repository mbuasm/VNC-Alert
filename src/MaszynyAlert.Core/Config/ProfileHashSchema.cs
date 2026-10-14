using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MaszynyAlert.Core.Config;

internal sealed record ProfileHashSchema(IReadOnlySet<string> Frozen, IReadOnlyDictionary<string, string> OmittedWhenDefault)
{
    /// <summary>
    /// Pola profilu grupy w wydaniu 0.4.79 — lista
    /// (<c>tests/MaszynyAlert.Core.Tests/Config/OdciskProfilu-0.4.79/pola.txt</c>). Nie zmieniaj jej.
    /// </summary>
    internal static readonly IReadOnlySet<string> Fields0479 = new HashSet<string>(StringComparer.Ordinal)
    {
        "AbortPattern.maxMismatch", "AbortPattern.maxShift", "AbortPattern.rows", "AbortPattern.whiteMinChannel",
        "AbortPattern.x", "AbortPattern.y",
        "AlarmCode.category", "AlarmCode.code", "AlarmCode.description", "AlarmCode.from", "AlarmCode.priority",
        "AlarmCode.shortText", "AlarmCode.to",
        "GroupProfile.instructions", "GroupProfile.machines", "GroupProfile.ocr", "GroupProfile.profiles",
        "GroupProfile.rules", "GroupProfile.timing",
        "HsvRange.hueMaxDeg", "HsvRange.hueMinDeg", "HsvRange.minSaturation", "HsvRange.minValue",
        "IdleWatchProfile.enabled", "IdleWatchProfile.fullAtFraction", "IdleWatchProfile.fullAtPercent",
        "IdleWatchProfile.invertForOcr", "IdleWatchProfile.mode", "IdleWatchProfile.ocrMarginPx",
        "IdleWatchProfile.ocrScale", "IdleWatchProfile.progressBar", "IdleWatchProfile.progressFill",
        "IdleWatchProfile.progressRoi",
        "MachineConfig.comment", "MachineConfig.enabled", "MachineConfig.host", "MachineConfig.id",
        "MachineConfig.mode", "MachineConfig.name", "MachineConfig.port", "MachineConfig.profile",
        "MachineConfig.requiresPassword", "MachineConfig.rsaAesKeySha256", "MachineConfig.username",
        "MainScreenWatchProfile.anchorLabels", "MainScreenWatchProfile.contextRoi", "MainScreenWatchProfile.enabled",
        "MainScreenWatchProfile.maxDistance", "MainScreenWatchProfile.otherScreenLabels",
        "MainScreenWatchProfile.retryScaled", "MainScreenWatchProfile.statusBarAlwaysVisible",
        "OcrConfig.engine", "OcrConfig.language",
        "ProgramStateProfile.abortMaxDistance", "ProgramStateProfile.abortPattern", "ProgramStateProfile.abortWords",
        "ProgramStateProfile.boxRoi", "ProgramStateProfile.enabled", "ProgramStateProfile.runFill",
        "ProgramStateProfile.runMinFraction",
        "ProgressBarSource.columnFillMinFraction", "ProgressBarSource.columnsOffset",
        "ProgressBarSource.columnsPerPercent", "ProgressBarSource.enabled", "ProgressBarSource.fill",
        "ProgressBarSource.surroundMaxValue", "ProgressBarSource.surroundRows",
        "ProgressBarSource.trackMaxSaturation", "ProgressBarSource.trackMinValue", "ProgressBarSource.trackRoi",
        "RedThresholds.hueMaxDeg", "RedThresholds.hueMinDeg", "RedThresholds.minSaturation", "RedThresholds.minValue",
        "Rule.category", "Rule.id", "Rule.maxDistance", "Rule.pattern", "Rule.phrases", "Rule.priority",
        "Rule.profile", "Rule.shortText",
        "RuleDatabase.codeIdPrefix", "RuleDatabase.codePattern", "RuleDatabase.codes", "RuleDatabase.description",
        "RuleDatabase.name", "RuleDatabase.rules",
        "RuleSet.codes", "RuleSet.databases", "RuleSet.rules",
        "ScreenProfile.darkTheme", "ScreenProfile.description", "ScreenProfile.id", "ScreenProfile.idle",
        "ScreenProfile.mainScreen", "ScreenProfile.minRedFraction", "ScreenProfile.ocrMarginPx",
        "ScreenProfile.ocrScale", "ScreenProfile.programState", "ScreenProfile.red", "ScreenProfile.statusBarRoi",
        "TimingConfig.ackMemoryHours", "TimingConfig.alertSoundRepeatMs", "TimingConfig.connectTimeoutMs",
        "TimingConfig.episodeBreakAfterMs", "TimingConfig.episodeCloseAfterMs", "TimingConfig.escalationAfterAckMs",
        "TimingConfig.fullFrameIntervalMs", "TimingConfig.idleAlertAfterMs", "TimingConfig.idleAtZeroAfterMs",
        "TimingConfig.idlePopupSeconds", "TimingConfig.idleUpdateTimeoutMs", "TimingConfig.mainScreenCheckIntervalMs",
        "TimingConfig.mainScreenOcrGraceMs", "TimingConfig.ocrMaxAttempts", "TimingConfig.ocrRetryIntervalMs",
        "TimingConfig.offlineAlertAfterMs", "TimingConfig.offlineAlertSound", "TimingConfig.pollIntervalMs",
        "TimingConfig.pollingModeIntervalMs", "TimingConfig.readTimeoutMs", "TimingConfig.reconnectBackoffMs",
        "TimingConfig.sameMessageRejoinMs", "TimingConfig.wrongScreenAlertAfterMs",
        "TimingConfig.wrongScreenAlertSound", "TimingConfig.wrongScreenRemindAfterMs",
    };

    internal static readonly IReadOnlyDictionary<string, string> AddedAfter0479 =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Reguła, którą liczy program.</summary>
    internal static ProfileHashSchema Current { get; } = new(Fields0479, AddedAfter0479);

    private Dictionary<string, JsonElement>? _defaults;

    /// <summary>Czy pole pominąć w odcisku: tylko pole spoza zamrożonej listy, zarejestrowane, w wartości domyślnej.</summary>
    internal bool Omits(string field, JsonElement value)
    {
        if (Frozen.Contains(field)) return false;
        _defaults ??= OmittedWhenDefault.ToDictionary(p => p.Key, p => ParseDefault(p.Value), StringComparer.Ordinal);
        return _defaults.TryGetValue(field, out var defaultValue) && JsonElement.DeepEquals(value, defaultValue);
    }

    /// <summary>Wszystkie pola (klucze „Typ.pole”) typów obiektowych osiągalnych z <paramref name="root"/>.</summary>
    internal static IReadOnlySet<string> FieldsOf(Type root)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in TypesOf(root))
            foreach (var property in GroupFileCodec.Options.GetTypeInfo(type).Properties)
                fields.Add(Key(type, property.Name));
        return fields;
    }

    /// <summary>Typy obiektowe (rekordy/klasy z polami JSON) osiągalne z <paramref name="root"/>.</summary>
    internal static IReadOnlyList<Type> TypesOf(Type root)
    {
        var seen = new HashSet<Type>();
        var objects = new List<Type>();
        void Walk(Type type)
        {
            if (!seen.Add(type)) return;
            var info = GroupFileCodec.Options.GetTypeInfo(type);
            if (info.Kind == JsonTypeInfoKind.Object)
            {
                objects.Add(type);
                foreach (var property in info.Properties) Walk(property.PropertyType);
            }
            else if (info.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary && info.ElementType is { } element)
                Walk(element);
        }
        Walk(root);
        return objects;
    }

    internal static string Key(Type type, string jsonName) => type.Name + "." + jsonName;

    private static JsonElement ParseDefault(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
