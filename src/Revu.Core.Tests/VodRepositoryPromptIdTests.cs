using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// v2.15.7: prompt_id round-trip on vod_bookmarks. Tagging a bookmark with
/// a prompt persists both objective_id (the prompt's parent) AND prompt_id;
/// post-game routing depends on both being readable.
/// </summary>
public sealed class VodRepositoryPromptIdTests
{
    [Fact]
    public async Task AddBookmarkAsync_PersistsPromptId()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync(
            "Wave management",
            skillArea: "macro",
            completionCriteria: "Crash before base");
        var promptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Key spells during trades", sortOrder: 0);

        var bookmarkId = await scope.Vod.AddBookmarkAsync(
            gameId,
            95,
            "Trade window opened",
            objectiveId: objectiveId,
            promptId: promptId);

        var bookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(gameId));
        Assert.Equal(bookmarkId, bookmark.Id);
        Assert.Equal(objectiveId, bookmark.ObjectiveId);
        Assert.Equal(promptId, bookmark.PromptId);
    }

    [Fact]
    public async Task SetBookmarkTagAsync_RoundTripsBothFields()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync("Wave mgmt");
        var promptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Spells", sortOrder: 0);

        // Start untagged.
        var bookmarkId = await scope.Vod.AddBookmarkAsync(gameId, 60, "note");
        var initial = Assert.Single(await scope.Vod.GetBookmarksAsync(gameId));
        Assert.Null(initial.ObjectiveId);
        Assert.Null(initial.PromptId);

        // Tag with prompt (which carries its parent objective).
        await scope.Vod.SetBookmarkTagAsync(bookmarkId, objectiveId, promptId);
        var tagged = Assert.Single(await scope.Vod.GetBookmarksAsync(gameId));
        Assert.Equal(objectiveId, tagged.ObjectiveId);
        Assert.Equal(promptId, tagged.PromptId);

        // Demote to objective-only — promptId should clear.
        await scope.Vod.SetBookmarkTagAsync(bookmarkId, objectiveId, promptId: null);
        var demoted = Assert.Single(await scope.Vod.GetBookmarksAsync(gameId));
        Assert.Equal(objectiveId, demoted.ObjectiveId);
        Assert.Null(demoted.PromptId);

        // Detach entirely.
        await scope.Vod.SetBookmarkTagAsync(bookmarkId, objectiveId: null, promptId: null);
        var detached = Assert.Single(await scope.Vod.GetBookmarksAsync(gameId));
        Assert.Null(detached.ObjectiveId);
        Assert.Null(detached.PromptId);
    }

    [Fact]
    public async Task SetBookmarkObjectiveAsync_ClearsAnyPriorPromptId()
    {
        // The legacy single-tag path delegates to SetBookmarkTagAsync with
        // promptId: null. If the user previously tagged a clip to a specific
        // prompt and then re-tags it to just the parent objective, the prompt
        // tag must drop — otherwise post-game routes the line to a stale answer.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync("Wave mgmt");
        var promptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Spells", sortOrder: 0);

        var bookmarkId = await scope.Vod.AddBookmarkAsync(
            gameId, 60, "x", objectiveId: objectiveId, promptId: promptId);
        await scope.Vod.SetBookmarkObjectiveAsync(bookmarkId, objectiveId);

        var bookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(gameId));
        Assert.Equal(objectiveId, bookmark.ObjectiveId);
        Assert.Null(bookmark.PromptId);
    }
}
