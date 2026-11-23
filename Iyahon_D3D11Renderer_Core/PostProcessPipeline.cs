using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Iyahon_D3D11Renderer_Core.Lighting;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// 現代リアルタイム3Dポストプロセス（Half-Res GTAO / SSGI / SSR / コンタクトシャドウ / フォグ / ゴッドレイ / Karis Bloom / トーンマッピング）。
/// すべてリニア FP16 HDR 空間で処理し、通常時は SDR トーンマップ出力、HDRInjector 有効時は実 FP16 HDR 出力を行う。
/// </summary>
internal sealed class PostProcessPipeline : IDisposable
{
    private const int BloomMipCount = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct CbPost
    {
        public float SrcWidth;
        public float SrcHeight;
        public float RcpSrcWidth;
        public float RcpSrcHeight;

        public float HalfScreenW;
        public float HalfScreenH;
        public float RcpDstWidth;
        public float RcpDstHeight;

        public float AoIntensity;
        public float AoRadiusWorld;
        public float GiIntensity;
        public float BlurDir;       // 0 = H, 1 = V

        public float BloomThreshold;
        public float BloomKnee;
        public float BloomIntensity;
        public float ExposureMult;

        public float ToneMode;      // 0=GT, 1=ACES, 2=AgX, 3=None
        public float PeakLuminance; // 1.0 in SDR, HdrPeakLuminance in HDR
        public float IsHdrOutput;   // 0 or 1
        public float DitherScale;   // 1 in SDR, 0 in HDR

        public Vector4 SsrParams;             // x = EnableSsr, y = SsrIntensity, z = SsrMaxRoughness, w = reserved
        public Vector4 ContactLightDirAndLen; // xyz = cam dir to light, w = ContactShadowLength
        public Vector4 ContactParams;         // x = EnableContact, y = ContactIntensity, z = hasLight, w = reserved
        public Vector4 FogColorAndDensity;    // rgb = linear fog color, w = FogDensity
        public Vector4 FogRangeAndHeight;     // x = FogStart, y = FogEnd, z = HeightFogStrength, w = HeightFogBaseY
        public Vector4 InvCamColY;            // xyz = colY of invCam, w = invCam.M42
        public Vector4 GodRayParams;          // xy = lightScreenUV, z = GodRayIntensity, w = GodRayDecay
        public Vector4 GodRayColorAndDensity; // rgb = linear ray color, w = GodRayDensity
    }

    private IntPtr _devicePointer = IntPtr.Zero;
    private bool _initialized;
    private bool _disposed;

    private ID3D11VertexShader? _vsPost;
    private ID3D11PixelShader? _psGtao;
    private ID3D11PixelShader? _psAoBlurH;
    private ID3D11PixelShader? _psAoBlurV;
    private ID3D11PixelShader? _psSsgi;
    private ID3D11PixelShader? _psGiBlurH;
    private ID3D11PixelShader? _psGiBlurV;
    private ID3D11PixelShader? _psSsr;
    private ID3D11PixelShader? _psSsrBlurH;
    private ID3D11PixelShader? _psSsrBlurV;
    private ID3D11PixelShader? _psGodRayMask;
    private ID3D11PixelShader? _psGodRayBlur;
    private ID3D11PixelShader? _psApplyAoGi;
    private ID3D11PixelShader? _psBloomPrefilter;
    private ID3D11PixelShader? _psBloomDownsample;
    private ID3D11PixelShader? _psBloomUpsample;
    private ID3D11PixelShader? _psToneMapComposite;

    private ID3D11Buffer? _cbPost;
    private ID3D11BlendState? _blendAdditive;

    // Half-res GTAO targets (R8_UNorm)
    private int _halfW;
    private int _halfH;
    private ID3D11Texture2D? _aoRawTex;
    private ID3D11RenderTargetView? _aoRawRtv;
    private ID3D11ShaderResourceView? _aoRawSrv;
    private ID3D11Texture2D? _aoTempTex;
    private ID3D11RenderTargetView? _aoTempRtv;
    private ID3D11ShaderResourceView? _aoTempSrv;
    private ID3D11Texture2D? _aoFinalTex;
    private ID3D11RenderTargetView? _aoFinalRtv;
    private ID3D11ShaderResourceView? _aoFinalSrv;

    // Half-res SSGI targets (R16G16B16A16_Float)
    private ID3D11Texture2D? _giRawTex;
    private ID3D11RenderTargetView? _giRawRtv;
    private ID3D11ShaderResourceView? _giRawSrv;
    private ID3D11Texture2D? _giTempTex;
    private ID3D11RenderTargetView? _giTempRtv;
    private ID3D11ShaderResourceView? _giTempSrv;
    private ID3D11Texture2D? _giFinalTex;
    private ID3D11RenderTargetView? _giFinalRtv;
    private ID3D11ShaderResourceView? _giFinalSrv;

    // Half-res SSR targets (R16G16B16A16_Float)
    private int _ssrHalfW;
    private int _ssrHalfH;
    private ID3D11Texture2D? _ssrTex;
    private ID3D11RenderTargetView? _ssrRtv;
    private ID3D11ShaderResourceView? _ssrSrv;
    private ID3D11Texture2D? _ssrTempTex;
    private ID3D11RenderTargetView? _ssrTempRtv;
    private ID3D11ShaderResourceView? _ssrTempSrv;

    // Half-res GodRay targets (R16G16B16A16_Float)
    private int _grHalfW;
    private int _grHalfH;
    private ID3D11Texture2D? _godRayTex;
    private ID3D11RenderTargetView? _godRayRtv;
    private ID3D11ShaderResourceView? _godRaySrv;
    private ID3D11Texture2D? _godRayTempTex;
    private ID3D11RenderTargetView? _godRayTempRtv;
    private ID3D11ShaderResourceView? _godRayTempSrv;

    // Full-res HDR scratch for AO/GI/SSR/Fog/GodRay composite
    private int _fullW;
    private int _fullH;
    private ID3D11Texture2D? _hdrPostTex;
    private ID3D11RenderTargetView? _hdrPostRtv;
    private ID3D11ShaderResourceView? _hdrPostSrv;

    // 5-mip Bloom pyramid
    private int _bloomBaseW;
    private int _bloomBaseH;
    private readonly ID3D11Texture2D?[] _bloomTex = new ID3D11Texture2D?[BloomMipCount];
    private readonly ID3D11RenderTargetView?[] _bloomRtv = new ID3D11RenderTargetView?[BloomMipCount];
    private readonly ID3D11ShaderResourceView?[] _bloomSrv = new ID3D11ShaderResourceView?[BloomMipCount];
    private readonly int[] _bloomW = new int[BloomMipCount];
    private readonly int[] _bloomH = new int[BloomMipCount];

