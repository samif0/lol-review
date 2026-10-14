#nullable enable

namespace Revu.Core.Lcu;

/// <summary>Identity and queue read together from the current gameflow session.</summary>
public sealed record LcuGameSession(long GameId, int? QueueId);
