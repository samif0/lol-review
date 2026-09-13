using System.Text.Json.Nodes;
using Revu.Core.Models;

namespace Revu.Core.Tests;

/// <summary>Explicit evidence for consumer tests; raw inference rejection has separate regression tests.</summary>
internal static class ConfirmedEventFixture
{
    public static string Reviewed(string details)
    {
        var node = JsonNode.Parse(details)!.AsObject();
        node["correction"] = new JsonObject { ["fixture"] = true };
        return node.ToJsonString();
    }
    public static string Supported(string details)
    {
        var node = JsonNode.Parse(details)!.AsObject();
        node["processing"] = new JsonObject { ["version"] = 1, ["verification"] = "supported", ["shadow"] = false,
            // Consumer fixtures create at most two objectives in a fresh database.
            ["objectiveIds"] = new JsonArray(JsonValue.Create(1L), JsonValue.Create(2L)),
            ["evidence"] = new JsonArray(new JsonObject { ["Id"] = "fixture:verified" }) };
        return node.ToJsonString();
    }
    public static GameEvent Fight(int id, int start, int end) => new()
    {
        Id = id, EventType = "TEAMFIGHT", GameTimeS = start,
        Details = Reviewed($$"""{"start_s":{{start}},"end_s":{{end}},"self":"in"}""")
    };
}
