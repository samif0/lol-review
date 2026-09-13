#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapAccounts(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ─────────────────────────────────────────────────────────────────────────────
        // RIOT AUTH / ACCOUNT (Batch 4). SECURITY-SENSITIVE: the email-OTP login flow
        // (mirrors OnboardingViewModel + SettingsViewModel's RiotAuth* commands) and the
        // account resolve that fills RiotPuuid + auto-detected rank. The session triplet
        // (RiotSessionToken/Email/ExpiresAt) and PUUID persist via the WRITE-graph
        // IConfigService, which round-trips through the DPAPI IProtectedSecretStore — so
        // these MUST run on WriteServices, never the read graph.
        //
        // These are PROXY calls to the Cloudflare Worker (IRiotAuthClient), not DB writes,
        // so most don't take the BackupGuard; the two that DO persist config (/verify,
        // /resolve, /logout) take it before the SaveAsync — belt-and-suspenders, matching
        // every other write endpoint. RiotAuthException carries a user-displayable message
        // (server-driven: invalid_email / invalid_or_expired_code / 429 / 401 / 502 …);
        // we surface it with HTTP 422 so the desktop transport's error-extraction shows it.
        // ─────────────────────────────────────────────────────────────────────────────

        // POST /api/auth/login  { email } — sends the magic-link / OTP email. No DB write.
        // Mirrors OnboardingViewModel.SendLoginCodeAsync (validates non-empty email).
        app.MapPost("/api/auth/login", async (AuthLoginBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Email))
                return Results.BadRequest(new { error = "Enter an email to continue." });
            try
            {
                await w.RiotAuth.LoginAsync(body.Email.Trim(), ct);
                log.LogInformation("Auth: login code sent.");
                return Results.Json(new { ok = true, info = $"Check {body.Email.Trim()} for a code." }, jsonOptions);
            }
            catch (RiotAuthException ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 422);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Auth: login failed");
                return Results.Json(new { ok = false, error = "Couldn't reach the server. Check your connection." }, jsonOptions, statusCode: 502);
            }
        });

        // POST /api/auth/signup  { email, inviteCode } — sends the email after validating
        // an invite code. Mirrors SettingsViewModel.RiotAuthSignup (both required; code
        // upper-cased). No DB write.
        app.MapPost("/api/auth/signup", async (AuthSignupBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.InviteCode))
                return Results.BadRequest(new { error = "Enter an email and an invite code." });
            try
            {
                await w.RiotAuth.SignupAsync(body.Email.Trim(), body.InviteCode.Trim().ToUpperInvariant(), ct);
                log.LogInformation("Auth: signup code sent.");
                return Results.Json(new { ok = true, info = $"Check {body.Email.Trim()} for a code." }, jsonOptions);
            }
            catch (RiotAuthException ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 422);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Auth: signup failed");
                return Results.Json(new { ok = false, error = "Couldn't reach the server. Check your connection." }, jsonOptions, statusCode: 502);
            }
        });

        // POST /api/auth/verify  { code } — exchanges the OTP for a session token and
        // PERSISTS the session triplet (token/email/expiresAt) via config (DPAPI). Mirrors
        // OnboardingViewModel.VerifyAsync + SettingsViewModel.RiotAuthVerify. The email we
        // stamp is the one the verify call was issued for — the frontend passes it back so
        // we record the right RiotSessionEmail (the proxy's verify response carries only
        // the token + expiry). Code is trimmed + upper-cased (mirror the VM).
        app.MapPost("/api/auth/verify", async (AuthVerifyBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Code))
                return Results.BadRequest(new { error = "Paste the code from your email." });
            try
            {
                var result = await w.RiotAuth.VerifyAsync(body.Code.Trim().ToUpperInvariant(), ct);
                await w.BackupGuard.EnsureBackedUpAsync();

                var cfg = await w.Config.LoadAsync();
                cfg.RiotSessionToken = result.SessionToken;
                // Prefer the email the frontend carried over from the login step; fall back
                // to whatever's already stored so a re-verify doesn't blank it.
                if (!string.IsNullOrWhiteSpace(body.Email)) cfg.RiotSessionEmail = body.Email.Trim();
                cfg.RiotSessionExpiresAt = result.ExpiresAt;
                await w.Config.SaveAsync(cfg);

                log.LogInformation("Auth: session verified + persisted (expires {ExpiresAt}).", result.ExpiresAt);
                return Results.Json(new { ok = true, email = cfg.RiotSessionEmail }, jsonOptions);
            }
            catch (RiotAuthException ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 422);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Auth: verify failed");
                return Results.Json(new { ok = false, error = "Couldn't verify the code." }, jsonOptions, statusCode: 502);
            }
        });

        // POST /api/auth/resolve  { riotId, region } — resolve the Riot ID to a PUUID
        // using the stored session token, persist RiotId/RiotRegion/RiotPuuid +
        // OnboardingSkipped=false, then best-effort auto-detect the ranked solo tier
        // (display-only, never throws). Mirrors OnboardingViewModel.FinishAccountAsync +
        // SettingsViewModel.Save's PUUID-resolution leg. Validates the gameName#tagLine
        // shape exactly like the VM. Returns the detected rank so the frontend can show
        // the "RANK DETECTED" line.
        app.MapPost("/api/auth/resolve", async (AuthResolveBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            var riotId = (body?.RiotId ?? "").Trim();
            var region = (body?.Region ?? "").Trim();
            // Mirror the VM validation: must contain '#', not start/end with it.
            if (!riotId.Contains('#') || riotId.StartsWith('#') || riotId.EndsWith('#'))
                return Results.BadRequest(new { error = "Enter your Riot ID as gameName#tagLine." });
            if (region.Length == 0)
                return Results.BadRequest(new { error = "Pick a region." });

            var cfg = await w.Config.LoadAsync();
            if (string.IsNullOrWhiteSpace(cfg.RiotSessionToken))
                return Results.Json(new { ok = false, error = "Session missing. Start over.", needsLogin = true }, jsonOptions, statusCode: 401);

            try
            {
                var regionLower = region.ToLowerInvariant();
                var account = await w.RiotAuth.ResolveAccountAsync(cfg.RiotSessionToken, riotId, regionLower, ct);

                await w.BackupGuard.EnsureBackedUpAsync();
                cfg.RiotId = riotId;
                cfg.RiotRegion = regionLower;
                cfg.RiotPuuid = account.Puuid;
                cfg.OnboardingSkipped = false; // login path: rely on RiotProxyEnabled + role
                await w.Config.SaveAsync(cfg);

                // Best-effort rank detection (display-only; never throws).
                var rank = await w.RiotAuth.GetSoloRankAsync(cfg.RiotSessionToken, account.Puuid, regionLower, ct);

                log.LogInformation("Auth: account resolved (puuid set, rank='{Rank}').", rank);
                return Results.Json(new { ok = true, puuid = account.Puuid, gameName = account.GameName, tagLine = account.TagLine, rank }, jsonOptions);
            }
            catch (RiotAuthException ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 422);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Auth: account resolve failed");
                return Results.Json(new { ok = false, error = "Couldn't validate that account." }, jsonOptions, statusCode: 502);
            }
        });

        // POST /api/auth/logout — clear the session triplet (and reset OnboardingSkipped so
        // a stale opt-out doesn't trap the user), then best-effort tell the server. Mirrors
        // SettingsViewModel.RiotAuthLogout: clear config FIRST (so the local state is gone
        // even if the network call fails), then call LogoutAsync(token).
        app.MapPost("/api/auth/logout", async (WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            await w.BackupGuard.EnsureBackedUpAsync();
            var cfg = await w.Config.LoadAsync();
            var token = cfg.RiotSessionToken;
            // Deliberate sign-out: clear the session via the dedicated path (a normal
            // SaveAsync now PRESERVES an empty token / zero expiry, so the manual blank-and-
            // save no longer clears anything). Reset OnboardingSkipped separately.
            await w.Config.ClearSessionAsync();
            if (cfg.OnboardingSkipped)
            {
                var fresh = await w.Config.LoadAsync();
                fresh.OnboardingSkipped = false;
                await w.Config.SaveAsync(fresh);
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                try { await w.RiotAuth.LogoutAsync(token, ct); }
                catch (Exception ex) { log.LogDebug(ex, "Auth: server logout failed (local session already cleared)"); }
            }
            log.LogInformation("Auth: logged out (session cleared).");
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/auth/clear-partial — blank a HALF-SAVED session (token saved but the
        // account never got resolved) so backing out of onboarding doesn't jam the gate.
        // Called by the onboarding Back buttons (onboarding.js backToWelcome/backToEmail).
        //
        // CRITICAL GUARD (config-clobber fix): only clear a session that is genuinely
        // PARTIAL — a token with NO resolved account (no RiotId AND no PUUID). A COMPLETE,
        // logged-in session (account resolved) must NEVER be cleared here: a stray Back hop
        // or a re-entered onboarding flow would otherwise wipe a valid user's session token,
        // leaving them "shown as logged in" (email/RiotId persist) but unable to share
        // (token blank -> proxy 401). The old guard only skipped when token+email+expiry were
        // ALL empty, so any user whose RiotSessionEmail was set got their live token wiped.
        // Sign-out (POST /api/auth/logout) is the ONLY path that clears a complete session.
        app.MapPost("/api/auth/clear-partial", async (WriteServices w, ILogger<Program> log) =>
        {
            try
            {
                var cfg = await w.Config.LoadAsync();

                // Nothing to clear: no session at all.
                if (string.IsNullOrEmpty(cfg.RiotSessionToken) && string.IsNullOrEmpty(cfg.RiotSessionEmail) && cfg.RiotSessionExpiresAt == 0)
                    return Results.Json(new { ok = true, cleared = false }, jsonOptions);

                // Session is COMPLETE (account resolved) -> this is NOT a partial session;
                // leave it untouched. This is the clobber guard.
                var accountResolved = !string.IsNullOrWhiteSpace(cfg.RiotId) || !string.IsNullOrWhiteSpace(cfg.RiotPuuid);
                if (accountResolved)
                {
                    log.LogInformation("Auth: clear-partial skipped — session is complete (account resolved), not partial.");
                    return Results.Json(new { ok = true, cleared = false, reason = "complete_session" }, jsonOptions);
                }

                await w.BackupGuard.EnsureBackedUpAsync();
                // Use the dedicated clear path — a normal SaveAsync now preserves an empty
                // token / zero expiry, so this is the only way to actually wipe a session.
                await w.Config.ClearSessionAsync();
                log.LogInformation("Auth: partial session cleared.");
                return Results.Json(new { ok = true, cleared = true }, jsonOptions);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Auth: could not clear partial onboarding session");
                return Results.Json(new { ok = false, error = ex.Message }, jsonOptions);
            }
        });

        // GET /api/auth/status — the signed-in snapshot the Onboarding/Settings/VOD-share
        // surfaces read: signed-in (token present + unexpired), the linked Riot ID/region/
        // email, whether a PUUID is resolved (gates backfill), and the persisted role. Read
        // from the WRITE-graph config via a fresh LoadAsync so it reflects the writes the
        // auth POSTs above just made (the read-graph config is a separate cache). Secrets
        // (token/PUUID value) are NOT returned — only the booleans derived from them.
        app.MapGet("/api/auth/status", async (WriteServices w, ILogger<Program> log) =>
        {
            try
            {
                var cfg = await w.Config.LoadAsync();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var signedIn = !string.IsNullOrWhiteSpace(cfg.RiotSessionToken) && cfg.RiotSessionExpiresAt > now;
                return Results.Json(new
                {
                    ok = true,
                    signedIn,
                    email = cfg.RiotSessionEmail ?? "",
                    riotId = cfg.RiotId ?? "",
                    region = cfg.RiotRegion ?? "",
                    hasPuuid = !string.IsNullOrWhiteSpace(cfg.RiotPuuid),
                    primaryRole = cfg.PrimaryRole ?? "",
                    // backfill is usable only when signed in AND a PUUID + region are set.
                    backfillReady = signedIn && !string.IsNullOrWhiteSpace(cfg.RiotPuuid) && !string.IsNullOrWhiteSpace(cfg.RiotRegion),
                }, jsonOptions);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Auth: status read failed");
                return Results.Json(new { ok = true, signedIn = false, email = "", riotId = "", region = "", hasPuuid = false, primaryRole = "", backfillReady = false }, jsonOptions);
            }
        });

        app.MapPost("/api/backfill/start", async (WriteServices w, SidecarEventHub hub, ILogger<Program> log, CancellationToken ct) =>
        {
            var cfg = await w.Config.LoadAsync();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var signedIn = !string.IsNullOrWhiteSpace(cfg.RiotSessionToken) && cfg.RiotSessionExpiresAt > now;
            if (!signedIn || string.IsNullOrWhiteSpace(cfg.RiotPuuid) || string.IsNullOrWhiteSpace(cfg.RiotRegion))
            {
                return Results.Json(new
                {
                    ok = true,
                    ranBackfill = false,
                    text = "Sign in and link your Riot ID first — backfill needs your account.",
                    enemy = new { scanned = 0, updated = 0, skipped = 0, failed = 0 },
                    laning = new { scanned = 0, updated = 0, skipped = 0, failed = 0 },
                }, jsonOptions);
            }

            await w.BackupGuard.EnsureBackedUpAsync();

            EnemyLanerBackfillResult enemy;
            try
            {
                enemy = await w.EnemyLanerBackfill.RunAsync(ct: ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Backfill: enemy-laner leg failed");
                return Results.Json(new { ok = false, error = $"Backfill failed: {ex.Message}" }, jsonOptions, statusCode: 502);
            }

            // Map-state leg (v3.2): jungle-proximity events + death map-state stamps from
            // the Match-V5 timeline. Runs BEFORE the laning leg on purpose: both walk the
            // same missing-game backlog two calls at a time, a deep history takes tens of
            // minutes at the Riot key's sustained budget, and if the run is cut short the
            // newest review-facing data (timeline markers) should have landed first. The
            // proxy edge-caches match/timeline, so the laning leg re-fetching the same
            // games afterwards is comparatively cheap. Degrades silently like laning.
            MapStateBackfillResult mapState = new(0, 0, 0, 0);
            try
            {
                mapState = await w.MapStateBackfill.RunAsync(ct: ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Backfill: map-state leg failed (degraded, non-fatal)");
            }

            // The fights and proximity rows that just landed change what an open VOD timeline
            // shows. Same event the post-game coordinator publishes; gameId 0 means "any game",
            // so a viewer refreshes whichever game it has open.
            if (mapState.Updated > 0)
                hub.Publish("mapStateUpdated", new { gameId = 0L, updated = mapState.Updated });

            // Laning leg degrades silently on a proxy 404 (mirror the VM try/catch).
            LaningBackfillResult laning = new(0, 0, 0, 0);
            try
            {
                laning = await w.LaningBackfill.RunAsync(ct: ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Backfill: laning leg failed (degraded, non-fatal)");
            }

            var totalUpdated = enemy.Updated + laning.Updated + mapState.Updated;
            var text = totalUpdated > 0
                ? $"Backfilled {totalUpdated} game(s). Enemy laners: {enemy.Updated}/{enemy.Scanned}. Laning@10: {laning.Updated}/{laning.Scanned}. Map state and teamfights: {mapState.Updated}/{mapState.Scanned}."
                : "Nothing to backfill — every game already has its matchup data.";

            log.LogInformation("Backfill done: enemy {EU}/{ES}, laning {LU}/{LS}, map-state {MU}/{MS}",
                enemy.Updated, enemy.Scanned, laning.Updated, laning.Scanned, mapState.Updated, mapState.Scanned);
            return Results.Json(new
            {
                ok = true,
                ranBackfill = true,
                text,
                enemy = new { scanned = enemy.Scanned, updated = enemy.Updated, skipped = enemy.Skipped, failed = enemy.Failed },
                laning = new { scanned = laning.Scanned, updated = laning.Updated, skipped = laning.Skipped, failed = laning.Failed },
                mapState = new { scanned = mapState.Scanned, updated = mapState.Updated, skipped = mapState.Skipped, failed = mapState.Failed },
            }, jsonOptions);
        });
    }
}
