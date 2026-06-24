#nullable enable

using System.Text.Json;
using Revu.Core.Models;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Lcu;

/// <summary>
/// Polls the Live Client Data API during a game to collect events.
/// Ported from Python LiveEventCollector class in live_events.py.
/// </summary>
public sealed class LiveEventCollector
{
    private readonly ILiveEventApi _liveEventApi;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;

    private readonly List<JsonElement> _rawEvents = [];
    private string? _playerName;

    // v2.17.7 (BROKEN) / v3.0.22 (disabled): summoner-spell cast detection.
    //
    // The original design synthesised casts by watching the active player's spell
    // cooldowns (prev ~0 → new >0 == just cast). That can NEVER fire against the
    // real Live Client Data API, because the cooldown signal it depends on does not
    // exist in ANY local endpoint:
    //   • /liveclientdata/activeplayer carries NO `summonerSpells` block at all
    //     (verified against Riot's documented schema). The old code read
    //     activePlayer.summonerSpells, so TryGetProperty returned false and the
    //     method bailed every tick — zero events, always.
    //   • /liveclientdata/playerlist DOES expose per-player summonerSpells, but only
    //     { displayName, rawDescription, rawDisplayName } — NO cooldown / no timing.
    //   • /liveclientdata/eventdata has no summoner-spell cast events.
    //   • Match-V5 / EOG only expose aggregate SPELL1_CAST..SPELL4_CAST COUNTS
    //     (already captured in StatsExtractor / GameStats.Spell{1..4}Casts), with no
    //     per-cast timestamps.
    // So timestamped FLASH/SUMMONER_SPELL game_events cannot be produced from any
    // available data source. The unit test that "passed" fabricated a `rawCooldown`
    // field the endpoint omits — green test, dead production. See .audit (capture
    // harness + decision note) and LiveEventCollectorTests for the honest coverage.
    //
    // We keep the synthesis plumbing (StopAsync still merges _summonerSpellEvents)
    // so a future source — an EOG count-only fallback, a replay parser, or a client
    // version that genuinely starts emitting cooldowns (re-confirm via the capture
    // harness) — can populate it without re-threading the merge. But the cooldown
    // detector is OFF by default: enabling it requires a real captured payload that
    // actually carries a usable cooldown on summonerSpells. Until then it stays a
    // no-op rather than masquerading as working.
    // static readonly (not const) on purpose: a const false would make the guarded
    // body unreachable and trip CS0162. This keeps the gate honest without the warning.
    private static readonly bool CooldownCastDetectionEnabled = false;
    private readonly Dictionary<string, SummonerSpellState> _summonerSpellState = new();
    private readonly List<GameEvent> _summonerSpellEvents = [];

    private sealed class SummonerSpellState
    {
        public string DisplayName { get; set; } = "";
        public double LastCooldown { get; set; }
    }

    // v3.0.18: recall (back) detection. Riot's API has no recall event, so we infer
    // it from shop purchases: currentGold dropping (while alive) means you bought at
    // the fountain, i.e. you just finished a recall. We anchor the RECALL event ~8s
    // earlier (the recall channel time). State = last observed gold + the time of the
    // last emitted recall (debounce so one back doesn't fire on every fountain buy).
    private readonly List<GameEvent> _recallEvents = [];
    private double _lastGold = double.NaN;       // NaN = no baseline sampled yet
    private int _lastRecallEmitS = int.MinValue; // game-time of the last recall we emitted
    // A drop ≥ this many gold reads as a real item purchase (filters trinket/ward
    // micro-buys like a 75g control ward bought without backing).
    private const int RecallMinGoldSpend = 250;
    // Recall channel time (standard back). The purchase lands AFTER the channel, so
    // the recall itself started roughly this many seconds earlier.
    private const int RecallChannelSeconds = 8;
    // Don't emit another recall within this window — a single back often produces
    // several fountain purchases across a few poll ticks. Shared by BOTH detectors
    // (gold + health-restore) so a recall-then-buy never double-fires.
    private const int RecallDebounceSeconds = 25;

