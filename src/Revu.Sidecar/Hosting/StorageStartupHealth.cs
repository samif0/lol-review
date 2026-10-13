#nullable enable

namespace Revu.Sidecar;

/// <summary>
/// Whether this launch's startup schema pass (additive migrations, seeds, indexes)
/// failed. The sidecar still starts (the degraded-start policy: database-dependent
/// endpoints report their own errors, and the idempotent pass re-runs next launch), but
/// /api/health says so instead of looking current while columns may be missing.
/// </summary>
public sealed class StorageStartupHealth
{
    private volatile bool _schemaUpgradeFailed;

    public bool SchemaUpgradeFailed => _schemaUpgradeFailed;

    public void MarkSchemaUpgradeFailed() => _schemaUpgradeFailed = true;
}
