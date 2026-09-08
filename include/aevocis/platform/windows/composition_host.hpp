#pragma once

#include <windows.h>
#include <d2d1_1.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dcomp.h>
#include <dwrite.h>
#include <wrl/client.h>

namespace aevocis::platform::windows {

class CompositionHost {
public:
    static CompositionHost& instance() noexcept;

    CompositionHost(const CompositionHost&) = delete;
    CompositionHost& operator=(const CompositionHost&) = delete;

    [[nodiscard]] bool available() const noexcept { return available_; }
    [[nodiscard]] ID3D11Device* d3d_device() const noexcept { return d3d_device_.Get(); }
    [[nodiscard]] ID2D1Device* d2d_device() const noexcept { return d2d_device_.Get(); }
    [[nodiscard]] IDXGIFactory2* dxgi_factory() const noexcept { return dxgi_factory_.Get(); }
    [[nodiscard]] IDCompositionDesktopDevice* composition_device() const noexcept { return composition_device_.Get(); }
    [[nodiscard]] IDWriteFactory* write_factory() const noexcept { return write_factory_.Get(); }

private:
    CompositionHost() noexcept;

    Microsoft::WRL::ComPtr<ID3D11Device> d3d_device_;
    Microsoft::WRL::ComPtr<IDXGIDevice> dxgi_device_;
    Microsoft::WRL::ComPtr<IDXGIFactory2> dxgi_factory_;
    Microsoft::WRL::ComPtr<ID2D1Factory1> d2d_factory_;
    Microsoft::WRL::ComPtr<ID2D1Device> d2d_device_;
    Microsoft::WRL::ComPtr<IDCompositionDesktopDevice> composition_device_;
    Microsoft::WRL::ComPtr<IDWriteFactory> write_factory_;
    bool available_{false};
};

// GPU-composited, truly-transparent per-pixel-alpha surface for one HWND: a DXGI swap chain
// bound into the DirectComposition visual tree, drawn into via a Direct2D device context.
// Falls back to unavailable() == not attached when the host device stack failed to create
// (old GPU / RDP without hardware accel / WARP unavailable) so callers can keep their existing
// ID2D1HwndRenderTarget code path instead of crashing.
class CompositionSurface {
public:
    CompositionSurface() = default;
    CompositionSurface(const CompositionSurface&) = delete;
    CompositionSurface& operator=(const CompositionSurface&) = delete;
    ~CompositionSurface() = default;

    [[nodiscard]] bool attach(HWND hwnd, int width, int height) noexcept;
    void resize(int width, int height) noexcept;
    [[nodiscard]] ID2D1DeviceContext* begin_draw() noexcept;
    void end_draw() noexcept;
    void set_opacity(float opacity) noexcept;
    void set_offset(float x, float y) noexcept;

private:
    Microsoft::WRL::ComPtr<IDCompositionTarget> target_;
    Microsoft::WRL::ComPtr<IDCompositionVisual3> visual_;
    Microsoft::WRL::ComPtr<IDXGISwapChain1> swap_chain_;
    Microsoft::WRL::ComPtr<ID2D1DeviceContext> device_context_;
    Microsoft::WRL::ComPtr<ID2D1Bitmap1> target_bitmap_;
    int width_{0};
    int height_{0};
};

}  // namespace aevocis::platform::windows
