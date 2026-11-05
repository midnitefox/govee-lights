// Reduce the newest primary-display frame on its owning GPU. Only the 64x36
// color samples cross to the CPU, and nothing is written to disk.
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <new>
#include <cstring>
#include "DownsampleShader.h"

using Microsoft::WRL::ComPtr;
constexpr UINT SampleBytes = 64 * 36 * sizeof(UINT);
#define CHECK(expr) do { HRESULT result = (expr); if (FAILED(result)) return result; } while (false)

struct Capture {
    bool haveDesktop{};
    HMONITOR monitor{};
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGIOutputDuplication> duplication;
    ComPtr<ID3D11ComputeShader> shader;
    ComPtr<ID3D11Texture2D> copy;
    ComPtr<ID3D11ShaderResourceView> copyView;
    ComPtr<ID3D11Buffer> reduced, staging;
    ComPtr<ID3D11UnorderedAccessView> output;

    HRESULT Init(IDXGIAdapter1* adapter, IDXGIOutput* out) {
        D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
        CHECK(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, &level, 1, D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<IDXGIOutput1> modern;
        CHECK(out->QueryInterface(IID_PPV_ARGS(&modern)));
        CHECK(modern->DuplicateOutput(device.Get(), &duplication));
        return SetupReduction();
    }
    HRESULT SetupReduction() {
        CHECK(device->CreateComputeShader(GoveeDownsampleShader, sizeof(GoveeDownsampleShader), nullptr, &shader));
        D3D11_BUFFER_DESC desc{};
        desc.ByteWidth = SampleBytes;
        desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
        desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
        desc.StructureByteStride = sizeof(UINT);
        CHECK(device->CreateBuffer(&desc, nullptr, &reduced));
        D3D11_UNORDERED_ACCESS_VIEW_DESC view{};
        view.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
        view.Buffer.NumElements = SampleBytes / sizeof(UINT);
        CHECK(device->CreateUnorderedAccessView(reduced.Get(), &view, &output));
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.MiscFlags = 0;
        desc.StructureByteStride = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        return device->CreateBuffer(&desc, nullptr, &staging);
    }
    HRESULT Open() {
        monitor = MonitorFromPoint({0,0}, MONITOR_DEFAULTTOPRIMARY);
        ComPtr<IDXGIFactory1> factory;
        CHECK(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        for (UINT a=0;; ++a) {
            ComPtr<IDXGIAdapter1> adapter;
            HRESULT hr = factory->EnumAdapters1(a, &adapter);
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            CHECK(hr);
            for (UINT i=0;; ++i) {
                ComPtr<IDXGIOutput> out;
                hr = adapter->EnumOutputs(i, &out);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                CHECK(hr);
                DXGI_OUTPUT_DESC info{};
                CHECK(out->GetDesc(&info));
                if (!info.AttachedToDesktop || info.Monitor != monitor) continue;
                if (info.Rotation != DXGI_MODE_ROTATION_IDENTITY && info.Rotation != DXGI_MODE_ROTATION_UNSPECIFIED)
                    return DXGI_ERROR_UNSUPPORTED; // GDI supplies an upright image for rotated displays.
                return Init(adapter.Get(), out.Get());
            }
        }
        return DXGI_ERROR_NOT_FOUND;
    }
    HRESULT Reduce(IDXGIResource* resource, BYTE* pixels) {
        ComPtr<ID3D11Texture2D> frame;
        CHECK(resource->QueryInterface(IID_PPV_ARGS(&frame)));
        ComPtr<ID3D11ShaderResourceView> view;
        D3D11_TEXTURE2D_DESC desc;
        frame->GetDesc(&desc);
        if (desc.BindFlags & D3D11_BIND_SHADER_RESOURCE) {
            CHECK(device->CreateShaderResourceView(frame.Get(), nullptr, &view));
        } else {
            if (!copy) {
                desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                desc.MiscFlags = desc.CPUAccessFlags = 0;
                desc.Usage = D3D11_USAGE_DEFAULT;
                CHECK(device->CreateTexture2D(&desc, nullptr, &copy));
                CHECK(device->CreateShaderResourceView(copy.Get(), nullptr, &copyView));
            }
            context->CopyResource(copy.Get(), frame.Get());
            view = copyView;
        }
        context->CSSetShader(shader.Get(), nullptr, 0);
        ID3D11ShaderResourceView* srv = view.Get();
        ID3D11UnorderedAccessView* uav = output.Get();
        context->CSSetShaderResources(0,1,&srv);
        context->CSSetUnorderedAccessViews(0,1,&uav,nullptr);
        context->Dispatch(8,9,1);
        srv=nullptr; uav=nullptr;
        context->CSSetShaderResources(0,1,&srv);
        context->CSSetUnorderedAccessViews(0,1,&uav,nullptr);
        context->CopyResource(staging.Get(), reduced.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        CHECK(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        std::memcpy(pixels, mapped.pData, SampleBytes);
        context->Unmap(staging.Get(),0);
        return S_OK;
    }
    HRESULT Read(BYTE* pixels) {
        if (monitor != MonitorFromPoint({0,0}, MONITOR_DEFAULTTOPRIMARY)) return DXGI_ERROR_ACCESS_LOST;
        DXGI_OUTDUPL_FRAME_INFO info{};
        ComPtr<IDXGIResource> resource;
        HRESULT hr = duplication->AcquireNextFrame(0,&info,&resource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return S_FALSE;
        CHECK(hr);
        if (haveDesktop && info.LastPresentTime.QuadPart == 0) {
            hr = duplication->ReleaseFrame();
            return FAILED(hr) ? hr : S_FALSE;
        }
        hr = Reduce(resource.Get(),pixels);
        if (SUCCEEDED(hr)) haveDesktop=true;
        HRESULT released = duplication->ReleaseFrame();
        return FAILED(hr) ? hr : released;
    }
};
extern "C" __declspec(dllexport) HRESULT CaptureOpen(Capture** result) {
    if (!result) return E_POINTER;
    *result = new(std::nothrow) Capture();
    if (!*result) return E_OUTOFMEMORY;
    HRESULT hr = (*result)->Open();
    if (FAILED(hr)) { delete *result; *result=nullptr; }
    return hr;
}
extern "C" __declspec(dllexport) HRESULT CaptureRead(Capture* capture, BYTE* pixels, UINT size) {
    if (!capture || !pixels || size < SampleBytes) return E_INVALIDARG;
    return capture->Read(pixels);
}
extern "C" __declspec(dllexport) void CaptureClose(Capture* capture) { delete capture; }
