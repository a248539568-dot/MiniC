sampler2D input : register(s0);
float2 dimensions : register(c0);
float cornerRadius : register(c2);
float2 direction : register(c3);
float lensWidth : register(c6);
float bevelExtension : register(c7);
float2 sampling : register(c8);
float3 phaseX : register(c9);
float3 phaseY : register(c10);
float2 waveSampling : register(c11);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 safeDimensions = dimensions;
    float2 halfSize = safeDimensions * 0.5;
    float2 p = uv * safeDimensions - halfSize;
    float radius = cornerRadius;
    float2 q = abs(p) - (halfSize - radius);
    float2 curved = max(q, 0);
    float curvedLength = length(curved);
    float distance = min(max(q.x, q.y), 0) + curvedLength - radius;
    // Rim lens and whole-pane waves only sample the background, never the UI.
    float edge = saturate(1 + distance / lensWidth);

    // Extend the curved normal across the bevel when the configured corner
    // radius is small. A nearest-face switch creates diagonal seams there.
    float2 normal = normalize(max(q + bevelExtension, 0.001)) * (step(0, p) * 2 - 1);
    float2 rotated = float2(normal.x * direction.x - normal.y * direction.y,
        normal.x * direction.y + normal.y * direction.x);
    float2 phase = float2(dot(float3(uv, 1), phaseX), dot(float3(uv, 1), phaseY));
    float2 t = frac(phase) * 2 - 1;
    // Periodic parabolas meet with continuous value and slope; no sawtooth seams.
    float2 wave = t * (1 - abs(t));
    float2 water = float2(wave.x + 0.35 * wave.y, wave.y - 0.35 * wave.x) * waveSampling;
    // Displacement is in DIPs, so resizing a box does not stretch the lens.
    // Curvature peaks inside the bevel; the silhouette stays in place.
    float bend = edge * (1 - edge) * 48 + edge * 2;
    float2 shift = -rotated * bend * sampling.xy + water;
    float2 sampleUv = saturate(uv + shift);
    // A single sharp sample avoids blurring the waves and preserves premultiplied alpha.
    return tex2D(input, sampleUv);
}