    internal const string ShaderSource = @"
cbuffer CbPost : register(b0)
{
    float SrcWidth;
    float SrcHeight;
    float RcpSrcWidth;
    float RcpSrcHeight;

    float HalfScreenW;
    float HalfScreenH;
    float RcpDstWidth;
    float RcpDstHeight;

    float AoIntensity;
    float AoRadiusWorld;
    float GiIntensity;
    float BlurDir;

    float BloomThreshold;
    float BloomKnee;
    float BloomIntensity;
    float ExposureMult;

    float ToneMode;
    float PeakLuminance;
    float IsHdrOutput;
    float DitherScale;

    float4 SsrParams;
    float4 ContactLightDirAndLen;
    float4 ContactParams;
    float4 FogColorAndDensity;
    float4 FogRangeAndHeight;
    float4 InvCamColY;
    float4 GodRayParams;
    float4 GodRayColorAndDensity;
};

Texture2D    gTex0           : register(t0);
Texture2D    gTex1           : register(t1);
Texture2D    gTex2           : register(t2);
Texture2D    gTex3           : register(t3);
Texture2D    gGbufNormalRough: register(t4);
Texture2D    gGbufSpecMeta   : register(t5);
Texture2D    gSsrTex         : register(t6);
Texture2D    gGodRayTex      : register(t7);
SamplerState gLinear         : register(s0);
SamplerState gPoint          : register(s1);

struct VSInput { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

PSInput VS_Post(VSInput input)
{
    PSInput o;
    o.Pos = float4(input.Pos.xy, 0.5, 1.0);
    o.UV = input.UV;
    return o;
}

float IGN(float2 p)
{
    float3 m = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(m.z * frac(dot(p, m.xy)));
}

float LinearDistFromDepth(float d)
{
    return 5.0 / max(0.505 - d, 1e-6);
}

bool IsBackgroundDepth(float d)
{
    return d >= 0.5049;
}

float3 ReconstructCamPos(float2 uv, float d)
{
    float dist = LinearDistFromDepth(d);
    float w = dist * 0.001;
    return float3(
        (uv.x * 2.0 - 1.0) * HalfScreenW * w,
        (uv.y * 2.0 - 1.0) * HalfScreenH * w,
        1000.0 - dist
    );
}

float3 ReconstructCamNormal(float2 uv, float dC, float3 posC, float2 texel)
{
    float dL = gTex1.SampleLevel(gPoint, uv + float2(-texel.x, 0.0), 0).r;
    float dR = gTex1.SampleLevel(gPoint, uv + float2( texel.x, 0.0), 0).r;
    float dU = gTex1.SampleLevel(gPoint, uv + float2(0.0, -texel.y), 0).r;
    float dD = gTex1.SampleLevel(gPoint, uv + float2(0.0,  texel.y), 0).r;

    float3 dPdx;
    if (abs(dR - dC) < abs(dC - dL))
        dPdx = IsBackgroundDepth(dR) ? (posC - ReconstructCamPos(uv - float2(texel.x, 0.0), dL)) : (ReconstructCamPos(uv + float2(texel.x, 0.0), dR) - posC);
    else
        dPdx = IsBackgroundDepth(dL) ? (ReconstructCamPos(uv + float2(texel.x, 0.0), dR) - posC) : (posC - ReconstructCamPos(uv - float2(texel.x, 0.0), dL));

    float3 dPdy;
    if (abs(dD - dC) < abs(dC - dU))
        dPdy = IsBackgroundDepth(dD) ? (posC - ReconstructCamPos(uv - float2(0.0, texel.y), dU)) : (ReconstructCamPos(uv + float2(0.0, texel.y), dD) - posC);
    else
        dPdy = IsBackgroundDepth(dU) ? (ReconstructCamPos(uv + float2(0.0, texel.y), dD) - posC) : (posC - ReconstructCamPos(uv - float2(0.0, texel.y), dU));

    float3 n = cross(dPdx, dPdy);
    float lenSq = dot(n, n);
    if (lenSq < 1e-10) return float3(0.0, 0.0, 1.0);
    n *= rsqrt(lenSq);
    if (n.z < 0.0) n = -n;
    return n;
}

bool ProjectCamPosToUV(float3 camPos, out float2 outUV, out float outDist)
{
    outDist = 1000.0 - camPos.z;
    if (outDist <= 1.0)
    {
        outUV = float2(0.0, 0.0);
        return false;
    }
    float w = outDist * 0.001;
    outUV.x = 0.5 + camPos.x / max(2.0 * HalfScreenW * w, 1e-4);
    outUV.y = 0.5 + camPos.y / max(2.0 * HalfScreenH * w, 1e-4);
    return (outUV.x > 0.001 && outUV.x < 0.999 && outUV.y > 0.001 && outUV.y < 0.999);
}

float3 GetCamNormal(float2 uv, float dC, float3 posC, float2 texel, out float outRoughness, out float3 outF0, out float outHasGbuf)
{
    float4 gbufNR = gGbufNormalRough.SampleLevel(gPoint, uv, 0);
    float lenSq = dot(gbufNR.xyz, gbufNR.xyz);
    if (gbufNR.a >= 0.03 && lenSq > 0.1)
    {
        outRoughness = clamp(gbufNR.a, 0.04, 1.0);
        outF0 = gGbufSpecMeta.SampleLevel(gPoint, uv, 0).rgb;
        outHasGbuf = 1.0;
        return gbufNR.xyz * rsqrt(lenSq);
    }
    outRoughness = 0.5;
    outF0 = float3(0.04, 0.04, 0.04);
    outHasGbuf = 0.0;
    return ReconstructCamNormal(uv, dC, posC, texel);
}

// --- Half-Res Horizon / Screen-Space GTAO ---
float PS_Gtao(PSInput input) : SV_Target
{
    float2 uv = input.UV;
    float dC = gTex1.SampleLevel(gPoint, uv, 0).r;
    if (IsBackgroundDepth(dC) || AoIntensity <= 0.001) return 1.0;

    float3 posC = ReconstructCamPos(uv, dC);
    float tmpR;
    float3 tmpF0;
    float tmpGbuf;
    float3 N = GetCamNormal(uv, dC, posC, float2(RcpSrcWidth, RcpSrcHeight), tmpR, tmpF0, tmpGbuf);

    float distC = max(LinearDistFromDepth(dC), 50.0);
    float w = distC * 0.001;
    float radiusUvX = clamp(AoRadiusWorld / max(2.0 * HalfScreenW * w, 1e-4), 2.0 * RcpSrcWidth, 0.14);
    float radiusUvY = clamp(AoRadiusWorld / max(2.0 * HalfScreenH * w, 1e-4), 2.0 * RcpSrcHeight, 0.14);
    float2 radiusUV = float2(radiusUvX, radiusUvY);

    float jitterAngle = IGN(input.Pos.xy) * 3.14159265;
    float jitterStep = frac(IGN(input.Pos.xy + float2(7.13, 3.71)));

    const int Dirs = 4;
    const int Steps = 4;
    float occlusion = 0.0;
    float radiusWorld = max(AoRadiusWorld, 5.0);

    [unroll]
    for (int d = 0; d < Dirs; d++)
    {
        float ang = jitterAngle + (float(d) * 3.14159265 / float(Dirs));
        float s, c;
        sincos(ang, s, c);
        float2 dirUV = float2(c, s);

        float maxCosA = 0.0;
        float maxCosB = 0.0;

        [unroll]
        for (int st = 0; st < Steps; st++)
        {
            float fracStep = (float(st) + 0.3 + 0.7 * jitterStep) / float(Steps);
            float2 off = dirUV * (radiusUV * fracStep);

            float2 uvA = uv + off;
            if (uvA.x > 0.0 && uvA.x < 1.0 && uvA.y > 0.0 && uvA.y < 1.0)
            {
                float dA = gTex1.SampleLevel(gPoint, uvA, 0).r;
                if (!IsBackgroundDepth(dA))
                {
                    float3 diffA = ReconstructCamPos(uvA, dA) - posC;
                    float lenA = length(diffA);
                    if (lenA > 0.5 && lenA < radiusWorld * 2.5)
                    {
                        float horizon = saturate(dot(diffA / lenA, N) - 0.15);
                        float atten = saturate(1.0 - (lenA / radiusWorld) * (lenA / radiusWorld));
                        maxCosA = max(maxCosA, horizon * atten);
                    }
                }
            }

            float2 uvB = uv - off;
            if (uvB.x > 0.0 && uvB.x < 1.0 && uvB.y > 0.0 && uvB.y < 1.0)
            {
                float dB = gTex1.SampleLevel(gPoint, uvB, 0).r;
                if (!IsBackgroundDepth(dB))
                {
                    float3 diffB = ReconstructCamPos(uvB, dB) - posC;
                    float lenB = length(diffB);
                    if (lenB > 0.5 && lenB < radiusWorld * 2.5)
                    {
                        float horizon = saturate(dot(diffB / lenB, N) - 0.15);
                        float atten = saturate(1.0 - (lenB / radiusWorld) * (lenB / radiusWorld));
                        maxCosB = max(maxCosB, horizon * atten);
                    }
                }
            }
        }

        occlusion += 0.5 * (maxCosA + maxCosB);
    }

    float visibility = saturate(1.0 - (occlusion / float(Dirs)) * 1.15);
    return pow(visibility, 1.35);
}

float BilateralBlurAo(PSInput input, float2 stepDir)
{
    float dC = gTex1.SampleLevel(gPoint, input.UV, 0).r;
    if (IsBackgroundDepth(dC)) return 1.0;

    float distC = LinearDistFromDepth(dC);
    float sum = 0.0;
    float wSum = 0.0;
    static const float weights[5] = { 0.227027, 0.194594, 0.121621, 0.054054, 0.016216 };

    [unroll]
    for (int i = -4; i <= 4; i++)
    {
        float2 suv = input.UV + stepDir * float(i);
        float dS = gTex1.SampleLevel(gPoint, suv, 0).r;
        float distS = LinearDistFromDepth(dS);
        float relDiff = abs(distS - distC) / max(distC * 0.05, 8.0);
        float wDepth = exp(-relDiff * relDiff * 4.0);
        float w = weights[abs(i)] * wDepth;
        float ao = gTex0.SampleLevel(gLinear, suv, 0).r;
        sum += ao * w;
        wSum += w;
    }

    return sum / max(wSum, 1e-4);
}

float PS_AoBlurH(PSInput input) : SV_Target { return BilateralBlurAo(input, float2(RcpDstWidth, 0.0)); }
float PS_AoBlurV(PSInput input) : SV_Target { return BilateralBlurAo(input, float2(0.0, RcpDstHeight)); }

// --- Half-Res Screen-Space Global Illumination (SSGI) ---
float4 PS_Ssgi(PSInput input) : SV_Target
{
    float2 uv = input.UV;
    float dC = gTex1.SampleLevel(gPoint, uv, 0).r;
    if (IsBackgroundDepth(dC) || GiIntensity <= 0.001) return float4(0.0, 0.0, 0.0, 0.0);

    float3 posC = ReconstructCamPos(uv, dC);
    float tmpR;
    float3 tmpF0;
    float tmpGbuf;
    float3 N = GetCamNormal(uv, dC, posC, float2(RcpSrcWidth, RcpSrcHeight), tmpR, tmpF0, tmpGbuf);

    float3 upVec = abs(N.y) > 0.9 ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
    float3 T = normalize(cross(upVec, N));
    float3 B = cross(N, T);

    float distC = max(LinearDistFromDepth(dC), 50.0);
    float marchRange = clamp(distC * 0.35, 90.0, 520.0);
    float thickness = clamp(distC * 0.08, 18.0, 90.0);

    float phi = IGN(input.Pos.xy) * 6.2831853;
    float jitter = frac(IGN(input.Pos.xy + float2(5.23, 9.41)));

    const int RayCount = 6;
    const int StepCount = 6;
    float3 indirect = float3(0.0, 0.0, 0.0);

    [unroll]
    for (int r = 0; r < RayCount; r++)
    {
        float u1 = (float(r) + 0.5) / float(RayCount);
        float cosTheta = sqrt(1.0 - u1 * 0.85);
        float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
        float angle = phi + float(r) * 2.39996323;
        float s, c;
        sincos(angle, s, c);

        float3 rayDir = normalize(T * (c * sinTheta) + B * (s * sinTheta) + N * cosTheta);

        [unroll]
        for (int st = 1; st <= StepCount; st++)
        {
            float t = ((float(st) - 0.3 + 0.6 * jitter) / float(StepCount));
            float stepWorld = marchRange * (t * t + 0.08 * t);
            float3 samplePos = posC + rayDir * stepWorld + N * 2.0;

            float2 hitUV;
            float rayDist;
            if (!ProjectCamPosToUV(samplePos, hitUV, rayDist))
                break;

            float dHit = gTex1.SampleLevel(gPoint, hitUV, 0).r;
            if (IsBackgroundDepth(dHit))
                continue;

            float sceneDist = LinearDistFromDepth(dHit);
            float delta = rayDist - sceneDist;
            if (delta > 1.5 && delta < thickness)
            {
                float4 hitSample = gTex0.SampleLevel(gLinear, hitUV, 0);
                float3 hitColor = hitSample.a > 1e-4 ? (hitSample.rgb / hitSample.a) : float3(0.0, 0.0, 0.0);
                // Prevent extreme HDR firefly runaway in diffuse bounce
                hitColor = min(hitColor, float3(4.0, 4.0, 4.0));

                float edgeFade = smoothstep(0.0, 0.08, min(min(hitUV.x, 1.0 - hitUV.x), min(hitUV.y, 1.0 - hitUV.y)));
                float distFade = 1.0 / (1.0 + stepWorld * 0.0025);
                indirect += hitColor * edgeFade * distFade * saturate(hitSample.a);
                break;
            }
        }
    }

    return float4((indirect / float(RayCount)) * 1.65, 1.0);
}

float4 BilateralBlurGi(PSInput input, float2 stepDir)
{
    float dC = gTex1.SampleLevel(gPoint, input.UV, 0).r;
    if (IsBackgroundDepth(dC)) return float4(0.0, 0.0, 0.0, 0.0);

    float distC = LinearDistFromDepth(dC);
    float3 sum = float3(0.0, 0.0, 0.0);
    float wSum = 0.0;
    static const float weights[5] = { 0.227027, 0.194594, 0.121621, 0.054054, 0.016216 };

    [unroll]
    for (int i = -4; i <= 4; i++)
    {
        float2 suv = input.UV + stepDir * float(i);
        float dS = gTex1.SampleLevel(gPoint, suv, 0).r;
        float distS = LinearDistFromDepth(dS);
        float relDiff = abs(distS - distC) / max(distC * 0.06, 10.0);
        float wDepth = exp(-relDiff * relDiff * 3.5);
        float w = weights[abs(i)] * wDepth;
        float3 gi = gTex0.SampleLevel(gLinear, suv, 0).rgb;
        sum += gi * w;
        wSum += w;
    }

    return float4(sum / max(wSum, 1e-4), 1.0);
}

float4 PS_GiBlurH(PSInput input) : SV_Target { return BilateralBlurGi(input, float2(RcpDstWidth, 0.0)); }
float4 PS_GiBlurV(PSInput input) : SV_Target { return BilateralBlurGi(input, float2(0.0, RcpDstHeight)); }

// --- Half-Res Screen-Space Reflections (SSR) ---
float4 PS_Ssr(PSInput input) : SV_Target
{
    if (SsrParams.x < 0.5 || SsrParams.y <= 0.001) return float4(0.0, 0.0, 0.0, 0.0);

    float2 uv = input.UV;
    float dC = gTex1.SampleLevel(gPoint, uv, 0).r;
    if (IsBackgroundDepth(dC)) return float4(0.0, 0.0, 0.0, 0.0);

    float3 posC = ReconstructCamPos(uv, dC);
    float roughness;
    float3 F0;
    float hasGbuf;
    float3 N = GetCamNormal(uv, dC, posC, float2(RcpSrcWidth, RcpSrcHeight), roughness, F0, hasGbuf);
    if (hasGbuf < 0.5 || roughness > SsrParams.z) return float4(0.0, 0.0, 0.0, 0.0);

    float3 camEye = float3(0.0, 0.0, 1000.0);
    float3 V = normalize(camEye - posC);
    float3 viewDir = -V;
    float3 R = normalize(reflect(viewDir, N));

    // Rays strongly pointing toward camera cannot hit screen geometry cleanly
    float backFade = smoothstep(0.65, 0.05, R.z);
    if (backFade <= 0.001) return float4(0.0, 0.0, 0.0, 0.0);

    float distC = max(LinearDistFromDepth(dC), 50.0);
    float maxRayLen = clamp(distC * 0.95, 250.0, 2200.0);
    const int Steps = 24;
    float stepWorld = maxRayLen / float(Steps);
    float3 rayOrigin = posC + N * clamp(distC * 0.006, 2.0, 10.0);
    float thickness = max(stepWorld * 2.2, clamp(distC * 0.06, 25.0, 180.0));

    float jitter = IGN(input.Pos.xy);
    float prevT = stepWorld * (0.2 + 0.6 * jitter);
    float hitT = -1.0;
    float2 hitUV = float2(0.0, 0.0);

    [loop]
    for (int i = 1; i <= Steps; i++)
    {
        float t = stepWorld * (float(i) + jitter * 0.7);
        float3 sp = rayOrigin + R * t;
        float2 suv;
        float rayDist;
        if (!ProjectCamPosToUV(sp, suv, rayDist))
            break;

        float dHit = gTex1.SampleLevel(gPoint, suv, 0).r;
        if (!IsBackgroundDepth(dHit))
        {
            float sceneDist = LinearDistFromDepth(dHit);
            float delta = rayDist - sceneDist;
            if (delta > 0.5 && delta < thickness)
            {
                // Binary search refinement
                float lo = prevT;
                float hi = t;
                [unroll]
                for (int b = 0; b < 5; b++)
                {
                    float mid = (lo + hi) * 0.5;
                    float3 mp = rayOrigin + R * mid;
                    float2 muv;
                    float mdist;
                    if (!ProjectCamPosToUV(mp, muv, mdist)) { hi = mid; continue; }
                    float md = gTex1.SampleLevel(gPoint, muv, 0).r;
                    if (IsBackgroundDepth(md) || mdist <= LinearDistFromDepth(md))
                        lo = mid;
                    else
                        hi = mid;
                }
                float3 fp = rayOrigin + R * hi;
                float fdist;
                if (ProjectCamPosToUV(fp, hitUV, fdist))
                {
                    float fd = gTex1.SampleLevel(gPoint, hitUV, 0).r;
                    if (!IsBackgroundDepth(fd) && abs(fdist - LinearDistFromDepth(fd)) < max(40.0, distC * 0.07))
                    {
                        hitT = hi;
                    }
                }
                break;
            }
        }
        prevT = t;
    }

    if (hitT < 0.0) return float4(0.0, 0.0, 0.0, 0.0);

    float4 hitSample = gTex0.SampleLevel(gLinear, hitUV, 0);
    if (hitSample.a <= 1e-4) return float4(0.0, 0.0, 0.0, 0.0);
    float3 hitRgb = min(hitSample.rgb / hitSample.a, float3(16.0, 16.0, 16.0));

    float edgeFade = smoothstep(0.0, 0.08, min(min(hitUV.x, 1.0 - hitUV.x), min(hitUV.y, 1.0 - hitUV.y)));
    float roughFade = 1.0 - smoothstep(SsrParams.z * 0.60, SsrParams.z, roughness);
    float distFade = 1.0 - saturate(hitT / maxRayLen);
    float conf = saturate(edgeFade * roughFade * distFade * backFade * saturate(hitSample.a));
    return float4(hitRgb, conf);
}

float4 BilateralBlurSsr(PSInput input, float2 stepDir)
{
    float dC = gTex1.SampleLevel(gPoint, input.UV, 0).r;
    if (IsBackgroundDepth(dC)) return float4(0.0, 0.0, 0.0, 0.0);

    float4 gbufNR = gGbufNormalRough.SampleLevel(gPoint, input.UV, 0);
    float roughness = gbufNR.a >= 0.03 ? clamp(gbufNR.a, 0.04, 1.0) : 0.5;
    float blurScale = saturate((roughness - 0.06) * 2.0);
    if (blurScale <= 0.05) return gTex0.SampleLevel(gPoint, input.UV, 0);

    float distC = LinearDistFromDepth(dC);
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float wSum = 0.0;
    static const float weights[4] = { 0.32, 0.24, 0.14, 0.06 };
    float2 step = stepDir * blurScale;

    [unroll]
    for (int i = -3; i <= 3; i++)
    {
        float2 suv = input.UV + step * float(i);
        float dS = gTex1.SampleLevel(gPoint, suv, 0).r;
        float distS = LinearDistFromDepth(dS);
        float relDiff = abs(distS - distC) / max(distC * 0.05, 8.0);
        float w = weights[abs(i)] * exp(-relDiff * relDiff * 3.5);
        sum += gTex0.SampleLevel(gLinear, suv, 0) * w;
        wSum += w;
    }
    return sum / max(wSum, 1e-4);
}

float4 PS_SsrBlurH(PSInput input) : SV_Target { return BilateralBlurSsr(input, float2(RcpDstWidth, 0.0)); }
float4 PS_SsrBlurV(PSInput input) : SV_Target { return BilateralBlurSsr(input, float2(0.0, RcpDstHeight)); }

// --- Half-Res Volumetric God Rays (Crepuscular Light Shafts) ---
float4 PS_GodRayMask(PSInput input) : SV_Target
{
    if (GodRayParams.z <= 0.001) return float4(0.0, 0.0, 0.0, 0.0);

    float2 uv = input.UV;
    float d = gTex1.SampleLevel(gPoint, uv, 0).r;
    if (!IsBackgroundDepth(d))
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float aspect = SrcWidth / max(SrcHeight, 1.0);
    float2 delta = (uv - GodRayParams.xy) * float2(aspect, 1.0);
    float distToSunSq = dot(delta, delta);
    float sunDisc = exp(-distToSunSq * 4.5);

    float4 bg = gTex0.SampleLevel(gLinear, uv, 0);
    float3 skyVal = (bg.a > 0.5)
        ? (min(bg.rgb / bg.a, float3(4.0, 4.0, 4.0)) * 0.65 + GodRayColorAndDensity.rgb * 0.6)
        : GodRayColorAndDensity.rgb;

    return float4(skyVal * (0.25 + 1.25 * sunDisc), 1.0);
}

float4 PS_GodRayBlur(PSInput input) : SV_Target
{
    if (GodRayParams.z <= 0.001) return float4(0.0, 0.0, 0.0, 0.0);

    float2 texCoord = input.UV;
    float2 deltaTexCoord = (texCoord - GodRayParams.xy);
    const int Samples = 24;
    deltaTexCoord *= (1.0 / float(Samples)) * GodRayColorAndDensity.w;

    float jitter = IGN(input.Pos.xy);
    texCoord -= deltaTexCoord * jitter;

    float3 accum = float3(0.0, 0.0, 0.0);
    float illuminationDecay = 1.0;
    float decay = clamp(GodRayParams.w, 0.75, 0.995);

    [unroll]
    for (int i = 0; i < Samples; i++)
    {
        texCoord -= deltaTexCoord;
        float2 clampedUv = saturate(texCoord);
        float inBounds = (texCoord.x >= 0.0 && texCoord.x <= 1.0 && texCoord.y >= 0.0 && texCoord.y <= 1.0) ? 1.0 : 0.0;
        float3 s = gTex0.SampleLevel(gLinear, clampedUv, 0).rgb * inBounds;
        accum += s * illuminationDecay;
        illuminationDecay *= decay;
    }

    return float4(accum / float(Samples), 1.0);
}

// --- Depth-aware Upsample & Composite AO + SSGI + SSR + Contact Shadow + Fog + GodRays ---
float3 EnvBRDFApproxPost(float3 F0, float roughness, float ndv)
{
    const float4 c0 = float4(-1.0, -0.0275, -0.572, 0.022);
    const float4 c1 = float4( 1.0,  0.0425,  1.04, -0.04);
    float4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * ndv)) * r.x + r.y;
    float2 AB = float2(-1.04, 1.04) * a004 + r.zw;
    return saturate(F0 * AB.x + AB.y);
}

