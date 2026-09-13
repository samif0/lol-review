#nullable enable

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Revu.Core.Data;

namespace Revu.Sidecar;

/// <summary>Shared JSON error handling, bearer authentication, and isolated-run policy.</summary>
public static class SidecarApiMiddleware
{
    public static void UseSidecarApi(this WebApplication app, string bearerToken, bool isolatedHostTest)
    {
        // ── Global exception handler (OUTERMOST middleware) ──────────────────────────
        // Without this, an unhandled throw in any route handler returns — in the default
        // Production environment — a bare HTTP 500 with an EMPTY body. The desktop transport
        // then failed to parse the empty body as JSON and surfaced the cryptic
        // "sidecar JSON parse failed", losing the real cause (locked DB, constraint, disk,
        // missing column). We catch everything here and return a JSON {error} body so the
        // failure is legible end-to-end. Registered first so it wraps the bearer middleware
        // and every endpoint. (Write routes still SHOULD validate + return 4xx for expected
        // failures; this is the safety net for the unexpected ones.)
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                app.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("UnhandledRequest")
                    .LogError(ex, "Unhandled exception serving {Method} {Path}",
                        context.Request.Method, context.Request.Path);
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/json";
                    // P-041: a database-open failure carries a user-actionable diagnosis
                    // (missing file, read-only attribute, blocked folder ...). Surface it
                    // instead of the generic sentence so ANY write endpoint that hits it
                    // tells the user what is actually wrong. Raw SqliteException 14/8 land
                    // here too when the failure surfaced at repo-query time (WAL sidecar
                    // problems never hit the factory's open). Clear pooled handles so a
                    // fixed file isn't shadowed by a stale downgraded connection.
                    string error;
                    if (ex is DatabaseUnavailableException dbEx)
                    {
                        SqliteConnection.ClearAllPools();
                        error = dbEx.Message;
                    }
                    else if (SqliteOpenHealth.IsOpenOrWriteAccessFailure(ex))
                    {
                        SqliteConnection.ClearAllPools();
                        var dbPath = app.Services.GetRequiredService<WriteServices>().DatabasePath;
                        error = $"Revu couldn't open its database: {SqliteOpenHealth.Describe(dbPath)}";
                    }
                    else
                    {
                        // Verb-aware copy: a GET that blew up was LOADING a page, not
                        // saving — "error saving" on a dashboard load sent users hunting
                        // for a save that never happened. Include the path so the desktop
                        // transport's error surface says which page/action failed.
                        error = HttpMethods.IsGet(context.Request.Method)
                            ? $"The app hit an unexpected error loading {context.Request.Path}. Please try again."
                            : $"The app hit an unexpected error saving ({context.Request.Path}). Please try again.";
                    }
                    await context.Response.WriteAsJsonAsync(new { ok = false, error });
                }
            }
        });

        // ── Bearer-token middleware (constant-time compare; /api/health exempt) ──────
        var expectedTokenBytes = Encoding.UTF8.GetBytes(bearerToken);
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api/health"))
            {
                await next();
                return;
            }

            if (!TryGetBearer(context.Request.Headers.Authorization.ToString(), out var presented)
                || !FixedTimeTokenEquals(expectedTokenBytes, presented))
            {
                // JSON body (not plain text) so the desktop transport's {error} extractor can
                // surface a clean "sidecar HTTP 401: Unauthorized" instead of choking on a
                // non-JSON body. The status-first ordering in desktop transport already handles the
                // plain-text case, but emitting JSON keeps the contract consistent.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }

            if (isolatedHostTest && !IsolatedHostPolicy.Allows(context.Request.Method, path.Value ?? ""))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "This operation has external effects and is disabled in isolated host testing." });
                return;
            }
            await next();
        });
    }

    private static bool TryGetBearer(string? authorizationHeader, out byte[] tokenBytes)
    {
        tokenBytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return false;

        const string prefix = "Bearer ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var token = authorizationHeader.Substring(prefix.Length).Trim();
        if (token.Length == 0) return false;

        tokenBytes = Encoding.UTF8.GetBytes(token);
        return true;
    }

    private static bool FixedTimeTokenEquals(byte[] expected, byte[] presented)
    {
        // Length check first would leak length via timing, but CryptographicOperations
        // .FixedTimeEquals requires equal lengths. Compare against a fixed-size hash of
        // each side so mismatched lengths still take constant time.
        Span<byte> expectedHash = stackalloc byte[32];
        Span<byte> presentedHash = stackalloc byte[32];
        SHA256.HashData(expected, expectedHash);
        SHA256.HashData(presented, presentedHash);
        return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
    }
}
