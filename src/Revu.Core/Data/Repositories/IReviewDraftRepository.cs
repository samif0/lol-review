#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

public interface IReviewDraftRepository
{
    Task<ReviewDraft?> GetAsync(long gameId);
    Task UpsertAsync(ReviewDraft draft);
    Task DeleteAsync(long gameId);
}
