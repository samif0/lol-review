/**
 * Real media through the HLS routes, with ffmpeg and ffprobe as the HLS
 * clients over HTTP on 127.0.0.1 (the Worker runs in-process on real SQLite
 * and the fake R2). Skipped unless these are set (local runs only, never CI):
 *
 *   HLS_MEDIA_DIR  a folder with index.m3u8, init.mp4 and its fMP4 segments, e.g.
 *                  ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=60 -f lavfi -i sine -t 40
 *                    -c:v libx264 -g 120 -keyint_min 120 -x264-params scenecut=0 -c:a aac -ac 2
 *                    -f hls -hls_time 2 -hls_segment_type fmp4 -hls_playlist_type vod
 *                    -hls_fmp4_init_filename init.mp4 index.m3u8
 *   FFMPEG, FFPROBE  the binaries
 */

import { describe, expect, it, vi } from "vitest";
import { createServer } from "node:http";
import { readFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { spawn } from "node:child_process";
import { tmpdir } from "node:os";
import worker from "../src/index";
import { makeRequest } from "./fakes";
import { endShare, hlsInit, hlsSetup, json, OWNER, play, putRun, putSeg } from "./hls-fixtures";

const MEDIA = process.env.HLS_MEDIA_DIR;
const FFMPEG = process.env.FFMPEG;
const FFPROBE = process.env.FFPROBE;

/** Async spawn: this process's HTTP server must keep answering while the tool runs. */
function run(cmd: string, args: string[], timeoutMs: number): Promise<{ status: number | null; stdout: string; stderr: string }> {
  return new Promise((resolve) => {
    const child = spawn(cmd, args);
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (d) => { stdout += d; });
    child.stderr.on("data", (d) => { stderr += d; });
    const timer = setTimeout(() => child.kill(), timeoutMs);
    child.on("close", (code) => { clearTimeout(timer); resolve({ status: code, stdout, stderr }); });
  });
}

function segmentsOf(dir: string): { file: string; ms: number }[] {
  const lines = readFileSync(join(dir, "index.m3u8"), "utf8").split(/\r?\n/);
  const out: { file: string; ms: number }[] = [];
  for (let i = 0; i < lines.length; i++) {
    const m = /^#EXTINF:([\d.]+),/.exec(lines[i]);
    if (m) out.push({ file: lines[i + 1].trim(), ms: Math.round(Number(m[1]) * 1000) });
  }
  return out;
}

describe.skipIf(!MEDIA || !FFMPEG || !FFPROBE)("real fMP4 media through the HLS routes, read by ffmpeg", () => {
  it("ffprobe reads the growing EVENT playlist; ffmpeg reads the ended one at full length, each segment once", async () => {
    vi.spyOn(console, "log").mockImplementation(() => {});
    const s = await hlsSetup();
    const segs = segmentsOf(MEDIA!);
    const total = segs.reduce((a, x) => a + x.ms, 0);
    const init = await hlsInit(s.env, OWNER, { size: 200_000_000, duration: Math.ceil(total / 1000) });
    expect(init.status).toBe(201);
    const id = (await json(init)).id as string;
    expect((await putRun(s.env, id, 0, { bytes: new Uint8Array(readFileSync(join(MEDIA!, "init.mp4"))) })).status).toBe(200);
    const put = async (seq: number) => {
      const res = await putSeg(s.env, id, seq, { bytes: new Uint8Array(readFileSync(join(MEDIA!, segs[seq].file))), ms: segs[seq].ms });
      expect(res.status, `segment ${seq}`).toBe(200);
    };
    // Out of order, the way two requests in flight can finish: 1, 0, 3, 2, ...
    const order: number[] = [];
    for (let b = 0; b < segs.length; b += 2) for (const k of [1, 0]) if (b + k < segs.length) order.push(b + k);
    const half = Math.max(5, Math.floor(order.length / 2));
    for (const seq of order.slice(0, half)) await put(seq);

    const served: string[] = [];
    const server = createServer(async (req, res) => {
      served.push(req.url ?? "");
      const r = await worker.fetch(makeRequest(`https://clips.revu.lol${req.url}`, { method: req.method }), s.env);
      res.writeHead(r.status, Object.fromEntries(r.headers.entries()));
      res.end(Buffer.from(await r.arrayBuffer()));
    });
    await new Promise<void>((ok) => server.listen(0, "127.0.0.1", () => ok()));
    const port = (server.address() as { port: number }).port;
    try {
      const token = (await json(await play(s.env, id))).token as string;
      const playlist = `http://127.0.0.1:${port}/vod-hls/${id}/${token}/index.m3u8`;
      const live = await run(FFPROBE!, ["-v", "error", "-show_entries", "stream=codec_name", "-of", "csv=p=0", playlist], 60_000);
      expect(live.stdout).toContain("h264");
      expect(live.stdout).toContain("aac");

      for (const seq of order.slice(half)) await put(seq);
      expect((await endShare(s.env, id, segs.length)).status).toBe(201);
      await new Promise((ok) => setTimeout(ok, 1100)); // past the 1 s playlist cache
      served.length = 0;
      const out = join(tmpdir(), `revu-hls-e2e-${id}.mp4`);
      const remux = await run(FFMPEG!, ["-v", "error", "-y", "-i", playlist, "-c", "copy", out], 180_000);
      expect(remux.status, remux.stderr).toBe(0);
      const probe = await run(FFPROBE!, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", out], 60_000);
      expect(Math.abs(Number(probe.stdout.trim()) - total / 1000)).toBeLessThan(0.2);
      const segmentGets = served.filter((u) => u.endsWith(".m4s"));
      expect(new Set(segmentGets).size).toBe(segs.length);
      expect(segmentGets).toHaveLength(segs.length);
      rmSync(out, { force: true });
    } finally {
      server.close();
    }
  }, 300_000);
});
