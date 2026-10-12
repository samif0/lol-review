/**
 * Cookie-authenticated web account API for revu.lol: /auth/web/*.
 *
 *   GET    /auth/web/providers         which sign-in methods are available
 *   POST   /auth/web/email/start       send a sign-in code (Turnstile)
 *   POST   /auth/web/email/verify      redeem it, set the session cookie
 *   GET    /auth/web/me                account, identities, session expiry
 *   GET    /auth/web/clips             the caller's shared clips
 *   DELETE /auth/web/clips/:id         delete one of them
 *   POST   /auth/web/logout            end this browser's web session
 *   POST   /auth/web/logout-all        end every web session (desktop stays)
 *   POST   /auth/web/account/delete    delete the account and everything in it
 *
 * The session cookie is read ONLY here. The desktop's Bearer auth (authOrDeny)
 * never accepts it, so no other route can be cookie-ridden.
 */

import {
  deleteAccountRows,
  deleteWebSession,
  deleteWebSessionsForUser,
  findUserById,
  findWebSession,
  IdentityRow,
  listClipObjectsForUser,
  listIdentities,
  UserRow,
} from "./db";
import { handleDeleteClip, handleListMyClips, SLUG_REGEX } from "./clips";
import { Env } from "./types";
import {
  gateWebRequest,
  hasWebCookie,
  readJsonObject,
  readWebCookie,
  WEB_CLEAR_COOKIE,
  webJson,
  webLimiters,
  webRateLimited,
  webTokenHash,
} from "./web-http";
import { handleEmailStart, handleEmailVerify, handleProviders } from "./web-email";

const PREFIX = "/auth/web/";
const MAX_UPLOAD_ABORTS = 20;
const R2_DELETE_CHUNK = 1000;

interface WebUser {
  user: UserRow;
  expiresAt: number;
}

type Handler = (request: Request, env: Env, param: string | null) => Promise<Response>;

interface Route {
  method: string;
  handler: Handler;
  /** start and verify have their own, tighter limiters. */
  general: boolean;
}

const ROUTES = new Map<string, Route>([
  ["providers", { method: "GET", handler: async (r, e) => handleProviders(r, e), general: true }],
  ["email/start", { method: "POST", handler: (r, e) => handleEmailStart(r, e), general: false }],
  ["email/verify", { method: "POST", handler: (r, e) => handleEmailVerify(r, e), general: false }],
  ["me", { method: "GET", handler: (r, e) => handleMe(r, e), general: true }],
  ["clips", { method: "GET", handler: (r, e) => handleClips(r, e), general: true }],
  ["logout", { method: "POST", handler: (r, e) => handleLogoutWeb(r, e), general: true }],
  ["logout-all", { method: "POST", handler: (r, e) => handleLogoutAll(r, e), general: true }],
  ["account/delete", { method: "POST", handler: (r, e) => handleAccountDelete(r, e), general: true }],
]);

const CLIP_DELETE: Route = {
  method: "DELETE",
  handler: (r, e, id) => handleClipDelete(r, e, id ?? ""),
  general: true,
};

export async function dispatchWebAccount(request: Request, url: URL, env: Env): Promise<Response | null> {
  if (!url.pathname.startsWith(PREFIX)) return null;

  const gated = gateWebRequest(request, url, env);
  if (gated) return gated;

  const sub = url.pathname.slice(PREFIX.length);
  let route = ROUTES.get(sub);
  let param: string | null = null;
  if (!route && sub.startsWith("clips/") && sub.indexOf("/", "clips/".length) === -1) {
    route = CLIP_DELETE;
    param = sub.slice("clips/".length);
  }
  if (!route) return webJson({ error: "not_found" }, 404, request, env);
  if (request.method !== route.method) {
    return webJson({ error: "method_not_allowed" }, 405, request, env);
  }
  if (route.general && !webLimiters.general.take(request)) return webRateLimited(request, env);
  return route.handler(request, env, param);
}

// ── Session resolution ──────────────────────────────────────────────────

function signedOut(request: Request, env: Env, clearCookie: boolean): Response {
  return webJson({ error: "signed_out" }, 401, request, env, clearCookie ? { setCookie: WEB_CLEAR_COOKIE } : {});
}

async function requireWebUser(request: Request, env: Env): Promise<WebUser | Response> {
  const token = readWebCookie(request);
  // A malformed cookie is still one the browser holds, so it gets cleared too.
  if (!token) return signedOut(request, env, hasWebCookie(request));
  const session = await findWebSession(env.DB, await webTokenHash(token));
  if (!session) return signedOut(request, env, true);
  const user = await findUserById(env.DB, session.user_id);
  if (!user) return signedOut(request, env, true);
  return { user, expiresAt: session.expires_at };
}

