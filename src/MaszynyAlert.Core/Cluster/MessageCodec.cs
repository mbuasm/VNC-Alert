using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Cluster;

public static class MessageCodec
{
    /// <summary>
    /// Bezpieczny górny limit rozmiaru pojedynczego datagramu UDP (bajty) — powyżej niego <see cref="Encode"/>
    /// przycina zawartość <see cref="StateMessage"/>, żeby pakiet się zmieścił (patrz <see cref="Encode"/>).
    /// </summary>
    public const int MaxDatagramSizeBytes = 60_000;

    /// <summary>Maksymalna długość <see cref="Alert.FullText"/> po przycięciu treści (patrz <see cref="Encode"/>).</summary>
    private const int MaxFullTextLength = 500;

    /// <summary>Maksymalna długość <see cref="Alert.ShortText"/> po przycięciu treści (patrz <see cref="Encode"/>).</summary>
    private const int MaxShortTextLength = 200;

    /// <summary>Wspólne ustawienia System.Text.Json dla wiadomości klastra: camelCase, enumy jako tekst, brak wcięć.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>
    /// Serializuje i podpisuje wiadomość. Jeśli wynik przekracza <see cref="MaxDatagramSizeBytes"/> (może się
    /// zdarzyć dla dużego <see cref="StateMessage"/> przy wielu alertach/maszynach), przycina listę
    /// <see cref="StateMessage.Machines"/> (w pierwszej kolejności), a potem <see cref="StateMessage.Alerts"/>
    /// (zostawiając co najmniej jeden alert, jeśli jakiś był). Gdy to nadal nie wystarczy — np. pojedynczy
    /// alert z gigantycznym <see cref="Alert.FullText"/> odczytanym przez OCR — przycina treść: najpierw
    /// <see cref="Alert.FullText"/> do <c>500</c> znaków (z „…”), a jeśli nadal za duże, <see cref="Alert.ShortText"/>
    /// do <c>200</c> znaków. Loguje ostrzeżenie, gdy cokolwiek przycięto. Nigdy nie rzuca z powodu rozmiaru.
    /// </summary>
    public static byte[] Encode(PeerMessage msg, HmacSigner signer, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(msg);
        ArgumentNullException.ThrowIfNull(signer);
        ILogger log = logger ?? NullLogger.Instance;

        byte[] sealedDatagram = SealMessage(msg, signer);
        if (sealedDatagram.Length <= MaxDatagramSizeBytes) return sealedDatagram;
        if (msg is StateMessage { Group: not null } groupState) return TrimGroupState(groupState, signer, log);
        if (msg.Group is not null)
            throw new InvalidDataException("Pakiet grupy przekracza limit UDP; dane nie zostały wysłane.");
        if (msg is not StateMessage state) return sealedDatagram;

        // Pakiet za duży: przycinamy najpierw Machines, potem Alerts (zostawiając co najmniej jeden alert),
        // aż się zmieści.
        StateMessage trimmed = state;
        while (sealedDatagram.Length > MaxDatagramSizeBytes && trimmed.Machines.Count > 0)
        {
            trimmed = trimmed with { Machines = trimmed.Machines.Take(trimmed.Machines.Count - 1).ToList() };
            sealedDatagram = SealMessage(trimmed, signer);
        }

        while (sealedDatagram.Length > MaxDatagramSizeBytes && trimmed.Alerts.Count > 1)
        {
            trimmed = trimmed with { Alerts = trimmed.Alerts.Take(trimmed.Alerts.Count - 1).ToList() };
            sealedDatagram = SealMessage(trimmed, signer);
        }

        // Przycinanie list nie wystarczyło (np. jeden pozostały alert z gigantycznym FullText z OCR) —
        // ostatni krok: przytnij treść alertów, najpierw FullText, potem (jeśli nadal za duże) ShortText.
        bool contentTrimmed = false;
        if (sealedDatagram.Length > MaxDatagramSizeBytes && trimmed.Alerts.Count > 0)
        {
            contentTrimmed = true;
            trimmed = trimmed with { Alerts = trimmed.Alerts.Select(TruncateFullText).ToList() };
            sealedDatagram = SealMessage(trimmed, signer);

            if (sealedDatagram.Length > MaxDatagramSizeBytes)
            {
                trimmed = trimmed with { Alerts = trimmed.Alerts.Select(TruncateShortText).ToList() };
                sealedDatagram = SealMessage(trimmed, signer);
            }
        }

        // Ostateczne zabezpieczenie (skrajny przypadek: nawet po przycięciu treści nadal za duże) —
        // dotnij listę Alerts do zera, żeby nigdy nie przekroczyć limitu.
        while (sealedDatagram.Length > MaxDatagramSizeBytes && trimmed.Alerts.Count > 0)
        {
            trimmed = trimmed with { Alerts = trimmed.Alerts.Take(trimmed.Alerts.Count - 1).ToList() };
            sealedDatagram = SealMessage(trimmed, signer);
        }

        log.LogWarning(
            "Pakiet STATE od \"{Sender}\" był za duży ({OriginalMachines} maszyn, {OriginalAlerts} alertów) i został " +
            "przycięty do {TrimmedMachines} maszyn / {TrimmedAlerts} alertów{ContentInfo}, żeby zmieścić się w limicie {LimitBytes} B.",
            msg.Sender, state.Machines.Count, state.Alerts.Count, trimmed.Machines.Count, trimmed.Alerts.Count,
            contentTrimmed ? " (dodatkowo przycięto FullText/ShortText alertów)" : string.Empty, MaxDatagramSizeBytes);

        return sealedDatagram;
    }