    // v3.0.18: second recall signal — a FOUNTAIN HP+mana restore, to catch recalls
    // with no purchase (recalled to defend, to TP back, or with no gold). The
    // discriminator vs an in-combat heal: a fountain restores HP AND mana to 100%
    // SIMULTANEOUSLY; a heal (Aatrox autos, Soraka ult, Mundo regen) restores HP but
    // not mana. So we ONLY use this for MANA champs (resourceType == "MANA") and
    // require BOTH at full, transitioning from below-full. Manaless / energy / rage /
    // fury champs can't use the mana tell (their resource is near-always full or
    // absent), so they stay gold-only — better to miss than to false-fire.
    private double _lastHealthFrac = double.NaN; // last currentHealth/maxHealth
    private double _lastManaFrac = double.NaN;   // last resourceValue/resourceMax (MANA only)
    // "Full" threshold (fraction). Fountain pins to exactly 1.0; allow a hair of float noise.
    private const double RestoreFullFrac = 0.99;
    // The transition gate: only fire when at least one resource was MEANINGFULLY below
    // full last tick (so we fire on the jump TO full, not every idle-at-full tick).
    private const double RestoreWasLowFrac = 0.80;

    // v3.1.8: trade detection — you took damage and lived. The Live Client API exposes
    // only YOUR championStats HP per tick (never the enemy's), so a "trade" is inferred
    // from your own HP dropping while alive. We track HP across ticks: while it's
    // dropping we accumulate the dip; when it stops dropping (recovered / stabilized)
    // we emit ONE trade for the whole dip — anchored at where the dip started — if the
    // total drop cleared the threshold. Severity comes from how long the dip ran: a
    // single dropping tick reads "short", two-or-more consecutive dropping ticks read
    // "extended" (sustained damage). A death is NOT a trade: guarded both by the dip
    // bottom (a sampled 0-HP frame) AND — the load-bearing check, since a death+respawn
    // often fits between two 10s polls so the 0-HP frame is never seen — by cross-checking
    // the dip window against the authoritative kill-feed (PlayerDiedBetween). Heuristic,
    // hence Details.detected = true.
    private readonly List<GameEvent> _tradeEvents = [];
    private double _lastTradeHpFrac = double.NaN; // last currentHealth/maxHealth (all champs)
    private double _tradeDipStartFrac = double.NaN; // HP frac when the current dip began
    private double _tradeDipBottomFrac = double.NaN; // lowest HP frac seen during the dip
    private int _tradeDipStartS = -1;               // game-time the current dip began
    private int _tradeDipTicks = 0;                 // consecutive dropping ticks in this dip
    private int _lastTradeGameTimeS = 0;            // last game-time CheckTrade saw (flush anchor)
    // A dip must lose at least this fraction of max HP to count as a trade (filters
    // minion/turret chip + small DoT ticks; a real trade is a meaningful chunk).
    private const double TradeMinDropFrac = 0.12;
    // Per-tick noise floor: a drop smaller than this doesn't extend a dip (passive
    // regen jitter / rounding). Keeps a slow heal-then-poke from looking sustained.
    private const double TradeTickDropEps = 0.03;
    // Slack (each side of a dip window) when cross-checking the kill-feed for a death:
    // the heuristic's HP-tick clock and the kill-feed EventTime aren't perfectly aligned,
    // and the killing blow lands after the dip's last sampled HP. Wide enough to catch a
    // between-poll death, tight enough not to swallow an unrelated death two polls away.
    private const int TradeDeathSlackSeconds = 12;

