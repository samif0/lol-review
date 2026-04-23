#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// Generates a paste-ready text block for a new Claude conversation.
/// Ported from Python database/context.py.
/// </summary>
public interface IClaudeContextService
{
    /// <summary>
    /// Build formatted text with: adherence streak, mental vs winrate,
    /// last 7 days of sessions, detailed game log, and persistent notes.
    /// </summary>
    Task<string> GenerateContextAsync();
}
