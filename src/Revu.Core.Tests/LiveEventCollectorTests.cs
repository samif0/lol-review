using System.Net;
using System.Text;
using System.Text.Json;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

public sealed class LiveEventCollectorTests
{
    [Fact]
    public void AppendNewRawEvents_AppendsByPositionWhenEventIdsRepeat()
    {
        var destination = new List<JsonElement>();
        var firstSnapshot = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 },
              { "EventID": 0, "EventName": "MinionsSpawning", "EventTime": 65.0 }
            ]
            """);
        var secondSnapshot = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 },
              { "EventID": 0, "EventName": "MinionsSpawning", "EventTime": 65.0 },
              { "EventID": 0, "EventName": "ChampionKill", "EventTime": 200.0, "KillerName": "Tester", "VictimName": "Enemy", "Assisters": [] },
              { "EventID": 0, "EventName": "DragonKill", "EventTime": 610.0, "DragonType": "Earth", "Stolen": false, "KillerName": "Tester" }
            ]
            """);

        LiveEventCollector.AppendNewRawEvents(destination, firstSnapshot);
        LiveEventCollector.AppendNewRawEvents(destination, secondSnapshot);

        Assert.Equal(4, destination.Count);
        Assert.Equal("ChampionKill", destination[2].GetProperty("EventName").GetString());
        Assert.Equal("DragonKill", destination[3].GetProperty("EventName").GetString());
    }

    [Fact]
    public void ParseLiveEvents_CapturesKillerAndAssistersOnDeath()
    {
        // A ChampionKill where the player is the victim, with two assisters. The DEATH
        // event must carry both killer and the assisters array (the jungle-gank signal).
        var raw = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "ChampionKill", "EventTime": 300.0,
                "KillerName": "EnemyMid", "VictimName": "Tester",
                "Assisters": ["EnemyJungler", "EnemySupport"] }
            ]
            """);

        var parsed = LiveEventCollector.ParseLiveEvents(raw, "Tester");
        var death = parsed.Single(e => e.EventType == GameEvent.EventTypes.Death);

        using var details = JsonDocument.Parse(death.Details);
        Assert.Equal("EnemyMid", details.RootElement.GetProperty("killer").GetString());
        var assisters = details.RootElement.GetProperty("assisters").EnumerateArray()
            .Select(a => a.GetString()).ToArray();
        Assert.Equal(new[] { "EnemyJungler", "EnemySupport" }, assisters);
    }

    [Fact]
    public async Task StopAsync_CollectsObjectiveAndPlayerEvents_WhenSnapshotsGrow()
    {
        var snapshots = new Queue<List<JsonElement>>(
        [
            CreateEvents(
                """
                [
                  { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }
                ]
                """),
            CreateEvents(
                """
                [
                  { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 },
                  { "EventID": 0, "EventName": "TurretKilled", "EventTime": 420.0, "TurretKilled": "Turret_T2_L_03_A", "KillerName": "Ally" },
                  { "EventID": 0, "EventName": "ChampionKill", "EventTime": 510.0, "KillerName": "Tester", "VictimName": "Enemy", "Assisters": [] },
                  { "EventID": 0, "EventName": "DragonKill", "EventTime": 610.0, "DragonType": "Earth", "Stolen": false, "KillerName": "Ally" }
                ]
                """)
        ]);

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () =>
            {
                if (snapshots.Count > 1)
                {
                    return snapshots.Dequeue();
                }

                return snapshots.Peek();
            });
        var collector = new LiveEventCollector(
            api,
            NullLogger.Instance,
            pollInterval: TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);

        await Task.Delay(35);
        await cts.CancelAsync();
        await runTask;

        var parsedEvents = await collector.StopAsync();

        Assert.Collection(
            parsedEvents,
            evt =>
            {
                Assert.Equal(GameEvent.EventTypes.Turret, evt.EventType);
                Assert.Equal(420, evt.GameTimeS);
            },
            evt =>
            {
                Assert.Equal(GameEvent.EventTypes.Kill, evt.EventType);
                Assert.Equal(510, evt.GameTimeS);
            },
            evt =>
            {
                Assert.Equal(GameEvent.EventTypes.Dragon, evt.EventType);
                Assert.Equal(610, evt.GameTimeS);
            });
    }

    // REGRESSION GUARD for the Flash-ingestion bug (v3.0.22). The previous version of
    // this test fabricated a `rawCooldown` field and asserted a FLASH event WAS
    // synthesised — but the real Live Client Data API exposes NO cooldown anywhere
    // (and /activeplayer carries no summonerSpells block at all; that's only on
    // /playerlist, names-only). So cooldown-transition detection can never fire in
    // production. These two tests now feed the collector a REALISTIC /activeplayer
    // snapshot (currentGold present, NO summonerSpells cooldown) and assert the
    // honest outcome: zero summoner-spell events. If someone re-enables cooldown
    // detection against a real captured payload, these are the tests to revisit.
    [Fact]
    public async Task StopAsync_ProducesNoFlashCast_FromRealisticActivePlayerWithoutCooldown()
    {
        // A growing event stream so the collector has a non-zero clock, exactly as in
        // a real game — but the active-player snapshots carry the REAL shape (no
        // cooldown on summonerSpells). The cooldown detector is disabled, so even a
        // "Flash then later" sequence yields nothing.
        var events = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 },
              { "EventID": 0, "EventName": "MinionsSpawning", "EventTime": 65.0 }
            ]
            """);

        var snapshots = new Queue<JsonElement>(
        [
            CreateActivePlayer(spell1Name: "Flash", spell2Name: "Ignite", gameTime: 120.0),
            CreateActivePlayer(spell1Name: "Flash", spell2Name: "Ignite", gameTime: 130.0),
        ]);

        var parsed = await RunCollector(events, snapshots);

        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.Flash);
        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.SummonerSpell);
    }

    [Fact]
    public async Task StopAsync_ProducesNoSummonerSpellCast_FromRealisticActivePlayerWithoutCooldown()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        var snapshots = new Queue<JsonElement>(
        [
            CreateActivePlayer(spell1Name: "Flash", spell2Name: "Ignite", gameTime: 80.0),
            CreateActivePlayer(spell1Name: "Flash", spell2Name: "Ignite", gameTime: 95.0),
        ]);

        var parsed = await RunCollector(events, snapshots);

        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.SummonerSpell);
        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.Flash);
    }

    // Realistic /liveclientdata/activeplayer snapshot. summonerSpells uses the REAL
    // field shape — { displayName, rawDescription, rawDisplayName }, NO cooldown — and
    // matches what the live endpoint returns (note: the real /activeplayer doesn't even
    // include summonerSpells; we include it here only to prove the detector ignores it
    // even when present, since the field it actually keyed on, rawCooldown, is absent).
    // currentGold/resourceType are genuinely on /activeplayer and drive the recall tests.
    private static JsonElement CreateActivePlayer(string spell1Name, string spell2Name, double gameTime, double currentGold = 500.0)
    {
        var json = $$"""
            {
              "gameTime": {{gameTime}},
              "currentGold": {{currentGold}},
              "summonerSpells": {
                "summonerSpellOne": { "displayName": "{{spell1Name}}", "rawDescription": "GeneratedTip_SummonerSpell_{{spell1Name}}_Description", "rawDisplayName": "GeneratedTip_SummonerSpell_{{spell1Name}}_DisplayName" },
                "summonerSpellTwo": { "displayName": "{{spell2Name}}", "rawDescription": "GeneratedTip_SummonerSpell_{{spell2Name}}_Description", "rawDisplayName": "GeneratedTip_SummonerSpell_{{spell2Name}}_DisplayName" }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // Active-player snapshot with championStats for HP/mana restore detection. Gold is
    // held constant (500) so the gold detector never fires — isolating the restore path.
    private static JsonElement CreatePlayerStats(double gameTime, string resourceType, double curHp, double maxHp, double curRes, double maxRes)
    {
        var json = $$"""
            {
              "gameTime": {{gameTime}},
              "currentGold": 500.0,
              "resourceType": "{{resourceType}}",
              "summonerSpells": {
                "summonerSpellOne": { "displayName": "Flash", "rawDescription": "GeneratedTip_SummonerSpell_Flash_Description", "rawDisplayName": "GeneratedTip_SummonerSpell_Flash_DisplayName" },
                "summonerSpellTwo": { "displayName": "Ignite", "rawDescription": "GeneratedTip_SummonerSpell_Ignite_Description", "rawDisplayName": "GeneratedTip_SummonerSpell_Ignite_DisplayName" }
              },
              "championStats": {
                "resourceType": "{{resourceType}}",
                "currentHealth": {{curHp}}, "maxHealth": {{maxHp}},
                "resourceValue": {{curRes}}, "resourceMax": {{maxRes}}
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task StopAsync_DerivesRecall_WhenManaChampHpAndManaBothRestoreToFull()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // Mana champ: HP 40%→100% AND mana 30%→100% at 408s ⇒ fountain restore ⇒ recall
        // anchored at 400s. (Gold constant, so this can only be the restore detector.)
        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStats(400.0, "MANA", curHp: 240, maxHp: 600, curRes: 90, maxRes: 300),
            CreatePlayerStats(408.0, "MANA", curHp: 600, maxHp: 600, curRes: 300, maxRes: 300),
        ]);

        var parsed = await RunCollector(events, snapshots);
        var recall = parsed.SingleOrDefault(e => e.EventType == GameEvent.EventTypes.Recall);

        Assert.NotNull(recall);
        Assert.Equal(400, recall!.GameTimeS); // 408 − 8s channel
        using var details = JsonDocument.Parse(recall.Details);
        Assert.Equal("health_restore", details.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task StopAsync_DoesNotDeriveRecall_WhenHpHealsToFullButManaIsNot()
    {
        // The Soraka-ult / Aatrox-lifesteal case: HP heals 30%→100% in combat, but mana
        // stays low (40%). NOT a fountain restore ⇒ no recall. This is the soundness test.
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");
        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStats(500.0, "MANA", curHp: 180, maxHp: 600, curRes: 120, maxRes: 300),
            CreatePlayerStats(510.0, "MANA", curHp: 600, maxHp: 600, curRes: 120, maxRes: 300),
        ]);

        var parsed = await RunCollector(events, snapshots);
        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.Recall);
    }

    [Fact]
    public async Task StopAsync_DoesNotDeriveRecall_ForManalessChampEvenWhenHpFull()
    {
        // Manaless champ (resourceType NONE): full HP is no signal at all ⇒ never fires
        // the restore detector (gold-only for these). HP jumps 50%→100%, still nothing.
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");
        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStats(300.0, "NONE", curHp: 300, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(310.0, "NONE", curHp: 600, maxHp: 600, curRes: 0, maxRes: 0),
        ]);

        var parsed = await RunCollector(events, snapshots);
        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.Recall);
    }

    [Fact]
    public async Task StopAsync_DoesNotDeriveRecall_ForEnergyChamp()
    {
        // Energy champ (Akali/Zed): energy is near-always full, so we treat it like
        // manaless — gold-only, no restore detector. Even HP+energy both "full" → nothing.
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");
        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStats(300.0, "ENERGY", curHp: 300, maxHp: 600, curRes: 100, maxRes: 200),
            CreatePlayerStats(310.0, "ENERGY", curHp: 600, maxHp: 600, curRes: 200, maxRes: 200),
        ]);

        var parsed = await RunCollector(events, snapshots);
        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.Recall);
    }

    [Fact]
    public async Task StopAsync_EmitsOneRecall_WhenRestoreAndPurchaseHappenTogether()
    {
        // The realistic case: recall → fountain restores HP+mana → then buys items, all
        // within the debounce window. The two detectors must NOT both fire — one recall.
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");
        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStatsGold(400.0, "MANA", curHp: 240, maxHp: 600, curRes: 90, maxRes: 300, gold: 1400),
            // restore fires here (HP+mana full); gold still high
            CreatePlayerStatsGold(408.0, "MANA", curHp: 600, maxHp: 600, curRes: 300, maxRes: 300, gold: 1400),
            // then a purchase a few seconds later — gold detector would fire, but debounced
            CreatePlayerStatsGold(414.0, "MANA", curHp: 600, maxHp: 600, curRes: 300, maxRes: 300, gold: 200),
        ]);

        var parsed = await RunCollector(events, snapshots);
        Assert.Single(parsed, e => e.EventType == GameEvent.EventTypes.Recall);
    }

    // Like CreatePlayerStats but with explicit gold (to exercise both detectors together).
    private static JsonElement CreatePlayerStatsGold(double gameTime, string resourceType, double curHp, double maxHp, double curRes, double maxRes, double gold)
    {
        var json = $$"""
            {
              "gameTime": {{gameTime}},
              "currentGold": {{gold}},
              "resourceType": "{{resourceType}}",
              "summonerSpells": {
                "summonerSpellOne": { "displayName": "Flash", "rawDescription": "GeneratedTip_SummonerSpell_Flash_Description", "rawDisplayName": "GeneratedTip_SummonerSpell_Flash_DisplayName" },
                "summonerSpellTwo": { "displayName": "Ignite", "rawDescription": "GeneratedTip_SummonerSpell_Ignite_Description", "rawDisplayName": "GeneratedTip_SummonerSpell_Ignite_DisplayName" }
              },
              "championStats": {
                "resourceType": "{{resourceType}}",
                "currentHealth": {{curHp}}, "maxHealth": {{maxHp}},
                "resourceValue": {{curRes}}, "resourceMax": {{maxRes}}
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // Shared runner: start the collector over a snapshot queue, let it poll, stop, parse.
    private static async Task<List<GameEvent>> RunCollector(List<JsonElement> events, Queue<JsonElement> snapshots)
    {
        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => events,
            fetchActivePlayerAsync: () => snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek());
        var collector = new LiveEventCollector(api, NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        await Task.Delay(60);
        await cts.CancelAsync();
        await runTask;
        return await collector.StopAsync();
    }

    [Fact]
    public async Task StopAsync_DerivesRecall_WhenGoldDropsLikeAShopPurchase()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // Gold goes 1300 → 200 (spent 1100) at game time 308s: a shop purchase ⇒ the
        // player just recalled. The recall is anchored 8s before the purchase = 300s.
        var snapshots = new Queue<JsonElement>(
        [
            CreateActivePlayer("Flash", "Ignite", gameTime: 300.0, currentGold: 1300.0),
            CreateActivePlayer("Flash", "Ignite", gameTime: 308.0, currentGold: 200.0),
        ]);

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => events,
            fetchActivePlayerAsync: () => snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek());

        var collector = new LiveEventCollector(api, NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        await Task.Delay(60);
        await cts.CancelAsync();
        await runTask;

        var parsed = await collector.StopAsync();
        var recall = parsed.SingleOrDefault(e => e.EventType == GameEvent.EventTypes.Recall);

        Assert.NotNull(recall);
        Assert.Equal(300, recall!.GameTimeS); // 308 purchase − 8s channel
        using var details = JsonDocument.Parse(recall.Details);
        Assert.True(details.RootElement.GetProperty("detected").GetBoolean());
        Assert.Equal(1100, details.RootElement.GetProperty("gold_spent").GetInt32());
    }

    [Fact]
    public async Task StopAsync_DoesNotDeriveRecall_ForSmallGoldDrops()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // Gold 400 → 325 (spent 75 — a control ward bought in lane, no recall). Below
        // the 250g threshold, so no RECALL is emitted.
        var snapshots = new Queue<JsonElement>(
        [
            CreateActivePlayer("Flash", "Ignite", gameTime: 200.0, currentGold: 400.0),
            CreateActivePlayer("Flash", "Ignite", gameTime: 210.0, currentGold: 325.0),
        ]);

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => events,
            fetchActivePlayerAsync: () => snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek());

        var collector = new LiveEventCollector(api, NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        await Task.Delay(60);
        await cts.CancelAsync();
        await runTask;

        var parsed = await collector.StopAsync();
        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.Recall);
    }

    [Fact]
    public async Task StopAsync_DebouncesRecall_AcrossConsecutiveFountainBuys()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // Two big buys close together (one back, several purchases): 1500→300 at 300s,
        // then 900→100 at 305s. Within the 25s debounce window ⇒ only ONE recall.
        var snapshots = new Queue<JsonElement>(
        [
            CreateActivePlayer("Flash", "Ignite", gameTime: 295.0, currentGold: 1500.0),
            CreateActivePlayer("Flash", "Ignite", gameTime: 300.0, currentGold: 300.0),
            CreateActivePlayer("Flash", "Ignite", gameTime: 305.0, currentGold: 100.0),
        ]);

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => events,
            fetchActivePlayerAsync: () => snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek());

        var collector = new LiveEventCollector(api, NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        await Task.Delay(80);
        await cts.CancelAsync();
        await runTask;

        var parsed = await collector.StopAsync();
        Assert.Single(parsed, e => e.EventType == GameEvent.EventTypes.Recall);
    }

    // ── trade detection (HP drop while alive) ────────────────────────────────────
    // CreatePlayerStats holds gold constant (500) and we use resourceType NONE so
    // neither recall detector can fire — isolating the trade path. gameTime is used
    // only as the dip anchor; pollInterval is fake-fast so each queued snapshot = a tick.

    [Fact]
    public async Task StopAsync_DerivesShortTrade_WhenHpDropsOneTickThenRecovers()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // HP 100% → 55% (one big poke, lost 45% — a clear trade) → regen back to 70%.
        // One dropping tick ⇒ SHORT. Dip opens at 300s (where the drop was seen).
        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStats(290.0, "NONE", curHp: 600, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(300.0, "NONE", curHp: 330, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(310.0, "NONE", curHp: 420, maxHp: 600, curRes: 0, maxRes: 0),
        ]);

        var parsed = await RunCollector(events, snapshots);
        var trade = parsed.SingleOrDefault(e => e.EventType == GameEvent.EventTypes.Trade);

        Assert.NotNull(trade);
        Assert.Equal(300, trade!.GameTimeS);
        using var details = JsonDocument.Parse(trade.Details);
        Assert.Equal("short", details.RootElement.GetProperty("kind").GetString());
        Assert.True(details.RootElement.GetProperty("detected").GetBoolean());
        Assert.Equal(45, details.RootElement.GetProperty("hp_lost_pct").GetInt32());
    }

    [Fact]
    public async Task StopAsync_DerivesExtendedTrade_WhenHpDropsAcrossConsecutiveTicks()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // HP 100% → 70% → 40% (damage sustained across TWO ticks) → recovers to 55%.
        // Two consecutive dropping ticks ⇒ EXTENDED. Deepest loss = 100%−40% = 60%.
        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStats(490.0, "NONE", curHp: 600, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(500.0, "NONE", curHp: 420, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(510.0, "NONE", curHp: 240, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(520.0, "NONE", curHp: 330, maxHp: 600, curRes: 0, maxRes: 0),
        ]);

        var parsed = await RunCollector(events, snapshots);
        var trade = parsed.SingleOrDefault(e => e.EventType == GameEvent.EventTypes.Trade);

        Assert.NotNull(trade);
        Assert.Equal(500, trade!.GameTimeS); // anchored where the dip began
        using var details = JsonDocument.Parse(trade.Details);
        Assert.Equal("extended", details.RootElement.GetProperty("kind").GetString());
        Assert.Equal(60, details.RootElement.GetProperty("hp_lost_pct").GetInt32());
    }

    [Fact]
    public async Task StopAsync_DoesNotDeriveTrade_ForSmallChipDamage()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // HP 100% → 92% (a minion auto / small DoT, 8% — below the 12% trade floor) →
        // recovers. No trade.
        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStats(200.0, "NONE", curHp: 600, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(210.0, "NONE", curHp: 552, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(220.0, "NONE", curHp: 600, maxHp: 600, curRes: 0, maxRes: 0),
        ]);

        var parsed = await RunCollector(events, snapshots);
        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.Trade);
    }

    [Fact]
    public async Task StopAsync_DoesNotDeriveTrade_WhenHpDropToZeroIsADeath()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // HP 80% → 0% (died) → 100% (respawn). The dip bottoms at 0 ⇒ NOT a trade, even
        // though the respawn rise closes the dip. (A death already has its own DEATH row
        // from the kill-feed; a trade is specifically surviving the damage.)
        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStats(600.0, "NONE", curHp: 480, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(610.0, "NONE", curHp: 0, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(620.0, "NONE", curHp: 600, maxHp: 600, curRes: 0, maxRes: 0),
        ]);

        var parsed = await RunCollector(events, snapshots);
        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.Trade);
    }

    [Fact]
    public async Task StopAsync_DoesNotDeriveTrade_WhenDeathHappensBetweenPolls()
    {
        // The between-tick death: a death+respawn fits ENTIRELY between two 10s polls, so
        // the 0-HP frame is NEVER sampled. HP looks like 80% → 30% (dip) → 100% (bounce
        // back), which without the kill-feed cross-check would emit a phantom "survived"
        // trade. The kill-feed (ChampionKill victim=Tester at 612s, inside the dip window)
        // is authoritative ⇒ this dip is that death, not a trade.
        var events = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 },
              { "EventID": 1, "EventName": "ChampionKill", "EventTime": 612.0, "KillerName": "Enemy", "VictimName": "Tester", "Assisters": [] }
            ]
            """);

        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStats(600.0, "NONE", curHp: 480, maxHp: 600, curRes: 0, maxRes: 0), // 80%
            CreatePlayerStats(610.0, "NONE", curHp: 180, maxHp: 600, curRes: 0, maxRes: 0), // 30% — dip; 0-HP frame at ~612 unsampled
            CreatePlayerStats(620.0, "NONE", curHp: 600, maxHp: 600, curRes: 0, maxRes: 0), // respawned full — closes the dip
        ]);

        var parsed = await RunCollector(events, snapshots);

        // The DEATH from the kill-feed is present; the phantom TRADE is suppressed.
        Assert.Contains(parsed, e => e.EventType == GameEvent.EventTypes.Death);
        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.Trade);
    }

    [Fact]
    public async Task StopAsync_StillDerivesTrade_WhenAnUnrelatedDeathIsFarFromTheDip()
    {
        // A real survived trade early, and an unrelated death much later (outside the dip
        // window + slack). The kill-feed guard must NOT swallow the trade just because the
        // player died at some OTHER point in the game.
        var events = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 },
              { "EventID": 1, "EventName": "ChampionKill", "EventTime": 900.0, "KillerName": "Enemy", "VictimName": "Tester", "Assisters": [] }
            ]
            """);

        var snapshots = new Queue<JsonElement>(
        [
            CreatePlayerStats(290.0, "NONE", curHp: 600, maxHp: 600, curRes: 0, maxRes: 0),
            CreatePlayerStats(300.0, "NONE", curHp: 330, maxHp: 600, curRes: 0, maxRes: 0), // -45% survived trade at 300s
            CreatePlayerStats(310.0, "NONE", curHp: 420, maxHp: 600, curRes: 0, maxRes: 0), // recovers (death at 900 is far away)
        ]);

        var parsed = await RunCollector(events, snapshots);
        Assert.Single(parsed, e => e.EventType == GameEvent.EventTypes.Trade);
    }

    [Fact]
    public async Task GetActivePlayerNameAsync_PrefersActivePlayerNameEndpoint()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var payload = request.RequestUri!.AbsolutePath switch
            {
                "/liveclientdata/activeplayername" => "\"Riot Tuxedo\"",
                "/liveclientdata/activeplayer" => """
                    {
                      "riotId": "Riot Tuxedo#TXC1",
                      "riotIdGameName": "Ignored Fallback",
                      "summonerName": "Ignored Summoner"
                    }
                    """,
                _ => throw new InvalidOperationException($"Unexpected request path {request.RequestUri.AbsolutePath}")
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        var api = new LiveEventApi(httpClient, NullLogger<LiveEventApi>.Instance);

        var playerName = await api.GetActivePlayerNameAsync();

        Assert.Equal("Riot Tuxedo", playerName);
    }

    [Fact]
    public void ResolveActivePlayerName_PrefersGameNameOverFullRiotId()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "riotId": "Riot Tuxedo#TXC1",
              "riotIdGameName": "Riot Tuxedo",
              "summonerName": "LegacyName"
            }
            """);

        var playerName = LiveEventApi.ResolveActivePlayerName(document.RootElement);

        Assert.Equal("Riot Tuxedo", playerName);
    }

    private static List<JsonElement> CreateEvents(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(static item => item.Clone())];
    }

    private sealed class FakeLiveEventApi : ILiveEventApi
    {
        private readonly Func<List<JsonElement>> _fetchEventsAsync;
        private readonly Func<JsonElement?>? _fetchActivePlayerAsync;

        public FakeLiveEventApi(
            Func<List<JsonElement>> fetchEventsAsync,
            Func<JsonElement?>? fetchActivePlayerAsync = null)
        {
            _fetchEventsAsync = fetchEventsAsync;
            _fetchActivePlayerAsync = fetchActivePlayerAsync;
        }

        public Task<string?> GetActivePlayerNameAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("Tester");

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<JsonElement>?> FetchEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<List<JsonElement>?>(_fetchEventsAsync());

        public Task<JsonElement?> FetchActivePlayerAsync(CancellationToken ct = default) =>
            Task.FromResult(_fetchActivePlayerAsync?.Invoke());
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
