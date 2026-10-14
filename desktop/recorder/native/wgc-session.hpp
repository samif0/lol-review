// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once

// Revu's own Windows Graphics Capture session for the one League window a recording is bound
// to. It replaces the pinned libobs-winrt 32.2.2 capture calls so Revu can pace the session
// (capture-pacing.hpp); the client-area copy and the draw follow libobs-winrt (OBS Studio,
// GPL-2.0-or-later). libobs-winrt.dll stays loaded because libobs uses it to give its graphics
// thread the dispatcher queue this session's frame pool runs on.

#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <dwmapi.h>
#include <inspectable.h>
#include <obs.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <memory>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include "capture-pacing.hpp"

namespace revu {
namespace wgc = winrt::Windows::Graphics::Capture;

// Captures one fixed window's client area, cursor included, in SDR BGRA. Create, use and destroy
// it on the libobs graphics thread. The frame pool is not free-threaded, so Windows queues each
// FrameArrived on that thread and libobs runs them once per video tick, after the sources tick
// and before the tick renders. Nothing ever retargets, rebuilds or restarts a capture.
class WgcWindowCapture {
public:
  // Call inside obs_enter_graphics. Returns null when Windows cannot capture the window.
  static WgcWindowCapture *start(HWND window, uint32_t fps)
  {
    std::unique_ptr<WgcWindowCapture> capture(new WgcWindowCapture(window));
    try {
      capture->open(fps);
      return capture.release();
    } catch (...) {
      report("could not start", winrt::to_hresult());
      return nullptr;
    }
  }
  ~WgcWindowCapture()
  {
    {
      Graphics graphics;
      if (lossCallbacks_) gs_unregister_loss_callbacks(this);
      if (texture_) gs_texture_destroy(texture_);
    }
    close();
  }
  WgcWindowCapture(const WgcWindowCapture &) = delete;
  WgcWindowCapture &operator=(const WgcWindowCapture &) = delete;

  // False for good once Windows closes the window's capture item, the Direct3D device is lost or
  // a frame cannot be used.
  bool active() const { return events_->active; }
  // Size of the newest copied client area; 0 until the first frame arrives.
  uint32_t width() const { return width_; }
  uint32_t height() const { return height_; }
  // The capture pacing Windows applies to this session, or 0 when it keeps its own default.
  int64_t minUpdateIntervalHns() const { return minUpdateIntervalHns_; }

  void showCursor(bool visible)
  {
    if (!cursorToggle_ || !session_ || visible == cursorVisible_) return;
    try {
      session_.IsCursorCaptureEnabled(visible);
      cursorVisible_ = visible;
    } catch (...) {
      cursorToggle_ = false;
      report("could not change the cursor", winrt::to_hresult());
    }
  }

  // Call inside video_render. Draws the newest client area at its own size.
  void render() const
  {
    if (!texture_ || !width_) return;
    gs_effect_t *effect = obs_get_base_effect(OBS_EFFECT_DEFAULT);
    gs_technique_t *technique = gs_effect_get_technique(effect, "Draw");
    const bool previous = gs_framebuffer_srgb_enabled();
    gs_enable_framebuffer_srgb(true);
    gs_blend_state_push();
    gs_blend_function(GS_BLEND_ONE, GS_BLEND_INVSRCALPHA);
    gs_effect_set_texture_srgb(gs_effect_get_param_by_name(effect, "image"), texture_);
    const size_t passes = gs_technique_begin(technique);
    for (size_t pass = 0; pass < passes; ++pass) {
      if (gs_technique_begin_pass(technique, pass)) {
        gs_draw_sprite(texture_, 0, 0, 0);
        gs_technique_end_pass(technique);
      }
    }
    gs_technique_end(technique);
    gs_blend_state_pop();
    gs_enable_framebuffer_srgb(previous);
  }

private:
  struct Graphics {
    Graphics() { obs_enter_graphics(); }
    ~Graphics() { obs_leave_graphics(); }
    Graphics(const Graphics &) = delete;
    Graphics &operator=(const Graphics &) = delete;
  };
  // Shared with the event handlers, so an event Windows still delivers after close() never
  // reaches a destroyed capture.
  struct Events {
    std::atomic<bool> open{true};
    std::atomic<bool> active{false};
  };
  static constexpr auto pixelFormat = winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized;
  static constexpr int32_t buffers = 2;
  static constexpr const wchar_t *sessionClass = L"Windows.Graphics.Capture.GraphicsCaptureSession";

  explicit WgcWindowCapture(HWND window) : window_(window) {}

