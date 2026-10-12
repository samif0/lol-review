using Xunit;
using Revu.Core.Models;

namespace Revu.Sidecar.Tests;

/// <summary>
/// POST /api/review/save honours Settings' "Require notes to finish a review": the route
/// used to hardcode the requirement off, so the toggle did nothing.
/// </summary>
public sealed class ReviewRequireNotesTests
{
    [Fact]
    public async Task Save_WithNotesRequired_RejectsAnEmptyReview()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(8801);
        await scope.Config.SaveAsync(new AppConfig { RequireReviewNotes = true });

        var result = await SidecarEndpoints.SaveReviewAsync(Body(8801, reviewNotes: ""),
            scope.ConceptTags, scope.ReviewWorkflow, scope.Config, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Review notes are required in Settings. Add review content before saving.", result.ErrorMessage);
        Assert.Equal(0, (await scope.Games.GetAsync(8801))!.Rating);
    }

    [Fact]
    public async Task Save_WithNotesRequired_AcceptsAReviewWithNotes()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(8802);
        await scope.Config.SaveAsync(new AppConfig { RequireReviewNotes = true });

        var result = await SidecarEndpoints.SaveReviewAsync(Body(8802, reviewNotes: "Held the wave"),
            scope.ConceptTags, scope.ReviewWorkflow, scope.Config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Held the wave", (await scope.Games.GetAsync(8802))!.ReviewNotes);
    }

    [Fact]
    public async Task Save_WithNotesOptional_AcceptsAnEmptyReview()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(8803);

        var result = await SidecarEndpoints.SaveReviewAsync(Body(8803, reviewNotes: ""),
            scope.ConceptTags, scope.ReviewWorkflow, scope.Config, CancellationToken.None);

        Assert.True(result.Success);
    }

    private static SaveReviewBody Body(long gameId, string reviewNotes) => new(
        GameId: gameId,
        ChampionName: "Ahri",
        Win: true,
        MentalRating: 6,
        WentWell: "",
        Mistakes: "",
        FocusNext: "",
        ReviewNotes: reviewNotes,
        ImprovementNote: null,
        Attribution: "",
        MentalHandled: null,
        SpottedProblems: "",
        OutsideControl: null,
        WithinControl: null,
        PersonalContribution: null,
        EnemyLaner: null,
        MatchupNote: null,
        SelectedTagIds: null,
        FreeTextTags: null,
        ObjectivePractices: null,
        FocusAdherence: null);
}
