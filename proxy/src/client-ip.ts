/**
 * Rate-limit keys for the caller's address.
 *
 * IPv4 addresses are used as-is. IPv6 addresses are grouped by their /64,
 * because one subscriber usually holds a whole /64 and could otherwise rotate
 * through billions of addresses to dodge a per-IP limit.
 */

import { sha256Hex } from "./crypto";

export function clientIpKey(request: Request): string {
  const raw = (request.headers.get("CF-Connecting-IP") ?? "").trim();
  if (!raw) return "unknown";
  if (!raw.includes(":")) return raw;
  return ipv6Prefix64(raw) ?? raw.toLowerCase();
}

/**
 * A short one-way hash of the caller's rate-limit key. Used wherever the key
 * leaves this isolate (the rate limiter Durable Object), so raw addresses are
 * never sent or held there.
 */
export async function hashedClientIpKey(request: Request): Promise<string> {
  return (await sha256Hex("ip:" + clientIpKey(request))).slice(0, 32);
}

/** "2001:db8:1:2:3:4:5:6" → "2001:db8:1:2::/64". Null when it isn't IPv6. */
export function ipv6Prefix64(ip: string): string | null {
  let addr = ip.split("%")[0].toLowerCase();
  // An IPv4-mapped address is really the IPv4 address.
  const mapped = /^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/.exec(addr);
  if (mapped) return mapped[1];
  if (addr.includes(".")) return null;
  const halves = addr.split("::");
  if (halves.length > 2) return null;
  const head = halves[0] ? halves[0].split(":") : [];
  const tail = halves.length === 2 && halves[1] ? halves[1].split(":") : [];
  let groups: string[];
  if (halves.length === 2) {
    const fill = 8 - head.length - tail.length;
    if (fill < 1) return null;
    groups = [...head, ...new Array<string>(fill).fill("0"), ...tail];
  } else {
    groups = head;
  }
  if (groups.length !== 8 || !groups.every((g) => /^[0-9a-f]{1,4}$/.test(g))) return null;
  addr = groups.slice(0, 4).map((g) => g.replace(/^0+(?=.)/, "")).join(":");
  return `${addr}::/64`;
}
