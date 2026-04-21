namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// FXAA シェーダソースを提供するヘルパー。
/// NVIDIA FXAA 3.11 ベースのアルゴリズム。
/// </summary>
internal static class FxaaShaderSource
{
    public const string Source = @"
cbuffer CbFxaa : register(b0)
{
    float2 rcpFrame;
    float2 _pad;
};

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

Texture2D    gTex    : register(t0);
SamplerState gSampler: register(s0);

PSInput VS_Fxaa(VSInput input)
{
    PSInput o;
    o.Pos = float4(input.Pos.xy, 0.5, 1.0);
    o.UV  = input.UV;
    return o;
}

float Luma(float4 c)
{
    return dot(c.rgb, float3(0.299, 0.587, 0.114)) + c.a;
}

float4 PS_Fxaa(PSInput input) : SV_Target
{
    float2 uv = input.UV;

    float4 center = gTex.Sample(gSampler, uv);
    float lumaC  = Luma(center);

    float lumaN  = Luma(gTex.Sample(gSampler, uv + float2( 0, -1) * rcpFrame));
    float lumaS  = Luma(gTex.Sample(gSampler, uv + float2( 0,  1) * rcpFrame));
    float lumaE  = Luma(gTex.Sample(gSampler, uv + float2( 1,  0) * rcpFrame));
    float lumaW  = Luma(gTex.Sample(gSampler, uv + float2(-1,  0) * rcpFrame));

    float lumaMin = min(lumaC, min(min(lumaN, lumaS), min(lumaE, lumaW)));
    float lumaMax = max(lumaC, max(max(lumaN, lumaS), max(lumaE, lumaW)));
    float lumaRange = lumaMax - lumaMin;

    if (lumaRange < max(0.0312, lumaMax * 0.125))
        return center;

    float lumaNW = Luma(gTex.Sample(gSampler, uv + float2(-1, -1) * rcpFrame));
    float lumaNE = Luma(gTex.Sample(gSampler, uv + float2( 1, -1) * rcpFrame));
    float lumaSW = Luma(gTex.Sample(gSampler, uv + float2(-1,  1) * rcpFrame));
    float lumaSE = Luma(gTex.Sample(gSampler, uv + float2( 1,  1) * rcpFrame));

    float edgeH = abs(-2.0 * lumaW + lumaNW + lumaSW)
                + abs(-2.0 * lumaC + lumaN  + lumaS ) * 2.0
                + abs(-2.0 * lumaE + lumaNE + lumaSE);
    float edgeV = abs(-2.0 * lumaN + lumaNW + lumaNE)
                + abs(-2.0 * lumaC + lumaW  + lumaE ) * 2.0
                + abs(-2.0 * lumaS + lumaSW + lumaSE);
    bool isHorizontal = edgeH >= edgeV;

    float luma1 = isHorizontal ? lumaN : lumaW;
    float luma2 = isHorizontal ? lumaS : lumaE;
    float grad1 = abs(luma1 - lumaC);
    float grad2 = abs(luma2 - lumaC);

    float stepLength = isHorizontal ? rcpFrame.y : rcpFrame.x;
    float lumaLocalAverage;
    if (grad1 >= grad2)
    {
        stepLength = -stepLength;
        lumaLocalAverage = 0.5 * (luma1 + lumaC);
    }
    else
    {
        lumaLocalAverage = 0.5 * (luma2 + lumaC);
    }

    float2 currentUV = uv;
    if (isHorizontal)
        currentUV.y += stepLength * 0.5;
    else
        currentUV.x += stepLength * 0.5;

    float2 offset = isHorizontal ? float2(rcpFrame.x, 0) : float2(0, rcpFrame.y);

    float2 uv1 = currentUV - offset;
    float2 uv2 = currentUV + offset;

    float lumaEnd1 = Luma(gTex.Sample(gSampler, uv1)) - lumaLocalAverage;
    float lumaEnd2 = Luma(gTex.Sample(gSampler, uv2)) - lumaLocalAverage;

    bool reached1 = abs(lumaEnd1) >= grad1;
    bool reached2 = abs(lumaEnd2) >= grad2;

    [unroll]
    for (int i = 0; i < 8; i++)
    {
        if (!reached1) { uv1 -= offset; lumaEnd1 = Luma(gTex.Sample(gSampler, uv1)) - lumaLocalAverage; reached1 = abs(lumaEnd1) >= grad1; }
        if (!reached2) { uv2 += offset; lumaEnd2 = Luma(gTex.Sample(gSampler, uv2)) - lumaLocalAverage; reached2 = abs(lumaEnd2) >= grad2; }
        if (reached1 && reached2) break;
    }

    float dist1 = isHorizontal ? (uv.x - uv1.x) : (uv.y - uv1.y);
    float dist2 = isHorizontal ? (uv2.x - uv.x) : (uv2.y - uv.y);
    float distMin = min(dist1, dist2);
    float edgeLen = dist1 + dist2;

    float pixelOffset = -distMin / edgeLen + 0.5;

    bool isLumaCSmaller = lumaC < lumaLocalAverage;
    bool correctVariation1 = (lumaEnd1 < 0.0) != isLumaCSmaller;
    bool correctVariation2 = (lumaEnd2 < 0.0) != isLumaCSmaller;
    bool correctVariation = (dist1 < dist2) ? correctVariation1 : correctVariation2;
    float finalOffset = correctVariation ? pixelOffset : 0.0;

    float lumaAvg = (1.0/12.0) * (2.0*(lumaN+lumaS+lumaE+lumaW) + lumaNW+lumaNE+lumaSW+lumaSE);
    float subPixelOffset = clamp(abs(lumaAvg - lumaC) / lumaRange, 0.0, 1.0);
    subPixelOffset = (-2.0*subPixelOffset+3.0)*subPixelOffset*subPixelOffset;
    subPixelOffset = subPixelOffset * subPixelOffset * 0.75;

    finalOffset = max(finalOffset, subPixelOffset);

    float2 finalUV = uv;
    if (isHorizontal)
        finalUV.y += finalOffset * stepLength;
    else
        finalUV.x += finalOffset * stepLength;

    return gTex.Sample(gSampler, finalUV);
}
";
}
