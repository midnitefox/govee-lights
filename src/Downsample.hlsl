Texture2D<float4> desktop : register(t0);
RWStructuredBuffer<uint> samples : register(u0);

[numthreads(8, 4, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint w, h;
    desktop.GetDimensions(w, h);
    float2 cell = float2(w, h) / float2(64, 36);
    float3 sum = 0;
    [unroll] for (uint y = 0; y < 4; ++y) {
        [unroll] for (uint x = 0; x < 4; ++x) {
            uint2 p = (uint2)((id.xy + (float2(x,y) + .5) / 4) * cell);
            sum += desktop.Load(int3(min(p, uint2(w-1,h-1)), 0)).rgb;
        }
    }
    uint3 rgb = (uint3)round(saturate(sum / 16) * 255);
    samples[id.y*64 + id.x] = rgb.b | (rgb.g << 8) | (rgb.r << 16);
}
