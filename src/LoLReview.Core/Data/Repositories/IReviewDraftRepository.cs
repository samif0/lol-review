#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Data.Repositories;

public interface IReviewDraftRepository
{
    Task<ReviewDraft?> GetAsync(long gameId);
    Task UpsertAsync(ReviewDraft draft);
    Task DeleteAsync(long gameId);
}
