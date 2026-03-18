/*
 * LoLReview Launcher — permanent tiny exe that never gets updated.
 *
 * Reads .current to find the active version, launches app-{version}/LoLReview.exe,
 * watches for crashes, and rolls back to .previous if the new version dies.
 *
 * Build: cl.exe /O2 /W4 /Fe:LoLReview.exe launcher.c /link kernel32.lib user32.lib
 * Output: ~30-50 KB standalone exe, no CRT dependency beyond Windows defaults.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAX_PATH_LEN    2048
#define MAX_VERSION_LEN 64
#define CRASH_TIMEOUT_MS 30000   /* 30 seconds — if app lives past this, it's healthy */
#define EXIT_CODE_UPDATE_RESTART 42
#define MAX_ROLLBACK_ATTEMPTS 1

/* ── Logging ─────────────────────────────────────────────────────────── */

static HANDLE g_log_file = INVALID_HANDLE_VALUE;

static void log_open(const wchar_t *launcher_dir) {
    wchar_t path[MAX_PATH_LEN];
    _snwprintf(path, MAX_PATH_LEN, L"%s\\.launcher.log", launcher_dir);
    g_log_file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ,
                             NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
}

static void log_msg(const char *msg) {
    if (g_log_file == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME st;
    GetLocalTime(&st);
    char buf[4096];
    int n = _snprintf(buf, sizeof(buf),
        "%04d-%02d-%02d %02d:%02d:%02d - %s\r\n",
        st.wYear, st.wMonth, st.wDay,
        st.wHour, st.wMinute, st.wSecond, msg);
    if (n > 0) {
        DWORD written;
        WriteFile(g_log_file, buf, (DWORD)n, &written, NULL);
    }
}

static void log_close(void) {
    if (g_log_file != INVALID_HANDLE_VALUE) {
        CloseHandle(g_log_file);
        g_log_file = INVALID_HANDLE_VALUE;
    }
}

/* ── File helpers ────────────────────────────────────────────────────── */

/* Read a pointer file (.current / .previous) into buf. Returns 0 on success. */
static int read_pointer(const wchar_t *path, char *buf, int buf_size) {
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    DWORD bytes_read = 0;
    ReadFile(f, buf, (DWORD)(buf_size - 1), &bytes_read, NULL);
    CloseHandle(f);
    buf[bytes_read] = '\0';
    /* Strip whitespace / newlines */
    while (bytes_read > 0 && (buf[bytes_read-1] == '\n' || buf[bytes_read-1] == '\r'
           || buf[bytes_read-1] == ' ')) {
        buf[--bytes_read] = '\0';
    }
    return (bytes_read > 0) ? 0 : -1;
}

/* Write a pointer file atomically (write .tmp then rename). */
static int write_pointer(const wchar_t *path, const char *value) {
    wchar_t tmp_path[MAX_PATH_LEN];
    _snwprintf(tmp_path, MAX_PATH_LEN, L"%s.tmp", path);

    HANDLE f = CreateFileW(tmp_path, GENERIC_WRITE, 0,
                           NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    DWORD written;
    WriteFile(f, value, (DWORD)strlen(value), &written, NULL);
    CloseHandle(f);

    /* Atomic rename (MoveFileEx with REPLACE_EXISTING) */
    if (!MoveFileExW(tmp_path, path, MOVEFILE_REPLACE_EXISTING)) {
        DeleteFileW(tmp_path);
        return -1;
    }
    return 0;
}

/* Check if a file/directory exists. */
static int path_exists(const wchar_t *path) {
    DWORD attr = GetFileAttributesW(path);
    return (attr != INVALID_FILE_ATTRIBUTES);
}

/* ── Fallback: scan for newest app-* directory ───────────────────────── */

static int find_newest_app_dir(const wchar_t *launcher_dir, char *version_out, int buf_size) {
    wchar_t pattern[MAX_PATH_LEN];
    _snwprintf(pattern, MAX_PATH_LEN, L"%s\\app-*", launcher_dir);

    WIN32_FIND_DATAW fd;
    HANDLE hFind = FindFirstFileW(pattern, &fd);
    if (hFind == INVALID_HANDLE_VALUE) return -1;

    char best[MAX_VERSION_LEN] = {0};
    do {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        if (wcsncmp(fd.cFileName, L"app-", 4) != 0) continue;

        /* Convert wide version string to narrow */
        char ver[MAX_VERSION_LEN];
        int i;
        for (i = 0; i < MAX_VERSION_LEN - 1 && fd.cFileName[4 + i]; i++)
            ver[i] = (char)fd.cFileName[4 + i];
        ver[i] = '\0';

        /* Simple string comparison works for semver with same digit counts */
        if (strcmp(ver, best) > 0) {
            strncpy(best, ver, MAX_VERSION_LEN - 1);
        }
    } while (FindNextFileW(hFind, &fd));
    FindClose(hFind);

    if (best[0] == '\0') return -1;
    strncpy(version_out, best, buf_size - 1);
    return 0;
}

/* ── Core launch logic ───────────────────────────────────────────────── */

static DWORD launch_and_wait(const wchar_t *exe_path, const wchar_t *cmdline) {
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));

    /* Build full command line: "exe_path" original_args */
    wchar_t full_cmd[MAX_PATH_LEN * 2];
    if (cmdline && cmdline[0]) {
        _snwprintf(full_cmd, sizeof(full_cmd)/sizeof(wchar_t),
                   L"\"%s\" %s", exe_path, cmdline);
    } else {
        _snwprintf(full_cmd, sizeof(full_cmd)/sizeof(wchar_t),
                   L"\"%s\"", exe_path);
    }

    if (!CreateProcessW(NULL, full_cmd, NULL, NULL, FALSE,
                        0, NULL, NULL, &si, &pi)) {
        return (DWORD)-1;
    }

    CloseHandle(pi.hThread);

    /* Wait for crash timeout */
    DWORD wait_result = WaitForSingleObject(pi.hProcess, CRASH_TIMEOUT_MS);

    if (wait_result == WAIT_TIMEOUT) {
        /* App survived past the crash window — it's healthy */
        CloseHandle(pi.hProcess);
        return (DWORD)-2;  /* sentinel: "still running" */
    }

    DWORD exit_code = 1;
    GetExitCodeProcess(pi.hProcess, &exit_code);
    CloseHandle(pi.hProcess);
    return exit_code;
}

