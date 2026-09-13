using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revu.Sidecar;

/// <summary>Stable JSON response options shared by all desktop API endpoints.</summary>
public static class SidecarJson
{
    public static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