  static void report(const char *what, winrt::hresult code) noexcept
  {
    std::fprintf(stderr, "[recorder] Window capture %s (0x%08X)\n", what, static_cast<unsigned>(code.value));
  }

  void open(uint32_t fps)
  {
    auto *d3d = static_cast<ID3D11Device *>(gs_get_device_obj());
    winrt::com_ptr<IDXGIDevice> dxgi;
    winrt::check_hresult(d3d ? d3d->QueryInterface(IID_PPV_ARGS(dxgi.put())) : E_POINTER);
    winrt::com_ptr<::IInspectable> inspectable;
    winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
    device_ = inspectable.as<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();
    d3d->GetImmediateContext(context_.put());
    const auto interop = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    winrt::check_hresult(interop->CreateForWindow(window_, winrt::guid_of<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem>(),
      winrt::put_abi(item_)));
    size_ = item_.Size();
    pool_ = wgc::Direct3D11CaptureFramePool::Create(device_, pixelFormat, buffers, size_);
    session_ = pool_.CreateCaptureSession(item_);
    using winrt::Windows::Foundation::Metadata::ApiInformation;
    if (ApiInformation::IsPropertyPresent(sessionClass, L"IsBorderRequired")) {
      wgc::GraphicsCaptureAccess::RequestAccessAsync(wgc::GraphicsCaptureAccessKind::Borderless).get();
      session_.IsBorderRequired(false);
    }
    cursorToggle_ = ApiInformation::IsPropertyPresent(sessionClass, L"IsCursorCaptureEnabled");
    if (cursorToggle_) session_.IsCursorCaptureEnabled(cursorVisible_);
    pace(fps);
    closed_ = item_.Closed(winrt::auto_revoke, [events = events_](const wgc::GraphicsCaptureItem &, const winrt::Windows::Foundation::IInspectable &) {
      events->active = false;
    });
    frameArrived_ = pool_.FrameArrived(winrt::auto_revoke,
      [events = events_, this](const wgc::Direct3D11CaptureFramePool &sender, const winrt::Windows::Foundation::IInspectable &) {
        if (events->open) onFrameArrived(sender);
      });
    session_.StartCapture();
    events_->active = true;
    gs_device_loss callbacks{lost, rebuilt, this};
    gs_register_loss_callbacks(&callbacks);
    lossCallbacks_ = true;
  }

  // Windows' default pacing drops game frames a 60 FPS recording needs (capture-pacing.hpp).
  // The property exists from Windows 11 24H2; earlier builds keep their own pacing.
  void pace(uint32_t fps)
  {
    const int64_t wanted = captureMinUpdateIntervalHns(fps);
    if (!wanted || !winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(sessionClass, L"MinUpdateInterval")) {
      std::fputs("[recorder] Window capture keeps the Windows capture pacing\n", stderr);
      return;
    }
    try {
      const int64_t initial = session_.MinUpdateInterval().count();
      session_.MinUpdateInterval(winrt::Windows::Foundation::TimeSpan{wanted});
      minUpdateIntervalHns_ = session_.MinUpdateInterval().count();
      std::fprintf(stderr, "[recorder] Window capture paced at %lld hns (Windows default %lld hns)\n",
        static_cast<long long>(minUpdateIntervalHns_), static_cast<long long>(initial));
    } catch (...) {
      minUpdateIntervalHns_ = 0;
      report("keeps the Windows capture pacing", winrt::to_hresult());
    }
  }

  void onFrameArrived(const wgc::Direct3D11CaptureFramePool &sender) noexcept
  {
    // A stopped capture leaves its frames in the pool; once it is full Windows stops copying.
    if (!events_->active) return;
    try {
      // Every frame that arrived since the last tick waits in the pool. Keep the newest and
      // hand the older ones straight back.
      wgc::Direct3D11CaptureFrame newest{nullptr};
      for (auto frame = sender.TryGetNextFrame(); frame; frame = sender.TryGetNextFrame()) {
        if (newest) newest.Close();
        newest = std::move(frame);
      }
      if (!newest) return;
      const auto size = newest.ContentSize();
      copy(newest);
      newest.Close();
      if (size.Width != size_.Width || size.Height != size_.Height) {
        // The window changed size: later frames arrive at its new size.
        Graphics graphics;
        pool_.Recreate(device_, pixelFormat, buffers, size);
        size_ = size;
      }
    } catch (...) {
      fail("stopped on a frame", winrt::to_hresult());
    }
  }

