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

  // Bindings
  DB: D1Database;
}
