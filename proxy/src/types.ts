export interface Env {
  // Secrets
  RIOT_API_KEY: string;
  RESEND_API_KEY: string;
  TURNSTILE_SECRET_KEY?: string;
  WEB_JWT_SECRET?: string;
  // Optional. When set, sign-in codes are stored as HMAC-SHA256(pepper, code)
  // instead of a plain sha256, so a D1 leak alone can't brute-force live codes.
  // Codes issued before it is first set stop verifying (they live 10 minutes).
  LOGIN_CODE_PEPPER?: string;

  // Vars
  // Riot budget (src/riot-budget.ts). AGGREGATE_RPS is the hard cap for
  // everyone; WEB_AGGREGATE_RPS (default a third of it) is the anonymous web
  // tool's share; WEB_PER_IP_RPS (default 3) caps one address across all of
  // its web tokens; WEB_SESSIONS_PER_IP (default 12) caps web tokens minted
  // per address per 30 minutes.
  AGGREGATE_RPS: string;
  PER_TOKEN_RPS: string;
  WEB_AGGREGATE_RPS?: string;
  WEB_PER_IP_RPS?: string;
  WEB_SESSIONS_PER_IP?: string;
  // Sign-in emails sent per UTC day across all users (default 100). Above it,
  // /auth/login and /auth/web/email/start answer 503 email_daily_cap.
  EMAIL_DAILY_SEND_CAP?: string;
  // "1" makes desktop /auth/verify require { email, code } (bound, attempt
  // capped). Leave unset until desktop builds that send the email are out.
  REQUIRE_VERIFY_EMAIL?: string;
  MAGIC_LINK_FROM: string;
  APP_NAME: string;
  ALLOWED_ORIGINS?: string;
  // Base used to build returned clip links (e.g. https://clips.revu.lol).
  PUBLIC_BASE?: string;
  // Base of the static site that serves clip.html (e.g. https://revu.lol).
  WATCH_BASE?: string;
  // Origin that serves the cookie-authenticated /auth/web/* account API (e.g.
  // https://clips.revu.lol). Those routes 404 on every other host, so the
  // session cookie is never set on workers.dev. Defaults to https://clips.revu.lol.
  AUTH_BASE?: string;
  // Comma-separated exact Origins allowed to call /auth/web/* with credentials.
  // Separate from ALLOWED_ORIGINS, which includes localhost in prod.
  CREDENTIALED_ORIGINS?: string;
  // Comma-separated hostnames a web sign-in Turnstile token must come from.
  // Empty (without dev relax) means web sign-in is unavailable (fail closed).
  TURNSTILE_HOSTNAMES?: string;
  // "1" skips the Turnstile action and hostname checks, but ONLY while
  // AUTH_BASE starts with http://localhost: (local dev via .dev.vars).
  TURNSTILE_DEV_RELAX?: string;

  // Bindings
  DB: D1Database;
  CLIPS: R2Bucket;
  // Durable Object namespace backing the GlobalRateLimiter — a single
  // ("global") instance enforces the TRUE aggregate RPS cap across all
  // isolates (see src/ratelimit.ts). Optional so test envs that don't exercise
  // the limiter (and the per-isolate fail-open path) can omit it.
  RATE_LIMITER?: DurableObjectNamespace;
  // Workers AI binding ([ai] in wrangler.toml). Powers POST /transcribe
  // (Whisper). Optional so test envs and an unbound deployment degrade to a
  // clean 503 transcribe_unavailable instead of crashing.
  AI?: Ai;
}