float ComputeContactShadow(float2 uv, float3 posC, float3 nCam, float distC, float2 pixelPos)
{
    if (ContactParams.x < 0.5 || ContactParams.y <= 0.001) return 1.0;

    float3 L = normalize(ContactLightDirAndLen.xyz);
    float nDotL = saturate(dot(nCam, L) + 0.35);
    if (nDotL <= 0.01) return 1.0;

    float rayLen = ContactLightDirAndLen.w * max(distC / 1000.0, 0.25);
    const int CSteps = 8;
    float3 origin = posC + nCam * clamp(distC * 0.002, 1.0, 4.0);
    float jitter = IGN(pixelPos + float2(3.7, 7.1));
    float occ = 0.0;

    [unroll]
    for (int i = 1; i <= CSteps; i++)
    {
        float t = (float(i) - 0.3 + 0.6 * jitter) / float(CSteps);
        float3 sp = origin + L * (t * rayLen);
        float2 suv;
        float rayDist;
        if (!ProjectCamPosToUV(sp, suv, rayDist))
            break;

        float dHit = gTex1.SampleLevel(gPoint, suv, 0).r;
        if (!IsBackgroundDepth(dHit))
        {
            float sceneDist = LinearDistFromDepth(dHit);
            float delta = rayDist - sceneDist;
            if (delta > 1.2 && delta < rayLen * 0.65)
            {
                occ = max(occ, 1.0 - t * 0.55);
            }
        }
    }

    return lerp(1.0, 1.0 - clamp(ContactParams.y, 0.0, 1.0) * 0.72 * nDotL, occ);
}

