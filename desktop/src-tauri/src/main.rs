// Prevents an extra console window on Windows in release builds.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    // Velopack lifecycle hooks. When Setup.exe / Update.exe finishes, it runs the
    // main exe with one of these flags as a short-lived hook and WAITS for it to
    // exit. A normal Velopack (C#) app handles them via VelopackApp.Build().Run();
    // since we're a Rust exe, we must do the equivalent ourselves: for the
    // install/update/obsolete/uninstall hooks, do nothing and EXIT 0 immediately so
    // Setup's "running app hooks" step completes. NOT exiting here is exactly what
    // caused "Install Partially Succeeded" — the GUI booted instead of the hook
    // returning, so Setup reported the hook step as failed.
    //
    // --veloapp-firstrun is the real first launch after install: fall through and
    // start the app normally (the user double-clicked the shortcut / Setup launched
    // it for real).
    let args: Vec<String> = std::env::args().collect();
    for a in args.iter().skip(1) {
        match a.as_str() {
            // Hook callbacks: the next arg is the version; we don't need it. Just
            // acknowledge by exiting cleanly so Setup/Update can proceed.
            "--veloapp-install"
            | "--veloapp-updated"
            | "--veloapp-obsolete"
            | "--veloapp-uninstall" => {
                std::process::exit(0);
            }
            // First run after install — continue to the app.
            "--veloapp-firstrun" => break,
            _ => {}
        }
    }

    revu_desktop_lib::run()
}
