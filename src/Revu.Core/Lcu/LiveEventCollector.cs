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

    // v2.17.7: summoner-spell cast detection. Riot's event stream doesn't emit
    // SummonerSpellCast events, so we synthesise them by watching the active
    // player's spell cooldowns: prev <= 0 + new > 0 means the spell was just
    // cast. Each entry maps spell slot ("spell1"|"spell2") → (display name,
    // last observed cooldown). _summonerSpellEvents holds the synthesised
    // casts so StopAsync can return them alongside the raw event-stream events.
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
        _lastGold = double.NaN;
        _lastRecallEmitS = int.MinValue;
        _lastHealthFrac = double.NaN;
        _lastManaFrac = double.NaN;
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

        if (_rawEvents.Count == 0 && _summonerSpellEvents.Count == 0 && _recallEvents.Count == 0)
        {
            _logger.LogInformation("No live events collected");
            return [];
        }

        var events = ParseLiveEvents(_rawEvents, _playerName ?? "");

        // v2.17.7 / v3.0.18: merge the synthesised summoner-spell casts and derived
        // recall events in chronological order alongside the parsed event-stream
        // events. All lists are already time-ordered; a single in-place sort by
        // GameTimeS produces a clean unified timeline.
        if (_summonerSpellEvents.Count > 0) events.AddRange(_summonerSpellEvents);
        if (_recallEvents.Count > 0) events.AddRange(_recallEvents);
        if (_summonerSpellEvents.Count > 0 || _recallEvents.Count > 0)
        {
            events.Sort(static (a, b) => a.GameTimeS.CompareTo(b.GameTimeS));
        }

        _logger.LogInformation(
            "Live event collector stopped -- {ParsedCount} events from {RawCount} raw events + {SpellCount} summoner-spell casts + {RecallCount} recalls",
            events.Count, _rawEvents.Count, _summonerSpellEvents.Count, _recallEvents.Count);

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
    /// v2.17.7: diff the latest summoner-spell cooldowns against the previous
    /// snapshot. A 0→positive transition means the spell was just cast.
    /// </summary>
    private void CheckSummonerSpellCasts(JsonElement activePlayer, int gameTimeS)
    {
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
                        events.Add(new GameEvent
                        {
                            EventType = GameEvent.EventTypes.Death,
                            GameTimeS = gameTimeS,
                            Details = JsonSerializer.Serialize(new { killer }),
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
