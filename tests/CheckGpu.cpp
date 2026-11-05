#include "GoveeCapture.cpp"
#include <vector>
#include <cstdio>
HRESULT CheckColors() {
    Capture cap;
    CHECK(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,
        &cap.device,nullptr,&cap.context));
    CHECK(cap.SetupReduction());
    const UINT w=2560,h=1440;
    std::vector<UINT> pixels(w*h);
    std::vector<UINT> out(SampleBytes/4);
    for (UINT bind : {0u, static_cast<UINT>(D3D11_BIND_SHADER_RESOURCE)}) {
        for(UINT scene=0; scene<3; ++scene) {
            UINT left=scene==0 ? 0xFF0000u : 0x00FF00u;
            UINT right=scene==2 ? 0x000000u : 0x0000FFu;
            for(UINT y=0;y<h;y++) for(UINT x=0;x<w;x++) pixels[y*w+x]=x<w/2 ? left : right;
            D3D11_TEXTURE2D_DESC d{};
            d.Width=w; d.Height=h; d.MipLevels=d.ArraySize=d.SampleDesc.Count=1;
            d.Format=DXGI_FORMAT_B8G8R8A8_UNORM; d.Usage=D3D11_USAGE_DEFAULT; d.BindFlags=bind;
            D3D11_SUBRESOURCE_DATA src{};src.pSysMem=pixels.data();src.SysMemPitch=4*w;
            ComPtr<ID3D11Texture2D> tex;CHECK(cap.device->CreateTexture2D(&d,&src,&tex));
            ComPtr<IDXGIResource> resource;CHECK(tex.As(&resource));
            CHECK(cap.Reduce(resource.Get(),reinterpret_cast<BYTE*>(out.data())));
            for(UINT i=0;i<out.size();i++) if(out[i]!=(i%64<32 ? left : right)) return E_FAIL;
        }
    }
    return S_OK;
}
int main() {
    auto hr=CheckColors();
    if(FAILED(hr)) { std::printf("GPU test failed: %08lX\n",static_cast<unsigned long>(hr)); return 1; }
    std::puts("GPU shader passed alternating split colors in direct and copied texture paths.");return 0;
}
