cbuffer Layer : register(b0)
{
    float4 targetBox;
    float4 sourceBox;
    float4 options; // Unavailable, global opacity, color-key enabled, COLORREF.
};
Texture2D sourceImage : register(t0);
SamplerState linearClamp : register(s0);

float4 vertex(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 keyedTexel(int2 position, int2 size)
{
    float4 color = sourceImage.Load(int3(clamp(position, int2(0, 0), size - 1), 0));
    uint key = (uint)options.w;
    float3 keyRgb = float3(key & 255, (key >> 8) & 255, (key >> 16) & 255);
    if (all(abs(round(color.rgb * 255) - keyRgb) < 0.5)) return 0;
    return color;
}

float4 pixel(float4 position : SV_Position) : SV_Target
{
    float2 screen = targetBox.xy + position.xy;
    if (any(screen < sourceBox.xy) || any(screen >= sourceBox.xy + sourceBox.zw)) discard;
    if (options.x != 0) return 0;
    float2 uv = (screen - sourceBox.xy) / sourceBox.zw;
    if (options.z == 0) return sourceImage.Sample(linearClamp, uv) * options.y;
    uint width, height;
    sourceImage.GetDimensions(width, height);
    int2 size = int2(width, height);
    float2 texel = uv * size - 0.5;
    int2 origin = (int2)floor(texel);
    float2 fraction = frac(texel);
    // Apply the color key before filtering, so transparent texels cannot leave black fringes.
    float4 top = lerp(keyedTexel(origin, size), keyedTexel(origin + int2(1, 0), size), fraction.x);
    float4 bottom = lerp(keyedTexel(origin + int2(0, 1), size), keyedTexel(origin + int2(1, 1), size), fraction.x);
    return lerp(top, bottom, fraction.y) * options.y;
}
