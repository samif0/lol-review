// Sidecar supervisor + HTTP proxy.
//
// The Revu backend logic lives in a C# process (Revu.Sidecar.exe) that exposes
// a localhost JSON API. On launch we spawn it, wait for it to write its
// {port, token} handshake file and report /api/health ready, then every Tauri
// command proxies an authenticated request to it. The C# process is killed when
// the app exits.

use std::path::PathBuf;
use std::sync::Mutex;
use std::time::Duration;

use serde::Deserialize;
use tokio::time::sleep;

#[derive(Debug, Clone, Deserialize)]
pub struct Handshake {
    pub port: u16,
    pub token: String,
}

/// Cached handshake. A Mutex<Option<>> (not OnceLock) so it can be REFRESHED:
/// the sidecar can restart on a new port (e.g. a relaunch), leaving the cache
/// stale. On a connection error we re-read the file and retry — see `get_json`.
static HANDSHAKE: Mutex<Option<Handshake>> = Mutex::new(None);

/// %LOCALAPPDATA%\Revu\sidecar.json — must match Revu.Sidecar's Program.cs.
fn handshake_path() -> Option<PathBuf> {
    dirs::data_local_dir().map(|d| d.join("Revu").join("sidecar.json"))
}

/// Resolve the Revu.Sidecar.exe location.
/// Order: explicit override → bundled build (next to the app exe, incl. common
/// resource subdirs) → dev build output under the repo. The bundled lookup is what
/// a packaged release uses; the dev lookup is for `tauri dev`.
fn sidecar_exe_path() -> Option<PathBuf> {
    // Allow an explicit override (handy for dev / CI).
    if let Ok(p) = std::env::var("REVU_SIDECAR_EXE") {
        let pb = PathBuf::from(p);
        if pb.exists() {
            return Some(pb);
        }
    }

    // Bundled build: the sidecar ships alongside the app exe. Tauri places extra
    // `resources` either next to the exe or under a `resources/` (sometimes
    // `resources/_up_/`) subdir depending on the bundler/source path, so probe the
    // exe's own directory and the common resource subdirs.
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            let candidates = [
                dir.join("Revu.Sidecar.exe"),
                dir.join("resources").join("Revu.Sidecar.exe"),
                dir.join("resources")
                    .join("sidecar")
                    .join("Revu.Sidecar.exe"),
            ];
            for cand in candidates {
                if cand.exists() {
                    return Some(cand);
                }
            }
        }
    }

    // dev: <repo>/src/Revu.Sidecar/bin/x64/Debug/<tfm>/win-x64/Revu.Sidecar.exe
    // cwd during `tauri dev` is desktop/src-tauri.
    let cwd = std::env::current_dir().ok()?;
    let repo = cwd.parent()?.parent()?; // src-tauri -> desktop -> repo
    let base = repo
        .join("src")
        .join("Revu.Sidecar")
        .join("bin")
        .join("x64")
        .join("Debug");
    // The TFM folder name can drift; pick the first dir that holds the exe.
    if let Ok(entries) = std::fs::read_dir(&base) {
        for e in entries.flatten() {
            let cand = e.path().join("win-x64").join("Revu.Sidecar.exe");
            if cand.exists() {
                return Some(cand);
            }
            let cand2 = e.path().join("Revu.Sidecar.exe");
            if cand2.exists() {
                return Some(cand2);
            }
        }
    }
    None
}

/// The spawned sidecar process, kept so we can KILL it on app exit / before an
/// update applies. Critical for updates: the sidecar (a self-contained .NET exe)
/// holds Revu.Sidecar.dll + LoLReview.Core.dll + the bundled runtime LOCKED. If it
/// survives the app, Velopack's Update.exe can't swap those files → "partially
/// installed". A dropped Child does NOT kill the process on Windows, so we must
/// hold + kill it explicitly.
static SIDECAR_CHILD: Mutex<Option<std::process::Child>> = Mutex::new(None);

/// Spawn the sidecar process. We delete any stale handshake first so `wait_ready`
/// only trusts a fresh one written by THIS launch.
pub fn spawn() -> Result<(), String> {
    if let Some(hp) = handshake_path() {
        let _ = std::fs::remove_file(&hp); // ignore if absent
    }

    let exe = sidecar_exe_path()
        .ok_or_else(|| "Revu.Sidecar.exe not found (build it: dotnet build src/Revu.Sidecar)".to_string())?;

    let mut cmd = std::process::Command::new(&exe);
    // Run from the exe's own dir so its relative paths resolve.
    if let Some(dir) = exe.parent() {
        cmd.current_dir(dir);
    }
    // The sidecar is a .NET console-subsystem exe; spawned normally Windows gives
    // it a visible console window. CREATE_NO_WINDOW suppresses it so no terminal
    // flashes/stays open beside the app. (No-op on non-Windows.)
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    let child = cmd
        .spawn()
        .map_err(|e| format!("failed to spawn sidecar {exe:?}: {e}"))?;
    // Keep the handle so stop() can kill it. Kill any prior one first (a relaunch
    // path could spawn twice).
    if let Ok(mut guard) = SIDECAR_CHILD.lock() {
        if let Some(mut old) = guard.take() {
            let _ = old.kill();
        }
        *guard = Some(child);
    }
    Ok(())
}

