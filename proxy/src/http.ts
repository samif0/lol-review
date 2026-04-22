/**
 * Small response helpers.
 */

export function jsonResponse(
  body: unknown,
  status: number,
  extraHeaders: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...extraHeaders },
  });
}

export function badRequest(message: string): Response {
  return jsonResponse({ error: "bad_request", message }, 400);
}
