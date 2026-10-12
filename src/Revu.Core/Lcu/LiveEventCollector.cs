#nullable enable

using System.Text.Json;
using Revu.Core.Constants;
using Revu.Core.Models;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Lcu;

/// <summary>
/// Polls the Live Client Data API during a game to collect events.
/// </summary>
public sealed class LiveEventCollector : Services.EventProcessing.IObservationSource
{
    public Services.EventProcessing.SourceCapabilities Capabilities => Services.EventProcessing.LiveProcessingCatalog.LiveSource;
    private readonly ILiveEventApi _liveEventApi;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;       // health-sample cadence (~1s), used only while a detector samples health
    private readonly TimeSpan _eventPollInterval;  // slow kill-feed event-stream cadence (~10s)
    private readonly Services.EventProcessing.ProcessingSession? _processing;
    private long _observationSequence;
    public Services.EventProcessing.ProcessingReport? ProcessingReport { get; private set; }

    private readonly List<JsonElement> _rawEvents = [];
    private string? _playerName;

    // v3.10.1: the ten players as the game reports them (/playerlist), captured on
    // the event cadence until every champion is named. The matchmaker's lane per
    // player lives here and nowhere else the app can reach at end-of-game (the LCU
    // EOG payload stopped carrying positions in 2026-08), so this is what lets the
    // matchup be on the saved row immediately. Bounded so a lobby that never fills
    // (a custom) stops costing a request every cycle.
    private const int RosterMaxAttempts = 30;
    private LiveRoster? _roster;
    private int _rosterAttempts;

    public LiveEventCollector(
        ILiveEventApi liveEventApi,
        ILogger logger,
        TimeSpan? pollInterval = null,
        TimeSpan? eventPollInterval = null,
        Services.EventProcessing.ProcessingSession? processing = null)
    {
        _liveEventApi = liveEventApi;
        _logger = logger;
        _processing = processing;
        // pollInterval is the fast health-sample cadence (default 1s), used only while an
        // active detector's sampling plan asks for player health. Otherwise the loop wakes
        // on the slower kill-feed eventPollInterval (default 10s).
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(GameConstants.HpSamplePollIntervalS);
        _eventPollInterval = eventPollInterval ?? TimeSpan.FromSeconds(GameConstants.LiveEventPollIntervalS);
    }

    /// <summary>Current player name discovered from the live API.</summary>
    public string? PlayerName => _playerName;

    /// <summary>Number of raw events collected so far.</summary>
    public int EventCount => _rawEvents.Count;

    /// <summary>
    /// v3.10.1: the lobby as the game reported it — champions, teams and the lanes the
    /// matchmaker assigned. Null until the live client answered <c>/playerlist</c> at
    /// least once; may be partial (loading screen) or lane-less (ARAM).
    /// </summary>
    public LiveRoster? Roster => _roster;

    /// <summary>
    /// Start collecting events. Runs as a background loop until cancelled.
    /// 1. Waits up to 5 minutes for the live API to become available (polls every 5s).
    /// 2. Gets the player name.
    /// 3. Polls every 10s for new events.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _rawEvents.Clear();
        _playerName = null;
        _roster = null;
        _rosterAttempts = 0;
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

