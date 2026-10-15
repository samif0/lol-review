/**
 * Play tokens for HLS shares.
 *
 * POST /vod-play/:id counts one play and mints `<exp>.<sig>`: exp is unix
 * seconds (now + PLAY_TOKEN_TTL_SECONDS), sig is the first 22 characters of
 * base64url(HMAC-SHA256(VOD_PLAY_SECRET, `${id}.${exp}`)). The token goes in
 * the media URLs' path (/vod-hls/<id>/<token>/...), so the playlist's relative
 * init and segment URIs inherit it. Checking one costs CPU only (no D1 or R2),
 * which is why every media route checks it first.
 */

import { Env } from "./types";

export const PLAY_TOKEN_TTL_SECONDS = 43_200;
export const PLAY_SECRET_MIN_CHARS = 32;
const SIG_CHARS = 22;
const TOKEN_SHAPE = /^(\d{10})\.([A-Za-z0-9_-]{22})$/;

let keyCache: { secret: string; key: Promise<CryptoKey> } | null = null;

/** True when VOD_PLAY_SECRET is set and long enough to sign with. */
export function playSecretOk(env: Env): boolean {
  return typeof env.VOD_PLAY_SECRET === "string" && env.VOD_PLAY_SECRET.length >= PLAY_SECRET_MIN_CHARS;
}

/** The imported HMAC key, once per isolate and secret. */
function hmacKey(secret: string): Promise<CryptoKey> {
  if (!keyCache || keyCache.secret !== secret) {
    const key = crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
    keyCache = { secret, key };
  }
  return keyCache.key;
}

function base64url(bytes: Uint8Array): string {
  let bin = "";
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

async function signature(secret: string, id: string, exp: number): Promise<string> {
  const mac = await crypto.subtle.sign("HMAC", await hmacKey(secret), new TextEncoder().encode(`${id}.${exp}`));
  return base64url(new Uint8Array(mac)).slice(0, SIG_CHARS);
}

/** Compares two equal-length strings without an early exit. */
function sameChars(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

/** A fresh token for share `id`. The caller has checked playSecretOk. */
export async function mintPlayToken(env: Env, id: string, now: number): Promise<{ token: string; expires_at: number }> {
  const exp = now + PLAY_TOKEN_TTL_SECONDS;
  return { token: `${exp}.${await signature(env.VOD_PLAY_SECRET as string, id, exp)}`, expires_at: exp };
}

/**
 * True when `token` was minted for `id` by this secret and has not expired.
 * False for any other shape, a bad signature, an expired one, or no secret.
 */
export async function verifyPlayToken(env: Env, id: string, token: string, now: number): Promise<boolean> {
  if (!playSecretOk(env)) return false;
  const m = TOKEN_SHAPE.exec(token);
  if (!m) return false;
  const exp = Number(m[1]);
  if (exp <= now || exp > now + PLAY_TOKEN_TTL_SECONDS + 60) return false;
  return sameChars(await signature(env.VOD_PLAY_SECRET as string, id, exp), m[2]);
}
