using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class DowntimeRequestIdLabTests
{
    // Wtorek 06:00 UTC.
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(90); // domyślne cluster.downtimeSyncWindowDays
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10); // domyślne cluster.downtimeSyncIntervalMs
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(15);

    // ---------------------------------------------------------------------------------------------------------------
    // 1. Trzy komputery, trzy kolejne podziały dłuższe niż doba w jednym przebiegu, zegary rozjechane (+150 s, −90 s, +40 s).
    // ---------------------------------------------------------------------------------------------------------------

    public static TheoryData<string> ThreeComputerRuns() => new()
    {
        // podział, godziny, czyj ogon wypada tuż po powrocie, ile sekund po powrocie, kolejność w sekundzie po powrocie
        "AC|B,26.5,A,3,BAC;A|BC,27,C,4,BCA;AB|C,26.5,B,6,CBA",
        "AB|C,26.5,B,2,CAB;AC|B,27,C,8,BCA;BC|A,26.5,B,5,ACB",
        "BC|A,27,C,9,ACB;AB|C,26.5,A,1,CBA;AC|B,26.5,A,7,BAC",
    };

    [Theory]
    [MemberData(nameof(ThreeComputerRuns))]
    public async Task ThreeComputers_RepeatedSplitsWithSkewedClocks_NoComputerLosesDowntime(string run)
    {
        await using var lab = new Lab();
        var history = Episode("hist", T0 - TimeSpan.FromDays(3), TimeSpan.FromMinutes(20), "B");
        await lab.AddAsync("A", 100, skewSeconds: 150, history);
        await lab.AddAsync("B", 10, skewSeconds: -90, history);
        await lab.AddAsync("C", 200, skewSeconds: 40, history);
        await lab.RunAsync(TimeSpan.FromSeconds(60), Second);

        var expected = new List<DowntimeEpisode> { history };
        var races = new List<string>();
        int number = 0;
        foreach (var cycle in Cycle.ParseRun(run))
        {
            var result = await SplitAndHealAsync(lab, cycle, ++number);
            expected.AddRange(result.Injected);
            races.AddRange(result.Races.Select(r => $"cykl {number}: {r}"));
            AssertNobodyLostDowntime(lab, expected, $"Cykl {number} ({cycle}), {Settle.TotalMinutes:0} min po powrocie; wyścigi -01: [{string.Join(", ", races)}]");
            AssertNoFullWindowRequestsInTheLast(lab, TimeSpan.FromMinutes(5), $"cykl {number} ({cycle})");
            AssertBoundedFullWindowRetries(lab, result, $"cykl {number} ({cycle})");
        }
        AssertReportsEqual(lab);
        // Czułość: przebieg naprawdę odtwarza wyścig (pierwsza przyjęta odpowiedź z drugiej strony = na ogon).
        Assert.True(races.Count > 0, "Żaden powrót łączności nie odtworzył wyścigu grupa-requestid-01 — scenariusz nie sprawdza poprawki.");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2. Cztery komputery: podział na TRZY części i powrót na raty (najpierw A z CD, B dopiero po 7 min), potem nowy podział.
    // ---------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string, string> ThreeWaySplitRuns() => new()
    {
        // kolejność po pierwszym powrocie (A z CD), po drugim (B z resztą), kolejność trzeciego cyklu (AD|BC)
        { "CDAB", "BDCA", "BCAD" },
        { "DACB", "BACD", "CBDA" },
    };

    [Theory]
    [MemberData(nameof(ThreeWaySplitRuns))]
    public async Task FourComputers_ThreeWaySplitAndStaggeredHeal_NoComputerLosesDowntime(string firstOrder, string secondOrder, string thirdOrder)
    {
        await using var lab = new Lab();
        var history = Episode("hist", T0 - TimeSpan.FromDays(3), TimeSpan.FromMinutes(20), "B");
        await lab.AddAsync("A", 100, skewSeconds: -120, history);
        await lab.AddAsync("B", 10, skewSeconds: 110, history);
        await lab.AddAsync("C", 200, skewSeconds: 0, history);
        await lab.AddAsync("D", 150, skewSeconds: 60, history);
        await lab.RunAsync(TimeSpan.FromSeconds(60), Second);
        var expected = new List<DowntimeEpisode> { history };

        // Podział A | B | CD na 26,5 h; każda część ma swój postój starszy niż doba i świeży.
        string[] parts = ["A", "B", "CD"];
        lab.Split(parts);
        var splitAt = lab.Clock.UtcNow;
        while (lab.Clock.UtcNow - splitAt < TimeSpan.FromHours(26.5)) await lab.StepAsync(FiveSeconds);
        var healAt = await RunUntilJustBeforeNextRequestAsync(lab, "C", secondsBefore: 4);
        expected.AddRange(InjectSplitEpisodes(lab, parts, healAt, cycle: 1, splitAt));

        // Powrót na raty: A z C i D (B dalej odcięty), po 7 min B ze wszystkimi.
        long firstHeal = lab.Mark();
        lab.LogHeartbeats = true;
        lab.HealBetween("A", "CD");
        await lab.RunAsync(TimeSpan.FromMinutes(7), Second, firstOrder);
        var racesFirst = Races(lab, ["A", "CD"], firstHeal, healAt);
        var bHealAt = lab.Clock.UtcNow;
        long secondHeal = lab.Mark();
        lab.HealAll();
        await SettleAsync(lab, secondOrder);
        var racesSecond = Races(lab, ["B", "ACD"], secondHeal, bHealAt);
        AssertNobodyLostDowntime(lab, expected, $"A|B|CD, powrót na raty ({firstOrder} → {secondOrder}); wyścigi: "
            + $"[{string.Join(", ", racesFirst.Concat(racesSecond))}]");
        AssertNoFullWindowRequestsInTheLast(lab, TimeSpan.FromMinutes(5), "A|B|CD");

        // Drugi cykl: AD | BC.
        var result = await SplitAndHealAsync(lab, new Cycle("AD|BC", 26.5, "B", 6, thirdOrder), 2);
        expected.AddRange(result.Injected);
        AssertNobodyLostDowntime(lab, expected, $"AD|BC ({thirdOrder}); wyścigi: [{string.Join(", ", result.Races)}]");
        AssertNoFullWindowRequestsInTheLast(lab, TimeSpan.FromMinutes(5), "AD|BC");
        AssertBoundedFullWindowRetries(lab, result, "AD|BC");
        AssertReportsEqual(lab);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 3. Utrata, duplikaty i opóźnienia pojedynczych pakietów w pierwszych minutach po powrocie łączności.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(11, "BADC")]
    [InlineData(23, "CBDA")]
    [InlineData(37, "DCBA")]
    public async Task FourComputers_LossDuplicatesAndDelaysAfterHeal_NoComputerLosesDowntime(int seed, string order)
    {
        await using var lab = new Lab { DelayedBy = TimeSpan.FromSeconds(40) };
        var history = Episode("hist", T0 - TimeSpan.FromDays(3), TimeSpan.FromMinutes(20), "B");
        await lab.AddAsync("A", 100, skewSeconds: 90, history);
        await lab.AddAsync("B", 10, skewSeconds: -100, history);
        await lab.AddAsync("C", 200, skewSeconds: 30, history);
        await lab.AddAsync("D", 150, skewSeconds: -40, history);
        await lab.RunAsync(TimeSpan.FromSeconds(60), Second);
        var expected = new List<DowntimeEpisode> { history };

        var random = new Random(seed);
        var firstFull = new Dictionary<string, Guid>(StringComparer.Ordinal);
        DateTimeOffset? lossUntil = null;
        int delayedReplies = 0, duplicated = 0, lost = 0;
        lab.Filter = (packet, to) =>
        {
            if (lossUntil is not { } until || lab.Clock.UtcNow >= until) return Fate.Deliver;
            // Pierwsza prośba o pełne okno każdego komputera po powrocie ginie w całości (UDP, u wszystkich odbiorców).
            if (packet.Message is DowntimeSyncRequest request && IsFull(request))
            {
                if (!firstFull.TryGetValue(packet.From, out var id)) firstFull[packet.From] = id = request.RequestId;
                if (id == request.RequestId) { lost++; return Fate.Drop; }
            }
            // Co trzecia odpowiedź przychodzi 40 s później (przestawienie kolejności); każda inna — podwójnie, z kopią po 40 s.
            if (packet.Message is DowntimeSyncReply)
            {
                if (random.Next(3) == 0) { delayedReplies++; return Fate.Delay; }
                duplicated++;
                return Fate.DelayedDuplicate;
            }
            if (random.NextDouble() < 0.15) { lost++; return Fate.Drop; }
            return random.Next(10) == 0 ? Fate.Duplicate : Fate.Deliver;
        };

        var result = await SplitAndHealAsync(lab, new Cycle("AB|CD", 26.5, "A", 5, order), 1,
            atHeal: () => lossUntil = lab.Clock.UtcNow + TimeSpan.FromMinutes(3), settle: TimeSpan.FromMinutes(20));
        expected.AddRange(result.Injected);

        AssertNobodyLostDowntime(lab, expected, $"ziarno {seed}, {order}: zgubione {lost}, opóźnione odpowiedzi {delayedReplies}, "
            + $"zdublowane {duplicated}; wyścigi: [{string.Join(", ", result.Races)}]");
        Assert.True(lost > 0 && delayedReplies > 0 && duplicated > 0, $"Zakłócenia nie zadziałały: {lost}/{delayedReplies}/{duplicated}.");
        AssertNoFullWindowRequestsInTheLast(lab, TimeSpan.FromMinutes(5), $"ziarno {seed}");
        AssertReportsEqual(lab);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 4. Partner usunięty z tabeli i dodany ponownie; „mruganie” łącza w czasie pełnego okna; kolejne długie odcięcia.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FourComputers_PartnerRemovedReAddedAndFlappingLink_NoComputerLosesDowntime()
    {
        await using var lab = new Lab();
        var history = Episode("hist", T0 - TimeSpan.FromDays(3), TimeSpan.FromMinutes(20), "B");
        await lab.AddAsync("A", 100, skewSeconds: 70, history);
        await lab.AddAsync("B", 10, skewSeconds: -70, history);
        await lab.AddAsync("C", 200, skewSeconds: 0, history);
        await lab.AddAsync("D", 150, skewSeconds: 140, history);
        await lab.RunAsync(TimeSpan.FromSeconds(60), Second);
        var expected = new List<DowntimeEpisode> { history };

        // D odcięty 26,5 h (po 2 h wypada z tabel i sam usuwa partnerów); powrót 4 s przed ogonem A, D pierwszy w sekundzie.
        string[] parts = ["ABC", "D"];
        lab.Split(parts);
        var splitAt = lab.Clock.UtcNow;
        while (lab.Clock.UtcNow - splitAt < TimeSpan.FromHours(26.5)) await lab.StepAsync(FiveSeconds);
        var healAt = await RunUntilJustBeforeNextRequestAsync(lab, "A", secondsBefore: 4);
        expected.AddRange(InjectSplitEpisodes(lab, parts, healAt, cycle: 1, splitAt));
        long healIndex = lab.Mark();
        lab.LogHeartbeats = true;
        lab.HealAll();
        // 8 s po powrocie (pełne okna jeszcze otwarte, partnerzy D już odpowiedzieli na ogon A) łącze D znów pada na 70 s: D martwy,
        // ale w tabeli; potem wraca. W tym czasie D zapisuje świeży postój.
        await lab.RunAsync(TimeSpan.FromSeconds(8), Second, "DABC");
        lab.Split(parts);
        var flapAt = lab.Clock.UtcNow;
        await lab.RunAsync(TimeSpan.FromSeconds(70), Second, "DABC");
        var flap = Episode("flap-D", flapAt + TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), "D");
        lab["D"].Journal.Merge(flap);
        expected.Add(flap);
        lab.HealAll();
        await SettleAsync(lab, "DCBA");
        var races = Races(lab, parts, healIndex, healAt);
        AssertNobodyLostDowntime(lab, expected, $"D odcięty 26,5 h + mruganie 70 s; wyścigi: [{string.Join(", ", races)}]");
        AssertNoFullWindowRequestsInTheLast(lab, TimeSpan.FromMinutes(5), "po mruganiu");

        // D znów odcięty 2,5 h (usunięty z tabel; okres krótszy niż doba — ogon), wraca; potem AD | BC na 26,5 h.
        lab.Split(parts);
        var shortSplit = lab.Clock.UtcNow;
        var during = Episode("short-D", shortSplit + TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(10), "D");
        lab["D"].Journal.Merge(during);
        expected.Add(during);
        while (lab.Clock.UtcNow - shortSplit < TimeSpan.FromHours(2.5)) await lab.StepAsync(FiveSeconds);
        lab.HealAll();
        await SettleAsync(lab, "BDAC");
        AssertNobodyLostDowntime(lab, expected, "D odcięty 2,5 h");

        var result = await SplitAndHealAsync(lab, new Cycle("AD|BC", 26.5, "D", 3, "CADB"), 3);
        expected.AddRange(result.Injected);
        AssertNobodyLostDowntime(lab, expected, $"AD|BC; wyścigi: [{string.Join(", ", result.Races)}]");
        AssertNoFullWindowRequestsInTheLast(lab, TimeSpan.FromMinutes(5), "AD|BC");
        AssertReportsEqual(lab);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 5. Restart komputera w trakcie pełnego okna; odpowiedzi do POPRZEDNIEGO procesu docierają po restarcie.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("A")] // restartuje proszący: odpowiedzi B na prośby starego procesu A dochodzą do nowego
    [InlineData("B")] // restartuje odpowiadający: odpowiedzi starego procesu B dochodzą po jego restarcie (wycofana instancja)
    [InlineData("C")] // restartuje trzeci komputer, którego heartbeat wywołał prośbę ogona A
    public async Task RestartDuringTheFullWindow_DelayedRepliesToThePreviousProcess_NoComputerLosesDowntime(string restarted)
    {
        await using var lab = new Lab();
        var history = Episode("hist", T0 - TimeSpan.FromDays(3), TimeSpan.FromMinutes(20), "B");
        await lab.AddAsync("A", 100, skewSeconds: 30, history);
        await lab.AddAsync("B", 10, skewSeconds: -30, history);
        await lab.AddAsync("C", 200, skewSeconds: 0, history);
        await lab.RunAsync(TimeSpan.FromSeconds(60), Second);
        var expected = new List<DowntimeEpisode> { history };

        // Wszystkie odpowiedzi B do A z pierwszych 25 s po powrocie są przetrzymane w sieci; restart w 12. sekundzie.
        DateTimeOffset? holdUntil = null;
        lab.Filter = (packet, to) => holdUntil is { } until && lab.Clock.UtcNow < until
            && packet.From == "B" && to == "A" && packet.Message is DowntimeSyncReply ? Fate.Hold : Fate.Deliver;
        var result = await SplitAndHealAsync(lab, new Cycle("AC|B", 26.5, "A", 3, "BAC"), 1,
            atHeal: () => holdUntil = lab.Clock.UtcNow + TimeSpan.FromSeconds(25),
            afterHeal: async () =>
            {
                await lab.RunAsync(TimeSpan.FromSeconds(12), Second, "BAC");
                await lab.RestartAsync(restarted);
                await lab.RunAsync(TimeSpan.FromSeconds(13), Second, "BAC");
                Assert.True(lab.HeldCount > 0, "Żadna odpowiedź B nie została przetrzymana.");
                lab.ReleaseHeld();
            });
        expected.AddRange(result.Injected);

        AssertNobodyLostDowntime(lab, expected, $"restart {restarted} w pełnym oknie; wyścigi: [{string.Join(", ", result.Races)}]");
        AssertNoFullWindowRequestsInTheLast(lab, TimeSpan.FromMinutes(5), $"restart {restarted}");
        AssertReportsEqual(lab);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 5b. Duże dzienniki: odpowiedź na pełne okno idzie kilkoma porcjami (More/Cursor), a odpowiedź na ogon — jedną.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ABD|C", "A", 3, "CABD")]
    [InlineData("AB|CD", "A", 2, "CDAB")]
    public async Task FourComputers_LargeJournals_FullWindowInSeveralChunks_NoComputerLosesDowntime(string split, string tailOf, int secondsBefore, string order)
    {
        await using var lab = new Lab();
        // 600 wspólnych postojów z ostatnich 80 dni (pełne okno to kilka porcji po ≤ 256 wpisów).
        var history = Enumerable.Range(0, 600)
            .Select(i => Episode($"hist{i % 9}", T0 - TimeSpan.FromDays(80) + TimeSpan.FromHours(3.2 * i), TimeSpan.FromMinutes(4), "B")).ToArray();
        await lab.AddAsync("A", 100, skewSeconds: 45, history);
        await lab.AddAsync("B", 10, skewSeconds: -45, history);
        await lab.AddAsync("C", 200, skewSeconds: 0, history);
        await lab.AddAsync("D", 150, skewSeconds: 20, history);
        await lab.RunAsync(TimeSpan.FromSeconds(60), Second);
        var expected = history.ToList();

        var parts = split.Split('|');
        lab.Split(parts);
        var splitAt = lab.Clock.UtcNow;
        while (lab.Clock.UtcNow - splitAt < TimeSpan.FromHours(26.5)) await lab.StepAsync(FiveSeconds);
        var healAt = await RunUntilJustBeforeNextRequestAsync(lab, tailOf, secondsBefore);
        expected.AddRange(InjectSplitEpisodes(lab, parts, healAt, cycle: 1, splitAt));
        // Druga część zapisała w czasie podziału 300 postojów starszych niż doba (kolejne porcje odpowiedzi).
        var many = Enumerable.Range(0, 300).Select(i => Episode($"many{i % 7}", splitAt + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(17 * i),
            TimeSpan.FromSeconds(10), parts[1][..1])).ToArray();
        Assert.All(many, e => Assert.True(e.LastObservedAt < healAt - TimeSpan.FromHours(25)));
        foreach (char h in parts[1]) lab[h].Journal.Apply(many);
        expected.AddRange(many);

        long healIndex = lab.Mark();
        lab.LogHeartbeats = true;
        lab.HealAll();
        await SettleAsync(lab, order);
        var races = Races(lab, parts, healIndex, healAt);
        int moreChunks = lab.Log.Count(s => s.Index > healIndex && s.Message is DowntimeSyncReply { More: true });

        AssertNobodyLostDowntime(lab, expected, $"{split}, {order}: porcje z More po powrocie {moreChunks}; wyścigi: [{string.Join(", ", races)}]");
        Assert.True(moreChunks > 0, "Żadna odpowiedź nie szła kilkoma porcjami — scenariusz nie sprawdza dużych dzienników.");
        AssertNoFullWindowRequestsInTheLast(lab, TimeSpan.FromMinutes(5), split);
        AssertReportsEqual(lab);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 6. Panel (DowntimeSyncedAt): „uzupełniony” pojawia się dopiero razem z danymi z pełnego okna.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PanelSyncedAt_AfterTheRace_AppearsOnlyWhenTheOutageOlderThanADayIsAlreadyInTheJournal()
    {
        await using var lab = new Lab();
        var history = Episode("hist", T0 - TimeSpan.FromDays(3), TimeSpan.FromMinutes(20), "B");
        await lab.AddAsync("A", 100, skewSeconds: 0, history);
        await lab.AddAsync("B", 10, skewSeconds: 0, history);
        await lab.AddAsync("C", 200, skewSeconds: 0, history);
        await lab.RunAsync(TimeSpan.FromSeconds(60), Second);

        string[] parts = ["AC", "B"];
        lab.Split(parts);
        var splitAt = lab.Clock.UtcNow;
        while (lab.Clock.UtcNow - splitAt < TimeSpan.FromHours(26.5)) await lab.StepAsync(FiveSeconds);
        var healAt = await RunUntilJustBeforeNextRequestAsync(lab, "A", secondsBefore: 3);
        var injected = InjectSplitEpisodes(lab, parts, healAt, cycle: 1, splitAt);
        var oldOfB = injected.Single(e => e.MachineId == "c1s1-old");
        lab.LogHeartbeats = true;

        // A usunął B z tabeli (2 h ciszy): Panel A pokazuje „brak”, dopóki nie skończy się pełne okno od B.
        DateTimeOffset? firstSynced = null;
        bool hadOldAtFirstSynced = false;
        lab.AfterStep = () =>
        {
            if (firstSynced is not null) return;
            if (lab["A"].Node.Peers.SingleOrDefault(p => p.Host == "B")?.DowntimeSyncedAt is { } at)
            {
                firstSynced = at;
                hadOldAtFirstSynced = lab["A"].Journal.Snapshot().Any(e => e.EpisodeId == oldOfB.EpisodeId);
            }
        };
        long healIndex = lab.Mark();
        lab.HealAll();
        await SettleAsync(lab, "BAC");
        lab.AfterStep = null;
        var races = Races(lab, parts, healIndex, healAt);

        Assert.Contains("A←B", string.Join(" ", races)); // wyścig dotyczy właśnie pary A←B
        Assert.NotNull(firstSynced);
        Assert.True(hadOldAtFirstSynced, $"Panel A pokazał „uzupełniony przez B: {firstSynced:HH:mm:ss}”, zanim A miał postój B sprzed "
            + $"{(healAt - oldOfB.LastObservedAt).TotalHours:0.#} h; wyścigi: [{string.Join(", ", races)}].");
        var firstFullReply = lab.Log.First(s => s.Index > healIndex && s.From == "B" && s.Message is DowntimeSyncReply { ReplyTo: "A" } r
            && lab.RequestById(r.RequestId) is { } q && IsFull(q));
        Assert.Equal(firstFullReply.At, firstSynced);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 7. Stan ustalony
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SteadyState_FourComputers_DowntimeTrafficPerTwoHours()
    {
        await using var lab = new Lab();
        var history = Episode("hist", T0 - TimeSpan.FromDays(3), TimeSpan.FromMinutes(20), "B");
        await lab.AddAsync("A", 100, skewSeconds: 0, history);
        await lab.AddAsync("B", 10, skewSeconds: 0, history);
        await lab.AddAsync("C", 200, skewSeconds: 0, history);
        await lab.AddAsync("D", 150, skewSeconds: 0, history);
        await lab.RunAsync(TimeSpan.FromMinutes(30), Second);
        long start = lab.Mark();
        await lab.RunAsync(TimeSpan.FromHours(2), FiveSeconds);

        var measured = lab.Log.Where(s => s.Index > start).ToArray();
        string summary = string.Join("; ", lab.Pcs.Keys.Select(h =>
            $"{h}: prośby {measured.Count(s => s.From == h && s.Message is DowntimeSyncRequest)}"
            + $" (pełne {measured.Count(s => s.From == h && s.Message is DowntimeSyncRequest q && IsFull(q))})"
            + $", odpowiedzi {measured.Count(s => s.From == h && s.Message is DowntimeSyncReply)}"));
        foreach (string h in lab.Pcs.Keys)
        {
            // Jedna prośba ogona na partnera co 10 min: 3 partnerów × 12 = 36 w 2 h, żadnej o pełne okno.
            Assert.True(measured.Count(s => s.From == h && s.Message is DowntimeSyncRequest) == 36, summary);
            Assert.True(!measured.Any(s => s.From == h && s.Message is DowntimeSyncRequest q && IsFull(q)), summary);
        }
        // Odpowiedzi: prośba bez adresata, limit 60 s na proszącego.
        Assert.True(measured.Count(s => s.Message is DowntimeSyncReply) == ExpectedSteadyStateReplies, summary);
    }

    /// <summary>
    /// Liczba odpowiedzi w 2 h stanu ustalonego: A 48, B 48, C 60,
    /// D 60 — razem 216, przy 36 prośbach ogona na komputer i zerze próśb o pełne okno.
    /// </summary>
    private const int ExpectedSteadyStateReplies = 216;

    // ---------------------------------------------------------------------------------------------------------------
    // Przebieg jednego cyklu: podział, postoje po obu stronach, powrót łączności tuż przed prośbą ogona, 15 min.
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record Cycle(string Split, double Hours, string TailOf, int SecondsBefore, string HealOrder)
    {
        public static IEnumerable<Cycle> ParseRun(string run) => run.Split(';').Select(part => part.Split(','))
            .Select(p => new Cycle(p[0], double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), p[2], int.Parse(p[3]), p[4]));

        public override string ToString() => $"{Split}, {Hours} h, ogon {TailOf} +{SecondsBefore} s, {HealOrder}";
    }

    private sealed record CycleResult(DateTimeOffset HealAt, long HealIndex, IReadOnlyList<DowntimeEpisode> Injected, IReadOnlyList<string> Races);

    private static async Task<CycleResult> SplitAndHealAsync(Lab lab, Cycle cycle, int number, Action? atHeal = null,
        Func<Task>? afterHeal = null, TimeSpan? settle = null)
    {
        var parts = cycle.Split.Split('|');
        lab.Split(parts);
        var splitAt = lab.Clock.UtcNow;
        while (lab.Clock.UtcNow - splitAt < TimeSpan.FromHours(cycle.Hours)) await lab.StepAsync(FiveSeconds);
        var healAt = await RunUntilJustBeforeNextRequestAsync(lab, cycle.TailOf, cycle.SecondsBefore);
        var injected = InjectSplitEpisodes(lab, parts, healAt, number, splitAt);
        long healIndex = lab.Mark();
        lab.LogHeartbeats = true;
        atHeal?.Invoke();
        lab.HealAll();
        if (afterHeal is not null) await afterHeal();
        await SettleAsync(lab, cycle.HealOrder, settle);
        return new CycleResult(healAt, healIndex, injected, Races(lab, parts, healIndex, healAt));
    }

    /// <summary>
    /// Przesuwa czas tak, by powrót łączności wypadł <paramref name="secondsBefore"/> s przed kolejną prośbą ogona komputera
    /// <paramref name="tailOf"/> (każdy partner z jego strony wywołuje ją co 10 min).
    /// </summary>
    private static async Task<DateTimeOffset> RunUntilJustBeforeNextRequestAsync(Lab lab, string tailOf, int secondsBefore)
    {
        DateTimeOffset healAt;
        while (true)
        {
            var cycle = lab.Log.Where(s => s.From == tailOf && s.Message is DowntimeSyncRequest
                && s.At > lab.Clock.UtcNow - Interval - FiveSeconds).ToArray();
            Assert.NotEmpty(cycle);
            Assert.DoesNotContain(cycle, s => IsFull((DowntimeSyncRequest)s.Message));
            healAt = cycle.Min(s => s.At) + Interval - TimeSpan.FromSeconds(secondsBefore);
            if (healAt - lab.Clock.UtcNow >= TimeSpan.FromSeconds(10)) break;
            await lab.StepAsync(FiveSeconds);
        }
        while (healAt - lab.Clock.UtcNow > TimeSpan.FromSeconds(10)) await lab.StepAsync(FiveSeconds);
        while (lab.Clock.UtcNow < healAt) await lab.StepAsync(Second);
        Assert.Equal(healAt, lab.Clock.UtcNow);
        return healAt;
    }

    /// <summary>
    /// Postoje z czasu podziału, które widziała tylko jedna część (w jej obrębie — replikacja na żywo): starszy niż doba przed
    /// powrotem łączności (dojdzie wyłącznie pełnym oknem) i świeży (ogon). Świeży wpis daje każdemu komputerowi aktualną
    /// najpóźniejszą obserwację, więc ogon (najnowszy wpis − doba) jest krótszy niż pełne okno.
    /// </summary>
    private static List<DowntimeEpisode> InjectSplitEpisodes(Lab lab, string[] parts, DateTimeOffset healAt, int cycle, DateTimeOffset splitAt)
    {
        var injected = new List<DowntimeEpisode>();
        for (int s = 0; s < parts.Length; s++)
        {
            var old = Episode($"c{cycle}s{s}-old", healAt - TimeSpan.FromHours(25.5) - TimeSpan.FromMinutes(17 * s), TimeSpan.FromMinutes(15), parts[s][..1]);
            var fresh = Episode($"c{cycle}s{s}-new", healAt - TimeSpan.FromMinutes(40 + 7 * s), TimeSpan.FromMinutes(15), parts[s][..1]);
            Assert.True(old.StartedAt > splitAt, "Postój „starszy niż doba” musi zacząć się po podziale.");
            foreach (char h in parts[s]) { lab[h].Journal.Merge(old); lab[h].Journal.Merge(fresh); }
            injected.Add(old);
            injected.Add(fresh);
        }
        return injected;
    }

    private static async Task SettleAsync(Lab lab, string order, TimeSpan? settle = null)
    {
        lab.LogHeartbeats = true;
        var total = settle ?? Settle;
        for (var after = TimeSpan.Zero; after < total;)
        {
            var step = after < TimeSpan.FromMinutes(3) ? Second : FiveSeconds;
            await lab.StepAsync(step, order);
            after += step;
        }
        lab.LogHeartbeats = false;
    }

    /// <summary>
    /// Pary „proszący ← odpowiadający z innej części”, w których pierwszą odpowiedzią odpowiadającego po jego pierwszym heartbeacie
    /// po powrocie łączności była odpowiedź na prośbę OGONA.
    /// </summary>
    private static List<string> Races(Lab lab, string[] parts, long healIndex, DateTimeOffset healAt)
    {
        var races = new List<string>();
        for (int i = 0; i < parts.Length; i++)
            for (int j = 0; j < parts.Length; j++)
            {
                if (i == j) continue;
                foreach (char x in parts[i])
                    foreach (char y in parts[j])
                    {
                        string requester = x.ToString(), responder = y.ToString();
                        var heartbeat = lab.Log.FirstOrDefault(s => s.Index > healIndex && s.From == responder && s.Message is HeartbeatMessage);
                        if (heartbeat is null) continue;
                        var reply = lab.Log.FirstOrDefault(s => s.Index > heartbeat.Index && s.From == responder
                            && s.Message is DowntimeSyncReply r && r.ReplyTo == requester);
                        if (reply is null) continue;
                        if (lab.RequestById(((DowntimeSyncReply)reply.Message).RequestId) is { } request && !IsFull(request))
                            races.Add($"{requester}←{responder} +{(reply.At - healAt).TotalSeconds:0} s");
                    }
            }
        return races;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Sprawdzenia.
    // ---------------------------------------------------------------------------------------------------------------

    private static void AssertNobodyLostDowntime(Lab lab, IEnumerable<DowntimeEpisode> expected, string context)
    {
        var all = expected.ToArray();
        var lost = lab.Pcs.Values
            .Select(pc => (pc.Host, Ids: pc.Journal.Snapshot().Select(e => e.EpisodeId).ToHashSet()))
            .Select(p => (p.Host, Missing: all.Where(e => !p.Ids.Contains(e.EpisodeId)).Select(e => e.MachineId).ToArray()))
            .Where(p => p.Missing.Length > 0).Select(p => $"{p.Host} nie ma {string.Join(", ", p.Missing)}").ToArray();
        Assert.True(lost.Length == 0, $"{context}: {string.Join("; ", lost)}.");
    }

    /// <summary>Brak wiecznego ponawiania: w ostatnich minutach nikt już nie prosi o pełne okno (wszystkie pełne okna zamknięte).</summary>
    private static void AssertNoFullWindowRequestsInTheLast(Lab lab, TimeSpan period, string context)
    {
        var late = lab.Log.Where(s => s.At >= lab.Clock.UtcNow - period && s.Message is DowntimeSyncRequest q && IsFull(q))
            .Select(s => $"{s.From} {s.At:HH:mm:ss}").ToArray();
        Assert.True(late.Length == 0, $"{context}: prośby o pełne okno w ostatnich {period.TotalMinutes:0} min: {string.Join(", ", late)}.");
    }

    /// <summary>Po powrocie łączności każdy komputer ponawia pełne okno najwyżej przez ~2 min (co 10 s, limit serii u partnera 60 s).</summary>
    private static void AssertBoundedFullWindowRetries(Lab lab, CycleResult result, string context)
    {
        foreach (string host in lab.Pcs.Keys)
        {
            int full = lab.Log.Count(s => s.Index > result.HealIndex && s.From == host && s.Message is DowntimeSyncRequest q && IsFull(q));
            Assert.True(full <= 3 * 13, $"{context}: {host} wysłał {full} próśb o pełne okno po powrocie łączności.");
        }
    }

    private static void AssertReportsEqual(Lab lab)
    {
        var reports = lab.Pcs.Values.ToDictionary(pc => pc.Host, pc =>
        {
            var rows = DowntimeReport.Build(pc.Journal.Snapshot(), TimeZoneInfo.Utc, now: lab.Clock.UtcNow);
            return (Rows: rows.Count, Minutes: rows.Sum(r => r.Minutes));
        });
        var first = reports.Values.First();
        Assert.True(reports.Values.All(r => r.Rows == first.Rows && Math.Abs(r.Minutes - first.Minutes) < 1e-6),
            "Raporty postojów różnią się: " + string.Join(", ", reports.Select(r => $"{r.Key} {r.Value.Rows} wierszy / {r.Value.Minutes:0.#} min")));
    }

    private static bool IsFull(DowntimeSyncRequest request) => request.Since == request.SentAt - Window;

    private static DowntimeEpisode Episode(string machine, DateTimeOffset start, TimeSpan length, string origin) =>
        new(machine, "Maszyna " + machine, "Servo 6", start, start + length, start + length,
            EpisodeId: Guid.NewGuid(), Origin: origin, Category: "restart");

    /// <summary>Ta sama reguła, co <c>AppOrchestrator.OnRemoteEpisodesReceived</c>.</summary>
    private static void Replicate(DowntimeJournal journal, IReadOnlyList<Alert> alerts, IReadOnlyList<EpisodeClosure> closures, string? origin)
    {
        foreach (var alert in alerts)
            if (DowntimeJournal.IsRecordedKind(alert.Kind)) journal.Merge(DowntimeEpisode.FromAlert(alert, origin));
        foreach (var closure in closures)
            if (DowntimeJournal.IsRecordedKind(closure.Kind)) journal.MergeClosure(closure.EpisodeId, closure.At, origin);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Laboratorium: komputery, zegary, sieć.
    // ---------------------------------------------------------------------------------------------------------------

    internal enum Fate { Deliver, Drop, Duplicate, Delay, DelayedDuplicate, Hold }

    internal sealed record Sent(long Index, DateTimeOffset At, string From, PeerMessage Message);

    /// <summary>Zegar komputera: czas prawdziwy + stałe przesunięcie (rozjazd zegarów); własny początek licznika monotonicznego.</summary>
    internal sealed class SkewClock(FakeClock clock, TimeSpan offset, long monotonicOffset) : IClock
    {
        public DateTimeOffset UtcNow => clock.UtcNow + offset;
        public DateTimeOffset Now => UtcNow;
        public long MonotonicMs => clock.MonotonicMs + monotonicOffset;
    }

    internal sealed class Pc(string host, int priority, SkewClock clock, string journalPath)
    {
        public string Host { get; } = host;
        public int Priority { get; } = priority;
        public SkewClock Clock { get; } = clock;
        public string JournalPath { get; } = journalPath;
        public GroupClusterNode Node { get; set; } = null!;
        public DowntimeJournal Journal { get; set; } = null!;
        public LabBus Bus { get; set; } = null!;
        public int Restarts { get; set; }
    }

    /// <summary>Magistrala komputera: stempluje jak UDP (zegar nadawcy), odbiera przez zwykłe TryDecode (zegar odbiorcy).</summary>
    internal sealed class LabBus(Lab lab, string host, IClock clock) : IPeerBus
    {
        private readonly string _instance = Guid.NewGuid().ToString("N");
        private long _seq;
        private bool _disposed;

        public string LocalHost => host;
        public List<string> Rejects { get; } = [];
        public event Action<PeerMessage>? Received;
        public event Action<string>? Rejected;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(PeerMessage msg, CancellationToken ct)
        {
            if (_disposed) return Task.CompletedTask;
            var stamped = msg with { Sender = host, Seq = ++_seq, SentAt = clock.UtcNow, InstanceId = _instance };
            lab.Dispatch(this, MessageCodec.Encode(stamped, lab.Signer), stamped);
            return Task.CompletedTask;
        }

        public void Deliver(byte[] datagram)
        {
            if (_disposed) return;
            if (!MessageCodec.TryDecode(datagram, lab.Signer, clock, TimeSpan.FromSeconds(lab.Config.ClockSkewToleranceSec),
                    out var message, out string? reason))
            {
                Rejects.Add(reason ?? "?");
                Rejected?.Invoke(reason ?? "?");
                return;
            }
            if (message is null || message.Sender == host) return;
            Received?.Invoke(message);
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Lab : IAsyncDisposable
    {
        private readonly TempDir _dir = new("test-requestid-lab");
        private readonly HashSet<(string, string)> _cuts = [];
        private readonly List<(string From, string To, byte[] Datagram, DateTimeOffset Due)> _delayed = [];
        private readonly List<(string To, byte[] Datagram)> _held = [];
        private readonly Dictionary<Guid, DowntimeSyncRequest> _requests = new();
        private long _index;

        public Lab(ClusterConfig? config = null)
        {
            var group = GroupFileTests.Invitation();
            Signer = HmacSigner.FromBase64(group.NetworkKey);
            Config = (config ?? new ClusterConfig()) with { Enabled = true, Group = group.Identity };
        }

        public FakeClock Clock { get; } = new(T0);
        public HmacSigner Signer { get; }
        public ClusterConfig Config { get; }
        public Dictionary<string, Pc> Pcs { get; } = new(StringComparer.Ordinal);
        public List<Sent> Log { get; } = [];
        public bool LogHeartbeats { get; set; }
        public Func<Sent, string, Fate>? Filter { get; set; }
        public TimeSpan DelayedBy { get; init; } = TimeSpan.FromSeconds(30);
        public Action? AfterStep { get; set; }
        public int HeldCount => _held.Count;
        public Pc this[string host] => Pcs[host];
        public Pc this[char host] => Pcs[host.ToString()];

        public async Task AddAsync(string host, int priority, int skewSeconds = 0, params DowntimeEpisode[] history)
        {
            var pc = new Pc(host, priority, new SkewClock(Clock, TimeSpan.FromSeconds(skewSeconds), 7_000_000L * (Pcs.Count + 1)),
                _dir.File(host + ".jsonl"));
            Pcs.Add(host, pc);
            await StartAsync(pc);
            foreach (var episode in history) pc.Journal.Merge(episode);
        }

        private async Task StartAsync(Pc pc)
        {
            var journal = new DowntimeJournal(pc.JournalPath);
            var bus = new LabBus(this, pc.Host, pc.Clock);
            var node = new GroupClusterNode(Config with { Priority = pc.Priority }, bus, pc.Clock, "test") { Downtime = journal };
            node.EpisodeStateReceived += (alerts, closures) => Replicate(journal, alerts, closures, node.WatcherHost);
            node.SetReadiness(new(true, false, []));
            pc.Journal = journal;
            pc.Bus = bus;
            pc.Node = node;
            await node.StartAsync(default);
        }

        /// <summary>Nowy proces programu: nowa instancja (Seq od 1), dziennik czytany z pliku, pamięć węzła od zera.</summary>
        public async Task RestartAsync(string host)
        {
            var pc = Pcs[host];
            pc.Journal.Flush();
            await pc.Node.DisposeAsync();
            pc.Restarts++;
            await StartAsync(pc);
        }

        public long Mark() => _index;

        public DowntimeSyncRequest? RequestById(Guid id) => _requests.GetValueOrDefault(id);

        /// <summary>Rozcina sieć między częściami (każda para komputerów z różnych części).</summary>
        public void Split(params string[] parts)
        {
            for (int i = 0; i < parts.Length; i++)
                for (int j = i + 1; j < parts.Length; j++)
                    foreach (char x in parts[i])
                        foreach (char y in parts[j]) _cuts.Add(Pair(x.ToString(), y.ToString()));
        }

        public void HealBetween(string first, string second)
        {
            foreach (char x in first)
                foreach (char y in second) _cuts.Remove(Pair(x.ToString(), y.ToString()));
        }

        public void HealAll() => _cuts.Clear();

        private static (string, string) Pair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

        public async Task StepAsync(TimeSpan step, string? order = null)
        {
            foreach (string host in order is null ? Pcs.Keys.ToArray() : order.Select(c => c.ToString()))
                await Pcs[host].Node.TickAsync(default);
            AfterStep?.Invoke();
            Clock.Advance(step);
            ReleaseDue();
        }

        public async Task RunAsync(TimeSpan duration, TimeSpan step, string? order = null)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += step) await StepAsync(step, order);
        }

        /// <summary>
        /// <see langword="true"/> (domyślnie): kolejka doręczeń — datagram wysłany w trakcie obsługi innego czeka, aż wcześniejsze
        /// dotrą do wszystkich odbiorców (kolejność FIFO od każdego nadawcy, jak UDP w sieci lokalnej). <see langword="false"/>:
        /// doręczanie „w głąb” jak <see cref="InMemoryPeerHub"/> — odpowiedź i kolejna prośba mogą wyprzedzić u trzeciego
        /// komputera wcześniejszy datagram tego samego nadawcy.
        /// </summary>
        public bool Fifo { get; init; } = true;

        private readonly Queue<(LabBus From, string To, byte[] Datagram)> _queue = new();
        private bool _pumping;

        internal void Dispatch(LabBus from, byte[] datagram, PeerMessage stamped)
        {
            if (!Pcs.TryGetValue(from.LocalHost, out var sender) || !ReferenceEquals(sender.Bus, from)) return;
            var sent = new Sent(++_index, Clock.UtcNow, from.LocalHost, stamped);
            if (stamped is DowntimeSyncRequest request) _requests[request.RequestId] = request;
            if (stamped is DowntimeSyncRequest or DowntimeSyncReply || LogHeartbeats && stamped is HeartbeatMessage) Log.Add(sent);
            foreach (var to in Pcs.Values.ToArray())
            {
                if (to.Host == from.LocalHost || _cuts.Contains(Pair(from.LocalHost, to.Host))) continue;
                switch (Filter?.Invoke(sent, to.Host) ?? Fate.Deliver)
                {
                    case Fate.Drop: break;
                    case Fate.Duplicate: Send(from, to.Host, datagram); Send(from, to.Host, datagram); break;
                    case Fate.Delay: _delayed.Add((from.LocalHost, to.Host, datagram, Clock.UtcNow + DelayedBy)); break;
                    case Fate.DelayedDuplicate:
                        Send(from, to.Host, datagram);
                        _delayed.Add((from.LocalHost, to.Host, datagram, Clock.UtcNow + DelayedBy));
                        break;
                    case Fate.Hold: _held.Add((to.Host, datagram)); break;
                    default: Send(from, to.Host, datagram); break;
                }
            }
            Pump();
        }

        private void Send(LabBus from, string to, byte[] datagram)
        {
            if (Fifo) _queue.Enqueue((from, to, datagram));
            else Pcs[to].Bus.Deliver(datagram);
        }

        private void Pump()
        {
            if (!Fifo || _pumping) return;
            _pumping = true;
            try
            {
                while (_queue.TryDequeue(out var item)) Pcs[item.To].Bus.Deliver(item.Datagram);
            }
            finally { _pumping = false; }
        }

        /// <summary>Opóźnione pakiety trafiają do BIEŻĄCEGO procesu odbiorcy (ten sam port po restarcie), o ile sieć jest cała.</summary>
        private void ReleaseDue()
        {
            var due = _delayed.Where(d => d.Due <= Clock.UtcNow).ToArray();
            if (due.Length == 0) return;
            _delayed.RemoveAll(d => d.Due <= Clock.UtcNow);
            foreach (var (from, to, datagram, _) in due)
                if (!_cuts.Contains(Pair(from, to))) Pcs[to].Bus.Deliver(datagram);
        }

        public void ReleaseHeld()
        {
            var held = _held.ToArray();
            _held.Clear();
            foreach (var (to, datagram) in held) Pcs[to].Bus.Deliver(datagram);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var pc in Pcs.Values) await pc.Node.DisposeAsync();
            _dir.Dispose();
        }
    }
}
