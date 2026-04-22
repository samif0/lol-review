export interface Env {
  // Secrets
  RIOT_API_KEY: string;
  ALLOWED_TOKENS: string;
  RESEND_API_KEY: string;

  // Vars
  AGGREGATE_RPS: string;
  PER_TOKEN_RPS: string;
  MAGIC_LINK_FROM: string;
  APP_NAME: string;

  // Bindings
  DB: D1Database;
}