    public LiveEventCollector(
        ILiveEventApi liveEventApi,
        ILogger logger,
        TimeSpan? pollInterval = null)
    {
        _liveEventApi = liveEventApi;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Current player name discovered from the live API.</summary>
    public string? PlayerName => _playerName;

    /// <summary>Number of raw events collected so far.</summary>
    public int EventCount => _rawEvents.Count;

    /// <summary>
    /// Start collecting events. Runs as a background loop until cancelled.
    /// 1. Waits up to 5 minutes for the live API to become available (polls every 5s).
    /// 2. Gets the player name.
    /// 3. Polls every 10s for new events.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _rawEvents.Clear();
        _summonerSpellState.Clear();
        _summonerSpellEvents.Clear();
        _recallEvents.Clear();
        _tradeEvents.Clear();
        _lastGold = double.NaN;
        _lastRecallEmitS = int.MinValue;
        _lastHealthFrac = double.NaN;
        _lastManaFrac = double.NaN;
        _lastTradeHpFrac = double.NaN;
        _tradeDipStartFrac = double.NaN;
        _tradeDipBottomFrac = double.NaN;
        _tradeDipStartS = -1;
        _tradeDipTicks = 0;
        _lastTradeGameTimeS = 0;
        _playerName = null;
        _logger.LogInformation("Live event collector started");

        // Wait for the live API to become available (game loading screen)
        var waitAttempts = 60; // Up to 5 minutes at 5s intervals
        for (var i = 0; i < waitAttempts; i++)
        {
            if (ct.IsCancellationRequested)
                return;

            if (await _liveEventApi.IsAvailableAsync(ct).ConfigureAwait(false))
                break;

            if (i == waitAttempts - 1)
            {
                _logger.LogWarning("Live Client API never became available");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }

        // Get the player's name for identifying kills vs deaths
        _playerName = await _liveEventApi.GetActivePlayerNameAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Live API active -- player: {PlayerName}", _playerName);

        // Poll for events until cancelled
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    break;
                _logger.LogDebug(ex, "Live event poll error");
            }

            try
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Stop collecting and return parsed events in the standard format.
    /// Does one final poll to catch any last-second events.
    /// </summary>
    public async Task<List<GameEvent>> StopAsync()
    {
        // Final poll to get any remaining events (use a short timeout)
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PollAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Ignored — best effort
        }

        // A dip still open at game end (HP never recovered before the last poll, e.g.
        // game ended mid-fight) still emits if it cleared the threshold.
        FlushOpenTrade();

        if (_rawEvents.Count == 0 && _summonerSpellEvents.Count == 0
            && _recallEvents.Count == 0 && _tradeEvents.Count == 0)
        {
            _logger.LogInformation("No live events collected");
            return [];
        }

        var events = ParseLiveEvents(_rawEvents, _playerName ?? "");

        // v2.17.7 / v3.0.18 / v3.1.8: merge the synthesised summoner-spell casts and
        // derived recall + trade events in chronological order alongside the parsed
        // event-stream events. All lists are already time-ordered; a single in-place
        // sort by GameTimeS produces a clean unified timeline.
        if (_summonerSpellEvents.Count > 0) events.AddRange(_summonerSpellEvents);
        if (_recallEvents.Count > 0) events.AddRange(_recallEvents);
        if (_tradeEvents.Count > 0) events.AddRange(_tradeEvents);
        if (_summonerSpellEvents.Count > 0 || _recallEvents.Count > 0 || _tradeEvents.Count > 0)
        {
            events.Sort(static (a, b) => a.GameTimeS.CompareTo(b.GameTimeS));
        }

        _logger.LogInformation(
            "Live event collector stopped -- {ParsedCount} events from {RawCount} raw events + {SpellCount} summoner-spell casts + {RecallCount} recalls + {TradeCount} trades",
            events.Count, _rawEvents.Count, _summonerSpellEvents.Count, _recallEvents.Count, _tradeEvents.Count);

        return events;
    }