void SampleAoGiUpsampled(float2 uv, float dC, out float outAo, out float3 outGi)
{
    float distC = LinearDistFromDepth(dC);
    float2 halfTexel = float2(RcpDstWidth, RcpDstHeight);
    float aoAcc = 0.0;
    float3 giAcc = float3(0.0, 0.0, 0.0);
    float wSum = 0.0;

    [unroll]
    for (int dy = -1; dy <= 1; dy += 2)
    {
        [unroll]
        for (int dx = -1; dx <= 1; dx += 2)
        {
            float2 suv = uv + float2(dx, dy) * halfTexel * 0.5;
            float dS = gTex1.SampleLevel(gPoint, suv, 0).r;
            float distS = LinearDistFromDepth(dS);
            float relDiff = abs(distS - distC) / max(distC * 0.06, 8.0);
            float w = exp(-relDiff * relDiff * 4.0) + 1e-4;
            aoAcc += gTex2.SampleLevel(gLinear, suv, 0).r * w;
            giAcc += gTex3.SampleLevel(gLinear, suv, 0).rgb * w;
            wSum += w;
        }
    }

    outAo = aoAcc / wSum;
    outGi = giAcc / wSum;
}

float4 PS_ApplyAoGi(PSInput input) : SV_Target
{
    float4 src = gTex0.SampleLevel(gPoint, input.UV, 0);
    float3 godRay = (GodRayParams.z > 0.001)
        ? gGodRayTex.SampleLevel(gLinear, input.UV, 0).rgb * GodRayParams.z
        : float3(0.0, 0.0, 0.0);

    float dC = gTex1.SampleLevel(gPoint, input.UV, 0).r;
    if (src.a <= 1e-4 || IsBackgroundDepth(dC))
    {
        if (GodRayParams.z > 0.001)
        {
            float rayLuma = dot(godRay, float3(0.2126, 0.7152, 0.0722));
            float rayAlpha = saturate(rayLuma / (1.0 + rayLuma) * 1.4);
            if (src.a <= 1e-4)
                return float4(godRay, rayAlpha);
            float outA = saturate(src.a + rayAlpha * (1.0 - src.a));
            return float4(src.rgb + godRay, outA);
        }
        return src;
    }

    float3 posC = ReconstructCamPos(input.UV, dC);
    float distC = LinearDistFromDepth(dC);
    float roughness;
    float3 F0;
    float hasGbuf;
    float3 nCam = GetCamNormal(input.UV, dC, posC, float2(RcpSrcWidth, RcpSrcHeight), roughness, F0, hasGbuf);

    float ao = 1.0;
    float3 gi = float3(0.0, 0.0, 0.0);
    if (AoIntensity > 0.001 || GiIntensity > 0.001)
    {
        SampleAoGiUpsampled(input.UV, dC, ao, gi);
    }

    float3 straight = src.rgb / src.a;
    float luma = dot(straight, float3(0.2126, 0.7152, 0.0722));

    // Luminance-aware AO preserves bright direct lights & speculars
    float aoFactor = 1.0;
    if (AoIntensity > 0.001)
    {
        float aoMod = pow(saturate(ao), max(AoIntensity, 0.01));
        aoFactor = lerp(1.0, aoMod, 1.0 - saturate(luma * 0.25));
    }

    // Contact shadow darkening on foreground geometry crevices
    float contactFactor = ComputeContactShadow(input.UV, posC, nCam, distC, input.Pos.xy);

    // Subtle albedo-coupled bounce contribution so dark objects do not glow unnaturally
    float3 bounceAlbedo = clamp(straight * 0.65 + 0.15, 0.0, 1.0);
    float giPeak = max(gi.r, max(gi.g, gi.b));
    float giHeadroom = lerp(1.0 - saturate(luma * 0.80), 1.0, saturate(giPeak - 1.0));
    float3 combined = straight * (aoFactor * contactFactor) + gi * bounceAlbedo * (GiIntensity * 0.45 * giHeadroom);

    // SSR composite (roughness + Fresnel F0 weighted)
    if (SsrParams.x > 0.5 && SsrParams.y > 0.001 && hasGbuf > 0.5 && roughness <= SsrParams.z)
    {
        float4 ssr = gSsrTex.SampleLevel(gLinear, input.UV, 0);
        if (ssr.a > 0.001)
        {
            float3 V = normalize(float3(0.0, 0.0, 1000.0) - posC);
            float ndv = max(dot(nCam, V), 0.05);
            float3 fresnel = EnvBRDFApproxPost(F0, roughness, ndv);
            float3 ssrW = saturate(fresnel * SsrParams.y * ssr.a);
            combined = combined * (1.0 - ssrW * 0.35) + ssr.rgb * ssrW;
        }
    }

    // Distance + Height Atmospheric Fog
    if (FogColorAndDensity.w > 0.0001)
    {
        float distFactor = saturate((distC - FogRangeAndHeight.x) / max(FogRangeAndHeight.y - FogRangeAndHeight.x, 1.0));
        float worldY = dot(posC, InvCamColY.xyz) + InvCamColY.w;
        float heightFactor = saturate((worldY - FogRangeAndHeight.w) / 600.0 + 0.5);
        float fogAmount = saturate(distFactor * FogColorAndDensity.w * lerp(1.0, heightFactor * 1.4, FogRangeAndHeight.z));
        combined = lerp(combined, FogColorAndDensity.rgb, fogAmount);
    }

    combined += godRay;
    return float4(combined * src.a, src.a);
}

// --- Multi-Mip Karis Average + Soft Knee Bloom ---
float3 SafeColor(float3 c)
{
    return clamp(max(c, 0.0), 0.0, 4096.0);
}

float KarisWeight(float3 c)
{
    float l = dot(c, float3(0.2126, 0.7152, 0.0722));
    return 1.0 / (1.0 + l);
}

float3 ApplySoftThreshold(float3 c, float threshold, float knee)
{
    float br = max(c.r, max(c.g, c.b));
    float excess = max(0.0, br - threshold);
    float contrib = (excess * excess) / max(excess + max(knee, 1e-4), 1e-4);
    return c * saturate(contrib / max(br, 1e-4));
}

float4 PS_BloomPrefilter(PSInput input) : SV_Target
{
    float2 texel = float2(RcpSrcWidth, RcpSrcHeight);
    float2 uv = input.UV;

    float3 a = SafeColor(gTex0.SampleLevel(gLinear, uv + texel * float2(-1, -1), 0).rgb) * ExposureMult;
    float3 b = SafeColor(gTex0.SampleLevel(gLinear, uv + texel * float2( 0, -1), 0).rgb) * ExposureMult;
    float3 c = SafeColor(gTex0.SampleLevel(gLinear, uv + texel * float2( 1, -1), 0).rgb) * ExposureMult;
    float3 d = SafeColor(gTex0.SampleLevel(gLinear, uv + texel * float2(-1,  0), 0).rgb) * ExposureMult;
    float3 e = SafeColor(gTex0.SampleLevel(gLinear, uv, 0).rgb) * ExposureMult;
    float3 f = SafeColor(gTex0.SampleLevel(gLinear, uv + texel * float2( 1,  0), 0).rgb) * ExposureMult;
    float3 g = SafeColor(gTex0.SampleLevel(gLinear, uv + texel * float2(-1,  1), 0).rgb) * ExposureMult;
    float3 h = SafeColor(gTex0.SampleLevel(gLinear, uv + texel * float2( 0,  1), 0).rgb) * ExposureMult;
    float3 i = SafeColor(gTex0.SampleLevel(gLinear, uv + texel * float2( 1,  1), 0).rgb) * ExposureMult;

    float3 g0 = (a + b + d + e) * 0.25;
    float3 g1 = (b + c + e + f) * 0.25;
    float3 g2 = (d + e + g + h) * 0.25;
    float3 g3 = (e + f + h + i) * 0.25;

    float w0 = KarisWeight(g0);
    float w1 = KarisWeight(g1);
    float w2 = KarisWeight(g2);
    float w3 = KarisWeight(g3);
    float3 avg = (g0 * w0 + g1 * w1 + g2 * w2 + g3 * w3) / max(w0 + w1 + w2 + w3, 1e-4);

    return float4(ApplySoftThreshold(avg, BloomThreshold, BloomKnee), 1.0);
}

