/**
 * Small crypto helpers. Workers expose Web Crypto (`crypto.subtle`, `crypto.getRandomValues`).
 */

export async function sha256Hex(input: string): Promise<string> {
  const data = new TextEncoder().encode(input);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

export async function sha256Prefix(input: string, hexChars: number): Promise<string> {
  const full = await sha256Hex(input);
  return full.slice(0, hexChars);
}

/** 48-char hex string — 24 random bytes. Session tokens. */
export function randomSessionToken(): string {
  const bytes = new Uint8Array(24);
  crypto.getRandomValues(bytes);
  return Array.from(bytes)
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

/**
 * Short one-time code sent via email. Avoids confusables (0/O, 1/l/I).
 * 8 chars from the alphabet below is ~39 bits — plenty for a 10-min TTL
 * with rate limits.
 */
export function randomOtpCode(length = 8): string {
  const alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
  // Rejection sampling: 256 is not a multiple of 31, so a plain `b % 31` would
  // favor the first few characters. Only bytes below 248 (8 * 31) are used.
  const limit = Math.floor(256 / alphabet.length) * alphabet.length;
  let out = "";
  while (out.length < length) {
    const bytes = new Uint8Array(length * 2);
    crypto.getRandomValues(bytes);
    for (let i = 0; i < bytes.length && out.length < length; i++) {
      if (bytes[i] < limit) out += alphabet[bytes[i] % alphabet.length];
    }
  }
  return out;
}