    /// <summary>
    /// Fetch new events since last poll.
    /// </summary>
    private async Task PollAsync(CancellationToken ct)
    {
        var raw = await _liveEventApi.FetchEventsAsync(ct).ConfigureAwait(false);
        if (raw is not null)
        {
            AppendNewRawEvents(_rawEvents, raw);
        }

        // v2.17.7: poll the active-player snapshot for summoner-spell cast detection.
        // Fetched every tick alongside events. (A throttled "every Nth tick" variant
        // was tried to save a loopback request, but it made first-cast detection
        // timing-dependent — the baseline cooldown state must be sampled densely so
        // the ready→cooldown transition we detect is never missed. The saving is a
        // single localhost request per tick, which isn't worth that risk.)
        try
        {
            var active = await _liveEventApi.FetchActivePlayerAsync(ct).ConfigureAwait(false);
            if (active is JsonElement el)
            {
                var t = ResolveGameTimeS(el);
                CheckSummonerSpellCasts(el, t);
                CheckRecall(el, t);
                CheckRecallByRestore(el, t);
                CheckTrade(el, t);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Active-player snapshot fetch failed");
        }
    }

    /// <summary>
    /// v2.17.7: pull a usable game-time-in-seconds from the active-player JSON.
    /// The live API exposes "gameTime" on /liveclientdata/gamestats but
    /// not consistently on /activeplayer. We fall back to the latest known
    /// EventTime from the raw event stream when activeplayer doesn't carry it.
    /// </summary>
    private int ResolveGameTimeS(JsonElement activePlayer)
    {
        if (activePlayer.ValueKind == JsonValueKind.Object
            && activePlayer.TryGetProperty("gameTime", out var gt)
            && gt.TryGetDouble(out var seconds))
        {
            return (int)seconds;
        }

        // Fall back to the most recent EventTime we've seen.
        for (var i = _rawEvents.Count - 1; i >= 0; i--)
        {
            if (_rawEvents[i].TryGetProperty("EventTime", out var et) && et.TryGetDouble(out var s))
            {
                return (int)s;
            }
        }

        return 0;
    }

    /// <summary>
    /// v2.17.7 (now disabled): diff the latest summoner-spell cooldowns against the
    /// previous snapshot — a 0→positive transition would mean the spell was just cast.
    /// This is a NO-OP unless <see cref="CooldownCastDetectionEnabled"/> is flipped on,
    /// because the cooldown field this depends on does not exist in the real Live
    /// Client Data API (see the field-level comment on <c>_summonerSpellState</c>).
    /// Kept (rather than deleted) so the shape is documented in one place and a future
    /// client version that genuinely emits a cooldown can re-enable it AFTER a real
    /// capture confirms the field — never on the strength of the old fabricated mock.
    /// </summary>
    private void CheckSummonerSpellCasts(JsonElement activePlayer, int gameTimeS)
    {
        // Off by default — no available endpoint carries the cooldown signal this
        // detector needs. Do not flip without a real captured payload that proves a
        // usable cooldown on summonerSpells (run .audit/capture-live-spells.js in a
        // live game first). NOTE: the real summonerSpells block lives on
        // /liveclientdata/playerlist, NOT on /activeplayer — so even if a cooldown
        // ever appears, the fetch source would need to change too.
        if (!CooldownCastDetectionEnabled) return;

        if (activePlayer.ValueKind != JsonValueKind.Object) return;
        if (!activePlayer.TryGetProperty("summonerSpells", out var spells)) return;
        if (spells.ValueKind != JsonValueKind.Object) return;

        ReadOnlySpan<string> slots = ["summonerSpellOne", "summonerSpellTwo"];
        ReadOnlySpan<string> slotKeys = ["spell1", "spell2"];

        for (var i = 0; i < slots.Length; i++)
        {
            if (!spells.TryGetProperty(slots[i], out var slotEl)) continue;
            if (slotEl.ValueKind != JsonValueKind.Object) continue;

            var displayName = slotEl.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
                ? dn.GetString() ?? ""
                : "";
            var cooldown = slotEl.TryGetProperty("rawCooldown", out var rcd) && rcd.TryGetDouble(out var rcdVal)
                ? rcdVal
                : slotEl.TryGetProperty("cooldown", out var cd) && cd.TryGetDouble(out var cdVal)
                    ? cdVal
                    : 0.0;

            var key = slotKeys[i].ToString();
            if (!_summonerSpellState.TryGetValue(key, out var prev))
            {
                _summonerSpellState[key] = new SummonerSpellState
                {
                    DisplayName = displayName,
                    LastCooldown = cooldown,
                };
                continue;
            }

            // Detect cast: previous cooldown was ~0 (ready) and current is >0.
            // Use a small epsilon because the API sometimes reports tiny float noise.
            const double readyThreshold = 0.5;
            if (prev.LastCooldown <= readyThreshold && cooldown > readyThreshold)
            {
                var spellName = string.IsNullOrWhiteSpace(displayName) ? prev.DisplayName : displayName;
                var isFlash = string.Equals(spellName, "Flash", StringComparison.OrdinalIgnoreCase);

                _summonerSpellEvents.Add(new GameEvent
                {
                    EventType = isFlash ? GameEvent.EventTypes.Flash : GameEvent.EventTypes.SummonerSpell,
                    GameTimeS = gameTimeS,
                    Details = JsonSerializer.Serialize(new
                    {
                        spell = spellName,
                        slot = key,
                        cooldown_seconds = (int)cooldown,
                    }),
                });
            }

            prev.DisplayName = string.IsNullOrWhiteSpace(displayName) ? prev.DisplayName : displayName;
            prev.LastCooldown = cooldown;
        }
    }

    /// <summary>
    /// Derive a RECALL event from a shop purchase. Riot's API has no recall event, so
    /// we infer it: <c>currentGold</c> dropping by ≥ <see cref="RecallMinGoldSpend"/>
    /// between ticks means the player bought items at the fountain — which only happens
    /// after a completed recall (gold-on-hand is NOT lost on death, so a drop while in
    /// a game is a purchase, not a death). We anchor the recall ~<see
    /// cref="RecallChannelSeconds"/>s before the purchase (the back channel time) and
    /// debounce so one back doesn't fire on every fountain buy. It's a heuristic, hence
    /// Details.detected = true and a "recall" source note.
    /// </summary>
    private void CheckRecall(JsonElement activePlayer, int gameTimeS)
    {
        if (activePlayer.ValueKind != JsonValueKind.Object) return;
        if (!activePlayer.TryGetProperty("currentGold", out var goldEl)) return;
        if (!goldEl.TryGetDouble(out var gold)) return;

        // First sample only establishes the baseline — no delta to compare yet.
        if (double.IsNaN(_lastGold))
        {
            _lastGold = gold;
            return;
        }

        var spent = _lastGold - gold;
        _lastGold = gold;

        // A meaningful gold drop = a shop purchase = the player is at fountain post-back.
        if (spent < RecallMinGoldSpend) return;

        TryEmitRecall(gameTimeS, source: "shop_purchase", goldSpent: (int)spent);
    }

    /// <summary>
    /// Second recall signal — a FOUNTAIN HP+mana restore — to catch recalls with NO
    /// purchase (recalled to defend / TP back / no gold to spend). The discriminator
    /// vs an in-combat heal: a fountain restores HP AND mana to 100% simultaneously,
    /// whereas a heal (Aatrox/Soraka/Mundo) restores HP but leaves mana where it was.
    /// So we fire ONLY when:
    ///   • the champ uses MANA (energy/rage/fury/manaless can't use the tell — their
    ///     resource is near-always full or absent → gold-only for them), AND
    ///   • BOTH HP and mana are now ≥ <see cref="RestoreFullFrac"/> (full), AND
    ///   • at least one of them was meaningfully below full last tick (the jump TO
    ///     full — not every idle-at-fountain tick).
    /// Shares the gold detector's debounce so a recall-then-buy never double-fires.
    /// </summary>
    private void CheckRecallByRestore(JsonElement activePlayer, int gameTimeS)
    {
        if (activePlayer.ValueKind != JsonValueKind.Object) return;
        if (!activePlayer.TryGetProperty("championStats", out var stats) || stats.ValueKind != JsonValueKind.Object) return;

        // MANA-only gate. resourceType is on activePlayer (preferred) or championStats.
        var resourceType = ReadStringProp(activePlayer, "resourceType");
        if (string.IsNullOrEmpty(resourceType)) resourceType = ReadStringProp(stats, "resourceType");
        if (!string.Equals(resourceType, "MANA", StringComparison.OrdinalIgnoreCase)) return;

        var maxHealth = ReadDoubleProp(stats, "maxHealth");
        var curHealth = ReadDoubleProp(stats, "currentHealth");
        var maxMana = ReadDoubleProp(stats, "resourceMax");
        var curMana = ReadDoubleProp(stats, "resourceValue");
        // Need positive maxima to form fractions; dead/loading ticks (maxHealth 0) skip.
        if (maxHealth <= 0 || maxMana <= 0) return;

        var hpFrac = curHealth / maxHealth;
        var manaFrac = curMana / maxMana;

        // First sample establishes the baseline only.
        if (double.IsNaN(_lastHealthFrac) || double.IsNaN(_lastManaFrac))
        {
            _lastHealthFrac = hpFrac;
            _lastManaFrac = manaFrac;
            return;
        }

        var prevHp = _lastHealthFrac;
        var prevMana = _lastManaFrac;
        _lastHealthFrac = hpFrac;
        _lastManaFrac = manaFrac;

        // Both must be full NOW…
        if (hpFrac < RestoreFullFrac || manaFrac < RestoreFullFrac) return;
        // …and at least one was meaningfully below full last tick (a real jump, not
        // an idle-at-full tick). A heal-to-full would top HP but NOT mana, so the mana
        // side of "both full now" is what makes this sound.
        if (prevHp >= RestoreWasLowFrac && prevMana >= RestoreWasLowFrac) return;

        TryEmitRecall(gameTimeS, source: "health_restore", goldSpent: 0);
    }

    /// <summary>
    /// Emit a derived RECALL anchored ~<see cref="RecallChannelSeconds"/>s before the
    /// detection tick (the back channel time), with the shared debounce so neither
    /// detector — nor a recall-then-buy — double-fires. <paramref name="goldSpent"/>
    /// is 0 for non-purchase signals.
    /// </summary>
    private void TryEmitRecall(int gameTimeS, string source, int goldSpent)
    {
        // Debounce: one back can mean several fountain purchases / a buy AND a restore
        // across a few ticks. Only the first within the window counts. Guard the
        // "no recall yet" sentinel explicitly so the subtraction can't overflow.
        var recallAtS = Math.Max(0, gameTimeS - RecallChannelSeconds);
        if (_lastRecallEmitS != int.MinValue && recallAtS - _lastRecallEmitS < RecallDebounceSeconds) return;
        _lastRecallEmitS = recallAtS;

        _recallEvents.Add(new GameEvent
        {
            EventType = GameEvent.EventTypes.Recall,
            GameTimeS = recallAtS,
            Details = JsonSerializer.Serialize(new
            {
                detected = true,
                source,
                gold_spent = goldSpent,
                detect_game_time_s = gameTimeS,
            }),
        });
    }

    /// <summary>
    /// Derive a TRADE from your own HP. The Live Client API never exposes the enemy's
    /// HP, so a trade is inferred from YOUR <c>championStats.currentHealth</c> falling
    /// while you stay alive. We follow the HP across ticks: a falling tick (≥ <see
    /// cref="TradeTickDropEps"/> below the last) opens or extends a "dip"; a non-falling
    /// tick CLOSES it, and if the dip's total loss cleared <see cref="TradeMinDropFrac"/>
    /// we emit one trade anchored where the dip began. Severity is the dip length: one
    /// dropping tick → "short", two-or-more consecutive → "extended".
    ///
    /// <para>A death is NOT a trade. Two guards enforce that: (1) if the heuristic
    /// sampled the 0-HP frame the dip bottom is 0; (2) — the load-bearing one at a 10s
    /// poll, since a death+respawn often fits ENTIRELY between two ticks so the 0-HP
    /// frame is never seen and HP appears to dip then bounce back to full — the dip
    /// window is cross-checked against the authoritative kill-feed: if the player has a
    /// ChampionKill-victim death inside the dip's time span, the dip is that death, not
    /// a trade. Without (2) a between-tick death emits a phantom "survived" trade.</para>
    /// </summary>
    private void CheckTrade(JsonElement activePlayer, int gameTimeS)
    {
        if (activePlayer.ValueKind != JsonValueKind.Object) return;
        if (!activePlayer.TryGetProperty("championStats", out var stats) || stats.ValueKind != JsonValueKind.Object) return;

        var maxHealth = ReadDoubleProp(stats, "maxHealth");
        var curHealth = ReadDoubleProp(stats, "currentHealth");
        if (maxHealth <= 0) return; // loading / dead tick — no usable fraction
        var hpFrac = curHealth / maxHealth;
        _lastTradeGameTimeS = gameTimeS; // remembered so an end-of-game flush can anchor its window

        // First sample establishes the baseline only.
        if (double.IsNaN(_lastTradeHpFrac))
        {
            _lastTradeHpFrac = hpFrac;
            return;
        }

        var prevFrac = _lastTradeHpFrac;
        _lastTradeHpFrac = hpFrac;

        var falling = hpFrac < prevFrac - TradeTickDropEps;
        if (falling)
        {
            // Open a new dip at the PRE-drop level, or extend the running one. Track the
            // lowest point so the trade's reported loss reflects the deepest dip, not
            // wherever HP happened to be when the dip closed (you may already be regen-ing).
            if (_tradeDipTicks == 0)
            {
                _tradeDipStartFrac = prevFrac;
                _tradeDipBottomFrac = prevFrac;
                _tradeDipStartS = gameTimeS;
            }
            if (hpFrac < _tradeDipBottomFrac) _tradeDipBottomFrac = hpFrac;
            _tradeDipTicks++;
            return;
        }

        // Not falling → the dip (if any) is over. Close it against this tick's time so
        // the kill-feed cross-check spans the whole window the player was losing HP.
        if (_tradeDipTicks > 0)
        {
            EmitTradeIfBigEnough(closingTimeS: gameTimeS);
        }
    }

    /// <summary>Flush a dip still open at game end (no recovery tick arrived).</summary>
    private void FlushOpenTrade()
    {
        if (_tradeDipTicks > 0) EmitTradeIfBigEnough(closingTimeS: _lastTradeGameTimeS);
    }

    /// <summary>
    /// Close the current dip: emit a TRADE if its deepest loss (start − bottom) cleared
    /// <see cref="TradeMinDropFrac"/>, the player didn't bottom out at death, AND the
    /// kill-feed shows no player death inside the dip window. Then reset the dip state.
    /// Severity = dip length (1 tick short / 2+ extended).
    /// </summary>
    private void EmitTradeIfBigEnough(int closingTimeS)
    {
        var lostFrac = _tradeDipStartFrac - _tradeDipBottomFrac;
        var bottomFrac = _tradeDipBottomFrac;
        var ticks = _tradeDipTicks;
        var startS = _tradeDipStartS;

        // Reset the dip BEFORE any early return so a sub-threshold dip doesn't linger.
        _tradeDipTicks = 0;
        _tradeDipStartFrac = double.NaN;
        _tradeDipBottomFrac = double.NaN;
        _tradeDipStartS = -1;

        if (bottomFrac <= 0.0) return;               // sampled a 0-HP frame → it was a death
        if (lostFrac < TradeMinDropFrac) return;     // not a meaningful chunk
        if (startS < 0) return;                      // defensive: no anchor recorded
        // The load-bearing death guard: a death+respawn can fit between two 10s polls,
        // so the 0-HP frame is often never sampled and the dip just looks like a big
        // drop that bounced back. The kill-feed is authoritative — if the player died
        // inside the dip window, this dip is that death, not a survived trade. Pad the
        // window a touch on each side (the heuristic's tick clock and the kill-feed's
        // EventTime aren't perfectly aligned, and the killing blow lands after the dip's
        // last sampled HP).
        if (PlayerDiedBetween(startS - TradeDeathSlackSeconds, closingTimeS + TradeDeathSlackSeconds)) return;

        var kind = ticks >= 2 ? "extended" : "short";
        _tradeEvents.Add(new GameEvent
        {
            EventType = GameEvent.EventTypes.Trade,
            GameTimeS = Math.Max(0, startS),
            Details = JsonSerializer.Serialize(new
            {
                detected = true,
                kind,
                hp_lost_pct = (int)Math.Round(lostFrac * 100),
                ticks,
            }),
        });
    }

    /// <summary>
    /// True if the player has a kill-feed death (a ChampionKill with VictimName == the
    /// active player) whose EventTime falls in [<paramref name="fromS"/>, <paramref
    /// name="toS"/>]. Scans the raw event stream — the authoritative death source — so a
    /// death+respawn that slipped entirely between two HP polls is still caught. With no
    /// known player name we can't attribute deaths, so we DON'T suppress (return false):
    /// better an occasional phantom trade than dropping every real one.
    /// </summary>
    private bool PlayerDiedBetween(int fromS, int toS)
    {
        if (string.IsNullOrEmpty(_playerName)) return false;
        foreach (var raw in _rawEvents)
        {
            if (raw.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(raw.GetPropertyOrDefault("EventName", ""), "ChampionKill", StringComparison.Ordinal)) continue;
            if (!raw.GetPropertyOrDefault("VictimName", "").Equals(_playerName, StringComparison.OrdinalIgnoreCase)) continue;
            var t = (int)raw.GetPropertyDoubleOrDefault("EventTime", -1.0);
            if (t >= fromS && t <= toS) return true;
        }
        return false;
    }

    // Read a JSON string-array property into a string[] ("" / non-array → empty).
    private static string[] ReadNameArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
        }
        return [.. list];
    }

    private static double ReadDoubleProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.TryGetDouble(out var v) ? v : 0.0;

    private static string ReadStringProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    internal static void AppendNewRawEvents(List<JsonElement> destination, IReadOnlyList<JsonElement> snapshot)
    {
        if (snapshot.Count <= destination.Count)
        {
            return;
        }

        for (var i = destination.Count; i < snapshot.Count; i++)
        {
            destination.Add(snapshot[i].Clone());
        }
    }

    /// <summary>
    /// Convert Live Client Data API events to our standard <see cref="GameEvent"/> format.
    /// Ported from Python _parse_live_events function.
    /// </summary>
    public static List<GameEvent> ParseLiveEvents(List<JsonElement> rawEvents, string playerName)
    {
        var events = new List<GameEvent>();
        var playerLower = playerName.ToLowerInvariant();

        foreach (var raw in rawEvents)
        {
            var eventName = raw.GetPropertyOrDefault("EventName", "");
            var eventTime = raw.GetPropertyDoubleOrDefault("EventTime", 0.0);
            var gameTimeS = (int)eventTime;

            switch (eventName)
            {
                case "ChampionKill":
                {
                    var killer = raw.GetPropertyOrDefault("KillerName", "");
                    var victim = raw.GetPropertyOrDefault("VictimName", "");

                    // Player got a kill
                    if (killer.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                    {
                        events.Add(new GameEvent
                        {
                            EventType = GameEvent.EventTypes.Kill,
                            GameTimeS = gameTimeS,
                            Details = JsonSerializer.Serialize(new { victim }),
                        });
                    }

                    // Player died
                    if (victim.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Capture the assisters too (the enemies who helped kill me), so a
                        // post-game pass can tell whether the enemy JUNGLER was on the kill
                        // (killer OR assister) — the signal for a jungle gank.
                        var deathAssisters = ReadNameArray(raw, "Assisters");
                        events.Add(new GameEvent
                        {
                            EventType = GameEvent.EventTypes.Death,
                            GameTimeS = gameTimeS,
                            Details = JsonSerializer.Serialize(new { killer, assisters = deathAssisters }),
                        });
                    }

                    // Player assisted
                    if (raw.TryGetProperty("Assisters", out var assisters)
                        && assisters.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in assisters.EnumerateArray())
                        {
                            var assisterName = a.GetString() ?? "";
                            if (assisterName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                            {
                                events.Add(new GameEvent
                                {
                                    EventType = GameEvent.EventTypes.Assist,
                                    GameTimeS = gameTimeS,
                                    Details = JsonSerializer.Serialize(new { killer, victim }),
                                });
                                break;
                            }
                        }
                    }

                    break;
                }

                case "DragonKill":
                {
                    events.Add(new GameEvent
                    {
                        EventType = GameEvent.EventTypes.Dragon,
                        GameTimeS = gameTimeS,
                        Details = JsonSerializer.Serialize(new
                        {
                            dragon_type = raw.GetPropertyOrDefault("DragonType", ""),
                            stolen = raw.GetPropertyBoolOrDefault("Stolen", false),
                            killer = raw.GetPropertyOrDefault("KillerName", ""),
                        }),
                    });
                    break;
                }

                case "BaronKill":
                {
                    events.Add(new GameEvent
                    {
                        EventType = GameEvent.EventTypes.Baron,
                        GameTimeS = gameTimeS,
                        Details = JsonSerializer.Serialize(new
                        {
                            stolen = raw.GetPropertyBoolOrDefault("Stolen", false),
                            killer = raw.GetPropertyOrDefault("KillerName", ""),
                        }),
                    });
                    break;
                }

                case "HeraldKill":
                {
                    events.Add(new GameEvent
                    {
                        EventType = GameEvent.EventTypes.Herald,
                        GameTimeS = gameTimeS,
                        Details = JsonSerializer.Serialize(new
                        {
                            killer = raw.GetPropertyOrDefault("KillerName", ""),
                        }),
                    });
                    break;
                }

                case "TurretKilled":
                {
                    events.Add(new GameEvent
                    {
                        EventType = GameEvent.EventTypes.Turret,
                        GameTimeS = gameTimeS,
                        Details = JsonSerializer.Serialize(new
                        {
                            killer = raw.GetPropertyOrDefault("KillerName", ""),
                            turret = raw.GetPropertyOrDefault("TurretKilled", ""),
                        }),
                    });
                    break;
                }

                case "InhibKilled":
                {
                    events.Add(new GameEvent
                    {
                        EventType = GameEvent.EventTypes.Inhibitor,
                        GameTimeS = gameTimeS,
                        Details = JsonSerializer.Serialize(new
                        {
                            killer = raw.GetPropertyOrDefault("KillerName", ""),
                            inhib = raw.GetPropertyOrDefault("InhibKilled", ""),
                        }),
                    });
                    break;
                }

                case "Multikill":
                {
                    var killer = raw.GetPropertyOrDefault("KillerName", "");
                    if (killer.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                    {
                        var streak = raw.GetPropertyIntOrDefault("KillStreak", 2);
                        var label = streak switch
                        {
                            2 => "Double Kill",
                            3 => "Triple Kill",
                            4 => "Quadra Kill",
                            5 => "Penta Kill",
                            _ => $"{streak}x Kill",
                        };

                        events.Add(new GameEvent
                        {
                            EventType = GameEvent.EventTypes.MultiKill,
                            GameTimeS = gameTimeS,
                            Details = JsonSerializer.Serialize(new { count = streak, label }),
                        });
                    }

                    break;
                }

                case "FirstBlood":
                {
                    var recipient = raw.GetPropertyOrDefault("Recipient", "");
                    if (recipient.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                    {
                        events.Add(new GameEvent
                        {
                            EventType = GameEvent.EventTypes.FirstBlood,
                            GameTimeS = gameTimeS,
                            Details = "{}",
                        });
                    }

                    break;
                }
            }
        }

        return events;
    }
}

/// <summary>
/// Extension methods for safely reading properties from <see cref="JsonElement"/>.
/// </summary>
internal static class JsonElementExtensions
{
    public static string GetPropertyOrDefault(this JsonElement el, string property, string defaultValue)
    {
        if (el.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? defaultValue;
        return defaultValue;
    }

    public static int GetPropertyIntOrDefault(this JsonElement el, string property, int defaultValue)
    {
        if (el.TryGetProperty(property, out var prop))
        {
            if (prop.TryGetInt32(out var value))
                return value;

            if (prop.ValueKind == JsonValueKind.String &&
                int.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    public static double GetPropertyDoubleOrDefault(this JsonElement el, string property, double defaultValue)
    {
        if (el.TryGetProperty(property, out var prop) && prop.TryGetDouble(out var value))
            return value;
        return defaultValue;
    }

    public static bool GetPropertyBoolOrDefault(this JsonElement el, string property, bool defaultValue)
    {
        if (el.TryGetProperty(property, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }
}
