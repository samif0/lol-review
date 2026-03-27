#nullable enable

using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

/// <summary>
/// Thin wrapper for session operations (intentions, debriefs, tilt warnings).
/// </summary>
public sealed class SessionService : ISessionService
{
    private readonly ISessionLogRepository _sessionLog;
    private readonly ILogger<SessionService> _logger;

    public SessionService(
        ISessionLogRepository sessionLog,
        ILogger<SessionService> logger)
    {
        _sessionLog = sessionLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SessionInfo> GetOrCreateSessionAsync(string dateStr)
    {
        var existing = await _sessionLog.GetSessionAsync(dateStr).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        // No session exists yet -- create one by setting a blank intention
        // (the repository handles upsert semantics)
        await _sessionLog.SetSessionIntentionAsync(dateStr, "").ConfigureAwait(false);

        // Fetch the newly created session
        var session = await _sessionLog.GetSessionAsync(dateStr).ConfigureAwait(false);
        return session ?? new SessionInfo { Date = dateStr };
    }

    /// <inheritdoc />
    public async Task SetIntentionAsync(string dateStr, string intention)
    {
        await _sessionLog.SetSessionIntentionAsync(dateStr, intention).ConfigureAwait(false);
        _logger.LogInformation("Session intention set for {Date}: {Intention}", dateStr, intention);
    }

    /// <inheritdoc />
    public async Task SaveDebriefAsync(string dateStr, int rating, string note)
    {
        await _sessionLog.SaveSessionDebriefAsync(dateStr, rating, note).ConfigureAwait(false);
        _logger.LogInformation("Session debrief saved for {Date}: rating={Rating}", dateStr, rating);
    }

    /// <inheritdoc />
    public async Task<TiltWarning?> CheckTiltWarningAsync(string dateStr)
    {
        return await _sessionLog.CheckTiltWarningAsync(dateStr).ConfigureAwait(false);
    }
}