float4 PS_BloomDownsample(PSInput input) : SV_Target
{
    float2 t = float2(RcpSrcWidth, RcpSrcHeight);
    float2 uv = input.UV;

    float3 a = gTex0.SampleLevel(gLinear, uv + t * float2(-2, -2), 0).rgb;
    float3 b = gTex0.SampleLevel(gLinear, uv + t * float2( 0, -2), 0).rgb;
    float3 c = gTex0.SampleLevel(gLinear, uv + t * float2( 2, -2), 0).rgb;
    float3 d = gTex0.SampleLevel(gLinear, uv + t * float2(-1, -1), 0).rgb;
    float3 e = gTex0.SampleLevel(gLinear, uv + t * float2( 1, -1), 0).rgb;
    float3 f = gTex0.SampleLevel(gLinear, uv + t * float2(-2,  0), 0).rgb;
    float3 g = gTex0.SampleLevel(gLinear, uv, 0).rgb;
    float3 h = gTex0.SampleLevel(gLinear, uv + t * float2( 2,  0), 0).rgb;
    float3 i = gTex0.SampleLevel(gLinear, uv + t * float2(-1,  1), 0).rgb;
    float3 j = gTex0.SampleLevel(gLinear, uv + t * float2( 1,  1), 0).rgb;
    float3 k = gTex0.SampleLevel(gLinear, uv + t * float2(-2,  2), 0).rgb;
    float3 l = gTex0.SampleLevel(gLinear, uv + t * float2( 0,  2), 0).rgb;
    float3 m = gTex0.SampleLevel(gLinear, uv + t * float2( 2,  2), 0).rgb;

    float3 res = g * 0.125;
    res += (a + c + k + m) * 0.03125;
    res += (b + f + h + l) * 0.0625;
    res += (d + e + i + j) * 0.125;
    return float4(max(res, 0.0), 1.0);
}

float4 PS_BloomUpsample(PSInput input) : SV_Target
{
    float2 t = float2(RcpSrcWidth, RcpSrcHeight);
    float2 uv = input.UV;

    float3 a = gTex0.SampleLevel(gLinear, uv + t * float2(-1, -1), 0).rgb;
    float3 b = gTex0.SampleLevel(gLinear, uv + t * float2( 0, -1), 0).rgb;
    float3 c = gTex0.SampleLevel(gLinear, uv + t * float2( 1, -1), 0).rgb;
    float3 d = gTex0.SampleLevel(gLinear, uv + t * float2(-1,  0), 0).rgb;
    float3 e = gTex0.SampleLevel(gLinear, uv, 0).rgb;
    float3 f = gTex0.SampleLevel(gLinear, uv + t * float2( 1,  0), 0).rgb;
    float3 g = gTex0.SampleLevel(gLinear, uv + t * float2(-1,  1), 0).rgb;
    float3 h = gTex0.SampleLevel(gLinear, uv + t * float2( 0,  1), 0).rgb;
    float3 i = gTex0.SampleLevel(gLinear, uv + t * float2( 1,  1), 0).rgb;

    float3 tent = (e * 4.0 + (b + d + f + h) * 2.0 + (a + c + g + i)) * (1.0 / 16.0);
    return float4(max(tent, 0.0), 1.0);
}

// --- Tone Mapping Curves (Gran Turismo / ACES Filmic / AgX) ---
float3 ToneMapGT(float3 x, float P)
{
    const float a = 1.0;
    const float m = 0.22;
    const float l = 0.40;
    const float c = 1.33;
    float l0 = ((P - m) * l) / a;
    float S0 = m + l0;
    float S1 = m + a * l0;
    float C2 = (a * P) / max(P - S1, 1e-4);
    float CP = -C2 / max(P, 1e-4);

    float3 w0 = 1.0 - smoothstep(0.0, m, x);
    float3 w2 = step(S0, x);
    float3 w1 = 1.0 - w0 - w2;

    float3 T = m * pow(max(x / m, 1e-6), c);
    float3 L = m + a * (x - m);
    float3 S = P - (P - S1) * exp(CP * (x - S0));
    return max(T * w0 + L * w1 + S * w2, 0.0);
}

float3 AcesCurve(float3 x)
{
    return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
}

float3 ToneMapACES(float3 x, float P)
{
    float3 sdr = AcesCurve(x);
    if (P <= 1.02) return sdr;
    float maxC = max(x.r, max(x.g, x.b));
    if (maxC <= 1.0) return sdr;
    float hdrGain = 1.0 + (P - 1.0) * (1.0 - exp(-(maxC - 1.0) / max(P * 0.75, 0.5)));
    return sdr * hdrGain;
}

float3 AgxDefaultContrastApprox(float3 x)
{
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    return + 15.5     * x4 * x2
           - 40.14    * x4 * x
           + 31.96    * x4
           - 6.868    * x2 * x
           + 0.4298   * x2
           + 0.1191   * x
           - 0.00232;
}

float3 AgxCurve(float3 val)
{
    const float3x3 agxMat = float3x3(
        0.84247906, 0.04232824, 0.04237565,
        0.07843360, 0.87846864, 0.07843360,
        0.07922375, 0.07916613, 0.87914297
    );
    const float3x3 agxMatInv = float3x3(
         1.1968790, -0.05289685, -0.05297163,
        -0.09802088, 1.1519031,  -0.09804345,
        -0.09902974,-0.09896118,  1.1510737
    );

    const float minEv = -12.47393;
    const float maxEv = 4.026069;

    val = mul(max(val, 1e-6), agxMat);
    val = clamp(log2(max(val, 1e-6)), minEv, maxEv);
    val = (val - minEv) / (maxEv - minEv);
    val = AgxDefaultContrastApprox(val);
    val = mul(val, agxMatInv);
    val = clamp(val, 0.0, 1.0);
    // AgX sRGB output in standard impl is already gamma-like; convert back to linear so final pass handles unified encoding
    return pow(max(val, 0.0), 2.2);
}

float3 ToneMapAgX(float3 x, float P)
{
    float3 sdr = AgxCurve(x);
    if (P <= 1.02) return sdr;
    float maxC = max(x.r, max(x.g, x.b));
    if (maxC <= 1.0) return sdr;
    float hdrGain = 1.0 + (P - 1.0) * (1.0 - exp(-(maxC - 1.0) / max(P * 0.75, 0.5)));
    return sdr * hdrGain;
}

float3 ApplyToneMap(float3 linearRgb, float mode, float P)
{
    if (mode > 2.5)
        return (IsHdrOutput > 0.5) ? min(max(linearRgb, 0.0), P) : saturate(linearRgb);
    if (mode > 1.5)
        return ToneMapAgX(linearRgb, P);
    if (mode > 0.5)
        return ToneMapACES(linearRgb, P);
    return ToneMapGT(linearRgb, P);
}

float LinearToSrgbCh(float x)
{
    x = max(x, 0.0);
    return (x <= 0.0031308) ? (12.92 * x) : (1.055 * pow(x, 1.0 / 2.4) - 0.055);
}

float3 LinearToSrgbFinal(float3 c)
{
    float3 s = float3(LinearToSrgbCh(c.r), LinearToSrgbCh(c.g), LinearToSrgbCh(c.b));
    return (IsHdrOutput > 0.5) ? s : saturate(s);
}

