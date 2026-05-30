export interface Env {
  // Secrets
  RIOT_API_KEY: string;
  ALLOWED_TOKENS: string;
  RESEND_API_KEY: string;
  TURNSTILE_SECRET_KEY?: string;
  WEB_JWT_SECRET?: string;

  // Vars
  AGGREGATE_RPS: string;
  PER_TOKEN_RPS: string;
  MAGIC_LINK_FROM: string;
  APP_NAME: string;
  ALLOWED_ORIGINS?: string;
  // Base used to build returned clip links (e.g. https://clips.revu.lol).
  PUBLIC_BASE?: string;
  // Base of the static site that serves clip.html (e.g. https://revu.lol).
  WATCH_BASE?: string;

  // Bindings
  DB: D1Database;
  CLIPS: R2Bucket;
}
