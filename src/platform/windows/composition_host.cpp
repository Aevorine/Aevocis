#include "aevocis/platform/windows/composition_host.hpp"

#include <algorithm>

namespace aevocis::platform::windows {

CompositionHost& CompositionHost::instance() noexcept {
    static CompositionHost host;
    return host;
}

CompositionHost::CompositionHost() noexcept {
    const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL feature_level{};
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION,
                                    d3d_device_.GetAddressOf(), &feature_level, nullptr);
    if (FAILED(hr)) {
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION,
                                d3d_device_.GetAddressOf(), &feature_level, nullptr);
    }
    if (FAILED(hr) || d3d_device_ == nullptr) {
        return;
    }
    if (FAILED(d3d_device_.As(&dxgi_device_)) || dxgi_device_ == nullptr) {
        return;
    }
    Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;
    if (FAILED(dxgi_device_->GetAdapter(&adapter)) || adapter == nullptr) {
        return;
    }
    if (FAILED(adapter->GetParent(IID_PPV_ARGS(&dxgi_factory_))) || dxgi_factory_ == nullptr) {
        return;
    }
    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2d_factory_.GetAddressOf())) || d2d_factory_ == nullptr) {
        return;
    }
    if (FAILED(d2d_factory_->CreateDevice(dxgi_device_.Get(), &d2d_device_)) || d2d_device_ == nullptr) {
        return;
    }
    if (FAILED(DCompositionCreateDevice2(dxgi_device_.Get(), IID_PPV_ARGS(&composition_device_))) || composition_device_ == nullptr) {
        return;
    }
    if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                    reinterpret_cast<IUnknown**>(write_factory_.GetAddressOf()))) ||
        write_factory_ == nullptr) {
        return;
    }
    available_ = true;
}

bool CompositionSurface::attach(HWND hwnd, int width, int height) noexcept {
    auto& host = CompositionHost::instance();
    if (!host.available() || hwnd == nullptr) {
        return false;
    }
    width_ = std::max(width, 1);
    height_ = std::max(height, 1);

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = static_cast<UINT>(width_);
    desc.Height = static_cast<UINT>(height_);
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.Stereo = FALSE;
    desc.SampleDesc.Count = 1;
    desc.SampleDesc.Quality = 0;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.Scaling = DXGI_SCALING_STRETCH;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;

    if (FAILED(host.dxgi_factory()->CreateSwapChainForComposition(host.d3d_device(), &desc, nullptr, &swap_chain_)) ||
        swap_chain_ == nullptr) {
        return false;
    }
    if (FAILED(host.composition_device()->CreateTargetForHwnd(hwnd, TRUE, &target_)) || target_ == nullptr) {
        return false;
    }
    Microsoft::WRL::ComPtr<IDCompositionVisual2> visual;
    if (FAILED(host.composition_device()->CreateVisual(&visual)) || visual == nullptr) {
        return false;
    }
    if (FAILED(visual.As(&visual_)) || visual_ == nullptr) {
        return false;
    }
    if (FAILED(visual_->SetContent(swap_chain_.Get()))) {
        return false;
    }
    if (FAILED(target_->SetRoot(visual_.Get()))) {
        return false;
    }
    if (FAILED(host.d2d_device()->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &device_context_)) || device_context_ == nullptr) {
        return false;
    }
    (void)host.composition_device()->Commit();
    return true;
}

void CompositionSurface::resize(int width, int height) noexcept {
    if (swap_chain_ == nullptr || device_context_ == nullptr) {
        return;
    }
    width_ = std::max(width, 1);
    height_ = std::max(height, 1);
    device_context_->SetTarget(nullptr);
    target_bitmap_.Reset();
    (void)swap_chain_->ResizeBuffers(0, static_cast<UINT>(width_), static_cast<UINT>(height_), DXGI_FORMAT_B8G8R8A8_UNORM, 0);
}

ID2D1DeviceContext* CompositionSurface::begin_draw() noexcept {
    if (device_context_ == nullptr || swap_chain_ == nullptr) {
        return nullptr;
    }
    if (target_bitmap_ == nullptr) {
        Microsoft::WRL::ComPtr<IDXGISurface> surface;
        if (FAILED(swap_chain_->GetBuffer(0, IID_PPV_ARGS(&surface))) || surface == nullptr) {
            return nullptr;
        }
        const auto properties =
            D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
                                     D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
        if (FAILED(device_context_->CreateBitmapFromDxgiSurface(surface.Get(), &properties, &target_bitmap_)) || target_bitmap_ == nullptr) {
            return nullptr;
        }
        device_context_->SetTarget(target_bitmap_.Get());
    }
    device_context_->BeginDraw();
    return device_context_.Get();
}

void CompositionSurface::end_draw() noexcept {
    if (device_context_ == nullptr) {
        return;
    }
    const HRESULT hr = device_context_->EndDraw();
    if (hr == D2DERR_RECREATE_TARGET) {
        device_context_->SetTarget(nullptr);
        target_bitmap_.Reset();
        return;
    }
    if (swap_chain_ != nullptr) {
        DXGI_PRESENT_PARAMETERS params{};
        (void)swap_chain_->Present1(1, 0, &params);
    }
}

void CompositionSurface::set_opacity(float opacity) noexcept {
    if (visual_ == nullptr) {
        return;
    }
    (void)visual_->SetOpacity(std::clamp(opacity, 0.0F, 1.0F));
    (void)CompositionHost::instance().composition_device()->Commit();
}

void CompositionSurface::set_offset(float x, float y) noexcept {
    if (visual_ == nullptr) {
        return;
    }
    (void)visual_->SetOffsetX(x);
    (void)visual_->SetOffsetY(y);
    (void)CompositionHost::instance().composition_device()->Commit();
}

}  // namespace aevocis::platform::windows
