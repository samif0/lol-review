/**
 * The hourly scheduled run (wrangler.toml [triggers]), in order: settle quiet
 * HLS shares (idle, paused too long or past their upload window: ended at their
 * edge when watchable, else discarded), purge expired clips, VOD shares and
 * expired pending uploads (R2 + D1), retry storage tombstones, stale sessions,
 * expired sign-in codes (with their email addresses), old transcribe_usage
 * rows, old email send counts, old VOD share start and part upload counts,
 * then discard at most IDLE_SWEEP_PER_RUN idle pending uploads of every
 * account (their reservations stop holding the storage pool). The sweep runs
 * last and small so a backlog of idle uploads can't use up the run's D1 query
 * budget before the purge. Each step is isolated so one failure doesn't skip
 * the rest. HLS key deletes share one budget of R2 calls per run (settle,
 * purge and tombstones).
 */

import { Env } from "./types";
import { deleteExpiredLoginRequests, deleteExpiredSessions } from "./db";
import { hlsRunBudget, purgeExpiredClips, purgeTombstones } from "./clip-purge";
import { settleHlsShares } from "./vod-hls-end";
import { pruneTranscribeUsage } from "./transcripts";
import { pruneEmailSendUsage } from "./email-budget";
import { pruneUploadPartUsage, pruneVodShareUsage } from "./vod-shares";
import { sweepIdleUploads } from "./upload-cleanup";
import { IDLE_SWEEP_PER_RUN } from "./share-limits";

async function step(scope: string, work: () => Promise<void>): Promise<void> {
  try {
    await work();
  } catch (err) {
    console.error(JSON.stringify({ scope, error: (err as Error).message }));
  }
}

export async function runHourly(env: Env): Promise<void> {
  const hlsCalls = hlsRunBudget();
  await step("cron.hls_settle", async () => {
    const settled = await settleHlsShares(env, hlsCalls);
    if (settled.ended + settled.discarded > 0) console.log(JSON.stringify({ scope: "cron.hls_settle", ...settled }));
  });
  await step("cron.purge", async () => {
    const purged = await purgeExpiredClips(env, hlsCalls);
    console.log(JSON.stringify({ scope: "cron.purge", clips: purged }));
  });
  await step("cron.tombstones", async () => {
    const tombs = await purgeTombstones(env, hlsCalls);
    if (tombs > 0) console.log(JSON.stringify({ scope: "cron.tombstones", purged: tombs }));
  });
  await step("cron.sessions", () => deleteExpiredSessions(env.DB));
  await step("cron.login_requests", () => deleteExpiredLoginRequests(env.DB));
  await step("cron.transcribe_usage", async () => { await pruneTranscribeUsage(env); });
  await step("cron.email_send_usage", async () => { await pruneEmailSendUsage(env); });
  await step("cron.vod_share_usage", async () => { await pruneVodShareUsage(env); });
  await step("cron.upload_part_usage", async () => { await pruneUploadPartUsage(env); });
  await step("cron.idle_uploads", async () => {
    const swept = await sweepIdleUploads(env, Math.floor(Date.now() / 1000), IDLE_SWEEP_PER_RUN);
    if (swept > 0) console.log(JSON.stringify({ scope: "cron.idle_uploads", discarded: swept }));
  });
}
