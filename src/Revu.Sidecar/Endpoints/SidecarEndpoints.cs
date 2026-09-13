#nullable enable

using System.Text.Json;

namespace Revu.Sidecar;

/// <summary>The desktop API, organized by feature while preserving its method/path contracts.</summary>
public static partial class SidecarEndpoints
{
    public static void MapSidecarEndpoints(this WebApplication app, JsonSerializerOptions jsonOptions,
        SidecarHostSession hostSession)
    {
        MapHost(app, jsonOptions, hostSession);
        MapGames(app, jsonOptions);
        MapReviews(app, jsonOptions);
        MapObjectives(app, jsonOptions);
        MapPlanning(app, jsonOptions);
        MapMatchups(app, jsonOptions);
        MapSettings(app, jsonOptions);
        MapAccounts(app, jsonOptions);
        MapEvidence(app, jsonOptions);
        MapMedia(app, jsonOptions);
        MapPatterns(app, jsonOptions);
        MapDiagnostics(app, jsonOptions);
        MapRecording(app, jsonOptions);
    }
}