// ── Routes ──────────────────────────────────────────────────────────────

async function handleMe(request: Request, env: Env): Promise<Response> {
  const auth = await requireWebUser(request, env);
  if (auth instanceof Response) return auth;
  const { user } = auth;

  let identities: IdentityRow[] = [];
  try {
    identities = await listIdentities(env.DB, user.id);
  } catch (err) {
    console.error(JSON.stringify({ scope: "web.identities", error: (err as Error).message }));
  }
  const shaped = identities.length > 0
    ? identities.map((i) => ({ provider: i.provider, email: i.email, created_at: i.created_at, last_used_at: i.last_used_at }))
    : [{ provider: "email", email: user.email, created_at: user.created_at, last_used_at: null }];

  return webJson(
    {
      user: { email: user.email, created_at: user.created_at },
      identities: shaped,
      session: { expires_at: auth.expiresAt },
    },
    200,
    request,
    env,
  );
}

/** Re-emit a desktop clip handler's JSON through the web headers. */
async function reemit(res: Response, request: Request, env: Env): Promise<Response> {
  const body = (await res.json()) as Record<string, unknown>;
  return webJson(body, res.status, request, env);
}

async function handleClips(request: Request, env: Env): Promise<Response> {
  const auth = await requireWebUser(request, env);
  if (auth instanceof Response) return auth;
  return reemit(await handleListMyClips(env, auth.user.id), request, env);
}

async function handleClipDelete(request: Request, env: Env, id: string): Promise<Response> {
  const auth = await requireWebUser(request, env);
  if (auth instanceof Response) return auth;
  if (!SLUG_REGEX.test(id)) return webJson({ error: "not_found" }, 404, request, env);
  const res = await handleDeleteClip(env, id, auth.user.id);
  // Another user's clip is indistinguishable from a missing one.
  if (res.status === 403) return webJson({ error: "not_found" }, 404, request, env);
  return reemit(res, request, env);
}

async function handleLogoutWeb(request: Request, env: Env): Promise<Response> {
  const token = readWebCookie(request);
  if (token) await deleteWebSession(env.DB, await webTokenHash(token));
  return webJson({ ok: true }, 200, request, env, { setCookie: WEB_CLEAR_COOKIE });
}

async function handleLogoutAll(request: Request, env: Env): Promise<Response> {
  const auth = await requireWebUser(request, env);
  if (auth instanceof Response) return auth;
  await deleteWebSessionsForUser(env.DB, auth.user.id);
  return webJson({ ok: true }, 200, request, env, { setCookie: WEB_CLEAR_COOKIE });
}

async function handleAccountDelete(request: Request, env: Env): Promise<Response> {
  const auth = await requireWebUser(request, env);
  if (auth instanceof Response) return auth;
  const { user } = auth;

  const body = await readJsonObject(request, env);
  if (body instanceof Response) return body;
  const typed = typeof body.email === "string" ? body.email.trim().toLowerCase() : "";
  if (typed !== user.email.toLowerCase()) {
    return webJson({ error: "confirm_mismatch" }, 400, request, env);
  }

  // R2 first: cascades never reach object storage. Best effort throughout;
  // the D1 rows are the source of truth and go regardless.
  const objects = await listClipObjectsForUser(env.DB, user.id);
  for (const obj of objects.filter((o) => o.upload_id).slice(0, MAX_UPLOAD_ABORTS)) {
    try {
      await env.CLIPS.resumeMultipartUpload(obj.r2_key, obj.upload_id as string).abort();
    } catch {
      // Already completed, aborted or unknown. The key delete below covers it.
    }
  }
  const keys = objects.map((o) => o.r2_key);
  for (let i = 0; i < keys.length; i += R2_DELETE_CHUNK) {
    try {
      await env.CLIPS.delete(keys.slice(i, i + R2_DELETE_CHUNK));
    } catch (err) {
      console.error(JSON.stringify({ scope: "web.account_delete.r2", user_id: user.id, error: (err as Error).message }));
    }
  }

  await deleteAccountRows(env.DB, user.id, user.email);
  console.log(JSON.stringify({ scope: "web.account_delete", user_id: user.id, clips: objects.length }));
  return webJson({ ok: true }, 200, request, env, { setCookie: WEB_CLEAR_COOKIE });
}
