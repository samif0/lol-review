using LoLReview.Core.Models;

namespace LoLReview.Core.Tests;

public sealed class CoachTrainingStatusTests
{
    [Fact]
    public void Summary_PrefersActiveGemmaAdapterOverGenericTrainingSummary()
    {
        var status = new CoachTrainingStatus
        {
            HasGemmaBaseModel = true,
            HasGemmaAdapter = true,
            LastTrainingSucceeded = true,
            LastTrainingSummary = "Registered Gemma 4 E4B and trained an adapter."
        };

        Assert.Equal("A fine-tuned Gemma coach adapter is active.", status.Summary);
    }

    [Fact]
    public void Summary_PrefersFailureSummaryEvenWhenGemmaIsActive()
    {
        var status = new CoachTrainingStatus
        {
            HasGemmaBaseModel = true,
            HasGemmaAdapter = true,
            LastTrainingSucceeded = false,
            LastTrainingSummary = "Gemma coach training failed: boom"
        };

        Assert.Equal("Gemma coach training failed: boom", status.Summary);
    }
}
