/**
 * Operator switches in D1 (`ops_flags`, migration 0007), for a kill switch
 * that takes effect without a deploy:
 *
 *   vod_serving = '0'   every public VOD route stops serving (503)
 *   vod_hls     = '0'   no new HLS share starts (409 hls_unavailable)
 *
 * No row (or any other value) means the wrangler.toml var decides. The table
 * is read in one statement and cached per isolate for OPS_FLAGS_TTL_MS, so a
 * change reaches every isolate within 30 seconds and a busy route costs at most
 * one D1 read per isolate per 30 seconds. A failed read keeps the last good
 * flags (none before the first) and is retried after OPS_FLAGS_RETRY_MS: a D1
 * hiccup must not turn every playback into a 503.
 */

import { Env } from "./types";

export const OPS_FLAGS_TTL_MS = 30_000;
export const OPS_FLAGS_RETRY_MS = 5_000;

let cached: { flags: Map<string, string>; until: number } | null = null;

export async function opsFlags(env: Env): Promise<Map<string, string>> {
  const now = Date.now();
  if (cached && now < cached.until) return cached.flags;
  try {
    const res = await env.DB.prepare("SELECT name, value FROM ops_flags").all<{ name: string; value: string }>();
    const flags = new Map<string, string>();
    for (const row of res.results ?? []) flags.set(String(row.name), String(row.value));
    cached = { flags, until: now + OPS_FLAGS_TTL_MS };
  } catch (err) {
    console.error(JSON.stringify({ scope: "ops_flags.read", error: (err as Error)?.message ?? String(err) }));
    cached = { flags: cached?.flags ?? new Map(), until: now + OPS_FLAGS_RETRY_MS };
  }
  return cached.flags;
}

/** False only when the flag's row says "0" or "off". */
export async function opsFlagOn(env: Env, name: string): Promise<boolean> {
  const value = (await opsFlags(env)).get(name);
  if (value === undefined) return true;
  const v = value.trim().toLowerCase();
  return v !== "0" && v !== "off";
}

/** Test hook: forget the cached flags. */
export function resetOpsFlags(): void {
  cached = null;
}