float4 PS_ToneMapComposite(PSInput input) : SV_Target
{
    float4 src = gTex0.SampleLevel(gPoint, input.UV, 0);
    float3 bloom = (BloomIntensity > 0.0001)
        ? gTex1.SampleLevel(gLinear, input.UV, 0).rgb * BloomIntensity
        : float3(0.0, 0.0, 0.0);

    float P = (IsHdrOutput > 0.5) ? max(PeakLuminance, 1.0) : 1.0;
    float a = saturate(src.a);

    float3 bodyOut = float3(0.0, 0.0, 0.0);
    if (a > 1e-4)
    {
        float3 straightHdr = (max(src.rgb, 0.0) / a) * ExposureMult + bloom;
        float3 mapped = ApplyToneMap(straightHdr, ToneMode, P);
        bodyOut = LinearToSrgbFinal(mapped) * a;
    }

    float3 haloMapped = ApplyToneMap(max(bloom, 0.0), ToneMode, P);
    float3 haloSrgb = LinearToSrgbFinal(haloMapped);
    float haloAlpha = saturate(max(haloSrgb.r, max(haloSrgb.g, haloSrgb.b)));

    float outA = saturate(a + haloAlpha * (1.0 - a));
    float3 finalRgb = bodyOut + haloSrgb * (1.0 - a);
    if (DitherScale > 0.5 && outA > 1e-4)
    {
        float noise = (IGN(input.Pos.xy) - 0.5) / 255.0;
        finalRgb = clamp(finalRgb + noise, 0.0, outA);
    }

    return float4(finalRgb, outA);
}
";

    public void Initialize(ID3D11Device device)
    {
        if (_disposed) return;
        if (_initialized && _devicePointer == device.NativePointer) return;

        Release();

        try
        {
            var vsBytes = ShaderBytecodeCache.GetOrCompile("Post_VS", ShaderSource, "VS_Post", "vs_5_0", "post_shader");
            var psGtaoBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_Gtao", ShaderSource, "PS_Gtao", "ps_5_0", "post_shader");
            var psAoHBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_AoBlurH", ShaderSource, "PS_AoBlurH", "ps_5_0", "post_shader");
            var psAoVBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_AoBlurV", ShaderSource, "PS_AoBlurV", "ps_5_0", "post_shader");
            var psSsgiBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_Ssgi", ShaderSource, "PS_Ssgi", "ps_5_0", "post_shader");
            var psGiHBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_GiBlurH", ShaderSource, "PS_GiBlurH", "ps_5_0", "post_shader");
            var psGiVBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_GiBlurV", ShaderSource, "PS_GiBlurV", "ps_5_0", "post_shader");
            var psSsrBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_Ssr", ShaderSource, "PS_Ssr", "ps_5_0", "post_shader");
            var psSsrHBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_SsrBlurH", ShaderSource, "PS_SsrBlurH", "ps_5_0", "post_shader");
            var psSsrVBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_SsrBlurV", ShaderSource, "PS_SsrBlurV", "ps_5_0", "post_shader");
            var psGrMaskBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_GodRayMask", ShaderSource, "PS_GodRayMask", "ps_5_0", "post_shader");
            var psGrBlurBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_GodRayBlur", ShaderSource, "PS_GodRayBlur", "ps_5_0", "post_shader");
            var psApplyBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_ApplyAoGi", ShaderSource, "PS_ApplyAoGi", "ps_5_0", "post_shader");
            var psPreBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_BloomPre", ShaderSource, "PS_BloomPrefilter", "ps_5_0", "post_shader");
            var psDownBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_BloomDown", ShaderSource, "PS_BloomDownsample", "ps_5_0", "post_shader");
            var psUpBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_BloomUp", ShaderSource, "PS_BloomUpsample", "ps_5_0", "post_shader");
            var psToneBytes = ShaderBytecodeCache.GetOrCompile("Post_PS_ToneMap", ShaderSource, "PS_ToneMapComposite", "ps_5_0", "post_shader");

            if (vsBytes == null || psGtaoBytes == null || psAoHBytes == null || psAoVBytes == null ||
                psSsgiBytes == null || psGiHBytes == null || psGiVBytes == null ||
                psSsrBytes == null || psSsrHBytes == null || psSsrVBytes == null ||
                psGrMaskBytes == null || psGrBlurBytes == null || psApplyBytes == null ||
                psPreBytes == null || psDownBytes == null || psUpBytes == null || psToneBytes == null)
            {
                return;
            }

            _vsPost = device.CreateVertexShader(vsBytes);
            _psGtao = device.CreatePixelShader(psGtaoBytes);
            _psAoBlurH = device.CreatePixelShader(psAoHBytes);
            _psAoBlurV = device.CreatePixelShader(psAoVBytes);
            _psSsgi = device.CreatePixelShader(psSsgiBytes);
            _psGiBlurH = device.CreatePixelShader(psGiHBytes);
            _psGiBlurV = device.CreatePixelShader(psGiVBytes);
            _psSsr = device.CreatePixelShader(psSsrBytes);
            _psSsrBlurH = device.CreatePixelShader(psSsrHBytes);
            _psSsrBlurV = device.CreatePixelShader(psSsrVBytes);
            _psGodRayMask = device.CreatePixelShader(psGrMaskBytes);
            _psGodRayBlur = device.CreatePixelShader(psGrBlurBytes);
            _psApplyAoGi = device.CreatePixelShader(psApplyBytes);
            _psBloomPrefilter = device.CreatePixelShader(psPreBytes);
            _psBloomDownsample = device.CreatePixelShader(psDownBytes);
            _psBloomUpsample = device.CreatePixelShader(psUpBytes);
            _psToneMapComposite = device.CreatePixelShader(psToneBytes);

            _cbPost = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbPost>(), BindFlags.ConstantBuffer));

            var addDesc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            addDesc.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = Blend.One,
                DestinationBlend = Blend.One,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.One,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            };
            _blendAdditive = device.CreateBlendState(addDesc);

            _devicePointer = device.NativePointer;
            _initialized = true;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"PostProcessPipeline Initialize エラー: {ex.Message}");
            Release();
        }
    }

    public (ID3D11Texture2D tex, ID3D11RenderTargetView rtv, ID3D11ShaderResourceView srv) ApplyAoAndGiIfEnabled(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11Texture2D hdrSceneTex,
        ID3D11RenderTargetView hdrSceneRtv,
        ID3D11ShaderResourceView hdrSceneSrv,
        ID3D11ShaderResourceView depthSrv,
        ID3D11InputLayout quadInputLayout,
        ID3D11Buffer quadVb,
        int vertexStride,
        ID3D11SamplerState samplerLinear,
        ID3D11SamplerState samplerPoint,
        ID3D11BlendState blendOpaqueCopy,
        ID3D11DepthStencilState depthDisabled,
        ID3D11RasterizerState rasterizer,
        int width,
        int height,
        int screenWidth,
        int screenHeight)
    {
        return ApplyPostOpaque(
            device, ctx,
            hdrSceneTex, hdrSceneRtv, hdrSceneSrv,
            depthSrv, null, null,
            quadInputLayout, quadVb, vertexStride,
            samplerLinear, samplerPoint, blendOpaqueCopy, depthDisabled, rasterizer,
            width, height, screenWidth, screenHeight,
            Matrix4x4.Identity, Array.Empty<LightData>(), default);
    }

    public (ID3D11Texture2D tex, ID3D11RenderTargetView rtv, ID3D11ShaderResourceView srv) ApplyPostOpaque(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11Texture2D hdrSceneTex,
        ID3D11RenderTargetView hdrSceneRtv,
        ID3D11ShaderResourceView hdrSceneSrv,
        ID3D11ShaderResourceView depthSrv,
        ID3D11ShaderResourceView? gbufNormalRoughSrv,
        ID3D11ShaderResourceView? gbufSpecMetaSrv,
        ID3D11InputLayout quadInputLayout,
        ID3D11Buffer quadVb,
        int vertexStride,
        ID3D11SamplerState samplerLinear,
        ID3D11SamplerState samplerPoint,
        ID3D11BlendState blendOpaqueCopy,
        ID3D11DepthStencilState depthDisabled,
        ID3D11RasterizerState rasterizer,
        int renderWidth,
        int renderHeight,
        int screenWidth,
        int screenHeight,
        in Matrix4x4 cameraMatrix,
        IReadOnlyList<LightData> activeLights,
        in AtmosphereParameters atmosphere)
    {
        var settings = D3D11RendererSettings.Default;
        bool wantAo = settings.EnableSsao && settings.SsaoIntensity > 0.001;
        bool wantGi = settings.EnableSsgi && settings.SsgiIntensity > 0.001;
        bool wantSsr = settings.EnableSsr && settings.SsrIntensity > 0.001 && gbufNormalRoughSrv != null && gbufSpecMetaSrv != null;
        bool wantContact = settings.EnableContactShadow && settings.ContactShadowIntensity > 0.001;

        bool fogFromEffect = atmosphere.Enabled && atmosphere.EnableFog && atmosphere.FogDensity > 0.0001f;
        bool fogFromGlobal = !atmosphere.EnableFog && settings.EnableFog && settings.FogDensity > 0.0001;
        bool wantFog = fogFromEffect || fogFromGlobal;

        bool grFromEffect = atmosphere.Enabled && atmosphere.EnableGodRays && atmosphere.GodRayIntensity > 0.001f;
        bool grFromGlobal = !atmosphere.EnableGodRays && settings.EnableGodRays && settings.GodRayIntensity > 0.001;
        bool wantGodRays = grFromEffect || grFromGlobal;

        if (!_initialized || (!wantAo && !wantGi && !wantSsr && !wantContact && !wantFog && !wantGodRays))
        {
            DisposeAoTargets();
            DisposeGiTargets();
            DisposeSsrTargets();
            DisposeGodRayTargets();
            DisposeHdrPostTarget();
            return (hdrSceneTex, hdrSceneRtv, hdrSceneSrv);
        }

        int halfW = Math.Max(1, renderWidth / 2);
        int halfH = Math.Max(1, renderHeight / 2);

        if (wantAo) EnsureAoTargets(device, halfW, halfH); else DisposeAoTargets();
        if (wantGi) EnsureGiTargets(device, halfW, halfH); else DisposeGiTargets();
        if (wantSsr) EnsureSsrTargets(device, halfW, halfH); else DisposeSsrTargets();
        if (wantGodRays) EnsureGodRayTargets(device, halfW, halfH); else DisposeGodRayTargets();
        EnsureHdrPostTarget(device, renderWidth, renderHeight);

        if (_hdrPostTex == null || _hdrPostRtv == null || _hdrPostSrv == null)
        {
            return (hdrSceneTex, hdrSceneRtv, hdrSceneSrv);
        }

        LightManager.ComputeDominantLightScreenAndDir(
            activeLights, in cameraMatrix, screenWidth, screenHeight,
            out Vector3 lightDirCam, out _, out Vector2 lightScreenUv, out Vector3 domLightColor, out float lightVis);

        if (!Matrix4x4.Invert(cameraMatrix, out var invCam))
            invCam = Matrix4x4.Identity;

        Vector3 fogColor = fogFromEffect ? atmosphere.FogColor : new Vector3(0.50f, 0.62f, 0.78f);
        float fogDensity = fogFromEffect ? atmosphere.FogDensity : (fogFromGlobal ? (float)settings.FogDensity : 0f);
        float fogStart = fogFromEffect ? atmosphere.FogStart : (float)settings.FogStart;
        float fogEnd = fogFromEffect ? atmosphere.FogEnd : MathF.Max((float)settings.FogStart + 50f, (float)settings.FogEnd);
        float heightFogStr = fogFromEffect ? atmosphere.HeightFogStrength : 0f;
        float heightFogBaseY = fogFromEffect ? atmosphere.HeightFogBaseY : 300f;

        Vector3 rayColor = grFromEffect
            ? atmosphere.GodRayColor
            : (domLightColor * 1.1f);
        float rayIntensity = grFromEffect
            ? atmosphere.GodRayIntensity
            : (grFromGlobal ? (float)settings.GodRayIntensity : 0f);
        rayIntensity *= (0.25f + 0.75f * lightVis);
        float rayDecay = grFromEffect ? atmosphere.GodRayDecay : 0.95f;
        float rayDensity = grFromEffect ? atmosphere.GodRayDensity : 0.90f;

        ctx.IASetInputLayout(quadInputLayout);
        ctx.IASetVertexBuffer(0, quadVb, vertexStride, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.RSSetState(rasterizer);
        ctx.OMSetDepthStencilState(depthDisabled, 0);
        ctx.OMSetBlendState(blendOpaqueCopy, null, unchecked((int)0xFFFFFFFF));
        ctx.VSSetShader(_vsPost);
        ctx.PSSetSampler(0, samplerLinear);
        ctx.PSSetSampler(1, samplerPoint);

        var cb = new CbPost
        {
            SrcWidth = renderWidth,
            SrcHeight = renderHeight,
            RcpSrcWidth = 1f / Math.Max(1, renderWidth),
            RcpSrcHeight = 1f / Math.Max(1, renderHeight),
            HalfScreenW = screenWidth * 0.5f,
            HalfScreenH = screenHeight * 0.5f,
            RcpDstWidth = 1f / halfW,
            RcpDstHeight = 1f / halfH,
            AoIntensity = wantAo ? (float)settings.SsaoIntensity : 0f,
            AoRadiusWorld = (float)settings.SsaoRadius,
            GiIntensity = wantGi ? (float)settings.SsgiIntensity : 0f,
            SsrParams = new Vector4(
                wantSsr ? 1f : 0f,
                (float)settings.SsrIntensity,
                (float)settings.SsrMaxRoughness,
                0f),
            ContactLightDirAndLen = new Vector4(lightDirCam, (float)settings.ContactShadowLength),
            ContactParams = new Vector4(
                wantContact ? 1f : 0f,
                (float)settings.ContactShadowIntensity,
                1f,
                0f),
            FogColorAndDensity = new Vector4(fogColor, wantFog ? fogDensity : 0f),
            FogRangeAndHeight = new Vector4(fogStart, fogEnd, heightFogStr, heightFogBaseY),
            InvCamColY = new Vector4(invCam.M12, invCam.M22, invCam.M32, invCam.M42),
            GodRayParams = new Vector4(lightScreenUv.X, lightScreenUv.Y, wantGodRays ? rayIntensity : 0f, rayDecay),
            GodRayColorAndDensity = new Vector4(rayColor, rayDensity),
        };
        ctx.UpdateSubresource(in cb, _cbPost!);
        ctx.PSSetConstantBuffer(0, _cbPost);

        // Bind G-Buffer SRVs to t4/t5 for all subpasses that inspect true normals/roughness
        if (gbufNormalRoughSrv != null) ctx.PSSetShaderResource(4, gbufNormalRoughSrv);
        if (gbufSpecMetaSrv != null) ctx.PSSetShaderResource(5, gbufSpecMetaSrv);

        // 1. Half-res GTAO + Bilateral Blur
        if (wantAo && _aoRawRtv != null && _aoTempRtv != null && _aoFinalRtv != null)
        {
            ctx.RSSetViewport(new Viewport(0, 0, halfW, halfH, 0f, 1f));

            ctx.OMSetRenderTargets(_aoRawRtv, null);
            ctx.PSSetShader(_psGtao);
            ctx.PSSetShaderResource(1, depthSrv);
            ctx.Draw(4, 0);

            ctx.OMSetRenderTargets(_aoTempRtv, null);
            ctx.PSSetShader(_psAoBlurH);
            ctx.PSSetShaderResource(0, _aoRawSrv!);
            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, null!);
            ctx.OMSetRenderTargets(_aoFinalRtv, null);
            ctx.PSSetShader(_psAoBlurV);
            ctx.PSSetShaderResource(0, _aoTempSrv!);
            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, null!);
            ctx.PSSetShaderResource(1, null!);
            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        }

        // 2. Half-res SSGI + Bilateral Blur
        if (wantGi && _giRawRtv != null && _giTempRtv != null && _giFinalRtv != null)
        {
            ctx.RSSetViewport(new Viewport(0, 0, halfW, halfH, 0f, 1f));

            ctx.OMSetRenderTargets(_giRawRtv, null);
            ctx.PSSetShader(_psSsgi);
            ctx.PSSetShaderResource(0, hdrSceneSrv);
            ctx.PSSetShaderResource(1, depthSrv);
            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, null!);
            ctx.OMSetRenderTargets(_giTempRtv, null);
            ctx.PSSetShader(_psGiBlurH);
            ctx.PSSetShaderResource(0, _giRawSrv!);
            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, null!);
            ctx.OMSetRenderTargets(_giFinalRtv, null);
            ctx.PSSetShader(_psGiBlurV);
            ctx.PSSetShaderResource(0, _giTempSrv!);
            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, null!);
            ctx.PSSetShaderResource(1, null!);
            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        }

        // 3. Half-res SSR + Roughness-aware Bilateral Blur
        if (wantSsr && _ssrRtv != null && _ssrTempRtv != null)
        {
            ctx.RSSetViewport(new Viewport(0, 0, halfW, halfH, 0f, 1f));

            ctx.OMSetRenderTargets(_ssrRtv, null);
            ctx.PSSetShader(_psSsr);
            ctx.PSSetShaderResource(0, hdrSceneSrv);
            ctx.PSSetShaderResource(1, depthSrv);
            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, null!);
            ctx.OMSetRenderTargets(_ssrTempRtv, null);
            ctx.PSSetShader(_psSsrBlurH);
            ctx.PSSetShaderResource(0, _ssrSrv!);
            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, null!);
            ctx.OMSetRenderTargets(_ssrRtv, null);
            ctx.PSSetShader(_psSsrBlurV);
            ctx.PSSetShaderResource(0, _ssrTempSrv!);
            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, null!);
            ctx.PSSetShaderResource(1, null!);
            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        }

        // 4. Half-res God Rays Mask + Radial Blur
        if (wantGodRays && _godRayTempRtv != null && _godRayRtv != null)
        {
            ctx.RSSetViewport(new Viewport(0, 0, halfW, halfH, 0f, 1f));

            ctx.OMSetRenderTargets(_godRayTempRtv, null);
            ctx.PSSetShader(_psGodRayMask);
            ctx.PSSetShaderResource(0, hdrSceneSrv);
            ctx.PSSetShaderResource(1, depthSrv);
            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, null!);
            ctx.OMSetRenderTargets(_godRayRtv, null);
            ctx.PSSetShader(_psGodRayBlur);
            ctx.PSSetShaderResource(0, _godRayTempSrv!);
            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, null!);
            ctx.PSSetShaderResource(1, null!);
            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        }

        // 5. Full-res composite into _hdrPostTex
        ctx.RSSetViewport(new Viewport(0, 0, renderWidth, renderHeight, 0f, 1f));
        ctx.OMSetRenderTargets(_hdrPostRtv, null);
        ctx.PSSetShader(_psApplyAoGi);
        ctx.PSSetShaderResource(0, hdrSceneSrv);
        ctx.PSSetShaderResource(1, depthSrv);
        ctx.PSSetShaderResource(2, wantAo && _aoFinalSrv != null ? _aoFinalSrv : hdrSceneSrv);
        ctx.PSSetShaderResource(3, wantGi && _giFinalSrv != null ? _giFinalSrv : hdrSceneSrv);
        ctx.PSSetShaderResource(6, wantSsr && _ssrSrv != null ? _ssrSrv : hdrSceneSrv);
        ctx.PSSetShaderResource(7, wantGodRays && _godRaySrv != null ? _godRaySrv : hdrSceneSrv);
        ctx.Draw(4, 0);

        UnbindSrvs(ctx);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);

        return (_hdrPostTex, _hdrPostRtv, _hdrPostSrv);
    }

    public void ApplyBloomAndToneMap(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11ShaderResourceView hdrInputSrv,
        ID3D11RenderTargetView outputRtv,
        ID3D11InputLayout quadInputLayout,
        ID3D11Buffer quadVb,
        int vertexStride,
        ID3D11SamplerState samplerLinear,
        ID3D11SamplerState samplerPoint,
        ID3D11BlendState blendOpaqueCopy,
        ID3D11DepthStencilState depthDisabled,
        ID3D11RasterizerState rasterizer,
        int width,
        int height,
        bool isHdrOutput)
    {
        if (!_initialized) return;

        var settings = D3D11RendererSettings.Default;
        bool wantBloom = settings.EnableBloom && settings.BloomIntensity > 0.001;
        float exposureMult = MathF.Pow(2f, (float)settings.Exposure);
        float threshold = (float)settings.BloomThreshold;

        if (wantBloom)
        {
            EnsureBloomPyramid(device, width, height);
        }
        else
        {
            DisposeBloomPyramid();
        }

        ctx.IASetInputLayout(quadInputLayout);
        ctx.IASetVertexBuffer(0, quadVb, vertexStride, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.RSSetState(rasterizer);
        ctx.OMSetDepthStencilState(depthDisabled, 0);
        ctx.OMSetBlendState(blendOpaqueCopy, null, unchecked((int)0xFFFFFFFF));
        ctx.VSSetShader(_vsPost);
        ctx.PSSetSampler(0, samplerLinear);
        ctx.PSSetSampler(1, samplerPoint);

        // 1. Multi-Mip Bloom Downsample & Additive Tent Upsample
        bool hasBloom = wantBloom && _bloomRtv[0] != null && _bloomSrv[0] != null;
        if (hasBloom)
        {
            // Mip 0 prefilter
            ctx.RSSetViewport(new Viewport(0, 0, _bloomW[0], _bloomH[0], 0f, 1f));
            ctx.OMSetRenderTargets(_bloomRtv[0]!, null);
            var preCb = new CbPost
            {
                RcpSrcWidth = 1f / Math.Max(1, width),
                RcpSrcHeight = 1f / Math.Max(1, height),
                BloomThreshold = threshold,
                BloomKnee = MathF.Max(threshold * 0.25f, 0.10f),
                ExposureMult = exposureMult,
            };
            ctx.UpdateSubresource(in preCb, _cbPost!);
            ctx.PSSetConstantBuffer(0, _cbPost);
            ctx.PSSetShader(_psBloomPrefilter);
            ctx.PSSetShaderResource(0, hdrInputSrv);
            ctx.Draw(4, 0);
            UnbindSrvs(ctx);

            // Downsample 1..4
            ctx.PSSetShader(_psBloomDownsample);
            for (int m = 1; m < BloomMipCount; m++)
            {
                ctx.RSSetViewport(new Viewport(0, 0, _bloomW[m], _bloomH[m], 0f, 1f));
                ctx.OMSetRenderTargets(_bloomRtv[m]!, null);
                var downCb = new CbPost
                {
                    RcpSrcWidth = 1f / Math.Max(1, _bloomW[m - 1]),
                    RcpSrcHeight = 1f / Math.Max(1, _bloomH[m - 1]),
                };
                ctx.UpdateSubresource(in downCb, _cbPost!);
                ctx.PSSetConstantBuffer(0, _cbPost);
                ctx.PSSetShaderResource(0, _bloomSrv[m - 1]!);
                ctx.Draw(4, 0);
                UnbindSrvs(ctx);
            }

            // Upsample 4->3->2->1->0 (additive)
            ctx.OMSetBlendState(_blendAdditive, null, unchecked((int)0xFFFFFFFF));
            ctx.PSSetShader(_psBloomUpsample);
            for (int m = BloomMipCount - 1; m >= 1; m--)
            {
                ctx.RSSetViewport(new Viewport(0, 0, _bloomW[m - 1], _bloomH[m - 1], 0f, 1f));
                ctx.OMSetRenderTargets(_bloomRtv[m - 1]!, null);
                var upCb = new CbPost
                {
                    RcpSrcWidth = 1f / Math.Max(1, _bloomW[m]),
                    RcpSrcHeight = 1f / Math.Max(1, _bloomH[m]),
                };
                ctx.UpdateSubresource(in upCb, _cbPost!);
                ctx.PSSetConstantBuffer(0, _cbPost);
                ctx.PSSetShaderResource(0, _bloomSrv[m]!);
                ctx.Draw(4, 0);
                UnbindSrvs(ctx);
            }

            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
            ctx.OMSetBlendState(blendOpaqueCopy, null, unchecked((int)0xFFFFFFFF));
        }

        // 2. Final Tone Mapping + Bloom Composite + sRGB/HDR Output
        ctx.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));
        ctx.OMSetRenderTargets(outputRtv, null);
        var finalCb = new CbPost
        {
            RcpSrcWidth = 1f / Math.Max(1, width),
            RcpSrcHeight = 1f / Math.Max(1, height),
            BloomIntensity = hasBloom ? (float)settings.BloomIntensity * 0.25f : 0f,
            ExposureMult = exposureMult,
            ToneMode = (float)(int)settings.ToneMapping,
            PeakLuminance = (float)settings.HdrPeakLuminance,
            IsHdrOutput = isHdrOutput ? 1f : 0f,
            DitherScale = isHdrOutput ? 0f : 1f,
        };
        ctx.UpdateSubresource(in finalCb, _cbPost!);
        ctx.PSSetConstantBuffer(0, _cbPost);
        ctx.PSSetShader(_psToneMapComposite);
        ctx.PSSetShaderResource(0, hdrInputSrv);
        ctx.PSSetShaderResource(1, hasBloom ? _bloomSrv[0]! : hdrInputSrv);
        ctx.Draw(4, 0);

        UnbindSrvs(ctx);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
    }

    private static void UnbindSrvs(ID3D11DeviceContext ctx)
    {
        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(1, null!);
        ctx.PSSetShaderResource(2, null!);
        ctx.PSSetShaderResource(3, null!);
        ctx.PSSetShaderResource(4, null!);
        ctx.PSSetShaderResource(5, null!);
        ctx.PSSetShaderResource(6, null!);
        ctx.PSSetShaderResource(7, null!);
    }

    private void EnsureSsrTargets(ID3D11Device device, int halfW, int halfH)
    {
        if (_ssrTex != null && _ssrHalfW == halfW && _ssrHalfH == halfH) return;
        DisposeSsrTargets();
        _ssrHalfW = halfW;
        _ssrHalfH = halfH;

        CreateRt2D(device, halfW, halfH, Format.R16G16B16A16_Float, out _ssrTex, out _ssrRtv, out _ssrSrv);
        CreateRt2D(device, halfW, halfH, Format.R16G16B16A16_Float, out _ssrTempTex, out _ssrTempRtv, out _ssrTempSrv);
    }

    private void DisposeSsrTargets()
    {
        _ssrRtv?.Dispose(); _ssrRtv = null;
        _ssrSrv?.Dispose(); _ssrSrv = null;
        _ssrTex?.Dispose(); _ssrTex = null;
        _ssrTempRtv?.Dispose(); _ssrTempRtv = null;
        _ssrTempSrv?.Dispose(); _ssrTempSrv = null;
        _ssrTempTex?.Dispose(); _ssrTempTex = null;
        _ssrHalfW = 0;
        _ssrHalfH = 0;
    }

    private void EnsureGodRayTargets(ID3D11Device device, int halfW, int halfH)
    {
        if (_godRayTex != null && _grHalfW == halfW && _grHalfH == halfH) return;
        DisposeGodRayTargets();
        _grHalfW = halfW;
        _grHalfH = halfH;

        CreateRt2D(device, halfW, halfH, Format.R16G16B16A16_Float, out _godRayTex, out _godRayRtv, out _godRaySrv);
        CreateRt2D(device, halfW, halfH, Format.R16G16B16A16_Float, out _godRayTempTex, out _godRayTempRtv, out _godRayTempSrv);
    }

    private void DisposeGodRayTargets()
    {
        _godRayRtv?.Dispose(); _godRayRtv = null;
        _godRaySrv?.Dispose(); _godRaySrv = null;
        _godRayTex?.Dispose(); _godRayTex = null;
        _godRayTempRtv?.Dispose(); _godRayTempRtv = null;
        _godRayTempSrv?.Dispose(); _godRayTempSrv = null;
        _godRayTempTex?.Dispose(); _godRayTempTex = null;
        _grHalfW = 0;
        _grHalfH = 0;
    }

    private void EnsureAoTargets(ID3D11Device device, int halfW, int halfH)
    {
        if (_aoRawTex != null && _halfW == halfW && _halfH == halfH) return;
        DisposeAoTargets();
        _halfW = halfW;
        _halfH = halfH;

        CreateRt2D(device, halfW, halfH, Format.R8_UNorm, out _aoRawTex, out _aoRawRtv, out _aoRawSrv);
        CreateRt2D(device, halfW, halfH, Format.R8_UNorm, out _aoTempTex, out _aoTempRtv, out _aoTempSrv);
        CreateRt2D(device, halfW, halfH, Format.R8_UNorm, out _aoFinalTex, out _aoFinalRtv, out _aoFinalSrv);
    }

    private void DisposeAoTargets()
    {
        _aoRawRtv?.Dispose(); _aoRawRtv = null;
        _aoRawSrv?.Dispose(); _aoRawSrv = null;
        _aoRawTex?.Dispose(); _aoRawTex = null;
        _aoTempRtv?.Dispose(); _aoTempRtv = null;
        _aoTempSrv?.Dispose(); _aoTempSrv = null;
        _aoTempTex?.Dispose(); _aoTempTex = null;
        _aoFinalRtv?.Dispose(); _aoFinalRtv = null;
        _aoFinalSrv?.Dispose(); _aoFinalSrv = null;
        _aoFinalTex?.Dispose(); _aoFinalTex = null;
    }

    private void EnsureGiTargets(ID3D11Device device, int halfW, int halfH)
    {
        if (_giRawTex != null && _halfW == halfW && _halfH == halfH) return;
        DisposeGiTargets();
        _halfW = halfW;
        _halfH = halfH;

        CreateRt2D(device, halfW, halfH, Format.R16G16B16A16_Float, out _giRawTex, out _giRawRtv, out _giRawSrv);
        CreateRt2D(device, halfW, halfH, Format.R16G16B16A16_Float, out _giTempTex, out _giTempRtv, out _giTempSrv);
        CreateRt2D(device, halfW, halfH, Format.R16G16B16A16_Float, out _giFinalTex, out _giFinalRtv, out _giFinalSrv);
    }

    private void DisposeGiTargets()
    {
        _giRawRtv?.Dispose(); _giRawRtv = null;
        _giRawSrv?.Dispose(); _giRawSrv = null;
        _giRawTex?.Dispose(); _giRawTex = null;
        _giTempRtv?.Dispose(); _giTempRtv = null;
        _giTempSrv?.Dispose(); _giTempSrv = null;
        _giTempTex?.Dispose(); _giTempTex = null;
        _giFinalRtv?.Dispose(); _giFinalRtv = null;
        _giFinalSrv?.Dispose(); _giFinalSrv = null;
        _giFinalTex?.Dispose(); _giFinalTex = null;
    }

    private void EnsureHdrPostTarget(ID3D11Device device, int width, int height)
    {
        if (_hdrPostTex != null && _fullW == width && _fullH == height) return;
        DisposeHdrPostTarget();
        _fullW = width;
        _fullH = height;

        CreateRt2D(device, width, height, Format.R16G16B16A16_Float, out _hdrPostTex, out _hdrPostRtv, out _hdrPostSrv);
    }

    private void DisposeHdrPostTarget()
    {
        _hdrPostRtv?.Dispose(); _hdrPostRtv = null;
        _hdrPostSrv?.Dispose(); _hdrPostSrv = null;
        _hdrPostTex?.Dispose(); _hdrPostTex = null;
        _fullW = 0;
        _fullH = 0;
    }

    private void EnsureBloomPyramid(ID3D11Device device, int width, int height)
    {
        if (_bloomTex[0] != null && _bloomBaseW == width && _bloomBaseH == height) return;
        DisposeBloomPyramid();
        _bloomBaseW = width;
        _bloomBaseH = height;

        int w = Math.Max(1, width / 2);
        int h = Math.Max(1, height / 2);
        for (int i = 0; i < BloomMipCount; i++)
        {
            _bloomW[i] = w;
            _bloomH[i] = h;
            CreateRt2D(device, w, h, Format.R16G16B16A16_Float, out _bloomTex[i], out _bloomRtv[i], out _bloomSrv[i]);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
    }

    private void DisposeBloomPyramid()
    {
        for (int i = 0; i < BloomMipCount; i++)
        {
            _bloomRtv[i]?.Dispose(); _bloomRtv[i] = null;
            _bloomSrv[i]?.Dispose(); _bloomSrv[i] = null;
            _bloomTex[i]?.Dispose(); _bloomTex[i] = null;
            _bloomW[i] = 0;
            _bloomH[i] = 0;
        }
        _bloomBaseW = 0;
        _bloomBaseH = 0;
    }

    private static void CreateRt2D(
        ID3D11Device device,
        int width,
        int height,
        Format format,
        out ID3D11Texture2D tex,
        out ID3D11RenderTargetView rtv,
        out ID3D11ShaderResourceView srv)
    {
        tex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        rtv = device.CreateRenderTargetView(tex);
        srv = device.CreateShaderResourceView(tex);
    }

    public void Release()
    {
        DisposeAoTargets();
        DisposeGiTargets();
        DisposeSsrTargets();
        DisposeGodRayTargets();
        DisposeHdrPostTarget();
        DisposeBloomPyramid();
        _halfW = 0;
        _halfH = 0;

        _vsPost?.Dispose(); _vsPost = null;
        _psGtao?.Dispose(); _psGtao = null;
        _psAoBlurH?.Dispose(); _psAoBlurH = null;
        _psAoBlurV?.Dispose(); _psAoBlurV = null;
        _psSsgi?.Dispose(); _psSsgi = null;
        _psGiBlurH?.Dispose(); _psGiBlurH = null;
        _psGiBlurV?.Dispose(); _psGiBlurV = null;
        _psSsr?.Dispose(); _psSsr = null;
        _psSsrBlurH?.Dispose(); _psSsrBlurH = null;
        _psSsrBlurV?.Dispose(); _psSsrBlurV = null;
        _psGodRayMask?.Dispose(); _psGodRayMask = null;
        _psGodRayBlur?.Dispose(); _psGodRayBlur = null;
        _psApplyAoGi?.Dispose(); _psApplyAoGi = null;
        _psBloomPrefilter?.Dispose(); _psBloomPrefilter = null;
        _psBloomDownsample?.Dispose(); _psBloomDownsample = null;
        _psBloomUpsample?.Dispose(); _psBloomUpsample = null;
        _psToneMapComposite?.Dispose(); _psToneMapComposite = null;
        _cbPost?.Dispose(); _cbPost = null;
        _blendAdditive?.Dispose(); _blendAdditive = null;

        _devicePointer = IntPtr.Zero;
        _initialized = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}