    /// <summary>
    /// STATE grupy (v2) za duży na jeden datagram: usuwamy całe wpisy, nigdy dowód tożsamości zachowanego alertu
    /// (UUID, poprzednicy, klucze). Kolejność: zamknięcia (nadawca powtarza je w każdym cyklu), FullText,
    /// najmniej pilne alerty z końca listy, na końcu maszyny (od nich zależy gotowość następcy).
    /// </summary>
    private static byte[] TrimGroupState(StateMessage state, HmacSigner signer, ILogger log)
    {
        StateMessage trimmed = state;
        byte[] datagram;
        while ((datagram = SealMessage(trimmed, signer)).Length > MaxDatagramSizeBytes)
        {
            if (trimmed.ClosedEpisodes.Count > 0)
                trimmed = trimmed with { ClosedEpisodes = trimmed.ClosedEpisodes.Take(trimmed.ClosedEpisodes.Count / 2).ToArray() };
            else if (trimmed.Alerts.Any(a => a.FullText is not null))
                trimmed = trimmed with { Alerts = trimmed.Alerts.Select(a => a with { FullText = null }).ToArray() };
            else if (trimmed.Alerts.Count > 1)
                trimmed = trimmed with { Alerts = trimmed.Alerts.Take(trimmed.Alerts.Count - 1).ToArray() };
            else if (trimmed.Machines.Count > 0)
                // Only when a single alert with its evidence still does not fit: fewer machines delays a takeover
                // (both sides keep observing), which is safer than silence about the alert.
                trimmed = trimmed with { Machines = trimmed.Machines.Take(trimmed.Machines.Count / 2).ToArray() };
            else
                throw new InvalidDataException($"Stan grupy przekracza limit UDP ({datagram.Length} B, {trimmed.Machines.Count} maszyn); dane nie zostały wysłane.");
        }
        log.LogWarning(
            "STATE grupy od \"{Sender}\" był za duży i został przycięty do {Alerts}/{OriginalAlerts} alertów, " +
            "{Machines}/{OriginalMachines} maszyn i {Closed}/{OriginalClosed} zamknięć, żeby zmieścić się w limicie {LimitBytes} B.",
            state.Sender, trimmed.Alerts.Count, state.Alerts.Count, trimmed.Machines.Count, state.Machines.Count,
            trimmed.ClosedEpisodes.Count, state.ClosedEpisodes.Count, MaxDatagramSizeBytes);
        return datagram;
    }

    /// <summary>Przycina <see cref="Alert.FullText"/> do <see cref="MaxFullTextLength"/> znaków (z „…”, gdy ucięty).</summary>
    private static Alert TruncateFullText(Alert alert)
    {
        if (alert.FullText is null || alert.FullText.Length <= MaxFullTextLength)
        {
            return alert;
        }

        return alert with { FullText = alert.FullText[..MaxFullTextLength] + "…" };
    }

    /// <summary>Przycina <see cref="Alert.ShortText"/> do <see cref="MaxShortTextLength"/> znaków (z „…”, gdy ucięty).</summary>
    private static Alert TruncateShortText(Alert alert)
    {
        if (alert.ShortText.Length <= MaxShortTextLength)
        {
            return alert;
        }

        return alert with { ShortText = alert.ShortText[..MaxShortTextLength] + "…" };
    }

    /// <summary>
    /// Weryfikuje podpis (<see cref="HmacSigner.TryOpen"/>), deserializuje treść i sprawdza, czy
    /// <see cref="PeerMessage.SentAt"/> mieści się w oknie <paramref name="skewTolerance"/> względem
    /// <see cref="IClock.UtcNow"/> (ochrona przed odtwarzaniem starych, przechwyconych pakietów). Zwraca
    /// <see langword="false"/> z polskim powodem w <paramref name="rejectReason"/>, gdy cokolwiek się nie zgadza.
    /// </summary>
    public static bool TryDecode(
        byte[] datagram,
        HmacSigner signer,
        IClock clock,
        TimeSpan skewTolerance,
        out PeerMessage? msg,
        out string? rejectReason)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(clock);

        if (datagram.Length > MaxDatagramSizeBytes)
        { msg = null; rejectReason = "pakiet przekracza limit UDP"; return false; }
        if (!signer.TryOpen(datagram, out byte[] payload, out string? openReason))
        {
            msg = null;
            rejectReason = openReason;
            return false;
        }

