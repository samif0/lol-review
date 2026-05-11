import { beforeEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { Env } from "../src/types";

function env(token: string): Env {
  return {
    RIOT_API_KEY: "riot-key",
    ALLOWED_TOKENS: token,
    RESEND_API_KEY: "",
    AGGREGATE_RPS: "100",
    PER_TOKEN_RPS: "20",
    MAGIC_LINK_FROM: "noreply@example.com",
    APP_NAME: "Revu",
    DB: {} as D1Database,
  };
}

async function json(response: Response): Promise<Record<string, unknown>> {
  return await response.json() as Record<string, unknown>;
}

describe("worker proxy", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("rejects proxied requests without bearer auth", async () => {
    const response = await worker.fetch(
      new Request("https://proxy.example/match/NA1_123?region=na1"),
      env("static-token"),
    );

    expect(response.status).toBe(401);
    expect(await json(response)).toEqual({ error: "missing_bearer" });
  });

  it("validates match id shape before calling Riot", async () => {
    const token = "shape-token";
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);

    const response = await worker.fetch(
      new Request("https://proxy.example/match/not-a-match-id?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      env(token),
    );

    expect(response.status).toBe(400);
    expect(await json(response)).toEqual({
      error: "bad_request",
      message: "invalid matchId shape",
    });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("maps platform region to Riot regional route", async () => {
    const token = "region-token";
    const fetchMock = vi.fn().mockResolvedValue(
      new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const response = await worker.fetch(
      new Request("https://proxy.example/match/NA1_123456?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      env(token),
    );

    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledWith(
      "https://americas.api.riotgames.com/lol/match/v5/matches/NA1_123456",
      { headers: { "X-Riot-Token": "riot-key" } },
    );
  });

  it("applies per-token rate limits offline", async () => {
    const token = "rate-token";
    const limitedEnv = { ...env(token), PER_TOKEN_RPS: "1" };

    const first = await worker.fetch(
      new Request("https://proxy.example/match/bad?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      limitedEnv,
    );
    const second = await worker.fetch(
      new Request("https://proxy.example/match/bad?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      limitedEnv,
    );

    expect(first.status).toBe(400);
    expect(second.status).toBe(429);
    expect(await json(second)).toEqual({ error: "rate_limit_per_token" });
  });
});
