using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Config;

/// <summary>Bounded, strict invitation codec. Validation errors never echo imported content or secrets.</summary>
public static class GroupFileCodec
{
    public const int MaxFileBytes = 1024 * 1024;
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object) return;
            // Remove computed properties (including read-only collections such as EffectiveAnchorLabels).
            foreach (var property in info.Properties.Where(p => p.Set is null).ToArray())
                info.Properties.Remove(property);
        });
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true, MaxDepth = 32, TypeInfoResolver = resolver
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new StrictRectConverter());
        return options;
    }

    public static GroupFile Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = stream.Read(chunk, 0, Math.Min(chunk.Length, MaxFileBytes + 1 - (int)buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, count);
            if (buffer.Length > MaxFileBytes) throw InvalidFile(TooBig);
        }
        return Parse(buffer.ToArray());
    }

    public static GroupFile Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxFileBytes) throw InvalidFile(TooBig);
        try
        {
            RejectDuplicateProperties(bytes);
            var file = JsonSerializer.Deserialize<GroupFile>(bytes, Options)
                ?? throw InvalidFile("plik nie zawiera ustawień grupy");
            Validate(file);
            // Odcisk profilu WYLICZAMY z profilu, który faktycznie wczytaliśmy — patrz Renew.
            return Renew(file);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or ConfigException or OverflowException
            or InvalidOperationException or FormatException)
        {
            throw InvalidFile("plik nie jest poprawnym zapisem ustawień grupy (uszkodzony JSON albo nieznane pole)");
        }
    }

    public static byte[] Serialize(GroupFile file)
    {
        Validate(file);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Renew(file), Options);
        if (bytes.Length > MaxFileBytes) throw InvalidFile("zapis ustawień grupy przekracza 1 MiB");
        return bytes;
    }

    public static GroupFile Renew(GroupFile file)
    {
        string hash = ProfileHash(file.Profile);
        return string.Equals(file.Identity.ProfileHash, hash, StringComparison.OrdinalIgnoreCase)
            ? file : file with { Identity = file.Identity with { ProfileHash = hash } };
    }

    public static void Validate(GroupFile file)
    {
        try
        {
            if (file.SchemaVersion != GroupFile.CurrentSchemaVersion)
                throw InvalidFile("nieobsługiwana wersja pliku grupy");
            if (file.Identity is null || file.Profile is null || file.Peers is null)
                throw InvalidFile("brak wymaganej sekcji pliku grupy");
            if (file.Peers.Count > 128) throw InvalidFile("lista komputerów grupy jest dłuższa niż 128 wpisów");
            if (file.UdpPort is < 1 or > 65535) throw InvalidFile("port UDP grupy jest poza zakresem 1–65535");
            if (!IsNetworkKey(file.NetworkKey)) throw InvalidFile("klucz sieciowy grupy nie ma wymaganych 32 bajtów");
            if (file.Peers.Any(p => !IsUnicastIpv4(p)))
                throw InvalidFile("adres komputera grupy nie jest zwykłym adresem IPv4");
            file.Identity.Validate();
            ValidateProfile(file.Profile);
            // Odcisku profilu tu NIE porównujemy — patrz Renew: niezgodny odcisk znaczy „profil z innej
            // wersji programu”, a nie „uszkodzony plik”.
        }
        catch (Exception ex) when (ex is ArgumentException or ConfigException or JsonException or OverflowException)
        {
            // Treści wyjątku NIE dopisujemy: komunikaty ConfigLoader cytują pola profilu, a ten kodek
            // z założenia nie wypisuje zawartości importowanego pliku.
            throw InvalidFile("tożsamość grupy albo profil nie przechodzą kontroli ustawień");
        }
    }

    internal static bool IsNetworkKey(string? key)
    {
        if (key is null || key.Length != 44) return false;
        Span<byte> decoded = stackalloc byte[32];
        try { return Convert.TryFromBase64String(key, decoded, out int count) && count == 32; }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }

    internal static bool IsUnicastIpv4(string? text) => IPAddress.TryParse(text, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] is > 0 and < 224
        && !ip.Equals(IPAddress.Broadcast);

    internal static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)!;

    public static string ProfileHash(GroupProfile profile) =>
        Fingerprint(profile with { Instructions = new(StringComparer.Ordinal) }, ProfileHashSchema.Current);

    /// <summary>Odcisk SHA-256 kanonicznego JSON-a wartości według <paramref name="schema"/> (testy: dowolny typ).</summary>
    internal static string Fingerprint<T>(T value, ProfileHashSchema schema)
    {
        var type = Options.GetTypeInfo(typeof(T));
        // Sort object keys ordinally at every level. Preserve array order: it affects rule precedence.
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, type));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, document.RootElement, type, schema);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void ValidateRuleLists(List<AlarmCode> codes, List<Rule> rules)
    {
        foreach (var rule in rules)
            if (rule is null || rule.Pattern?.Length > 4000 || rule.Phrases is null
                || rule.Phrases.Length > 128 || rule.Phrases.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 1000)
                || rule.MaxDistance is < 0 or > 20)
                throw InvalidFile("reguła alarmu ma za długi wzorzec, pustą frazę albo zły próg podobieństwa");
        if (codes.Any(c => c is null)) throw InvalidFile("słownik kodów alarmów ma pusty wpis");
    }

    internal static void ValidateProfile(GroupProfile profile)
    {
        if (profile.Machines is null || profile.Profiles is null || profile.Timing is null
            || profile.Ocr is null || profile.Rules is null || profile.Instructions is null
            || profile.Rules.Codes is null || profile.Rules.Rules is null || profile.Rules.Databases is null
            || profile.Timing.ReconnectBackoffMs is null)
            throw InvalidFile("profil grupy nie ma wszystkich wymaganych sekcji");
        if (profile.Machines.Count is < 1 or > 128 || profile.Profiles.Count is < 1 or > 64
            || profile.Rules.Codes.Count > 2048 || profile.Rules.Rules.Count > 2048
            || profile.Instructions.Count > 2048 || profile.Timing.ReconnectBackoffMs.Length is < 1 or > 32)
            throw InvalidFile("profil grupy przekracza limit liczby maszyn, profili ekranu, reguł albo instrukcji");
        foreach (var machine in profile.Machines)
            if (machine is null || !SafeId(machine.Id) || string.IsNullOrWhiteSpace(machine.Name)
                || !Enum.IsDefined(machine.Mode))
                throw InvalidFile("wpis maszyny nie ma poprawnego identyfikatora, nazwy albo trybu połączenia");
        if (profile.Machines.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Machines.Count)
            throw InvalidFile("dwie maszyny mają ten sam identyfikator"); // IDs also name local capture/test files on Windows.
        foreach (var (id, screen) in profile.Profiles)
        {
            if (!SafeId(id) || screen is null || screen.Id != id
                || screen.OcrScale is < 1 or > 8 || screen.OcrMarginPx is < 0 or > 100)
                throw InvalidFile("profil ekranu ma zły identyfikator, skalę OCR albo margines");
            ValidateRect(screen.StatusBarRoi, "paska statusu");
            if (screen.MainScreen is { } main && (main.AnchorLabels is null || main.AnchorLabels.Count > 32
                || main.AnchorLabels.Any(v => v is null || v.Count is < 1 or > 32 || v.Any(string.IsNullOrWhiteSpace))
                || main.OtherScreenLabels is null || main.OtherScreenLabels.Count > 32
                || main.OtherScreenLabels.Any(v => v is null || v.Count is < 1 or > 32 || v.Any(string.IsNullOrWhiteSpace))))
                throw InvalidFile("etykiety ekranu głównego są puste albo jest ich za dużo");
            if (screen.Idle is { } idle)
            {
                if (idle.ProgressFill is null || !Enum.IsDefined(idle.Mode))
                    throw InvalidFile("sekcja postoju profilu ekranu jest niekompletna");
                if (idle.Enabled) ValidateRect(idle.ProgressRoi, "pola postępu");
                if (idle.ProgressBar is { } bar)
                {
                    if (bar.Fill is null)
                        throw InvalidFile("sekcja paska postępu profilu ekranu jest niekompletna");
                    if (idle.Enabled && bar.Enabled) ValidateRect(bar.TrackRoi, "toru paska postępu");
                }
            }
            if (screen.ProgramState is { } state)
            {
                if (state.RunFill is null || state.AbortWords is null || state.AbortWords.Count > 32
                    || state.AbortWords.Any(w => w is null || w.Length > 64)
                    || (state.AbortPattern is { } pattern && (pattern.Rows is null || pattern.Rows.Count > 256
                        || pattern.Rows.Any(r => r is null || r.Length > 1024))))
                    throw InvalidFile("sekcja stanu programu profilu ekranu jest niekompletna albo za duża");
                if (state.Enabled) ValidateRect(state.BoxRoi, "pudełka stanu programu");
            }
        }
        ValidateRuleLists(profile.Rules.Codes, profile.Rules.Rules);
        if (profile.Rules.Databases.Count > 64)
            throw InvalidFile("profil grupy przekracza limit liczby baz błędów");
        foreach (var (key, database) in profile.Rules.Databases)
        {
            if (!SafeId(key) || database is null || database.Codes is null || database.Rules is null
                || database.Codes.Count > 2048 || database.Rules.Count > 2048
                || database.CodePattern?.Length > 4000 || database.CodeIdPrefix?.Length > 64
                || database.Name?.Length > 200 || database.Description?.Length > 4000)
                throw InvalidFile("baza błędów ma zły klucz, brakującą sekcję albo przekracza limit wielkości");
            ValidateRuleLists(database.Codes, database.Rules);
        }
        foreach (var (key, text) in profile.Instructions)
        {
            if (text is null) throw InvalidFile("instrukcja naprawy jest pusta");
            AlarmInstructionStore.Validate(key, text);
        }
        var config = profile.ApplyTo(new AppConfig());
        ConfigLoader.Validate(config);
        ConfigLoader.Validate(profile.Rules, config);
    }

    private static bool SafeId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 100
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void ValidateRect(RectInt rect, string what)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0
            || (long)rect.X + rect.Width > 8192 || (long)rect.Y + rect.Height > 8192)
            throw InvalidFile($"obszar {what} na ekranie jest poza zakresem 0–8192 pikseli");
    }

    internal static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw InvalidFile("ten sam klucz powtarza się w pliku");
        }
    }

    /// <summary>
    /// Kanoniczny zapis: klucze obiektów posortowane porządkowo na każdym poziomie, kolejność tablic zachowana.
    /// <paramref name="type"/> (metadane serializacji) mówi, czy obiekt JSON to rekord z polami (wtedy pola spoza
    /// listy 0.4.79 w wartości domyślnej są pomijane — <see cref="ProfileHashSchema.Omits"/>), czy słownik (klucze to
    /// dane, np. identyfikatory profili ekranu, i nigdy nie są pomijane).
    /// </summary>
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, JsonTypeInfo? type, ProfileHashSchema schema)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                JsonTypeInfo? child = null;
                if (type?.Kind == JsonTypeInfoKind.Object)
                {
                    if (schema.Omits(ProfileHashSchema.Key(type.Type, property.Name), property.Value)) continue;
                    var info = type.Properties.FirstOrDefault(p => p.Name == property.Name);
                    child = info is null ? null : Options.GetTypeInfo(info.PropertyType);
                }
                else if (type?.Kind == JsonTypeInfoKind.Dictionary && type.ElementType is { } element)
                    child = Options.GetTypeInfo(element);
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value, child, schema);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var child = type?.Kind == JsonTypeInfoKind.Enumerable && type.ElementType is { } element
                ? Options.GetTypeInfo(element) : null;
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item, child, schema);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private const string TooBig = "plik jest pusty albo większy niż 1 MiB";

    /// <summary>Powód mówi, CO jest nie tak, ale nigdy nie cytuje zawartości pliku ani sekretów.</summary>
    private static InvalidDataException InvalidFile(string reason) => new(
        $"Nieprawidłowy plik grupy: {reason}. Konfiguracja nie została zmieniona.");

    private sealed class StrictRectConverter : JsonConverter<RectInt>
    {
        public override RectInt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4
                || root.EnumerateObject().Any(p => p.Name is not ("x" or "y" or "width" or "height")))
                throw new JsonException();
            return new(root.GetProperty("x").GetInt32(), root.GetProperty("y").GetInt32(),
                root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32());
        }
        public override void Write(Utf8JsonWriter writer, RectInt value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X); writer.WriteNumber("y", value.Y);
            writer.WriteNumber("width", value.Width); writer.WriteNumber("height", value.Height);
            writer.WriteEndObject();
        }
    }
}
