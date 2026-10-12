/**
 * POST /clip-view/:id: the public view counter on shared clips.
 *
 * A view counts at most once per viewer per clip per hour. The viewer is a
 * one-way hash of their address (IPv6 by /64) and the clip id; it is held in
 * memory only (this isolate, and the rate limiter Durable Object when bound)
 * and is never written to D1. Without the dedupe, a reload loop could inflate
 * the public count without limit and turn into unbounded D1 writes.
 *
 * Counting fails CLOSED: if the limiter can't confirm a first view, the view
 * is simply not counted. The response is always 200 { ok: true }.
 */

import { Env } from "./types";
import { jsonResponse } from "./http";
import { sha256Hex } from "./crypto";
import { clientIpKey } from "./client-ip";
import { findClip, SLUG_REGEX } from "./clips";
import { takeGlobalKey } from "./riot-budget";

export const VIEW_WINDOW_MS = 60 * 60 * 1000;
const MAX_SEEN = 20_000;
const seen = new Map<string, number>(); // viewer key → until (ms)

async function viewerKey(request: Request, clipId: string): Promise<string> {
  return (await sha256Hex(`view:${clientIpKey(request)}|${clipId}`)).slice(0, 32);
}

/** True when this viewer has not been counted for this clip in the last hour. */
export async function isFirstView(request: Request, env: Env, clipId: string): Promise<boolean> {
  const key = await viewerKey(request, clipId);
  const now = Date.now();
  const until = seen.get(key);
  if (until !== undefined && until > now) return false;
  if (seen.size >= MAX_SEEN) {
    for (const [k, u] of seen) if (u <= now) seen.delete(k);
    if (seen.size >= MAX_SEEN) return false;
  }
  seen.set(key, now + VIEW_WINDOW_MS);
  // No limiter bound (tests, local dev): this isolate's memory is all there is.
  if (!env.RATE_LIMITER) return true;
  return takeGlobalKey(env, "view", key, 1, VIEW_WINDOW_MS, false);
}

export async function handleClipView(request: Request, env: Env, id: string): Promise<Response> {
  if (!SLUG_REGEX.test(id)) return jsonResponse({ ok: true }, 200);
  try {
    const clip = await findClip(env.DB, id);
    if (clip && (await isFirstView(request, env, id))) {
      await env.DB.prepare("UPDATE clips SET view_count = view_count + 1 WHERE id = ?1").bind(id).run();
    }
  } catch {
    // Views are not worth failing a request over.
  }
  return jsonResponse({ ok: true }, 200);
}

/** Test hook: forget every viewer this isolate has counted. */
export function resetClipViews(): void {
  seen.clear();
}
