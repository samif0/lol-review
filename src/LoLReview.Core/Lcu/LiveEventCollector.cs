#nullable enable

using System.Text.Json;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Lcu;

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
    private int _lastEventId = -1;
    private string? _playerName;

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
        _lastEventId = -1;
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

        if (_rawEvents.Count == 0)
        {
            _logger.LogInformation("No live events collected");
            return [];
        }

        var events = ParseLiveEvents(_rawEvents, _playerName ?? "");
        _logger.LogInformation(
            "Live event collector stopped -- {ParsedCount} events from {RawCount} raw events",
            events.Count, _rawEvents.Count);

        return events;
    }

    /// <summary>
    /// Fetch new events since last poll.
    /// </summary>
    private async Task PollAsync(CancellationToken ct)
    {
        var raw = await _liveEventApi.FetchEventsAsync(ct).ConfigureAwait(false);
        if (raw is null)
            return;

        // Only process events we haven't seen yet
        foreach (var evt in raw)
        {
            var eid = -1;
            if (evt.TryGetProperty("EventID", out var eventIdProp)
                && eventIdProp.TryGetInt32(out var eventId))
            {
                eid = eventId;
            }

            if (eid > _lastEventId)
            {
                _rawEvents.Add(evt.Clone());
                _lastEventId = eid;
            }
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
