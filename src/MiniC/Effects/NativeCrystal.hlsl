cbuffer Glass : register(b0)
{
    float4 box;       // Physical screen origin and size.
    float4 output;    // Physical output origin and upright size.
    float4 optics;    // Scale, radius in DIPs, strength, angle in radians.
    float4 ripple;    // Strength, frequency, output rotation, window-composite flag.
};
Texture2D background : register(t0);
SamplerState linearClamp : register(s0);

float4 vertex(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
}

float2 textureUv(float2 screen)
{
    float2 uv = (screen - output.xy) / output.zw;
    // Desktop Duplication returns an unrotated texture for rotated outputs.
    if (ripple.z == 2) return float2(uv.y, 1 - uv.x);
    if (ripple.z == 3) return 1 - uv;
    if (ripple.z == 4) return float2(1 - uv.y, uv.x);
    return uv;
}

float4 pixel(float4 position : SV_Position) : SV_Target
{
    float2 screen = box.xy + position.xy;
    // Adjacent outputs draw into the same target without overwriting each other.
    if (any(screen < output.xy) || any(screen >= output.xy + output.zw)) discard;
    float2 size = box.zw / optics.x;
    float2 uv = position.xy / box.zw;
    float2 p = (uv - 0.5) * size;
    float radius = min(optics.y, min(size.x, size.y) * 0.5);
    float2 q = abs(p) - (size * 0.5 - radius);
    float distance = min(max(q.x, q.y), 0) + length(max(q, 0)) - radius;
    float coverage = saturate(0.5 - distance * optics.x);
    float lensWidth = min(32, min(size.x, size.y) * 0.4);
    float edge = saturate(1 + distance / max(0.01, lensWidth));
    float2 normal = normalize(max(q + max(0, lensWidth - radius), 0.001)) * (step(0, p) * 2 - 1);
    float c = cos(optics.w), s = sin(optics.w);
    float2 rotated = float2(normal.x * c - normal.y * s, normal.x * s + normal.y * c);
    float2 local = uv * size;
    float2 phase = float2(dot(local, float2(0.006, 0.009)), dot(local, float2(-0.008, 0.005)))
        * ripple.y + float2(0.17, 0.41);
    float2 t = frac(phase) * 2 - 1;
    float2 wave = t * (1 - abs(t)) * 4;
    float2 water = float2(wave.x + 0.35 * wave.y, wave.y - 0.35 * wave.x) * (ripple.x * optics.z * 6);
    float bend = edge * (1 - edge) * 48 + edge * 2;
    float2 shift = (-rotated * optics.z * bend + water) * optics.x;
    float4 sampled = background.Sample(linearClamp, textureUv(screen + shift));
    // DXGI alpha is undefined. Window composition instead uses alpha for unavailable pixels.
    if (ripple.w != 0)
    {
        float a = min(background.Sample(linearClamp, textureUv(screen)).a, sampled.a);
        if (a < 0.99) return 0;
    }
    // Tint and the two-contrast contour belong exclusively to the WPF foreground.
    return float4(sampled.rgb * coverage, coverage);
}
