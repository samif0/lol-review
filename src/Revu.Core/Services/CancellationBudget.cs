#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// A token that is not cancelled while <c>trigger</c> is live, and is cancelled
/// <c>budget</c> after <c>trigger</c> fires (at once-plus-budget when it already has).
/// For best-effort cleanup that must still run after a stop, but must not hold up a
/// shutdown drain for its full timeout.
/// </summary>
public sealed class CancellationBudget : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _registration;

    public CancellationBudget(CancellationToken trigger, TimeSpan budget)
    {
        _registration = trigger.Register(() =>
        {
            try { _cts.CancelAfter(budget); }
            catch (ObjectDisposedException) { }
        });
    }

    public CancellationToken Token => _cts.Token;

    public void Dispose()
    {
        _registration.Dispose();
        _cts.Dispose();
    }
}