/* ── Main ────────────────────────────────────────────────────────────── */

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance,
                    LPWSTR lpCmdLine, int nCmdShow)
{
    (void)hInstance; (void)hPrevInstance; (void)nCmdShow;

    /* Determine launcher directory */
    wchar_t launcher_path[MAX_PATH_LEN];
    GetModuleFileNameW(NULL, launcher_path, MAX_PATH_LEN);
    wchar_t *last_slash = wcsrchr(launcher_path, L'\\');
    wchar_t launcher_dir[MAX_PATH_LEN];
    if (last_slash) {
        wcsncpy(launcher_dir, launcher_path, last_slash - launcher_path);
        launcher_dir[last_slash - launcher_path] = L'\0';
    } else {
        wcscpy(launcher_dir, L".");
    }

    log_open(launcher_dir);
    log_msg("Launcher starting");

    /* Build paths to pointer files */
    wchar_t current_path[MAX_PATH_LEN], previous_path[MAX_PATH_LEN];
    _snwprintf(current_path,  MAX_PATH_LEN, L"%s\\.current",  launcher_dir);
    _snwprintf(previous_path, MAX_PATH_LEN, L"%s\\.previous", launcher_dir);

    int rollback_count = 0;

launch:
    ;  /* label needs a statement */

    /* Read the active version */
    char version[MAX_VERSION_LEN] = {0};
    int have_version = 0;

    if (read_pointer(current_path, version, MAX_VERSION_LEN) == 0) {
        have_version = 1;
    }

    /* If .current is missing or empty, try to find any app-* directory */
    if (!have_version) {
        log_msg("No .current file, scanning for app-* directories");
        if (find_newest_app_dir(launcher_dir, version, MAX_VERSION_LEN) == 0) {
            have_version = 1;
            write_pointer(current_path, version);
            char msg[256];
            _snprintf(msg, sizeof(msg), "Auto-detected version: %s", version);
            log_msg(msg);
        }
    }

    if (!have_version) {
        log_msg("FATAL: No version found, no app-* directories");
        MessageBoxW(NULL,
            L"LoLReview could not find any installed version.\n"
            L"Please re-download from GitHub.",
            L"LoLReview Launcher", MB_OK | MB_ICONERROR);
        log_close();
        return 1;
    }

    /* Build path to the versioned exe */
    wchar_t exe_path[MAX_PATH_LEN];
    wchar_t version_w[MAX_VERSION_LEN];
    MultiByteToWideChar(CP_UTF8, 0, version, -1, version_w, MAX_VERSION_LEN);
    _snwprintf(exe_path, MAX_PATH_LEN, L"%s\\app-%s\\LoLReview.exe",
               launcher_dir, version_w);

    if (!path_exists(exe_path)) {
        char msg[512];
        _snprintf(msg, sizeof(msg),
                  "Version %s exe not found, trying fallback", version);
        log_msg(msg);

        /* Try .previous */
        char prev[MAX_VERSION_LEN] = {0};
        if (rollback_count < MAX_ROLLBACK_ATTEMPTS &&
            read_pointer(previous_path, prev, MAX_VERSION_LEN) == 0 &&
            strcmp(prev, version) != 0) {
            write_pointer(current_path, prev);
            rollback_count++;
            goto launch;
        }

        /* Try scanning */
        char scanned[MAX_VERSION_LEN] = {0};
        if (find_newest_app_dir(launcher_dir, scanned, MAX_VERSION_LEN) == 0 &&
            strcmp(scanned, version) != 0) {
            write_pointer(current_path, scanned);
            rollback_count++;
            goto launch;
        }

        log_msg("FATAL: No working version found");
        MessageBoxW(NULL,
            L"LoLReview could not find a working version.\n"
            L"Please re-download from GitHub.",
            L"LoLReview Launcher", MB_OK | MB_ICONERROR);
        log_close();
        return 1;
    }

    /* Launch the app */
    {
        char msg[512];
        _snprintf(msg, sizeof(msg), "Launching version %s", version);
        log_msg(msg);
    }

    DWORD result = launch_and_wait(exe_path, lpCmdLine);

    if (result == (DWORD)-1) {
        /* CreateProcess failed */
        char msg[512];
        _snprintf(msg, sizeof(msg),
                  "CreateProcess failed for version %s (error %lu)",
                  version, GetLastError());
        log_msg(msg);
        /* Fall through to rollback */
    }
    else if (result == (DWORD)-2) {
        /* App is running fine (survived past crash timeout) */
        char msg[128];
        _snprintf(msg, sizeof(msg),
                  "Version %s running successfully, launcher exiting", version);
        log_msg(msg);
        log_close();
        return 0;
    }
    else if (result == 0) {
        /* Clean exit (user quit within 30s) */
        log_msg("App exited cleanly (code 0)");
        log_close();
        return 0;
    }
    else if (result == EXIT_CODE_UPDATE_RESTART) {
        /* Update restart — re-read .current and launch new version */
        log_msg("App requested update restart (code 42), re-launching");
        rollback_count = 0;  /* reset since this is intentional */
        goto launch;
    }

    /* Crash or CreateProcess failure — attempt rollback */
    {
        char msg[512];
        _snprintf(msg, sizeof(msg),
                  "Version %s crashed (exit code %lu)", version, result);
        log_msg(msg);
    }

    if (rollback_count >= MAX_ROLLBACK_ATTEMPTS) {
        log_msg("Max rollback attempts reached, giving up");
        MessageBoxW(NULL,
            L"LoLReview failed to start after rollback.\n"
            L"Please re-download from GitHub.",
            L"LoLReview Launcher", MB_OK | MB_ICONERROR);
        log_close();
        return 1;
    }

    /* Try rolling back to previous version */
    char prev_version[MAX_VERSION_LEN] = {0};
    if (read_pointer(previous_path, prev_version, MAX_VERSION_LEN) == 0 &&
        strcmp(prev_version, version) != 0) {
        char msg[512];
        _snprintf(msg, sizeof(msg),
                  "Rolling back from %s to %s", version, prev_version);
        log_msg(msg);
        write_pointer(current_path, prev_version);
        rollback_count++;
        goto launch;
    }

    /* No .previous available — try scanning */
    {
        char scanned[MAX_VERSION_LEN] = {0};
        if (find_newest_app_dir(launcher_dir, scanned, MAX_VERSION_LEN) == 0 &&
            strcmp(scanned, version) != 0) {
            char msg[512];
            _snprintf(msg, sizeof(msg),
                      "No .previous, falling back to scanned version %s", scanned);
            log_msg(msg);
            write_pointer(current_path, scanned);
            rollback_count++;
            goto launch;
        }
    }

    log_msg("No rollback version available");
    MessageBoxW(NULL,
        L"LoLReview crashed and no previous version is available.\n"
        L"Please re-download from GitHub.",
        L"LoLReview Launcher", MB_OK | MB_ICONERROR);
    log_close();
    return 1;
}
