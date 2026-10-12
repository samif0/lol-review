/**
 * Riot API passthrough for the desktop app (/account, /matches, /match/:id,
 * /rank, /timeline/:id) and the web review tool (the same under /web/).
 *
 * Every handler validates first and calls `charge` only right before an
 * upstream Riot request, so bad requests and edge-cache hits never spend the
 * Riot budget (see riot-budget.ts).
 */

import { Env } from "./types";
import { badRequest, jsonResponse } from "./http";

/** Spends one upstream call, or returns the 429 to send instead. */
export type Charge = () => Promise<Response | null>;

// ── Region mapping ──────────────────────────────────────────────────────

// Match-V5 regional clusters (https://developer.riotgames.com/docs/lol,
// "Routing Values").
const PLATFORM_TO_REGIONAL: Record<string, string> = {
  na1: "americas",
  br1: "americas",
  la1: "americas",
  la2: "americas",
  euw1: "europe",
  eun1: "europe",
  tr1: "europe",
  ru: "europe",
  me1: "europe",
  kr: "asia",
  jp1: "asia",
  oc1: "sea",
  ph2: "sea",
  sg2: "sea",
  th2: "sea",
  tw2: "sea",
  vn2: "sea",
};

/** Every platform the proxy accepts, for the web tool's region list. */
export const SUPPORTED_PLATFORMS = Object.keys(PLATFORM_TO_REGIONAL);

export function regionalFor(platform: string): string | null {
  // Own-property check: a bare index on a plain object would resolve
  // "__proto__" / "constructor" to a truthy non-string, and /rank would then
  // build a Riot URL from that region with the API key attached.
  const key = platform.toLowerCase();
  return Object.hasOwn(PLATFORM_TO_REGIONAL, key) ? PLATFORM_TO_REGIONAL[key] : null;
}

/**
 * Account-V1 is only served by the americas, asia and europe clusters, and
 * each one answers for every account ("You can query for any account in any
 * region"; the RSO section of https://developer.riotgames.com/docs/lol says
 * the account data from each cluster is identical). There is no sea host for
 * it, so the sea platforms (OCE and SEA) go to the nearest one, asia.
 */
export function accountRegionalFor(platform: string): string | null {
  const regional = regionalFor(platform);
  return regional === "sea" ? "asia" : regional;
}

// ── Upstream fetch ──────────────────────────────────────────────────────

export const RIOT_TIMEOUT_MS = 10_000;

/**
 * One Riot GET. The body is streamed through untouched (no text() decode and
 * re-encode: Match-V5 timelines are MB-scale and CPU on a Worker is scarce).
 * A Riot call whose response headers take longer than RIOT_TIMEOUT_MS is a 504
 * riot_timeout. The timer stops once headers arrive: the same signal would
 * otherwise also abort the streamed body, truncating a large timeline for a
 * slow reader on the uncached path.
 */
export async function riotGet(url: string, env: Env): Promise<Response> {
  const controller = new AbortController();
  const timer = setTimeout(
    () => controller.abort(new DOMException("Riot call timed out", "TimeoutError")),
    RIOT_TIMEOUT_MS,
  );
  let r: Response;
  try {
    r = await fetch(url, {
      headers: { "X-Riot-Token": env.RIOT_API_KEY },
      signal: controller.signal,
    });
  } catch (err) {
    const name = (err as Error)?.name;
    if (name === "TimeoutError" || name === "AbortError") {
      return jsonResponse(
        { error: "riot_timeout", message: "Riot's servers took too long to answer. Try again in a moment." },
        504,
      );
    }
    throw err;
  } finally {
    clearTimeout(timer);
  }
  const headers = new Headers({ "Content-Type": "application/json" });
  const retryAfter = r.headers.get("Retry-After");
  if (retryAfter) headers.set("Retry-After", retryAfter);
  return new Response(r.body, { status: r.status, headers });
}

// ── Edge cache for IMMUTABLE upstream resources ─────────────────────────
//
// A finished game never changes, so match detail and timeline are cacheable
// forever. Two users opening the same match share one upstream call, and a
// re-open is served entirely from cache with no Riot traffic and no charge.
//
// The cache key is the regional Riot URL. It deliberately does NOT include the
// X-Riot-Token (a request header), so all callers share one entry. Account,
// matches list and rank are not immutable and never flow through here.
async function riotGetCached(
  url: string,
  env: Env,
  ctx: ExecutionContext | undefined,
  charge: Charge,
): Promise<Response> {
  // `caches` may be absent in non-Worker test runtimes; then this is a plain
  // passthrough, so the cache is an optimization, never a hard dependency.
  const cache = (globalThis as { caches?: CacheStorage }).caches?.default;
  const cacheKey = new Request(url);
  if (cache) {
    const hit = await cache.match(cacheKey);
    if (hit) return hit;
  }

  const denied = await charge();
  if (denied) return denied;
  const fresh = await riotGet(url, env);
  // Only cache a genuine success: a transient 404/429/5xx must not be pinned.
  if (!cache || fresh.status !== 200) return fresh;

  const headers = new Headers(fresh.headers);
  headers.set("Cache-Control", "public, max-age=31536000, immutable");
  const cacheable = new Response(fresh.body, { status: fresh.status, headers });
  const put = cache.put(cacheKey, cacheable.clone());
  if (ctx) ctx.waitUntil(put);
  else await put; // no ctx (tests): still populate, inline.
  return cacheable;
}

