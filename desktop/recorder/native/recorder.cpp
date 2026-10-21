// SPDX-License-Identifier: GPL-2.0-or-later
// This process is the only Revu component linked against libobs.
#include <windows.h>
#include <objbase.h>
#include <tlhelp32.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <obs.h>
#include <util/base.h>
#include <util/platform.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include "encoder-policy.hpp"
#include "frame-stats.hpp"
#include "wgc-session.hpp"

#ifndef PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION
#define PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION 0x4
#endif

namespace {
constexpr uint64_t filetimeEpoch = 116444736000000000ULL;
constexpr DWORD startupTimeoutMs = 30000;
constexpr DWORD drainTimeoutMs = 6000;
std::atomic<uint64_t> watchdogDeadline{0};
std::atomic<bool> watchdogDone{false};
std::mutex protocolMutex;

struct Failure : std::runtime_error {
  std::string code;
  Failure(std::string code_, std::string message) : std::runtime_error(message), code(std::move(code_)) {}
};

struct Handle {
  HANDLE value = nullptr;
  explicit Handle(HANDLE handle = nullptr) : value(handle) {}
  ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
  Handle(const Handle &) = delete;
  Handle &operator=(const Handle &) = delete;
  explicit operator bool() const { return value && value != INVALID_HANDLE_VALUE; }
};

std::string utf8(const std::wstring &value)
{
  if (value.empty()) return {};
  const int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
  if (count <= 0) throw Failure("invalid-text", "A Windows path or window name is not valid Unicode");
  std::string result(static_cast<size_t>(count), '\0');
  WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), count, nullptr, nullptr);
  return result;
}

std::string quote(const std::string &value)
{
  std::string result = "\"";
  constexpr char hex[] = "0123456789abcdef";
  for (const unsigned char ch : value) {
    if (ch == '"' || ch == '\\') { result += '\\'; result += static_cast<char>(ch); }
    else if (ch < 0x20) { result += "\\u00"; result += hex[ch >> 4]; result += hex[ch & 15]; }
    else result += static_cast<char>(ch);
  }
  return result + '"';
}

void emit(const std::string &json)
{
  std::lock_guard lock(protocolMutex);
  const std::string line = json + '\n';
  DWORD written = 0;
  if (!WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), line.data(), static_cast<DWORD>(line.size()), &written, nullptr) || written != line.size())
    TerminateProcess(GetCurrentProcess(), 125);
}

void obsLog(int level, const char *format, va_list args, void *)
{
  // stdout is a strict protocol channel. OBS and FFmpeg diagnostics stay private.
  if (level > LOG_INFO) return;
  std::fputs("[libobs] ", stderr);
  std::vfprintf(stderr, format, args);
  std::fputc('\n', stderr);
}

std::filesystem::path runtimeDirectory()
{
  std::wstring filename(32768, L'\0');
  const DWORD count = GetModuleFileNameW(nullptr, filename.data(), static_cast<DWORD>(filename.size()));
  if (!count || count == filename.size()) throw Failure("runtime-path", "Cannot locate the recorder runtime");
  filename.resize(count);
  return std::filesystem::path(filename).parent_path();
}

void boundProcess()
{
  if (!SetPriorityClass(GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS))
    throw Failure("process-priority", "Cannot lower the recorder priority");
  // Windows 11 throttles a windowless background process: it can ignore the 1 ms timer
  // request obs.dll makes when it loads, so the 60 FPS render loop wakes up to 15 ms late and
  // the video repeats frames. Opting out keeps timers and clock speed normal. It does not
  // raise the scheduling priority, so League still wins every contested core.
  PROCESS_POWER_THROTTLING_STATE throttling{PROCESS_POWER_THROTTLING_CURRENT_VERSION,
    PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION, 0};
  if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &throttling, sizeof(throttling))) {
    throttling.ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED; // Windows 10 has no timer flag.
    SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &throttling, sizeof(throttling));
  }
  // Keep the job handle alive until process termination. Closing it would also
  // kill this process; on crash the OS closes it and kills the muxer child.
  HANDLE job = CreateJobObjectW(nullptr, nullptr);
  if (!job) throw Failure("process-job", "Cannot create the recorder process job");
  JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
  limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_JOB_MEMORY | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
  limits.ProcessMemoryLimit = static_cast<SIZE_T>(2ULL * 1024 * 1024 * 1024);
  limits.JobMemoryLimit = static_cast<SIZE_T>(3ULL * 1024 * 1024 * 1024);
  if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) || !AssignProcessToJobObject(job, GetCurrentProcess())) {
    CloseHandle(job);
    throw Failure("process-job", "Cannot enforce recorder resource and child lifetime limits");
  }
  SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
  watchdogDeadline = GetTickCount64() + startupTimeoutMs;
  std::thread([] {
    while (!watchdogDone) {
      const uint64_t deadline = watchdogDeadline.load();
      if (deadline && GetTickCount64() >= deadline) TerminateProcess(GetCurrentProcess(), 124);
      Sleep(100);
    }
  }).detach();
}

struct GameWindow {
  HWND window = nullptr;
  DWORD pid = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  std::string title;
  std::string windowClass;
  std::string executable;
};

bool leagueProcess(DWORD pid, std::string *executable = nullptr)
{
  Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
  if (!process) return false;
  std::wstring image(32768, L'\0');
  DWORD count = static_cast<DWORD>(image.size());
  if (!QueryFullProcessImageNameW(process.value, 0, image.data(), &count)) return false;
  image.resize(count);
  const std::wstring name = std::filesystem::path(image).filename().wstring();
  if (_wcsicmp(name.c_str(), L"League of Legends.exe") != 0) return false;
  if (executable) *executable = utf8(name);
  return true;
}

std::vector<DWORD> leaguePids()
{
  std::vector<DWORD> result;
  Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
  if (!snapshot) throw Failure("process-discovery", "Cannot enumerate running game processes");
  PROCESSENTRY32W process{};
  process.dwSize = sizeof(process);
  if (Process32FirstW(snapshot.value, &process)) {
    do {
      if (_wcsicmp(process.szExeFile, L"League of Legends.exe") == 0 && leagueProcess(process.th32ProcessID)) result.push_back(process.th32ProcessID);
    } while (Process32NextW(snapshot.value, &process));
  }
  return result;
}

