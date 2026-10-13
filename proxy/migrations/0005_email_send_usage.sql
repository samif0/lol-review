-- Global daily ceiling on sign-in emails (desktop /auth/login and web
-- /auth/web/email/start share it). One row per UTC day holding only a count:
-- no email addresses. The hourly cron drops rows older than 7 days.
CREATE TABLE IF NOT EXISTS email_send_usage (
    day   TEXT    PRIMARY KEY,           -- 'YYYY-MM-DD' (UTC)
    sends INTEGER NOT NULL DEFAULT 0
);