/// Kill the sidecar and wait for it to exit, so it releases its file locks. Call
/// before exiting the app for an update (and on normal shutdown). Best-effort:
/// safe to call when there's no child. Returns once the process is gone (or the
/// short wait elapses) so the caller can proceed to swap files / exit.
pub fn stop() {
    let child = SIDECAR_CHILD.lock().ok().and_then(|mut g| g.take());
    if let Some(mut child) = child {
        let _ = child.kill();
        // Reap it so the OS releases the handle + file locks promptly.
        let _ = child.wait();
    }
}

/// Poll for the handshake file + /api/health=ready. Returns once ready or errors
/// after the timeout. Caches the handshake for command proxying.
pub async fn wait_ready(timeout: Duration) -> Result<Handshake, String> {
    let started = std::time::Instant::now();
    let client = reqwest::Client::new();

    loop {
        if started.elapsed() > timeout {
            return Err("sidecar did not become ready in time".into());
        }

        // 1) handshake file present + parseable?
        if let Some(hp) = handshake_path() {
            if let Ok(txt) = std::fs::read_to_string(&hp) {
                if let Ok(hs) = serde_json::from_str::<Handshake>(&txt) {
                    // 2) health endpoint reports ready?
                    let url = format!("http://127.0.0.1:{}/api/health", hs.port);
                    if let Ok(resp) = client
                        .get(&url)
                        .timeout(Duration::from_millis(800))
                        .send()
                        .await
                    {
                        if resp.status().is_success() {
                            *HANDSHAKE.lock().unwrap() = Some(hs.clone());
                            return Ok(hs);
                        }
                    }
                }
            }
        }

        sleep(Duration::from_millis(120)).await;
    }
}

/// Read the handshake file fresh and update the cache. Returns the new value, or
/// None if the file is missing/unparseable. Used on startup and to recover from a
/// stale cache (sidecar restarted on a different port).
fn reload_handshake() -> Option<Handshake> {
    let hp = handshake_path()?;
    let txt = std::fs::read_to_string(&hp).ok()?;
    let hs: Handshake = serde_json::from_str(&txt).ok()?;
    *HANDSHAKE.lock().unwrap() = Some(hs.clone());
    Some(hs)
}

/// Resolve the handshake — cached value if present, else read the file.
fn handshake() -> Result<Handshake, String> {
    if let Some(hs) = HANDSHAKE.lock().unwrap().clone() {
        return Ok(hs);
    }
    reload_handshake().ok_or_else(|| "sidecar not ready".into())
}

/// GET an authenticated JSON endpoint. Retries once with a freshly-read handshake
/// if the first attempt can't CONNECT (the cached port is stale — sidecar
/// restarted on a new port).
pub async fn get_json(path: &str) -> Result<serde_json::Value, String> {
    for attempt in 0..2 {
        let hs = if attempt == 0 { handshake()? } else {
            reload_handshake().ok_or_else(|| "sidecar not ready".to_string())?
        };
        let url = format!("http://127.0.0.1:{}{}", hs.port, path);
        let client = reqwest::Client::new();
        match client
            .get(&url)
            .bearer_auth(&hs.token)
            .timeout(Duration::from_secs(10))
            .send()
            .await
        {
            Ok(resp) => {
                if !resp.status().is_success() {
                    return Err(format!("sidecar returned HTTP {}", resp.status()));
                }
                return resp
                    .json::<serde_json::Value>()
                    .await
                    .map_err(|e| format!("sidecar JSON parse failed: {e}"));
            }
            // Connection-level failure (stale port). Re-read handshake and retry once.
            Err(e) if attempt == 0 && e.is_connect() => continue,
            Err(e) => return Err(format!("sidecar request failed: {e}")),
        }
    }
    Err("sidecar request failed after retry".into())
}

/// One Server-Sent Event parsed off the sidecar's /api/events stream: the
/// `event:` type tag and the JSON-decoded `data:` payload.
#[derive(Debug, Clone)]
pub struct SseEvent {
    pub event_type: String,
    pub data: serde_json::Value,
}

