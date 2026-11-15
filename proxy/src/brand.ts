// Generated from brand.json by scripts/rebrand.mjs. Do not edit by hand: change brand.json, then run
// `node scripts/rebrand.mjs --sync`. CI fails while this file and brand.json disagree.
// Defaults for the vars of the same name in wrangler.toml, which a deployed Worker reads first.
import type { Env } from "./types";

export const APP_NAME = "Revu";
export const SITE_HOST = "revu.lol";
const SITE_URL = "https://revu.lol";
const API_URL = "https://clips.revu.lol";

const origin = (value: string): string => value.replace(/\/+$/, "");

/** The product name shown in emails and pages. */
export const appName = (env: Pick<Env, "APP_NAME">): string => env.APP_NAME || APP_NAME;
/** Origin of the static site that serves the watch pages. No trailing slash. */
export const watchBase = (env: Pick<Env, "WATCH_BASE">): string => origin(env.WATCH_BASE || SITE_URL);
/** Origin shareable clip links are built on. No trailing slash. */
export const publicBase = (env: Pick<Env, "PUBLIC_BASE">): string => origin(env.PUBLIC_BASE || API_URL);
/** The one origin the cookie-authenticated web account API answers on. */
export const authBase = (env: Pick<Env, "AUTH_BASE">): string => env.AUTH_BASE || API_URL;
/** The site host as people type it, for copy such as "enter this code on …". */
export const siteHost = (env: Pick<Env, "WATCH_BASE">): string => {
  try { return new URL(watchBase(env)).host; } catch { return new URL(SITE_URL).host; }
};
