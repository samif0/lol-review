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

    // Decoupled active-player poll cadence: check summoner-spell cooldowns every
    // Nth tick (N=3 ≈ 30s at 10s poll interval). Spell cooldowns are 60-300s so
    // this resolution is more than sufficient to detect casts without the extra
    // per-tick HTTP round-trip when League is running.
    private const int ActivePlayerPollEveryNTicks = 3;
    private int _activePlayerTickCounter;

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
        _playerName = null;
        _activePlayerTickCounter = 0;
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

        if (_rawEvents.Count == 0 && _summonerSpellEvents.Count == 0)
        {
            _logger.LogInformation("No live events collected");
            return [];
        }

        var events = ParseLiveEvents(_rawEvents, _playerName ?? "");

        // v2.17.7: merge the synthesised summoner-spell casts in chronological
        // order alongside the parsed event-stream events. Both lists are
        // already sorted by time-of-arrival; a single in-place sort by
        // GameTimeS preserves that and produces a clean timeline ordering.
        if (_summonerSpellEvents.Count > 0)
        {
            events.AddRange(_summonerSpellEvents);
            events.Sort(static (a, b) => a.GameTimeS.CompareTo(b.GameTimeS));
        }

        _logger.LogInformation(
            "Live event collector stopped -- {ParsedCount} events from {RawCount} raw events + {SpellCount} summoner-spell casts",
            events.Count, _rawEvents.Count, _summonerSpellEvents.Count);

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
        // Spell cooldowns are 60-300s, so checking every Nth tick (≈30s) is sufficient
        // to catch every cast. This avoids an extra HTTP round-trip on most ticks.
        _activePlayerTickCounter++;
        if (_activePlayerTickCounter >= ActivePlayerPollEveryNTicks)
        {
            _activePlayerTickCounter = 0;
            try
            {
                var active = await _liveEventApi.FetchActivePlayerAsync(ct).ConfigureAwait(false);
                if (active is JsonElement el)
                {
                    CheckSummonerSpellCasts(el, ResolveGameTimeS(el));
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
