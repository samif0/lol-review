/**
 * Markers document v1: validation and normalization (pure).
 */

import { describe, expect, it } from "vitest";
import { cleanText, normalizeVodMeta, vodTitle } from "../src/vod-markers";

const base = { version: 1, gameTimeAtVideoStart: 0, game: {}, markers: [], notes: null };
const run = (doc: unknown) => normalizeVodMeta(typeof doc === "string" ? doc : JSON.stringify(doc));

function ok(doc: unknown) {
  const r = run(doc);
  if (!r.ok) throw new Error(`expected ok, got ${r.error}: ${r.message}`);
  return r;
}

function fails(doc: unknown) {
  const r = run(doc);
  if (r.ok) throw new Error("expected a refusal");
  return r;
}

describe("normalizeVodMeta", () => {
  it("normalizes a full document: rounding, sorting, label fallback, unknown keys dropped", () => {
    const r = ok({
      version: 1,
      gameTimeAtVideoStart: -12.3456,
      game: {
        champion: "  Kai'Sa\u0007 ", opponentChampion: "", role: "utility", result: "remake", durationS: 1834, playedAt: "2026-10-12",
        riotId: "Someone#NA1", summoner: "x",
      },
      markers: [
        { t: 600.04, kind: "BARON", label: "", tone: "loud", summary: "private" },
        { t: 12.06, kind: "KILL", label: "Kill\u0000\u001f\u007f\u0085 on mid", tone: "win" },
        { t: 12.06, kind: "DEATH", label: "Death", tone: "loss" },
      ],
      notes: [
        { t: 700, end: 690, kind: "clip", text: "end before t is dropped" },
        { t: 30, end: 45.04, kind: "bookmark", text: "  \tlook at the wave\n ", extra: 1 },
      ],
      encounterNote: "never stored",
    });
    expect(r.doc).toEqual({
      version: 1,
      gameTimeAtVideoStart: -12.35,
      game: { champion: "Kai'Sa", role: "UTILITY", result: "remake", durationS: 1834, playedAt: "2026-10-12" },
      markers: [
        { t: 12.1, kind: "KILL", label: "Kill on mid", tone: "win" },
        { t: 12.1, kind: "DEATH", label: "Death", tone: "loss" },
        { t: 600, kind: "BARON", label: "BARON" },
      ],
      notes: [
        { t: 30, end: 45, kind: "bookmark", text: "look at the wave" },
        { t: 700, kind: "clip", text: "end before t is dropped" },
      ],
    });
    expect(r.hasNotes).toBe(true);
    expect(Object.keys(r.doc)).toEqual(["version", "gameTimeAtVideoStart", "game", "markers", "notes"]);
  });

  it("defaults missing game, markers and notes, and drops invalid game fields", () => {
    const r = ok({ version: 1, gameTimeAtVideoStart: 0 });
    expect(r.doc).toEqual({ version: 1, gameTimeAtVideoStart: 0, game: {}, markers: [], notes: null });
    expect(r.hasNotes).toBe(false);
    const g = ok({
      ...base,
      game: { champion: 5, role: "TOP LANE", result: "draw", durationS: 10801, playedAt: "12/10/2026", opponentChampion: "x".repeat(60) },
    });
    expect(g.doc.game).toEqual({ opponentChampion: "x".repeat(40) });
    expect(ok({ ...base, notes: [] }).hasNotes).toBe(false);
    expect(ok({ ...base, gameTimeAtVideoStart: -0.001 }).doc.gameTimeAtVideoStart).toBe(0);
  });

  it("refuses a bad version, video start or container", () => {
    for (const [doc, message] of [
      ["not json", "body must be JSON"],
      [[1, 2], "body must be a JSON object"],
      [{ ...base, version: 2 }, "version must be 1"],
      [{ ...base, version: "1" }, "version must be 1"],
      [{ ...base, gameTimeAtVideoStart: -600.5 }, "gameTimeAtVideoStart must be a number from -600 to 7200"],
      [{ ...base, gameTimeAtVideoStart: 7201 }, "gameTimeAtVideoStart must be a number from -600 to 7200"],
      [{ version: 1 }, "gameTimeAtVideoStart must be a number from -600 to 7200"],
      [{ ...base, game: "Jinx" }, "game must be an object"],
      [{ ...base, markers: {} }, "markers must be an array"],
      [{ ...base, notes: "x" }, "notes must be null or an array"],
    ] as Array<[unknown, string]>) {
      const r = fails(doc);
      expect(r).toEqual({ ok: false, status: 400, error: "bad_markers", message });
    }
  });

  it("refuses a bad marker t or kind and names the field", () => {
    for (const [marker, message] of [
      [{ t: "1", kind: "KILL" }, "markers[0].t must be a number from 0 to 10800"],
      [{ t: 10800.1, kind: "KILL" }, "markers[0].t must be a number from 0 to 10800"],
      [{ kind: "KILL" }, "markers[0].t must be a number from 0 to 10800"],
      [{ t: 1, kind: "kill" }, "markers[0].kind must match [A-Z0-9_]{1,32}"],
      [{ t: 1, kind: "K".repeat(33) }, "markers[0].kind must match [A-Z0-9_]{1,32}"],
      [{ t: 1 }, "markers[0].kind must match [A-Z0-9_]{1,32}"],
      ["KILL", "markers[0] must be an object"],
    ] as Array<[unknown, string]>) {
      expect(fails({ ...base, markers: [marker] }).message).toBe(message);
    }
    expect(fails({ ...base, markers: [{ t: 1, kind: "A" }, { t: 2, kind: "B-" }] }).message).toBe("markers[1].kind must match [A-Z0-9_]{1,32}");
  });

  it("refuses bad notes and caps list lengths", () => {
    expect(fails({ ...base, notes: [{ t: 1, kind: "note" }] }).message).toBe("notes[0].kind must be clip or bookmark");
    expect(fails({ ...base, notes: [{ t: -1, kind: "clip" }] }).message).toBe("notes[0].t must be a number from 0 to 10800");
    const tooManyMarkers = Array.from({ length: 1001 }, (_, i) => ({ t: i, kind: "K" }));
    expect(fails({ ...base, markers: tooManyMarkers }).message).toBe("markers may hold at most 1000 entries");
    const tooManyNotes = Array.from({ length: 201 }, (_, i) => ({ t: i, kind: "bookmark", text: "" }));
    expect(fails({ ...base, notes: tooManyNotes }).message).toBe("notes may hold at most 200 entries");
    expect(ok({ ...base, notes: [{ t: 3, kind: "clip", text: 7 }] }).doc.notes).toEqual([{ t: 3, kind: "clip", text: "" }]);
  });

  it("413s oversized markers and notes after normalization", () => {
    const markers = Array.from({ length: 1000 }, (_, i) => ({ t: i, kind: "OBJECTIVE_TAKEN", label: "L".repeat(40) }));
    expect(fails({ ...base, markers })).toEqual({
      ok: false, status: 413, error: "markers_too_large", message: "markers exceed 49152 bytes",
    });
    // Labels over 40 chars are cut first, so long input labels alone never overflow.
    const cut = Array.from({ length: 400 }, (_, i) => ({ t: i, kind: "K", label: "L".repeat(500) }));
    expect(ok({ ...base, markers: cut }).doc.markers[0].label).toHaveLength(40);
    const notes = Array.from({ length: 40 }, (_, i) => ({ t: i, kind: "clip", text: "n".repeat(500) }));
    expect(fails({ ...base, notes })).toEqual({ ok: false, status: 413, error: "notes_too_large", message: "notes exceed 14336 bytes" });
  });
});

describe("helpers", () => {
  it("cleanText strips control characters, trims and cuts by code point", () => {
    expect(cleanText(" a\u0000b\u009fc ", 40)).toBe("abc");
    expect(cleanText("😀😀😀", 2)).toBe("😀😀");
    expect(cleanText("ab   cd", 3)).toBe("ab");
  });

  it("vodTitle names the matchup, the champion, or nothing", () => {
    expect(vodTitle({ champion: "Jinx", opponentChampion: "Caitlyn" })).toBe("Jinx vs Caitlyn");
    expect(vodTitle({ champion: "Jinx" })).toBe("Jinx game");
    expect(vodTitle({ opponentChampion: "Caitlyn" })).toBeNull();
    expect(vodTitle({})).toBeNull();
  });
});