std::vector<GameWindow> discover()
{
  std::vector<GameWindow> games;
  const auto pids = leaguePids();
  struct Enumeration { std::vector<GameWindow> &games; const std::vector<DWORD> &pids; } enumeration{games, pids};
  EnumWindows([](HWND window, LPARAM parameter) -> BOOL {
    auto &state = *reinterpret_cast<Enumeration *>(parameter);
    if (!IsWindowVisible(window) || IsIconic(window) || (GetWindowLongPtrW(window, GWL_STYLE) & WS_CHILD) || (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW)) return TRUE;
    GameWindow game;
    GetWindowThreadProcessId(window, &game.pid);
    if (std::find(state.pids.begin(), state.pids.end(), game.pid) == state.pids.end()) return TRUE;
    if (!leagueProcess(game.pid, &game.executable)) return TRUE;
    RECT rect{};
    if (!GetClientRect(window, &rect) || rect.right <= 0 || rect.bottom <= 0) return TRUE;
    game.window = window;
    game.width = static_cast<uint32_t>(rect.right);
    game.height = static_cast<uint32_t>(rect.bottom);
    wchar_t title[4096]{};
    wchar_t windowClass[256]{};
    GetWindowTextW(window, title, static_cast<int>(std::size(title)));
    GetClassNameW(window, windowClass, static_cast<int>(std::size(windowClass)));
    game.title = utf8(title);
    game.windowClass = utf8(windowClass);
    if (!game.title.empty() && !game.windowClass.empty()) state.games.push_back(std::move(game));
    return TRUE;
  }, reinterpret_cast<LPARAM>(&enumeration));
  return games;
}

void emitGames()
{
  std::string json = "{\"type\":\"games\",\"games\":[";
  const auto pids = leaguePids();
  const auto windows = discover();
  for (size_t index = 0; index < pids.size(); ++index) {
    const auto pid = pids[index];
    const auto window = std::find_if(windows.begin(), windows.end(), [pid](const GameWindow &game) { return game.pid == pid; });
    if (index) json += ',';
    const uint32_t width = window == windows.end() ? 0 : window->width;
    const uint32_t height = window == windows.end() ? 0 : window->height;
    json += "{\"pid\":" + std::to_string(pid) + ",\"width\":" + std::to_string(width) + ",\"height\":" + std::to_string(height) + '}';
  }
  emit(json + "]}");
}

std::string windowSetting(const GameWindow &game)
{
  const auto encode = [](const std::string &text) {
    std::string result;
    for (const char ch : text) result += ch == '#' ? "#22" : ch == ':' ? "#3A" : std::string(1, ch);
    return result;
  };
  return encode(game.title) + ':' + encode(game.windowClass) + ':' + encode(game.executable);
}

struct Preset { uint32_t width, height, fps, bitrate; };
Preset preset(const std::wstring &name)
{
  if (name == L"720p30") return {1280, 720, 30, 5000};
  if (name == L"1080p30") return {1920, 1080, 30, 8000};
  if (name == L"1080p60") return {1920, 1080, 60, 12000};
  throw Failure("invalid-preset", "Unsupported recording preset");
}

// Below normal is the default: League gets the CPU and GPU first. Normal exists only to
// compare recording smoothness against game smoothness; nothing ever runs above the game.
enum class Priority { BelowNormal, Normal };
Priority priority(const std::wstring &name)
{
  if (name == L"below-normal") return Priority::BelowNormal;
  if (name == L"normal") return Priority::Normal;
  throw Failure("invalid-priority", "Unsupported recorder priority");
}
const char *priorityName(Priority value) { return value == Priority::Normal ? "normal" : "below-normal"; }

// D3DKMT_SCHEDULINGPRIORITYCLASS values used by the Windows GPU scheduler.
constexpr int gpuBelowNormal = 1, gpuNormal = 2;
const char *gpuClassName(int value)
{
  constexpr const char *names[] = {"idle", "below-normal", "normal", "above-normal", "high", "realtime"};
  return value >= 0 && value <= 5 ? names[value] : "unknown";
}

