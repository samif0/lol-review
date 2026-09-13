using System.Text.Json;
using System.Text.Json.Nodes;
using Revu.Core.Services.EventProcessing;

namespace Revu.Core.Tests;

public sealed class DeathRecapRecoveryTests
{
    private static JsonElement Match() => JsonSerializer.SerializeToElement(new
    {
        metadata = new { matchId = "TEST_1" },
        info = new { participants = Enumerable.Range(1, 10).Select(id => new
        { participantId = id, teamId = id <= 5 ? 100 : 200, championName = $"Champion{id}", puuid = $"player{id}" }) }
    });
    private static JsonObject Hit(int id, string champion, int damage = 10) => new()
    {
        ["participantId"] = id, ["name"] = champion, ["type"] = "OTHER",
        ["magicDamage"] = 0, ["physicalDamage"] = damage, ["trueDamage"] = 0
    };
    private static JsonObject Death(int victim = 1, int opponent = 6, int timestamp = 120000) => new()
    {
        ["type"] = "CHAMPION_KILL", ["timestamp"] = timestamp, ["victimId"] = victim,
        ["victimDamageDealt"] = new JsonArray(Hit(opponent, $"Champion{victim}", 20)),
        ["victimDamageReceived"] = new JsonArray(Hit(opponent, $"Champion{opponent}", 40))
    };
    private static Task<ProcessingReport> Run(JsonObject[] deaths, ObjectiveSubscription[]? subscriptions = null)
    {
        var timeline = new JsonObject
        {
            ["metadata"] = new JsonObject { ["matchId"] = "TEST_1" },
            ["info"] = new JsonObject { ["frames"] = new JsonArray(new JsonObject
                { ["events"] = new JsonArray(deaths.Cast<JsonNode?>().ToArray()) }) }
        };
        return DeathRecapRecovery.ProcessAsync(Match(), JsonSerializer.SerializeToElement(timeline), "player1",
            subscriptions ?? [new(47, "TRADE")]);
    }

    [Fact]
    public async Task ReciprocalDamage_HasFrozenAssociationsEvidenceAndPointTime_ButRemainsShadow()
    {
        var report = await Run([Death()], [new(47,"TRADE"), new(49,"TRADE"), new(48,"EVEN")]);
        var item = Assert.Single(report.Events);
        Assert.False(EventEligibility.IsEligible(item));
        using var doc = JsonDocument.Parse(item.Details);
        var root = doc.RootElement;
        Assert.Equal(120, root.GetProperty("start_s").GetInt32());
        Assert.Equal(120, root.GetProperty("end_s").GetInt32());
        Assert.Equal(new long[] { 47, 49 }, root.GetProperty("processing").GetProperty("objectiveIds").EnumerateArray().Select(v=>v.GetInt64()));
        Assert.Equal(20, root.GetProperty("attributes").GetProperty("DamageDealt").GetDouble());
        Assert.DoesNotContain("fight_numbers", item.Details);
        Assert.DoesNotContain("SHORT_TRADE", item.Details);
    }

    [Fact]
    public async Task EnemyDeath_ReversesDamageDirectionToPlayerPerspective()
    {
        var item = Assert.Single((await Run([Death(6, 1)])).Events);
        var attributes = JsonDocument.Parse(item.Details).RootElement.GetProperty("attributes");
        Assert.Equal("Champion6", attributes.GetProperty("Opponent").GetString());
        Assert.Equal(40, attributes.GetProperty("DamageDealt").GetDouble());
        Assert.Equal(20, attributes.GetProperty("DamageReceived").GetDouble());
    }

    [Theory]
    [InlineData("one-way")]
    [InlineData("missing")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("identity")]
    [InlineData("environment")]
    public async Task InsufficientEvidence_NeverProducesCandidate(string problem)
    {
        var death = Death();
        var hit = death["victimDamageReceived"]![0]!;
        switch (problem)
        {
            case "one-way": death["victimDamageReceived"] = new JsonArray(); break;
            case "missing": death.Remove("victimDamageReceived"); break;
            case "zero": hit["physicalDamage"] = 0; break;
            case "negative": hit["physicalDamage"] = -1; break;
            case "identity": hit["name"] = "Champion7"; break;
            case "environment": hit["participantId"] = 0; break;
        }
        Assert.Empty((await Run([death])).Events);
    }

    [Fact]
    public async Task AmbiguousRecordDoesNotDiscardIndependentEvidence()
    {
        var bad = Death(timestamp: 60000);
        bad["victimDamageReceived"]![0]!["name"] = "WrongChampion";
        var report = await Run([bad, Death()]);
        Assert.Single(report.Events);
        Assert.Contains("1 ambiguous", Assert.Single(report.Coverage).Reason);
    }

    [Fact]
    public async Task DuplicateRecordsAndRerunsHaveStableIdentity()
    {
        var first = await Run([Death(), Death()]);
        var second = await Run([Death()]);
        Assert.Equal(Assert.Single(first.Events).EventKey, Assert.Single(second.Events).EventKey);
    }

    [Fact]
    public async Task NoSubscriptionOrUnrelatedOrAlliedCombatHasNoOutput()
    {
        Assert.Empty((await Run([Death()], [])).Events);
        Assert.Empty((await Run([Death()], [new(48,"EVEN")])).Events);
        Assert.Empty((await Run([Death(2,6)])).Events);
        Assert.Empty((await Run([Death(1,2)])).Events);
    }

    [Fact]
    public async Task ObservationBudgetFailsClosed()
    {
        var report = await Run(Enumerable.Range(0,257).Select(i=>Death(timestamp:i*1000)).ToArray());
        Assert.Empty(report.Events);
        Assert.Equal("incomplete", Assert.Single(report.Coverage).Status);
    }
}