async function chargedGet(url: string, env: Env, charge: Charge): Promise<Response> {
  const denied = await charge();
  if (denied) return denied;
  return riotGet(url, env);
}

// ── Handlers ────────────────────────────────────────────────────────────

const MATCH_ID = /^[A-Z0-9]{2,5}_[0-9]+$/;

function handleAccount(url: URL, env: Env, charge: Charge): Promise<Response> | Response {
  const riotId = url.searchParams.get("riotId");
  const platform = url.searchParams.get("region");
  if (!riotId || !platform) return badRequest("riotId and region required");
  const regional = accountRegionalFor(platform);
  if (!regional) return badRequest(`unknown region '${platform}'`);
  const hashPos = riotId.indexOf("#");
  if (hashPos < 1 || hashPos === riotId.length - 1) {
    return badRequest("riotId must be gameName#tagLine");
  }
  const gameName = encodeURIComponent(riotId.slice(0, hashPos));
  const tagLine = encodeURIComponent(riotId.slice(hashPos + 1));
  return chargedGet(`https://${regional}.api.riotgames.com/riot/account/v1/accounts/by-riot-id/${gameName}/${tagLine}`, env, charge);
}

function handleMatches(url: URL, env: Env, charge: Charge): Promise<Response> | Response {
  const puuid = url.searchParams.get("puuid");
  const platform = url.searchParams.get("region");
  const countRaw = parseInt(url.searchParams.get("count") ?? "20", 10);
  const count = Number.isFinite(countRaw) ? Math.min(Math.max(countRaw, 1), 100) : 20;
  const queue = url.searchParams.get("queue");
  if (!puuid || !platform) return badRequest("puuid and region required");
  const regional = regionalFor(platform);
  if (!regional) return badRequest(`unknown region '${platform}'`);
  const params = new URLSearchParams();
  params.set("count", String(count));
  if (queue) params.set("queue", queue);
  return chargedGet(
    `https://${regional}.api.riotgames.com/lol/match/v5/matches/by-puuid/${encodeURIComponent(puuid)}/ids?${params.toString()}`,
    env,
    charge,
  );
}

// League-V4 ranked entries by PUUID. Platform-routed (na1, euw1, ...), unlike
// Match-V5. Powers rank auto-detection when the user links their Riot account.
function handleRank(url: URL, env: Env, charge: Charge): Promise<Response> | Response {
  const puuid = url.searchParams.get("puuid");
  const platform = url.searchParams.get("region");
  if (!puuid || !platform) return badRequest("puuid and region required");
  if (!regionalFor(platform)) return badRequest(`unknown region '${platform}'`);
  return chargedGet(
    `https://${platform.toLowerCase()}.api.riotgames.com/lol/league/v4/entries/by-puuid/${encodeURIComponent(puuid)}`,
    env,
    charge,
  );
}

/** Match detail (`suffix` "") or timeline (`suffix` "/timeline"); both immutable. */
function handleMatchResource(
  matchId: string,
  suffix: "" | "/timeline",
  url: URL,
  env: Env,
  ctx: ExecutionContext | undefined,
  charge: Charge,
): Promise<Response> | Response {
  const platform = url.searchParams.get("region");
  if (!platform) return badRequest("region required");
  const regional = regionalFor(platform);
  if (!regional) return badRequest(`unknown region '${platform}'`);
  if (!MATCH_ID.test(matchId)) return badRequest("invalid matchId shape");
  return riotGetCached(
    `https://${regional}.api.riotgames.com/lol/match/v5/matches/${encodeURIComponent(matchId)}${suffix}`,
    env,
    ctx,
    charge,
  );
}

/**
 * Route one passthrough request. `path` is the pathname with any "/web"
 * prefix removed. Unknown paths are a 404.
 */
export async function dispatchRiot(
  path: string,
  url: URL,
  env: Env,
  ctx: ExecutionContext | undefined,
  charge: Charge,
): Promise<Response> {
  if (path === "/account") return handleAccount(url, env, charge);
  if (path === "/matches") return handleMatches(url, env, charge);
  if (path === "/rank") return handleRank(url, env, charge);
  if (path.startsWith("/timeline/")) {
    return handleMatchResource(path.slice("/timeline/".length), "/timeline", url, env, ctx, charge);
  }
  if (path.startsWith("/match/")) {
    return handleMatchResource(path.slice("/match/".length), "", url, env, ctx, charge);
  }
  return jsonResponse({ error: "not_found" }, 404);
}