        // Two cadences, one loop. Player health is sampled every tick (_pollInterval, ~1s)
        // only while an active detector's sampling plan asks for it; otherwise the loop
        // wakes on the kill-feed cadence (_eventPollInterval, ~10s). The first iteration
        // fetches the event stream immediately; nextEventPoll tracks the next due time.
        var samplesHealth = _processing?.SamplingPlan.ContainsKey(Services.EventProcessing.LiveProcessingCatalog.HealthKind) == true;
        var tick = samplesHealth ? _pollInterval : _eventPollInterval;
        var elapsed = TimeSpan.Zero;
        var nextEventPoll = TimeSpan.Zero; // due immediately on the first iteration
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (elapsed >= nextEventPoll)
                {
                    // The player name is what attributes kill-feed events to YOU
                    // (ParseLiveEvents). It is fetched once above; if that one fetch failed transiently,
                    // every event of the game would be unattributed — so keep
                    // retrying at the event cadence until it resolves.
                    if (string.IsNullOrWhiteSpace(_playerName))
                    {
                        _playerName = await _liveEventApi.GetActivePlayerNameAsync(ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(_playerName))
                        {
                            _logger.LogInformation("Live API player name resolved late: {PlayerName}", _playerName);
                        }
                    }

                    await PollEventStreamAsync(ct).ConfigureAwait(false);
                    await CaptureRosterAsync(ct).ConfigureAwait(false);
                    nextEventPoll = elapsed + _eventPollInterval;
                }
                if (samplesHealth)
                    await SampleHpAsync(ct).ConfigureAwait(false);
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
                await Task.Delay(tick, ct).ConfigureAwait(false);
                elapsed += tick;
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
        // Final poll to get any remaining events (short timeout).
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await PollEventStreamAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Ignored — best effort
        }

        if (_processing is not null)
            ProcessingReport = await _processing.CompleteAsync().ConfigureAwait(false);

        if (_rawEvents.Count == 0 && ProcessingReport?.Events.Count is not > 0)
        {
            _logger.LogInformation("No live events collected");
            return [];
        }

        var events = ParseLiveEvents(_rawEvents, _playerName ?? "");
        if (ProcessingReport is { Shadow: false }) events.AddRange(ProcessingReport.Events);

        _logger.LogInformation(
            "Live event collector stopped -- {ParsedCount} events from {RawCount} raw events",
            events.Count, _rawEvents.Count);

        return events;
    }

    /// <summary>
    /// Fetch the kill-feed event stream (/eventdata) and append any new raw events.
    /// Polled on the slow (~10s) cadence — the stream changes coarsely.
    /// </summary>
    private async Task PollEventStreamAsync(CancellationToken ct)
    {
        var raw = await _liveEventApi.FetchEventsAsync(ct).ConfigureAwait(false);
        if (raw is not null)
        {
            var before = _rawEvents.Count;
            AppendNewRawEvents(_rawEvents, raw);
            if (_processing is not null)
                foreach (var item in _rawEvents.Skip(before))
                    foreach (var parsed in ParseLiveEvents([item], _playerName ?? ""))
                        PublishObservation(Services.EventProcessing.LiveProcessingCatalog.FeedKind, parsed.GameTimeS,
                            TimeSpan.Zero, new Services.EventProcessing.FeedEvent(parsed.EventType, parsed.Details));
        }
        else _processing?.ReportGap(Services.EventProcessing.LiveProcessingCatalog.FeedKind, "Event source unavailable");
    }

    /// <summary>
    /// v3.10.1: fetch <c>/playerlist</c> until the lobby is complete (ten named
    /// champions) or the attempt budget is spent. Keeps the fullest snapshot seen —
    /// the list can be short during the loading screen and never shrinks after.
    /// Polled on the slow cadence alongside the event stream.
    /// </summary>
    private async Task CaptureRosterAsync(CancellationToken ct)
    {
        if (_roster is { IsComplete: true } || _rosterAttempts >= RosterMaxAttempts) return;
        _rosterAttempts++;

        var raw = await _liveEventApi.FetchPlayerListAsync(ct).ConfigureAwait(false);
        if (raw is not JsonElement el) return;

        var parsed = LiveRoster.Parse(el);
        if (parsed is null || parsed.Players.Count == 0) return;
        if (_roster is not null && parsed.Players.Count < _roster.Players.Count) return;

        _roster = parsed;
        if (parsed.IsComplete)
        {
            _logger.LogInformation(
                "Live roster captured: {Count} players, lanes assigned: {HasPositions}",
                parsed.Players.Count, parsed.HasPositions);
        }
    }

    /// <summary>
    /// Sample YOUR champion HP (/activeplayer) against the authoritative game clock
    /// (/gamestats) and publish it to the processing session. Runs on the fast (~1s)
    /// cadence, and only while an active detector's sampling plan asks for health. All
    /// requests are localhost, so the extra cadence is cheap.
    /// </summary>
    private async Task SampleHpAsync(CancellationToken ct)
    {
        try
        {
            // The authoritative game clock anchors derived events; /activeplayer does NOT
            // reliably carry gameTime (see ResolveGameTimeS).
            var gameStats = await _liveEventApi.FetchGameStatsAsync(ct).ConfigureAwait(false);
            var active = await _liveEventApi.FetchActivePlayerAsync(ct).ConfigureAwait(false);
            if (active is JsonElement el)
            {
                var t = ResolveGameTimeS(el, gameStats);
                if (_processing is not null && el.TryGetProperty("championStats", out var championStats)
                    && championStats.TryGetProperty("currentHealth", out var hp) && hp.TryGetDouble(out var currentHp)
                    && championStats.TryGetProperty("maxHealth", out var maxHp) && maxHp.TryGetDouble(out var maximumHp))
                    PublishObservation(Services.EventProcessing.LiveProcessingCatalog.HealthKind, t,
                        gameStats is null ? TimeSpan.FromDays(1) : TimeSpan.FromSeconds(1),
                        new Services.EventProcessing.PlayerHealth(currentHp, maximumHp));
                else _processing?.ReportGap(Services.EventProcessing.LiveProcessingCatalog.HealthKind, "Health payload incompatible");
            }
            else _processing?.ReportGap(Services.EventProcessing.LiveProcessingCatalog.HealthKind, "Health source unavailable");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Active-player snapshot fetch failed");
            _processing?.ReportGap(Services.EventProcessing.LiveProcessingCatalog.HealthKind, "Health sampling failed");
        }
    }

    private void PublishObservation(string kind, int seconds, TimeSpan uncertainty,
        Services.EventProcessing.IObservationPayload payload) => _processing?.Publish(new(
            $"live:{Interlocked.Increment(ref _observationSequence)}", Services.EventProcessing.LiveProcessingCatalog.SourceId,
            1, kind, 1, _playerName ?? "", TimeSpan.FromSeconds(seconds), DateTimeOffset.UtcNow,
            uncertainty, !string.IsNullOrWhiteSpace(_playerName), payload));

    /// <summary>
    /// Resolve the current game-time-in-seconds for anchoring a derived event.
    /// Precedence, most authoritative first:
    ///   1. <c>/liveclientdata/gamestats</c> <c>gameTime</c> — the real game clock.
    ///   2. <c>/activeplayer</c> <c>gameTime</c> — present on some client versions.
    ///   3. The latest kill-feed <c>EventTime</c> — LAST resort only.
    ///
    /// <para>Why this order matters: the old code read only (2) and fell straight to (3).
    /// But <c>/activeplayer</c> usually does NOT carry <c>gameTime</c>, so early game —
    /// before any kill happens — (3) returns a stale near-zero timestamp (e.g. the
    /// GameStart event at t=0), and a real 1:08 sample gets stamped at ~0:30. Reading the
    /// authoritative <c>/gamestats</c> clock fixes the mis-anchoring.</para>
    /// </summary>
    private int ResolveGameTimeS(JsonElement activePlayer, JsonElement? gameStats)
    {
        if (gameStats is JsonElement gs && TryReadGameTime(gs, out var gsTime))
            return gsTime;

        if (TryReadGameTime(activePlayer, out var apTime))
            return apTime;

        // Last resort: the most recent EventTime we've seen. Only reached when NEITHER
        // real clock is available — kept so a client that exposes no gameTime at all
        // still produces monotonic-ish anchors rather than 0.
        for (var i = _rawEvents.Count - 1; i >= 0; i--)
        {
            if (_rawEvents[i].TryGetProperty("EventTime", out var et) && et.TryGetDouble(out var s))
                return (int)s;
        }

        return 0;
    }

    private static bool TryReadGameTime(JsonElement el, out int seconds)
    {
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("gameTime", out var gt)
            && gt.TryGetDouble(out var v)
            && v >= 0)
        {
            seconds = (int)v;
            return true;
        }
        seconds = 0;
        return false;
    }

    /// <summary>
    /// Kill-feed name comparison tolerant of Riot-ID form drift: /activeplayername
    /// returns "GameName#TAG" on modern clients while the kill feed usually carries
    /// the bare game name (and vice versa on older shapes). The EOG capture side
    /// normalizes with PlayerNameForms for exactly this reason; without the same
    /// tolerance here every KILL/DEATH/ASSIST would silently vanish whenever the two
    /// forms diverge.
    /// </summary>
    internal static bool NamesMatch(string feedName, string playerName)
    {
        if (string.IsNullOrWhiteSpace(feedName) || string.IsNullOrWhiteSpace(playerName)) return false;
        if (feedName.Equals(playerName, StringComparison.OrdinalIgnoreCase)) return true;
        // Bare-name fallback ONLY when the two forms genuinely diverge (one
        // side carries a #tag, the other doesn't). When BOTH are fully
        // qualified and unequal they are different players — a lobby can hold
        // two Riot IDs sharing a game name (Smurf#111 vs Smurf#222), and
        // bare-matching those would attribute a namesake's kills to the player.
        var feedTagged = feedName.IndexOf('#') > 0;
        var playerTagged = playerName.IndexOf('#') > 0;
        if (feedTagged == playerTagged) return false;
        var bareFeed = BareGameName(feedName);
        var barePlayer = BareGameName(playerName);
        return bareFeed.Length > 0 && bareFeed.Equals(barePlayer, StringComparison.OrdinalIgnoreCase);
    }

    private static string BareGameName(string name)
    {
        var hash = name.IndexOf('#');
        return (hash > 0 ? name[..hash] : name).Trim();
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
                    if (NamesMatch(killer, playerName))
                    {
                        events.Add(new GameEvent
                        {
                            EventType = GameEvent.EventTypes.Kill,
                            GameTimeS = gameTimeS,
                            Details = JsonSerializer.Serialize(new { victim }),
                        });
                    }

                    // Player died
                    if (NamesMatch(victim, playerName))
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
                            if (NamesMatch(assisterName, playerName))
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