/// Open the authenticated SSE stream at `path` (e.g. "/api/events") and invoke
/// `on_event` for every `event:`/`data:` record until the stream ends, the
/// sidecar drops, or `should_stop` returns true. Bearer auth rides in the header
/// (the C# endpoint is token-gated like every other), which is exactly why this
/// lives in Rust — a browser EventSource can't set Authorization, so the Tauri
/// host owns the connection and re-emits events to the webview.
///
/// Best-effort + self-healing: on a connection error it re-reads the handshake
/// (the sidecar may have restarted on a new port) and reconnects after a short
/// backoff. Returns only when `should_stop` is observed true.
pub async fn stream_sse<F, S>(path: &str, mut on_event: F, should_stop: S)
where
    F: FnMut(SseEvent),
    S: Fn() -> bool,
{
    use futures_util::StreamExt;

    loop {
        if should_stop() {
            return;
        }

        let hs = match handshake() {
            Ok(hs) => hs,
            Err(_) => match reload_handshake() {
                Some(hs) => hs,
                None => {
                    sleep(Duration::from_millis(500)).await;
                    continue;
                }
            },
        };

        let url = format!("http://127.0.0.1:{}{}", hs.port, path);
        let client = reqwest::Client::new();
        // No read timeout: an SSE stream is intentionally long-lived. The server
        // sends ": keep-alive" comment frames so the socket stays warm.
        let resp = client.get(&url).bearer_auth(&hs.token).send().await;

        let resp = match resp {
            Ok(r) if r.status().is_success() => r,
            // Bad status or connect error — drop the cache and retry after backoff.
            _ => {
                let _ = reload_handshake();
                sleep(Duration::from_millis(800)).await;
                continue;
            }
        };

        // Parse the byte stream into SSE records. Records are separated by a blank
        // line; within a record, `event:` sets the type and `data:` (possibly
        // multi-line) accumulates the payload. Comment lines (": …") are ignored.
        let mut stream = resp.bytes_stream();
        let mut buf = String::new();
        let mut cur_event = String::new();
        let mut cur_data = String::new();

        'read: while let Some(chunk) = stream.next().await {
            if should_stop() {
                return;
            }
            let bytes = match chunk {
                Ok(b) => b,
                Err(_) => break 'read, // stream error — reconnect
            };
            buf.push_str(&String::from_utf8_lossy(&bytes));

            // Drain complete lines out of the buffer.
            while let Some(nl) = buf.find('\n') {
                let line = buf[..nl].trim_end_matches('\r').to_string();
                buf.drain(..=nl);

                if line.is_empty() {
                    // End of one SSE record — dispatch it.
                    if !cur_event.is_empty() || !cur_data.is_empty() {
                        let data = serde_json::from_str::<serde_json::Value>(&cur_data)
                            .unwrap_or(serde_json::Value::Null);
                        on_event(SseEvent {
                            event_type: if cur_event.is_empty() {
                                "message".to_string()
                            } else {
                                cur_event.clone()
                            },
                            data,
                        });
                    }
                    cur_event.clear();
                    cur_data.clear();
                } else if let Some(rest) = line.strip_prefix("event:") {
                    cur_event = rest.trim().to_string();
                } else if let Some(rest) = line.strip_prefix("data:") {
                    if !cur_data.is_empty() {
                        cur_data.push('\n');
                    }
                    cur_data.push_str(rest.trim_start());
                }
                // ": comment" / unknown fields are ignored.
            }
        }

        // Stream ended (sidecar closed or restarted) — reconnect after a beat.
        let _ = reload_handshake();
        sleep(Duration::from_millis(800)).await;
    }
}

/// POST a JSON body. Same stale-port retry as get_json. 30s timeout — fine for the
/// daily-loop writes; long-running endpoints (clip upload, Riot-API backfill) use
/// post_json_timeout below.
pub async fn post_json(path: &str, body: serde_json::Value) -> Result<serde_json::Value, String> {
    post_json_timeout(path, body, Duration::from_secs(30)).await
}

/// POST a JSON body with an explicit request timeout. Clip upload (large bodies,
/// 5-min sidecar cap) and the Riot-API backfill (throttled, many round-trips) can
/// run far past the default 30s, so they pass a wider window here. Same stale-port
/// retry as post_json.
pub async fn post_json_timeout(
    path: &str,
    body: serde_json::Value,
    timeout: Duration,
) -> Result<serde_json::Value, String> {
    for attempt in 0..2 {
        let hs = if attempt == 0 { handshake()? } else {
            reload_handshake().ok_or_else(|| "sidecar not ready".to_string())?
        };
        let url = format!("http://127.0.0.1:{}{}", hs.port, path);
        let client = reqwest::Client::new();
        match client
            .post(&url)
            .bearer_auth(&hs.token)
            .json(&body)
            .timeout(timeout)
            .send()
            .await
        {
            Ok(resp) => {
                let status = resp.status();
                let value = resp
                    .json::<serde_json::Value>()
                    .await
                    .map_err(|e| format!("sidecar JSON parse failed: {e}"))?;
                if !status.is_success() {
                    let msg = value
                        .get("error")
                        .and_then(|v| v.as_str())
                        .unwrap_or("write failed");
                    return Err(format!("sidecar HTTP {status}: {msg}"));
                }
                return Ok(value);
            }
            Err(e) if attempt == 0 && e.is_connect() => continue,
            Err(e) => return Err(format!("sidecar request failed: {e}")),
        }
    }
    Err("sidecar request failed after retry".into())
}