  void copy(const wgc::Direct3D11CaptureFrame &frame)
  {
    winrt::com_ptr<ID3D11Texture2D> surface;
    winrt::check_hresult(frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>()
      ->GetInterface(winrt::guid_of<ID3D11Texture2D>(), surface.put_void()));
    D3D11_TEXTURE2D_DESC description{};
    surface->GetDesc(&description); // The texture bounds the copy; ContentSize is not reliable.
    if (description.Format != DXGI_FORMAT_B8G8R8A8_UNORM) { fail("received a frame that is not SDR BGRA", E_UNEXPECTED); return; }
    D3D11_BOX box{};
    if (!clientBox(description.Width, description.Height, &box)) return; // Minimized: keep the last frame.
    const uint32_t width = box.right - box.left, height = box.bottom - box.top;
    Graphics graphics;
    if (texture_ && (gs_texture_get_width(texture_) != width || gs_texture_get_height(texture_) != height)) {
      gs_texture_destroy(texture_);
      texture_ = nullptr;
    }
    if (!texture_) texture_ = gs_texture_create(width, height, GS_BGRA, 1, nullptr, 0);
    auto *target = texture_ ? static_cast<ID3D11Texture2D *>(gs_texture_get_obj(texture_)) : nullptr;
    if (!target) { fail("could not create its texture", E_OUTOFMEMORY); return; }
    context_->CopySubresourceRegion(target, 0, 0, 0, 0, surface.get(), 0, &box);
    width_ = width;
    height_ = height;
  }

  bool clientBox(uint32_t width, uint32_t height, D3D11_BOX *box) const
  {
    RECT client{}, frame{};
    POINT origin{};
    // Minimized is checked twice; a change and change back between the reads is very unlikely.
    if (IsIconic(window_) || !GetClientRect(window_, &client) || IsIconic(window_) || client.right <= 0 || client.bottom <= 0
        || DwmGetWindowAttribute(window_, DWMWA_EXTENDED_FRAME_BOUNDS, &frame, sizeof(frame)) != S_OK || !ClientToScreen(window_, &origin))
      return false;
    const auto left = static_cast<uint32_t>(origin.x > frame.left ? origin.x - frame.left : 0);
    const auto top = static_cast<uint32_t>(origin.y > frame.top ? origin.y - frame.top : 0);
    box->left = left;
    box->top = top;
    box->right = left + (width > left ? std::min(width - left, static_cast<uint32_t>(client.right)) : 1);
    box->bottom = top + (height > top ? std::min(height - top, static_cast<uint32_t>(client.bottom)) : 1);
    box->front = 0;
    box->back = 1;
    return box->right <= width && box->bottom <= height;
  }

  void fail(const char *what, winrt::hresult code) noexcept
  {
    if (events_->active.exchange(false)) report(what, code);
  }

  // Stops capturing and releases every capture object. Safe to repeat.
  void close() noexcept
  {
    events_->open = false;
    events_->active = false;
    frameArrived_.revoke();
    closed_.revoke();
    try { if (pool_) pool_.Close(); } catch (...) {}
    try { if (session_) session_.Close(); } catch (...) {}
    pool_ = nullptr;
    session_ = nullptr;
    item_ = nullptr;
    device_ = nullptr;
    context_ = nullptr;
  }

  // libobs rebuilds its Direct3D device after a driver reset. This capture belonged to the old
  // device and is not rebuilt: it stops, and the recording ends as a lost window.
  static void lost(void *data)
  {
    auto &self = *static_cast<WgcWindowCapture *>(data);
    if (self.events_->active) std::fputs("[recorder] Window capture stopped: the Direct3D device was lost\n", stderr);
    self.close();
  }
  static void rebuilt(void *, void *) {}

  HWND window_;
  std::shared_ptr<Events> events_ = std::make_shared<Events>();
  std::atomic<uint32_t> width_{0};
  std::atomic<uint32_t> height_{0};
  int64_t minUpdateIntervalHns_ = 0;
  bool cursorToggle_ = false;
  bool cursorVisible_ = true;
  bool lossCallbacks_ = false;
  gs_texture_t *texture_ = nullptr;
  winrt::com_ptr<ID3D11DeviceContext> context_;
  winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice device_{nullptr};
  wgc::GraphicsCaptureItem item_{nullptr};
  wgc::Direct3D11CaptureFramePool pool_{nullptr};
  wgc::GraphicsCaptureSession session_{nullptr};
  winrt::Windows::Graphics::SizeInt32 size_{};
  wgc::GraphicsCaptureItem::Closed_revoker closed_;
  wgc::Direct3D11CaptureFramePool::FrameArrived_revoker frameArrived_;
};
} // namespace revu
