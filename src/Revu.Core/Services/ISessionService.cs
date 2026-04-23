#nullable enable

using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Thin wrapper for session operations (intentions, debriefs, tilt warnings).
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Get or create a session record for the given date.
    /// Returns session info including intention and debrief data.
    /// </summary>
    Task<SessionInfo> GetOrCreateSessionAsync(string dateStr);

    /// <summary>Set or update the session intention for a given date.</summary>
    Task SetIntentionAsync(string dateStr, string intention);

    /// <summary>Save the session debrief rating and note.</summary>
    Task SaveDebriefAsync(string dateStr, int rating, string note);

    /// <summary>
    /// Check for tilt warning signs in the current session.
    /// Returns a TiltWarning if mental dropped sharply between games, or null.
    /// </summary>
    Task<TiltWarning?> CheckTiltWarningAsync(string dateStr);
}