        PeerMessage? decoded;
        try
        {
            // Inspect the wire before deserialization: a repeated group followed by null must
            // not disguise a v2 packet as legacy traffic. Property names include JSON escapes.
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(p => p.NameEquals("group") && p.Value.ValueKind != JsonValueKind.Null))
                RejectDuplicateProperties(document.RootElement);
            decoded = JsonSerializer.Deserialize<PeerMessage>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            msg = null;
            rejectReason = $"niepoprawny JSON: {ex.Message}";
            return false;
        }

        if (decoded is null)
        {
            msg = null;
            rejectReason = "niepoprawny JSON: pusta wiadomość";
            return false;
        }

        TimeSpan diff = (decoded.SentAt - clock.UtcNow).Duration();
        if (diff > skewTolerance)
        {
            msg = null;
            rejectReason = $"pakiet spoza okna czasowego (różnica {diff.TotalSeconds:0.#} s) – sprawdź zegary komputerów";
            return false;
        }

        if (decoded is StateMessage { Group: not null } && !AlertsCarryEpisodeIds(payload))
        { msg = null; rejectReason = "STATE grupy zawiera alert bez jawnego UUID epizodu"; return false; }

        msg = decoded;
        rejectReason = null;
        return true;
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Powtórzone pole w pakiecie grupy.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    /// <summary>
    /// A v2 alert must name its episode on the wire. Without this check a missing <c>episodeId</c> would
    /// deserialize into a fresh random <see cref="Guid"/> and look like a brand-new episode in every packet.
    /// Legacy (non-group) traffic is not affected.
    /// </summary>
    private static bool AlertsCarryEpisodeIds(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("alerts", out var alerts) || alerts.ValueKind != JsonValueKind.Array)
                return true; // brak/nie-lista → odrzuca walidacja odbiorcy (GroupClusterNode)
            foreach (var alert in alerts.EnumerateArray())
            {
                if (alert.ValueKind != JsonValueKind.Object || !alert.TryGetProperty("episodeId", out var id)
                    || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var guid) || guid == Guid.Empty)
                    return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Dzieli listę na tyle wiadomości, ile trzeba, żeby każda zmieściła się w jednym datagramie.
    /// Rozmiar liczymy z samego JSON-a powiększonego o stałą kopertę podpisu, bo w tym miejscu kodu
    /// nie ma klucza sieciowego — ma go tylko <see cref="IPeerBus"/>. Każdy element jest serializowany
    /// dokładnie raz, więc podział tysiąca wpisów nie kosztuje tysiąca serializacji całej wiadomości.
    /// </summary>
    /// <param name="items">Elementy do rozesłania; pusta lista daje jedną pustą wiadomość (odpowiedź „nic nie mam”).</param>
    /// <param name="build">Buduje wiadomość z porcji elementów i informacji, czy po niej idzie następna.</param>
    /// <param name="maxItemsPerMessage">Twardy limit liczby elementów w jednej wiadomości (walidacja odbiorcy).</param>
    /// <exception cref="InvalidDataException">Pojedynczy element nie mieści się w datagramie.</exception>
    public static IReadOnlyList<TMessage> Chunk<TItem, TMessage>(
        IReadOnlyList<TItem> items, Func<IReadOnlyList<TItem>, bool, TMessage> build, int maxItemsPerMessage = int.MaxValue)
        where TMessage : PeerMessage
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItemsPerMessage, 1);
        int envelope = StampedSize(build([], true));
        if (items.Count > 0)
            envelope = Math.Max(envelope, StampedSize(build([items[0]], true))
                - JsonSerializer.SerializeToUtf8Bytes(items[0], JsonOptions).Length);
        int budget = MaxDatagramSizeBytes - HmacSigner.SealOverheadBytes - envelope - 64;
        var chunks = new List<List<TItem>>();
        var current = new List<TItem>();
        int used = 0;
        foreach (var item in items)
        {
            int size = JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions).Length + 1;
            if (size > budget)
                throw new InvalidDataException("Pojedynczy wpis nie mieści się w jednym pakiecie UDP.");
            if (current.Count > 0 && (used + size > budget || current.Count >= maxItemsPerMessage))
            { chunks.Add(current); current = []; used = 0; }
            current.Add(item);
            used += size;
        }
        chunks.Add(current);
        return chunks.Select((chunk, index) => build(chunk, index < chunks.Count - 1)).ToArray();
    }

    /// <summary>
    /// Rozmiar JSON wiadomości po ostemplowaniu przez <see cref="IPeerBus"/> w najgorszym przypadku: najdłuższy numer
    /// kolejny, znacznik czasu z ułamkiem sekund i przesunięciem strefy, identyfikator instancji (GUID „N”).
    /// </summary>
    private static int StampedSize(PeerMessage msg) => JsonSerializer.SerializeToUtf8Bytes<PeerMessage>(msg with
    {
        Seq = long.MaxValue,
        SentAt = new DateTimeOffset(2999, 12, 31, 23, 59, 59, TimeSpan.FromHours(-14)).AddTicks(9_999_999),
        InstanceId = new string('f', 32),
    }, JsonOptions).Length;

    private static byte[] SealMessage(PeerMessage msg, HmacSigner signer)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
        return signer.Seal(json);
    }
}
