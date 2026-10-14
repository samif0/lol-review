/**
 * A real-SQLite D1 for the full-game (VOD) share tests: node:sqlite in memory
 * with every migration in proxy/migrations applied in order (0001 to 0006), so
 * the new SQL (UNION ALL budget, UPSERT with a guard, RETURNING claims, play
 * cap) runs against real SQLite semantics instead of a substring fake. It also
 * proves migration 0006 applies on top of 0001 to 0005.
 *
 * D1 behaviors modelled: numbered parameters (?1..?N, rewritten to named ones
 * because node:sqlite treats "?N" as named), undefined is not bindable, foreign
 * keys are enforced, at most 100 bound parameters, first() / all() / run(), and
 * batch() is one transaction.
 */

import { createRequire } from "node:module";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const MIGRATIONS_DIR = resolve(dirname(fileURLToPath(import.meta.url)), "../migrations");

/* eslint-disable @typescript-eslint/no-explicit-any */
type Raw = any;

export type SqliteD1 = D1Database & {
  _raw: Raw;
  _sql: string[];
  /** Make the next statement whose SQL contains `fragment` throw (after `skip` matches). */
  _failNext(fragment: string, skip?: number): void;
  /** Run `hook` right after the next statement whose SQL starts with `prefix` completes. */
  _after(prefix: string, hook: () => void | Promise<void>): void;
  /** Plain query helper for assertions and seeding (numbered params). */
  q<T = Record<string, unknown>>(sql: string, ...args: unknown[]): T[];
  x(sql: string, ...args: unknown[]): void;
};

export function migrationFiles(): string[] {
  return readdirSync(MIGRATIONS_DIR).filter((f) => f.endsWith(".sql")).sort();
}

function named(sql: string): { text: string; names: Set<number> } {
  const names = new Set<number>();
  const text = sql.replace(/\?(\d+)/g, (_m, n: string) => {
    names.add(Number(n));
    return `:p${n}`;
  });
  return { text, names };
}

/** D1 needs exactly as many bindings as the highest ?N in the statement. */
function params(names: Set<number>, args: unknown[]): Record<string, unknown> {
  const expected = names.size === 0 ? 0 : Math.max(...names);
  if (args.length !== expected) {
    throw new Error(`D1_ERROR: Wrong number of parameter bindings (expected ${expected}, got ${args.length})`);
  }
  const out: Record<string, unknown> = {};
  for (const n of names) out[`p${n}`] = args[n - 1];
  return out;
}

const plain = <T>(row: unknown): T => (row ? ({ ...(row as object) } as T) : (null as T));

export function makeSqliteD1(): SqliteD1 {
  const { DatabaseSync } = require("node:sqlite");
  const raw: Raw = new DatabaseSync(":memory:");
  raw.exec("PRAGMA foreign_keys = ON");
  for (const file of migrationFiles()) raw.exec(readFileSync(join(MIGRATIONS_DIR, file), "utf8"));

  const sqlLog: string[] = [];
  const failures: { fragment: string; skip: number }[] = [];
  const hooks: { prefix: string; hook: () => void | Promise<void> }[] = [];

  function maybeFail(sql: string): void {
    const i = failures.findIndex((f) => sql.includes(f.fragment));
    if (i < 0) return;
    if (failures[i].skip > 0) {
      failures[i].skip--;
      return;
    }
    failures.splice(i, 1);
    throw new Error(`sqlite D1: injected failure for ${sql.slice(0, 60)}`);
  }

  async function afterHooks(sql: string): Promise<void> {
    const i = hooks.findIndex((h) => sql.startsWith(h.prefix));
    if (i < 0) return;
    const [h] = hooks.splice(i, 1);
    await h.hook();
  }

  function prepare(sql: string) {
    const { text, names } = named(sql);
    let args: unknown[] = [];
    const stmt = () => raw.prepare(text);
    const api = {
      _sql: sql,
      bind(...a: unknown[]) {
        if (a.some((v) => v === undefined)) throw new TypeError("D1_TYPE_ERROR: undefined is not a bindable value");
        if (a.length > 100) throw new Error("D1: too many SQL variables");
        args = a;
        return api;
      },
      async first<T>(): Promise<T | null> {
        sqlLog.push(sql);
        maybeFail(sql);
        const row = stmt().get(params(names, args));
        await afterHooks(sql);
        return row ? plain<T>(row) : null;
      },
      async all<T>(): Promise<{ results: T[]; success: true; meta: Record<string, unknown> }> {
        sqlLog.push(sql);
        maybeFail(sql);
        const rows = stmt().all(params(names, args)) as unknown[];
        await afterHooks(sql);
        return { results: rows.map((r) => plain<T>(r)), success: true, meta: {} };
      },
      async run() {
        sqlLog.push(sql);
        maybeFail(sql);
        const r = stmt().run(params(names, args));
        await afterHooks(sql);
        return { success: true, results: [], meta: { changes: Number(r.changes), last_row_id: Number(r.lastInsertRowid) } };
      },
      _runSync() {
        sqlLog.push(sql);
        maybeFail(sql);
        const r = stmt().run(params(names, args));
        return { success: true, results: [], meta: { changes: Number(r.changes) } };
      },
    };
    return api;
  }

  async function batch(statements: Array<ReturnType<typeof prepare>>): Promise<unknown[]> {
    raw.exec("BEGIN");
    try {
      const out = statements.map((st) => st._runSync());
      raw.exec("COMMIT");
      return out;
    } catch (err) {
      raw.exec("ROLLBACK");
      throw err;
    }
  }

  const db = {
    prepare,
    batch,
    async exec(sql: string) {
      raw.exec(sql);
      return { count: 1, duration: 0 };
    },
    _raw: raw,
    _sql: sqlLog,
    _failNext(fragment: string, skip = 0) { failures.push({ fragment, skip }); },
    _after(prefix: string, hook: () => void | Promise<void>) { hooks.push({ prefix, hook }); },
    q<T>(sql: string, ...a: unknown[]): T[] {
      const { text, names } = named(sql);
      return (raw.prepare(text).all(params(names, a)) as unknown[]).map((r) => plain<T>(r));
    },
    x(sql: string, ...a: unknown[]): void {
      const { text, names } = named(sql);
      raw.prepare(text).run(params(names, a));
    },
  };
  return db as unknown as SqliteD1;
}
