using System.Text.Json;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapRecovery(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // No client-supplied game stats or identities: the service reads the
        // current League client's history and honors persisted dismissals.
        app.MapPost("/api/settings/recover-games", async (RecentGameRecovery recovery) =>
            Results.Json(await recovery.RecoverAsync(), jsonOptions));
    }
}
