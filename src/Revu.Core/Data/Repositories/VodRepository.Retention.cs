#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>
/// One clip file Revu created (a vod_bookmarks.clip_path) and whether the user kept it:
/// rated it, saved it as evidence or a highlight, or tagged it to an objective or prompt
/// themselves (an auto-clip's objective tag does not count).
/// </summary>
public sealed record ClipFileRetention(string ClipPath, bool Kept);

/// <summary>H5 clips-folder eviction: the only files it may ever consider.</summary>
public sealed partial class VodRepository
{
    public async Task<IReadOnlyList<ClipFileRetention>> ListClipFilesAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.clip_path,
                   CASE
                       WHEN COALESCE(b.quality, '') != '' THEN 1
                       WHEN EXISTS (SELECT 1 FROM evidence_items e
                                    WHERE e.game_id = b.game_id AND e.source_kind = 'clip'
                                      AND e.source_id = b.id
                                      AND e.status IN ('evidence', 'highlight')) THEN 1
                       WHEN (b.objective_id IS NOT NULL OR b.prompt_id IS NOT NULL)
                            AND NOT EXISTS (SELECT 1 FROM evidence_items e
                                            WHERE e.game_id = b.game_id AND e.source_kind = 'clip'
                                              AND e.source_id = b.id
                                              AND e.source_key LIKE 'autoclip%') THEN 1
                       ELSE 0
                   END AS kept
            FROM vod_bookmarks b
            WHERE b.clip_path IS NOT NULL AND b.clip_path != ''
            """;
        var rows = new List<ClipFileRetention>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ClipFileRetention(reader.GetString(0), reader.GetInt64(1) != 0));
        }
        return rows;
    }
}