// libobs raises its process above every game (HIGH with hardware-accelerated GPU scheduling,
// otherwise REALTIME) when it creates its Direct3D device. Windows allows that without
// administrator rights; only libobs's later thread-priority step needs them, so its "GPU
// priority setup failed" log line does not mean the raise was undone. Recording must never
// take GPU time ahead of League, so the class is pinned at or below normal.
struct GpuScheduling {
  LONG(APIENTRY *set)(HANDLE, int) = nullptr;
  LONG(APIENTRY *get)(HANDLE, int *) = nullptr;
  GpuScheduling()
  {
    const HMODULE gdi = LoadLibraryExW(L"gdi32.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!gdi) return;
    set = reinterpret_cast<decltype(set)>(GetProcAddress(gdi, "D3DKMTSetProcessSchedulingPriorityClass"));
    get = reinterpret_cast<decltype(get)>(GetProcAddress(gdi, "D3DKMTGetProcessSchedulingPriorityClass"));
  }
  int current() const
  {
    int value = -1;
    return get && get(GetCurrentProcess(), &value) == 0 && value >= 0 && value <= 5 ? value : -1;
  }
  // Returns the class now in effect, or -1 when Windows cannot report it.
  int pin(int wanted) const
  {
    int value = current();
    if (value != wanted && set) { set(GetCurrentProcess(), wanted); value = current(); }
    return value;
  }
};
const GpuScheduling gpuScheduling;

struct WinrtApi {
  HMODULE module = nullptr;
  BOOL (*supported)() = nullptr;
  bool initialize(const std::filesystem::path &runtime)
  {
    if (module) return true;
    module = LoadLibraryExW((runtime / L"libobs-winrt.dll").c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!module) return false;
    supported = reinterpret_cast<decltype(supported)>(GetProcAddress(module, "winrt_capture_supported"));
    if (supported) return true;
    FreeLibrary(module);
    module = nullptr;
    return false;
  }
};
// Keep the pinned libobs-winrt loaded until process exit: libobs uses it to give its graphics
// thread the dispatcher queue Revu's capture session runs on (wgc-session.hpp). Its capture
// functions are not used. No stock win-capture module, game hook or updater is loaded.
WinrtApi winrtApi;
void registerWgcSource(const std::filesystem::path &runtime);

struct RenderAdapter {
  uint32_t vendorId = 0;
  uint64_t luid = 0;
  revu::HardwareVendor vendor() const { return revu::hardwareVendor(vendorId); }
};

RenderAdapter renderAdapter()
{
  Microsoft::WRL::ComPtr<IDXGIDevice> device;
  Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;
  DXGI_ADAPTER_DESC description{};
  obs_enter_graphics();
  auto *d3d = static_cast<ID3D11Device *>(gs_get_device_obj());
  const bool valid = d3d && SUCCEEDED(d3d->QueryInterface(IID_PPV_ARGS(device.GetAddressOf()))) && SUCCEEDED(device->GetAdapter(adapter.GetAddressOf())) && SUCCEEDED(adapter->GetDesc(&description));
  obs_leave_graphics();
  if (!valid) throw Failure("render-adapter-unavailable", "Cannot identify the recording engine's active Direct3D adapter");
  return {description.VendorId, (static_cast<uint64_t>(static_cast<uint32_t>(description.AdapterLuid.HighPart)) << 32) | description.AdapterLuid.LowPart};
}

void resetGpuThreadPriority()
{
  // libobs's thread-priority step only succeeds in an elevated process; undo it there too.
  Microsoft::WRL::ComPtr<IDXGIDevice> device;
  obs_enter_graphics();
  auto *d3d = static_cast<ID3D11Device *>(gs_get_device_obj());
  if (d3d && SUCCEEDED(d3d->QueryInterface(IID_PPV_ARGS(device.GetAddressOf())))) device->SetGPUThreadPriority(0);
  obs_leave_graphics();
}

struct ObsContext {
  bool initialized = false;
  RenderAdapter adapter;
  int gpuWanted = gpuBelowNormal;
  int gpuClass = -1;
  ~ObsContext() { if (initialized) obs_shutdown(); }
  // Re-applied every second while recording: a Direct3D device rebuild raises it again.
  bool keepGpuPriority() { gpuClass = gpuScheduling.pin(gpuWanted); return gpuClass <= gpuNormal; }
  void initialize(const std::filesystem::path &runtime, const Preset &quality, uint32_t baseWidth, uint32_t baseHeight,
    bool captureModules = true, Priority priority = Priority::BelowNormal)
  {
    base_set_log_handler(obsLog, nullptr);
    if (!obs_startup("en-US", nullptr, nullptr)) throw Failure("obs-startup", "OBS recording engine could not initialize");
    initialized = true;
    if (std::string(obs_get_version_string()) != "32.2.2") throw Failure("obs-version", "The recorder requires the pinned OBS 32.2.2 runtime");
    obs_add_data_path((utf8((runtime / L"data" / L"libobs").wstring()) + '/').c_str());
    obs_video_info video{};
    video.graphics_module = "libobs-d3d11";
    video.fps_num = quality.fps;
    video.fps_den = 1;
    video.base_width = baseWidth;
    video.base_height = baseHeight;
    video.output_width = quality.width;
    video.output_height = quality.height;
    video.output_format = VIDEO_FORMAT_NV12;
    video.gpu_conversion = true;
    video.colorspace = VIDEO_CS_709;
    video.range = VIDEO_RANGE_PARTIAL;
    video.scale_type = OBS_SCALE_BILINEAR;
    if (obs_reset_video(&video) != OBS_VIDEO_SUCCESS) throw Failure("video-unavailable", "Direct3D video initialization failed");
    adapter = renderAdapter();
    gpuWanted = priority == Priority::Normal ? gpuNormal : gpuBelowNormal;
    resetGpuThreadPriority();
    if (!keepGpuPriority()) throw Failure("gpu-priority-unavailable", "The recorder could not keep its GPU priority at or below the game's");
    obs_audio_info2 audio{48000, SPEAKERS_STEREO, 1000, false};
    if (!obs_reset_audio2(&audio)) throw Failure("audio-unavailable", "48 kHz stereo audio initialization failed");
    // Never search the user's OBS plugin directories or load the UI/browser.
    for (const auto *name : {L"win-wasapi", L"obs-ffmpeg", L"obs-nvenc", L"obs-qsv11"}) {
      if (!captureModules && std::wstring(name) == L"win-wasapi") continue;
      const auto moduleName = utf8(name);
      // Vendor probes can have their own timeouts. Do not spend a working
      // NVIDIA/AMD startup waiting on an unrelated Intel device detector.
      if (!revu::shouldLoadRecorderModule(moduleName, adapter.vendor())) continue;
      const auto data = runtime / L"data" / L"obs-plugins" / name;
      obs_module_t *module = nullptr;
      const auto binary = runtime / L"obs-plugins" / L"64bit" / (std::wstring(name) + L".dll");
      if (obs_open_module(&module, utf8(binary.wstring()).c_str(), utf8(data.wstring()).c_str()) != MODULE_SUCCESS || !obs_init_module(module)) {
        if (revu::optionalEncoderModule(moduleName)) {
          std::fprintf(stderr, "[recorder] Optional hardware encoder module %s is unavailable\n", moduleName.c_str());
          continue;
        }
        throw Failure("obs-module", "A required OBS recording module could not load");
      }
    }
    obs_post_load_modules();
    if (captureModules) registerWgcSource(runtime);
  }
};

std::vector<revu::EncoderChoice> encoders(revu::HardwareVendor renderVendor)
{
  std::vector<revu::RegisteredEncoder> registered;
  const char *id = nullptr;
  for (size_t index = 0; obs_enum_encoder_types(index, &id); ++index) {
    const char *codec = obs_get_encoder_codec(id);
    const auto caps = obs_get_encoder_caps(id);
    registered.push_back({id, codec ? codec : "", obs_get_encoder_type(id) == OBS_ENCODER_VIDEO,
      (caps & OBS_ENCODER_CAP_PASS_TEXTURE) != 0, (caps & OBS_ENCODER_CAP_INTERNAL) != 0, (caps & OBS_ENCODER_CAP_DEPRECATED) != 0});
  }
  return revu::eligibleEncoders(registered, renderVendor);
}

bool wgcSupported(const std::filesystem::path &runtime)
{
  return winrtApi.initialize(runtime) && winrtApi.supported();
}

bool captureSourcesSupported()
{
  const char *id = nullptr;
  bool processAudio = false;
  bool window = false;
  for (size_t index = 0; obs_enum_input_types(index, &id); ++index) {
    if (std::string(id) == "wasapi_process_output_capture") processAudio = true;
    if (std::string(id) == "revu_wgc_window") window = true;
  }
  return processAudio && window;
}

struct WgcSource {
  GameWindow game;
  std::atomic<revu::WgcWindowCapture *> capture{nullptr};
  bool attempted = false;
  bool identityValid() const
  {
    DWORD pid = 0;
    GetWindowThreadProcessId(game.window, &pid);
    return IsWindow(game.window) && pid == game.pid;
  }
  bool ready() const
  {
    const auto *current = capture.load();
    return current && identityValid() && current->active() && current->width() && current->height();
  }
  static void hooked(void *parameter, calldata_t *call)
  {
    const auto &self = *static_cast<WgcSource *>(parameter);
    calldata_set_bool(call, "hooked", self.ready());
    calldata_set_string(call, "title", self.game.title.c_str());
    calldata_set_string(call, "class", self.game.windowClass.c_str());
    calldata_set_string(call, "executable", self.game.executable.c_str());
  }
  static void pacing(void *parameter, calldata_t *call)
  {
    const auto *current = static_cast<WgcSource *>(parameter)->capture.load();
    calldata_set_int(call, "min_update_interval_hns", current ? current->minUpdateIntervalHns() : 0);
  }
};

void registerWgcSource(const std::filesystem::path &runtime)
{
  if (!winrtApi.initialize(runtime)) throw Failure("wgc-unavailable", "The pinned OBS Windows Graphics Capture backend is unavailable");
  obs_source_info source{};
  source.id = "revu_wgc_window";
  source.type = OBS_SOURCE_TYPE_INPUT;
  source.output_flags = OBS_SOURCE_VIDEO | OBS_SOURCE_CUSTOM_DRAW | OBS_SOURCE_SRGB;
  source.get_name = [](void *) -> const char * { return "Revu exact League window"; };
  source.create = [](obs_data_t *settings, obs_source_t *source) -> void * {
    auto *data = new WgcSource;
    data->game.window = reinterpret_cast<HWND>(static_cast<uintptr_t>(obs_data_get_int(settings, "hwnd")));
    data->game.pid = static_cast<DWORD>(obs_data_get_int(settings, "pid"));
    data->game.width = static_cast<uint32_t>(obs_data_get_int(settings, "base_width"));
    data->game.height = static_cast<uint32_t>(obs_data_get_int(settings, "base_height"));
    data->game.title = obs_data_get_string(settings, "title");
    data->game.windowClass = obs_data_get_string(settings, "class");
    data->game.executable = obs_data_get_string(settings, "executable");
    if (!data->identityValid() || !leagueProcess(data->game.pid) || !winrtApi.supported()) { delete data; return nullptr; }
    proc_handler_add(obs_source_get_proc_handler(source), "void get_hooked(out bool hooked, out string title, out string class, out string executable)", WgcSource::hooked, data);
    proc_handler_add(obs_source_get_proc_handler(source), "void get_capture_pacing(out int min_update_interval_hns)", WgcSource::pacing, data);
    return data;
  };
  source.destroy = [](void *data) {
    obs_queue_task(OBS_TASK_GRAPHICS, [](void *parameter) {
      auto *source = static_cast<WgcSource *>(parameter);
      delete source->capture.load();
      delete source;
    }, data, false);
  };
  source.video_tick = [](void *data, float) {
    auto &self = *static_cast<WgcSource *>(data);
    if (!self.identityValid()) return;
    if (!self.attempted && !IsIconic(self.game.window)) {
      self.attempted = true;
      obs_video_info video{};
      const uint32_t fps = obs_get_video_info(&video) && video.fps_den ? video.fps_num / video.fps_den : 0;
      obs_enter_graphics();
      // Fixed HWND, client area, cursor included, SDR. There is no search or
      // reacquisition of another window and no alternative capture method.
      self.capture = revu::WgcWindowCapture::start(self.game.window, fps);
      obs_leave_graphics();
    }
    auto *capture = self.capture.load();
    if (capture) {
      DWORD foregroundPid = 0;
      GetWindowThreadProcessId(GetForegroundWindow(), &foregroundPid);
      capture->showCursor(foregroundPid == self.game.pid);
    }
  };
  source.get_width = [](void *data) -> uint32_t {
    const auto &self = *static_cast<WgcSource *>(data);
    return self.ready() && !IsIconic(self.game.window) ? self.game.width : 0;
  };
  source.get_height = [](void *data) -> uint32_t {
    const auto &self = *static_cast<WgcSource *>(data);
    return self.ready() && !IsIconic(self.game.window) ? self.game.height : 0;
  };
  source.video_render = [](void *data, gs_effect_t *) {
    auto &self = *static_cast<WgcSource *>(data);
    if (!self.ready() || IsIconic(self.game.window)) return;
    const auto *capture = self.capture.load();
    const auto width = capture->width(), height = capture->height();
    const float scale = std::min(static_cast<float>(self.game.width) / width, static_cast<float>(self.game.height) / height);
    gs_matrix_push();
    gs_matrix_translate3f((self.game.width - width * scale) / 2, (self.game.height - height * scale) / 2, 0);
    gs_matrix_scale3f(scale, scale, 1);
    capture->render();
    gs_matrix_pop();
  };
  source.video_get_color_space = [](void *, size_t, const gs_color_space *) -> gs_color_space { return GS_CS_SRGB; };
  source.icon_type = OBS_ICON_TYPE_WINDOW_CAPTURE;
  obs_register_source(&source);
}

bool sourceHooked(obs_source_t *source, const GameWindow &game)
{
  calldata_t call{};
  const bool called = proc_handler_call(obs_source_get_proc_handler(source), "get_hooked", &call);
  const auto *title = calldata_string(&call, "title");
  const auto *windowClass = calldata_string(&call, "class");
  const auto *executable = calldata_string(&call, "executable");
  const bool hooked = called && calldata_bool(&call, "hooked") && title && windowClass && executable && game.title == title && game.windowClass == windowClass && _stricmp(game.executable.c_str(), executable) == 0;
  calldata_free(&call);
  return hooked;
}

// The capture pacing in effect as JSON: hundred-nanosecond units, or null for the Windows default.
std::string capturePacing(obs_source_t *source)
{
  calldata_t call{};
  const bool called = proc_handler_call(obs_source_get_proc_handler(source), "get_capture_pacing", &call);
  const long long interval = called ? calldata_int(&call, "min_update_interval_hns") : 0;
  calldata_free(&call);
  return interval > 0 ? std::to_string(interval) : "null";
}

struct ClockAnchor {
  int64_t epochNs = 0;
  uint64_t monotonicNs = 0;
  uint64_t uncertaintyNs = std::numeric_limits<uint64_t>::max();
  ClockAnchor()
  {
    // Use the tightest of several clock reads. No process creation/start signal
    // is substituted for a media timestamp.
    for (int attempt = 0; attempt < 8; ++attempt) {
      const auto before = os_gettime_ns();
      FILETIME time{};
      GetSystemTimePreciseAsFileTime(&time);
      const auto after = os_gettime_ns();
      const uint64_t ticks = (static_cast<uint64_t>(time.dwHighDateTime) << 32) | time.dwLowDateTime;
      if (after - before < uncertaintyNs) {
        epochNs = static_cast<int64_t>(ticks - filetimeEpoch) * 100;
        monotonicNs = before + (after - before) / 2;
        uncertaintyNs = after - before;
      }
    }
  }
  int64_t epochMilliseconds(int64_t monotonic) const
  {
    return (epochNs + monotonic - static_cast<int64_t>(monotonicNs)) / 1000000;
  }
};

std::string isoTime(int64_t epochMs)
{
  const uint64_t ticks = static_cast<uint64_t>(epochMs) * 10000 + filetimeEpoch;
  FILETIME time{static_cast<DWORD>(ticks), static_cast<DWORD>(ticks >> 32)};
  SYSTEMTIME utc{};
  FileTimeToSystemTime(&time, &utc);
  char result[40]{};
  std::snprintf(result, sizeof(result), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ", utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond, utc.wMilliseconds);
  return result;
}

struct Recording {
  obs_source_t *video = nullptr;
  obs_source_t *audio = nullptr;
  obs_encoder_t *videoEncoder = nullptr;
  obs_encoder_t *audioEncoder = nullptr;
  obs_output_t *output = nullptr;
  std::string encoderId;
  ClockAnchor clock;
  std::atomic<int64_t> movieEpochMs{0};
  std::atomic<uint64_t> videoPackets{0};
  std::atomic<int64_t> lastVideoPtsNs{0};
  std::atomic<bool> firstPacketSeen{false};
  std::atomic<bool> stopped{false};
  std::atomic<int> stopCode{OBS_OUTPUT_SUCCESS};
  bool outputStarted = false;
  std::mutex statsMutex;
  revu::FrameStats frameStats{0, 0};
  void sampleLagged()
  {
    std::lock_guard lock(statsMutex);
    frameStats.countLagged(obs_get_lagged_frames(), lastVideoPtsNs.load());
  }
  ~Recording()
  {
    if (output) {
      watchdogDeadline = GetTickCount64() + drainTimeoutMs;
      if (outputStarted && !stopped) obs_output_force_stop(output);
      obs_output_remove_packet_callback(output, packet, this);
      signal_handler_disconnect(obs_output_get_signal_handler(output), "stop", stopSignal, this);
      obs_output_release(output);
    }
    if (videoEncoder) obs_encoder_release(videoEncoder);
    if (audioEncoder) obs_encoder_release(audioEncoder);
    obs_set_output_source(0, nullptr);
    obs_set_output_source(1, nullptr);
    if (video) obs_source_release(video);
    if (audio) obs_source_release(audio);
  }
  static void stopSignal(void *parameter, calldata_t *call)
  {
    auto &self = *static_cast<Recording *>(parameter);
    self.stopCode = static_cast<int>(calldata_int(call, "code"));
    self.stopped = true;
  }
  static void packet(obs_output_t *, encoder_packet *packet, encoder_packet_time *timing, void *parameter)
  {
    if (!packet || packet->type != OBS_ENCODER_VIDEO || packet->track_idx != 0 || packet->timebase_den <= 0) return;
    auto &self = *static_cast<Recording *>(parameter);
    const auto ptsNs = static_cast<int64_t>(static_cast<long double>(packet->pts) * packet->timebase_num * 1000000000.0L / packet->timebase_den);
    self.lastVideoPtsNs = ptsNs;
    ++self.videoPackets;
    {
      std::lock_guard lock(self.statsMutex);
      self.frameStats.countPacket(ptsNs, packet->size, packet->keyframe);
    }
    if (!self.firstPacketSeen.exchange(true) && timing && timing->cts && self.clock.uncertaintyNs <= 1000000) {
      // PTS here is the final interleaved PTS, after libobs start alignment.
      // CTS is OBS composition time, not the WGC source frame timestamp.
      self.movieEpochMs = self.clock.epochMilliseconds(static_cast<int64_t>(timing->cts) - ptsNs);
    }
  }
};

class Input {
  std::string pending;
public:
  bool shouldStop()
  {
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    DWORD available = 0;
    if (!PeekNamedPipe(input, nullptr, 0, nullptr, &available, nullptr)) {
      const auto error = GetLastError();
      if (error == ERROR_BROKEN_PIPE || error == ERROR_PIPE_NOT_CONNECTED || error == ERROR_INVALID_HANDLE) return true;
      throw Failure("control-pipe", "Recorder requires a parent-owned input pipe");
    }
    while (available) {
      char buffer[1024];
      DWORD read = 0;
      if (!ReadFile(input, buffer, std::min<DWORD>(available, sizeof(buffer)), &read, nullptr) || !read) return true;
      pending.append(buffer, read);
      if (pending.size() > 4096) throw Failure("invalid-command", "Recorder control line is too large");
      size_t end;
      while ((end = pending.find('\n')) != std::string::npos) {
        const auto line = pending.substr(0, end);
        pending.erase(0, end + 1);
        obs_data_t *data = obs_data_create_from_json(line.c_str());
        if (!data) throw Failure("invalid-command", "Recorder control command is invalid JSON");
        const std::string command = obs_data_get_string(data, "command");
        obs_data_release(data);
        if (command != "stop") throw Failure("invalid-command", "Unknown recorder control command");
        return true;
      }
      if (!PeekNamedPipe(input, nullptr, 0, nullptr, &available, nullptr)) return true;
    }
    return false;
  }
};

bool gameExited(HANDLE process)
{
  DWORD code = STILL_ACTIVE;
  if (!GetExitCodeProcess(process, &code)) throw Failure("game-process-unavailable", "Cannot observe the League game process lifetime");
  return code != STILL_ACTIVE;
}

struct GeneratedSource {
  obs_source_t *source;
  uint32_t width;
  uint32_t height;
  std::atomic<bool> stopping{false};
  std::thread audioThread;
  GeneratedSource(obs_source_t *source_, uint32_t width_, uint32_t height_) : source(source_), width(width_), height(height_)
  {
    audioThread = std::thread([this] {
      std::vector<float> left(480), right(480);
      uint64_t sample = 0;
      const uint64_t origin = os_gettime_ns();
      while (!stopping) {
        for (size_t index = 0; index < left.size(); ++index) {
          left[index] = right[index] = static_cast<float>(0.1 * std::sin(6.283185307179586 * 440 * static_cast<double>(sample + index) / 48000));
        }
        obs_source_audio audio{};
        audio.data[0] = reinterpret_cast<uint8_t *>(left.data());
        audio.data[1] = reinterpret_cast<uint8_t *>(right.data());
        audio.frames = static_cast<uint32_t>(left.size());
        audio.speakers = SPEAKERS_STEREO;
        audio.format = AUDIO_FORMAT_FLOAT_PLANAR;
        audio.samples_per_sec = 48000;
        audio.timestamp = origin + sample * 1000000000ULL / 48000;
        obs_source_output_audio(source, &audio);
        sample += left.size();
        const uint64_t next = origin + sample * 1000000000ULL / 48000;
        const uint64_t now = os_gettime_ns();
        if (next > now) std::this_thread::sleep_for(std::chrono::nanoseconds(next - now));
      }
    });
  }
  ~GeneratedSource() { stopping = true; if (audioThread.joinable()) audioThread.join(); }
};

void configureGeneratedSource(Recording &recording, const Preset &quality)
{
  // A manual diagnostic only. It contains no desktop/window/process capture.
  obs_source_info source{};
  source.id = "revu_generated_test";
  source.type = OBS_SOURCE_TYPE_INPUT;
  source.output_flags = OBS_SOURCE_VIDEO | OBS_SOURCE_AUDIO | OBS_SOURCE_CUSTOM_DRAW;
  source.get_name = [](void *) -> const char * { return "Revu generated recorder diagnostic"; };
  source.create = [](obs_data_t *settings, obs_source_t *source) -> void * {
    return new GeneratedSource(source, static_cast<uint32_t>(obs_data_get_int(settings, "width")), static_cast<uint32_t>(obs_data_get_int(settings, "height")));
  };
  source.destroy = [](void *data) { delete static_cast<GeneratedSource *>(data); };
  source.get_width = [](void *data) -> uint32_t { return static_cast<GeneratedSource *>(data)->width; };
  source.get_height = [](void *data) -> uint32_t { return static_cast<GeneratedSource *>(data)->height; };
  source.video_render = [](void *, gs_effect_t *) {
    vec4 color{};
    const double phase = static_cast<double>(os_gettime_ns() % 10000000000ULL) / 10000000000.0;
    color.x = static_cast<float>(phase);
    color.y = static_cast<float>(1.0 - phase);
    color.z = 0.5f;
    color.w = 1.0f;
    gs_clear(GS_CLEAR_COLOR, &color, 0, 0);
  };
  obs_register_source(&source);
  obs_data_t *settings = obs_data_create();
  obs_data_set_int(settings, "width", quality.width);
  obs_data_set_int(settings, "height", quality.height);
  recording.video = obs_source_create(source.id, "Generated recorder diagnostic", settings, nullptr);
  obs_data_release(settings);
  if (!recording.video) throw Failure("test-source-unavailable", "Generated diagnostic media could not initialize");
  obs_source_set_audio_mixers(recording.video, 1);
  obs_source_set_monitoring_type(recording.video, OBS_MONITORING_TYPE_NONE);
  obs_set_output_source(0, recording.video);
}

void configureSources(Recording &recording, const GameWindow &game)
{
  const auto window = windowSetting(game);
  obs_data_t *settings = obs_data_create();
  obs_data_set_string(settings, "window", window.c_str());
  // OBS title priority can select another executable with the same title. Exe
  // priority confines matching to League; startup also requires one live PID.
  obs_data_set_int(settings, "priority", 2);
  obs_data_set_int(settings, "method", 2); // Explicit WGC; support checked before creation.
  obs_data_set_bool(settings, "cursor", true);
  obs_data_set_bool(settings, "client_area", true);
  obs_data_set_bool(settings, "force_sdr", true);
  obs_data_set_bool(settings, "capture_audio", false); // Separate process-only audio source.
  obs_data_set_int(settings, "hwnd", static_cast<int64_t>(reinterpret_cast<uintptr_t>(game.window)));
  obs_data_set_int(settings, "pid", game.pid);
  obs_data_set_int(settings, "base_width", game.width);
  obs_data_set_int(settings, "base_height", game.height);
  obs_data_set_string(settings, "title", game.title.c_str());
  obs_data_set_string(settings, "class", game.windowClass.c_str());
  obs_data_set_string(settings, "executable", game.executable.c_str());
  recording.video = obs_source_create("revu_wgc_window", "League window", settings, nullptr);
  recording.audio = obs_source_create("wasapi_process_output_capture", "League audio", settings, nullptr);
  obs_data_release(settings);
  if (!recording.video || !recording.audio) throw Failure("capture-unavailable", "League window and application audio sources could not initialize");
  obs_source_set_audio_mixers(recording.audio, 1);
  obs_source_set_monitoring_type(recording.audio, OBS_MONITORING_TYPE_NONE);
  obs_set_output_source(0, recording.video);
  obs_set_output_source(1, recording.audio);
}

void configureOutput(Recording &recording, const std::filesystem::path &file, const Preset &quality, const revu::EncoderChoice &encoder)
{
  const auto config = revu::encoderConfiguration(encoder, quality.bitrate);
  if (!config) throw Failure("hardware-encoder-unavailable", "A compatible NVIDIA, AMD or Intel H.264 hardware encoder is required");
  recording.encoderId = config->encoder.id;
  recording.frameStats = revu::FrameStats(quality.bitrate, quality.fps);
  obs_data_t *settings = obs_encoder_defaults(recording.encoderId.c_str());
  if (!settings) throw Failure("hardware-encoder-unavailable", "The selected H.264 hardware encoder is unavailable");
  for (const auto &setting : config->settings) {
    const std::string name(setting.name);
    if (const auto *text = std::get_if<std::string_view>(&setting.value)) obs_data_set_string(settings, name.c_str(), std::string(*text).c_str());
    else if (const auto *integer = std::get_if<int64_t>(&setting.value)) obs_data_set_int(settings, name.c_str(), *integer);
    else obs_data_set_bool(settings, name.c_str(), std::get<bool>(setting.value));
  }
  recording.videoEncoder = obs_video_encoder_create(recording.encoderId.c_str(), "Revu hardware H.264", settings, nullptr);
  obs_data_release(settings);
  settings = obs_encoder_defaults("ffmpeg_aac");
  if (!settings) throw Failure("audio-encoder-unavailable", "The AAC encoder is unavailable");
  obs_data_set_int(settings, "bitrate", 160);
  recording.audioEncoder = obs_audio_encoder_create("ffmpeg_aac", "Revu AAC", settings, 0, nullptr);
  obs_data_release(settings);
  if (!recording.videoEncoder || !recording.audioEncoder) throw Failure("encoder-unavailable", "Recording encoders could not initialize");
  obs_encoder_set_video(recording.videoEncoder, obs_get_video());
  obs_encoder_set_audio(recording.audioEncoder, obs_get_audio());
  settings = obs_data_create();
  obs_data_set_string(settings, "path", utf8(file.wstring()).c_str());
  // Fragmented while recording, so an interrupted file stays playable. A clean stop
  // writes a complete index at the end, so players open it without walking every
  // fragment. The muxer finalizes in place: no remux, split or software fallback.
  obs_data_set_string(settings, "muxer_settings", "movflags=frag_keyframe+empty_moov+default_base_moof+hybrid_fragmented frag_duration=500000 avoid_negative_ts=disabled flush_packets=1");
  obs_data_set_bool(settings, "split_file", false);
  obs_data_set_bool(settings, "allow_overwrite", false);
  recording.output = obs_output_create("ffmpeg_muxer", "Revu MP4", settings, nullptr);
  obs_data_release(settings);
  if (!recording.output) throw Failure("output-unavailable", "MP4 recording output could not initialize");
  obs_output_set_video_encoder(recording.output, recording.videoEncoder);
  obs_output_set_audio_encoder(recording.output, recording.audioEncoder, 0);
  signal_handler_connect(obs_output_get_signal_handler(recording.output), "stop", Recording::stopSignal, &recording);
  obs_output_add_packet_callback(recording.output, Recording::packet, &recording);
}

void record(const std::filesystem::path &runtime, DWORD pid, const std::filesystem::path &file, const Preset &requestedQuality,
  bool selfTest = false, Priority level = Priority::BelowNormal)
{
  if (!file.is_absolute() || _wcsicmp(file.extension().c_str(), L".mp4") || std::filesystem::exists(file))
    throw Failure("invalid-output", "Recording output must be a new absolute MP4 path");
  GameWindow game{};
  if (selfTest) { game.width = requestedQuality.width; game.height = requestedQuality.height; }
  else {
    if (!leagueProcess(pid)) throw Failure("game-process-unavailable", "The selected League game process is unavailable");
    const auto games = discover();
    if (leaguePids().size() != 1) throw Failure("game-window-unavailable", "A single League game process is required");
    std::vector<GameWindow> matching;
    for (const auto &candidate : games) if (candidate.pid == pid) matching.push_back(candidate);
    if (matching.empty()) throw Failure("source-window-not-ready", "The League game window is not ready for capture");
    if (matching.size() != 1 || games.size() != 1) throw Failure("game-window-unavailable", "An unambiguous, visible League game window is required");
    game = matching.front();
  }
  Preset quality = requestedQuality;
  if (!selfTest) {
    const double scale = std::min({1.0, static_cast<double>(quality.width) / game.width, static_cast<double>(quality.height) / game.height});
    quality.width = static_cast<uint32_t>(std::floor(game.width * scale / 2)) * 2;
    quality.height = static_cast<uint32_t>(std::floor(game.height * scale / 2)) * 2;
    if (quality.width < 16 || quality.height < 16) throw Failure("source-window-not-ready", "The League game window is too small for capture");
  }
  Handle gameProcess(selfTest ? nullptr : OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
  if (!selfTest && !gameProcess) throw Failure("game-process-unavailable", "Cannot observe the League game process");
  // Set before libobs starts its threads and the muxer child, which inherit it.
  if (level == Priority::Normal && !SetPriorityClass(GetCurrentProcess(), NORMAL_PRIORITY_CLASS))
    throw Failure("process-priority", "Cannot set the recorder priority");
  ObsContext obs;
  obs.initialize(runtime, quality, game.width, game.height, !selfTest, level);
  if (!selfTest && (!wgcSupported(runtime) || !captureSourcesSupported())) throw Failure("wgc-unavailable", "Windows Graphics Capture or application audio is unavailable; no other capture method is allowed");
  const auto hardware = encoders(obs.adapter.vendor());
  if (hardware.empty()) throw Failure("hardware-encoder-unavailable", "A compatible NVIDIA, AMD or Intel H.264 hardware encoder is required");
  Recording recording;
  Input input;
  if (selfTest) configureGeneratedSource(recording, quality);
  else configureSources(recording, game);
  const auto captureDeadline = GetTickCount64() + 12000;
  while (!selfTest && (!sourceHooked(recording.video, game) || !sourceHooked(recording.audio, game) || !obs_source_get_width(recording.video) || !obs_source_get_height(recording.video))) {
    if (input.shouldStop()) throw Failure("recording-cancelled", "Recording was cancelled before media capture started");
    if (GetTickCount64() >= captureDeadline) {
      // Alt-tab in exclusive Full Screen minimizes League, and a minimized window yields no
      // frames. That is not a capture fault: report it as not ready so the host retries.
      if (IsIconic(game.window)) throw Failure("source-window-not-ready", "The League game window was minimized while capture started");
      throw Failure("capture-unavailable", "League window or application audio capture did not become active");
    }
    Sleep(50);
  }
  configureOutput(recording, file, quality, hardware.front());
  if (!obs_output_start(recording.output)) throw Failure("recording-start-failed", "Hardware recording output could not start");
  recording.outputStarted = true;
  {
    // Ticks missed while modules loaded happened before the movie began.
    std::lock_guard lock(recording.statsMutex);
    recording.frameStats.startLagged(obs_get_lagged_frames());
  }
  bool emittedStart = false;
  bool requestedStop = false;
  std::string failureCode;
  uint64_t nextHeartbeat = GetTickCount64() + 1000;
  const uint64_t mediaDeadline = GetTickCount64() + 10000;
  uint64_t selfTestDeadline = 0;
  uint64_t nextIdentityCheck = GetTickCount64() + 1000;
  uint64_t nextPriorityCheck = GetTickCount64() + 1000;
  uint64_t identityLostAt = 0;
  while (!recording.stopped) {
    const auto now = GetTickCount64();
    recording.sampleLagged();
    if (!emittedStart && recording.movieEpochMs.load() > 0 && recording.videoPackets.load() > 0) {
      const auto startedAt = recording.movieEpochMs.load();
      emit("{\"type\":\"started\",\"encoder\":" + quote(recording.encoderId) + ",\"width\":" + std::to_string(quality.width) + ",\"height\":" + std::to_string(quality.height) + ",\"fps\":" + std::to_string(quality.fps) + ",\"bitrate\":" + std::to_string(quality.bitrate) + ",\"sourceWidth\":" + std::to_string(game.width) + ",\"sourceHeight\":" + std::to_string(game.height) + ",\"priority\":" + quote(priorityName(level)) + ",\"gpuPriority\":" + quote(gpuClassName(obs.gpuClass)) + ",\"captureMinIntervalHns\":" + capturePacing(recording.video) + ",\"startedAtVerified\":true,\"startedAt\":" + quote(isoTime(startedAt)) + ",\"startTimeEpoch\":" + std::to_string(startedAt) + ",\"clockSource\":\"obs-composition-packet-pts\",\"clockUncertaintyMs\":1}");
      emittedStart = true;
      if (selfTest) selfTestDeadline = now + 5000;
      watchdogDeadline = 0;
    }
    if (!requestedStop && !emittedStart && now >= mediaDeadline) { failureCode = "media-clock-unverified"; requestedStop = true; }
    if (!requestedStop && (selfTest ? (selfTestDeadline && now >= selfTestDeadline) : (input.shouldStop() || gameExited(gameProcess.value)))) requestedStop = true;
    if (!requestedStop && !selfTest) {
      DWORD currentPid = 0;
      GetWindowThreadProcessId(game.window, &currentPid);
      // Windows closes the capture with its window, and a lost Direct3D device ends it for good;
      // nothing rebuilds or reacquires it, so a stopped capture counts as a lost window.
      if (!IsWindow(game.window) || currentPid != game.pid || !sourceHooked(recording.video, game)) {
        if (!identityLostAt) identityLostAt = now;
        // A normal game exit can destroy its HWND just before the process exits.
        // The source stops drawing immediately and never retargets another HWND;
        // allow the process-exit signal to arrive before classifying it partial.
        if (now - identityLostAt >= 2000) { failureCode = "source-window-lost"; requestedStop = true; }
      } else identityLostAt = 0;
      if (now >= nextIdentityCheck) {
        if (leaguePids().size() > 1) { failureCode = "game-process-ambiguous"; requestedStop = true; }
        nextIdentityCheck = now + 1000;
      }
    }
    if (!requestedStop && now >= nextPriorityCheck) {
      // Stopping is better than letting a recording outrank the game for GPU time.
      if (!obs.keepGpuPriority()) { failureCode = "gpu-priority-unavailable"; requestedStop = true; }
      nextPriorityCheck = now + 1000;
    }
    if (requestedStop) {
      // Set once; a continually moving deadline would fail to bound shutdown.
      watchdogDeadline = now + drainTimeoutMs;
      obs_output_stop(recording.output);
      break;
    }
    if (emittedStart && now >= nextHeartbeat) {
      emit("{\"type\":\"heartbeat\",\"frames\":" + std::to_string(recording.videoPackets.load()) + ",\"droppedFrames\":" + std::to_string(std::max(0, obs_output_get_frames_dropped(recording.output))) + ",\"laggedFrames\":" + std::to_string(obs_get_lagged_frames()) + ",\"bytes\":" + std::to_string(obs_output_get_total_bytes(recording.output)) + '}');
      nextHeartbeat = now + 1000;
    }
    Sleep(50);
  }
  watchdogDeadline = GetTickCount64() + drainTimeoutMs;
  while (!recording.stopped) Sleep(25);
  if (!failureCode.empty()) emit("{\"type\":\"error\",\"code\":" + quote(failureCode) + ",\"message\":\"Recording could not continue with verified media timing and game identity\"}");
  const bool hasError = recording.stopCode != OBS_OUTPUT_SUCCESS || !emittedStart || !failureCode.empty();
  const auto frames = recording.videoPackets.load();
  const double duration = frames ? static_cast<double>(recording.lastVideoPtsNs.load()) / 1e6 + 1000.0 / quality.fps : 0;
  recording.sampleLagged();
  std::string perMinute;
  uint64_t repeated = 0;
  {
    std::lock_guard lock(recording.statsMutex);
    perMinute = recording.frameStats.json();
    repeated = recording.frameStats.repeatedTotal();
  }
  emit("{\"type\":\"stopped\",\"hasError\":" + std::string(hasError ? "true" : "false") + ",\"filePath\":" + quote(utf8(file.wstring())) + ",\"duration\":" + std::to_string(duration) + ",\"startTimeEpoch\":" + std::to_string(recording.movieEpochMs.load()) + ",\"splitCount\":0,\"stats\":{\"frames\":" + std::to_string(frames) + ",\"droppedFrames\":" + std::to_string(std::max(0, obs_output_get_frames_dropped(recording.output))) + ",\"laggedFrames\":" + std::to_string(obs_get_lagged_frames()) + ",\"repeatedFrames\":" + std::to_string(repeated) + ",\"perMinute\":" + perMinute + ",\"stopCode\":" + std::to_string(recording.stopCode.load()) + "}}");
}
} // namespace

int wmain(int argc, wchar_t **argv)
{
  try {
    boundProcess();
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    const auto runtime = runtimeDirectory();
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    AddDllDirectory(runtime.c_str());
    std::filesystem::current_path(runtime);
    if (argc == 2 && std::wstring(argv[1]) == L"--discover") { emitGames(); watchdogDone = true; return 0; }
    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com)) throw Failure("com-unavailable", "Windows media initialization failed");
    if (argc == 2 && std::wstring(argv[1]) == L"--probe") {
      ObsContext obs;
      obs.initialize(runtime, preset(L"720p30"), 1280, 720);
      const auto available = encoders(obs.adapter.vendor());
      const bool capture = wgcSupported(runtime) && captureSourcesSupported();
      const bool supported = capture && !available.empty();
      std::string list;
      for (const auto &encoder : available) { if (!list.empty()) list += ','; list += quote(std::string(encoder.id)); }
      const auto selected = supported ? quote(std::string(available.front().id)) : "null";
      const auto unavailable = supported ? "" : (",\"code\":" + quote(capture ? "hardware-encoder-unavailable" : "capture-unavailable"));
      emit("{\"type\":\"capabilities\",\"protocol\":1,\"available\":" + std::string(supported ? "true" : "false") + ",\"encoders\":[" + list + "],\"selectedEncoder\":" + selected + ",\"adapterVendor\":" + quote(std::string(revu::vendorName(obs.adapter.vendor()))) + ",\"adapterVendorId\":" + std::to_string(obs.adapter.vendorId) + ",\"adapterLuid\":" + quote(std::to_string(obs.adapter.luid)) + ",\"version\":\"32.2.2\",\"capture\":\"wgc-window\",\"audio\":\"application-only\"" + unavailable + '}');
    } else if (argc == 4 && std::wstring(argv[1]) == L"--self-test" && std::wstring(argv[2]) == L"--output") {
      record(runtime, 0, argv[3], preset(L"720p30"), true);
    } else if ((argc == 8 || (argc == 10 && std::wstring(argv[8]) == L"--priority")) && std::wstring(argv[1]) == L"--record" && std::wstring(argv[2]) == L"--pid" && std::wstring(argv[4]) == L"--output" && std::wstring(argv[6]) == L"--preset") {
      const std::wstring value = argv[3];
      if (value.empty() || value.find_first_not_of(L"0123456789") != std::wstring::npos) throw Failure("invalid-pid", "A positive game process id is required");
      const auto pid = std::stoull(value);
      if (!pid || pid > MAXDWORD) throw Failure("invalid-pid", "A positive game process id is required");
      record(runtime, static_cast<DWORD>(pid), argv[5], preset(argv[7]), false, argc == 10 ? priority(argv[9]) : Priority::BelowNormal);
    } else throw Failure("invalid-arguments", "Use --probe, --discover, or --record --pid PID --output ABSOLUTE.mp4 --preset 720p30|1080p30|1080p60 [--priority below-normal|normal]");
    CoUninitialize();
    watchdogDone = true;
    return 0;
  } catch (const Failure &error) {
    emit("{\"type\":\"error\",\"code\":" + quote(error.code) + ",\"message\":" + quote(error.what()) + '}');
  } catch (const std::exception &) {
    emit("{\"type\":\"error\",\"code\":\"recorder-failed\",\"message\":\"The native recording helper could not complete\"}");
  }
  watchdogDone = true;
  return 1;
}
