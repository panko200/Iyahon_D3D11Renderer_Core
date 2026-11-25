using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

/// <summary>
/// 3Dノイズ図形 (Noise3D) の専用レンダラー。
/// YMM4 公式の全8種ノイズに対応し、Surface3D / IsoSurface3D / Volume3D (2パスメッシュ境界レイマーチング) を描画する。
/// </summary>
internal sealed class Noise3DVolumeRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CbNoise3D
    {
        public Matrix4x4 WorldMatrix;    // 64
        public Matrix4x4 ViewProjMatrix; // 128
        public Matrix4x4 NormalMatrix;   // 192
        public Matrix4x4 InvWorldMatrix; // 256
        public Vector4 BoundsMin;        // 272
        public Vector4 BoundsMax;        // 288
        public Vector4 CameraLocalPos;   // 304
        public Vector4 ScaleEvolution;   // 320 : ScaleX, ScaleY, ScaleZ, Evolution
        public Vector4 OffsetSeed;       // 336 : OffsetX, OffsetY, OffsetZ, Seed
        public Vector4 NoiseParams0;     // 352 : NoiseType, Complexity, Threshold, Softness
        public Vector4 NoiseParams1;     // 368 : Density, BumpStrength, StepCount, Invert
        public Vector4 ColorA;           // 384 : linear rgb + pad
        public Vector4 ColorB;           // 400 : linear rgb + EmissionStrength
        public Vector4 ScreenAndMode;    // 416 : HalfWidth, HalfHeight, Opacity, RenderMode
    }

    private const int ObjVertexStride = 48;

    private IntPtr _devicePointer;
    private bool _initialized;
    private bool _disposed;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psBackPos;
    private ID3D11PixelShader? _psSurface;
    private ID3D11PixelShader? _psVolume;
    private ID3D11PixelShader? _psIso;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _cbBuffer;

    private ID3D11RasterizerState? _rsCullBackCcw;
    private ID3D11RasterizerState? _rsCullBackCw;
    private ID3D11RasterizerState? _rsCullFrontCcw;
    private ID3D11RasterizerState? _rsCullFrontCw;

    private ID3D11BlendState? _blendOpaque;
    private ID3D11BlendState? _blendPremul;
    private ID3D11DepthStencilState? _dsOpaque;
    private ID3D11DepthStencilState? _dsReadOnly;
    private ID3D11DepthStencilState? _dsDisabled;

    private ID3D11Texture2D? _backPosTex;
    private ID3D11RenderTargetView? _backPosRtv;
    private ID3D11ShaderResourceView? _backPosSrv;
    private int _backTexW;
    private int _backTexH;

    // Pure ASCII HLSL: 8 genuine 3D noise functions + Surface3D / IsoSurface3D / Volume3D
    private const string HlslSource = @"
cbuffer CbNoise3D : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ViewProjMatrix;
    row_major float4x4 NormalMatrix;
    row_major float4x4 InvWorldMatrix;
    float4 BoundsMin;
    float4 BoundsMax;
    float4 CameraLocalPos;
    float4 ScaleEvolution;
    float4 OffsetSeed;
    float4 NoiseParams0;
    float4 NoiseParams1;
    float4 ColorA;
    float4 ColorB;
    float4 ScreenAndMode;
};

Texture2D gBackPos : register(t0);

struct VSInput
{
    float3 Pos    : POSITION;
    float3 Normal : NORMAL;
    float2 UV     : TEXCOORD0;
    float4 Color  : COLOR;
};

struct PSInput
{
    float4 Pos        : SV_POSITION;
    float3 LocalPos   : TEXCOORD0;
    float3 WorldPos   : TEXCOORD1;
    float3 WorldNormal: TEXCOORD2;
};

struct IsoPSOut
{
    float4 Color : SV_Target;
    float  Depth : SV_Depth;
};

float4 ProjectWorldToClip(float4 worldPos)
{
    float hw = ScreenAndMode.x;
    float hh = ScreenAndMode.y;
    if (hw > 0.0)
    {
        float4 pos = mul(worldPos, ViewProjMatrix);
        float4 o;
        o.x =  pos.x / hw;
        o.y = -pos.y / hh;
        o.z = -pos.z / 200000.0 + 0.5 * pos.w;
        o.w =  pos.w;
        return o;
    }
    return mul(worldPos, ViewProjMatrix);
}

PSInput VS_Noise3D(VSInput input)
{
    PSInput o;
    float4 worldPos = mul(float4(input.Pos, 1.0), WorldMatrix);
    o.Pos = ProjectWorldToClip(worldPos);
    o.LocalPos = input.Pos;
    o.WorldPos = worldPos.xyz;
    float3 wn = mul(input.Normal, (float3x3)NormalMatrix);
    float lenSq = dot(wn, wn);
    o.WorldNormal = lenSq > 1e-8 ? wn * rsqrt(lenSq) : float3(0.0, 1.0, 0.0);
    return o;
}

float4 PS_NoiseBackPos(PSInput input) : SV_Target
{
    return float4(input.LocalPos, 1.0);
}

// ---- Deterministic 3D Hash Helpers ----
float Hash31(float3 p, float seed)
{
    p = frac(p * 0.1031 + seed * 0.0317);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

float3 Hash33(float3 p, float seed)
{
    p = float3(
        dot(p, float3(127.1, 311.7,  74.7)),
        dot(p, float3(269.5, 183.3, 246.1)),
        dot(p, float3(113.5, 271.9, 124.6)));
    return frac(sin(p + seed * 1.37) * 43758.5453123);
}

float3 Grad3(float3 p, float seed)
{
    float3 r = Hash33(p, seed) * 2.0 - 1.0;
    float ls = dot(r, r);
    return ls > 1e-5 ? r * rsqrt(ls) : float3(1.0, 0.0, 0.0);
}

float3 Quintic(float3 t)
{
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

// Type 0: Value / Fractal smooth 3D noise
float ValueNoise3D(float3 p, float seed)
{
    float3 i = floor(p);
    float3 f = Quintic(frac(p));
    float n000 = Hash31(i + float3(0,0,0), seed);
    float n100 = Hash31(i + float3(1,0,0), seed);
    float n010 = Hash31(i + float3(0,1,0), seed);
    float n110 = Hash31(i + float3(1,1,0), seed);
    float n001 = Hash31(i + float3(0,0,1), seed);
    float n101 = Hash31(i + float3(1,0,1), seed);
    float n011 = Hash31(i + float3(0,1,1), seed);
    float n111 = Hash31(i + float3(1,1,1), seed);
    float nx00 = lerp(n000, n100, f.x);
    float nx10 = lerp(n010, n110, f.x);
    float nx01 = lerp(n001, n101, f.x);
    float nx11 = lerp(n011, n111, f.x);
    return lerp(lerp(nx00, nx10, f.y), lerp(nx01, nx11, f.y), f.z);
}

// Type 100: Block lattice
float BlockNoise3D(float3 p, float seed)
{
    return Hash31(floor(p), seed);
}

// Type 101: Perlin 3D Gradient
float PerlinNoise3D(float3 p, float seed)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = Quintic(f);

    float n000 = dot(Grad3(i + float3(0,0,0), seed), f - float3(0,0,0));
    float n100 = dot(Grad3(i + float3(1,0,0), seed), f - float3(1,0,0));
    float n010 = dot(Grad3(i + float3(0,1,0), seed), f - float3(0,1,0));
    float n110 = dot(Grad3(i + float3(1,1,0), seed), f - float3(1,1,0));
    float n001 = dot(Grad3(i + float3(0,0,1), seed), f - float3(0,0,1));
    float n101 = dot(Grad3(i + float3(1,0,1), seed), f - float3(1,0,1));
    float n011 = dot(Grad3(i + float3(0,1,1), seed), f - float3(0,1,1));
    float n111 = dot(Grad3(i + float3(1,1,1), seed), f - float3(1,1,1));

    float nx00 = lerp(n000, n100, u.x);
    float nx10 = lerp(n010, n110, u.x);
    float nx01 = lerp(n001, n101, u.x);
    float nx11 = lerp(n011, n111, u.x);
    float v = lerp(lerp(nx00, nx10, u.y), lerp(nx01, nx11, u.y), u.z);
    return saturate(v * 0.65 + 0.5);
}

// Type 102 & 103: Cellular F1 & Voronoi F2-F1 (single [loop] to avoid 27x AST unroll explosion in fxc)
void Worley3D(float3 p, float seed, out float f1Out, out float f2Out)
{
    float3 n = floor(p);
    float3 f = frac(p);
    float d1 = 8.0;
    float d2 = 8.0;
    [loop]
    for (int k = 0; k < 27; k++)
    {
        int z = (k / 9) - 1;
        int y = ((k / 3) % 3) - 1;
        int x = (k % 3) - 1;
        float3 g = float3((float)x, (float)y, (float)z);
        float3 o = Hash33(n + g, seed);
        float3 r = g + o - f;
        float d = dot(r, r);
        if (d < d1) { d2 = d1; d1 = d; }
        else if (d < d2) { d2 = d; }
    }
    f1Out = sqrt(d1);
    f2Out = sqrt(d2);
}

// Type 104: 3D Simplex Noise
float SimplexNoise3D(float3 v, float seed)
{
    const float F3 = 1.0 / 3.0;
    const float G3 = 1.0 / 6.0;
    float s = dot(v, float3(F3, F3, F3));
    float3 i = floor(v + s);
    float t = dot(i, float3(G3, G3, G3));
    float3 x0 = v - (i - t);

    float3 g = step(x0.yzx, x0.xyz);
    float3 l = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);

    float3 x1 = x0 - i1 + G3;
    float3 x2 = x0 - i2 + 2.0 * G3;
    float3 x3 = x0 - 1.0 + 3.0 * G3;

    float4 w;
    w.x = dot(x0, x0);
    w.y = dot(x1, x1);
    w.z = dot(x2, x2);
    w.w = dot(x3, x3);
    w = max(0.6 - w, 0.0);
    w = w * w * w * w;

    float4 n;
    n.x = dot(Grad3(i, seed), x0);
    n.y = dot(Grad3(i + i1, seed), x1);
    n.z = dot(Grad3(i + i2, seed), x2);
    n.w = dot(Grad3(i + 1.0, seed), x3);

    float val = dot(w, n) * 32.0;
    return saturate(val * 0.5 + 0.5);
}

// Type 105: Fast 3D Curl Noise from analytical potential
float CurlNoise3D(float3 p, float seed)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = Quintic(f);
    float3 du = 30.0 * f * f * (f * (f - 2.0) + 1.0);

    float3 g000 = Hash33(i + float3(0,0,0), seed) * 2.0 - 1.0;
    float3 g100 = Hash33(i + float3(1,0,0), seed) * 2.0 - 1.0;
    float3 g010 = Hash33(i + float3(0,1,0), seed) * 2.0 - 1.0;
    float3 g110 = Hash33(i + float3(1,1,0), seed) * 2.0 - 1.0;
    float3 g001 = Hash33(i + float3(0,0,1), seed) * 2.0 - 1.0;
    float3 g101 = Hash33(i + float3(1,0,1), seed) * 2.0 - 1.0;
    float3 g011 = Hash33(i + float3(0,1,1), seed) * 2.0 - 1.0;
    float3 g111 = Hash33(i + float3(1,1,1), seed) * 2.0 - 1.0;

    float3 k1 = g100 - g000;
    float3 k2 = g010 - g000;
    float3 k3 = g001 - g000;
    float3 k4 = g000 - g100 - g010 + g110;
    float3 k5 = g000 - g010 - g001 + g011;
    float3 k6 = g000 - g100 - g001 + g101;
    float3 k7 = -g000 + g100 + g010 - g110 + g001 - g101 - g011 + g111;

    float3 dPotDx = (k1 + k4 * u.y + k6 * u.z + k7 * u.y * u.z) * du.x;
    float3 dPotDy = (k2 + k4 * u.x + k5 * u.z + k7 * u.x * u.z) * du.y;
    float3 dPotDz = (k3 + k5 * u.y + k6 * u.x + k7 * u.x * u.y) * du.z;

    float3 curlVec = float3(
        dPotDy.z - dPotDz.y,
        dPotDz.x - dPotDx.z,
        dPotDx.y - dPotDy.x);
    return saturate(length(curlVec) * 0.38);
}

float EvalBaseOctave(float3 p, float nType, float seed)
{
    if (nType > 104.5) // 105: Curl
        return CurlNoise3D(p, seed);
    if (nType > 103.5) // 104: Simplex
        return SimplexNoise3D(p, seed);
    if (nType > 101.5) // 102: Cellular, 103: Voronoi
    {
        float f1, f2;
        Worley3D(p, seed, f1, f2);
        return (nType > 102.5)
            ? saturate((f2 - f1) * 1.4)
            : saturate(1.0 - f1 * 0.95);
    }
    if (nType > 100.5) // 101: Perlin
        return PerlinNoise3D(p, seed);
    if (nType > 99.5)  // 100: Block
        return BlockNoise3D(p, seed);
    if (nType > 2.5)   // 3: Random grain
        return BlockNoise3D(p * 4.0, seed);
    return ValueNoise3D(p, seed); // 0: Fractal
}

float EvalNoise3D(float3 localPos)
{
    float nType = NoiseParams0.x;
    float complexity = clamp(NoiseParams0.y, 1.0, 8.0);
    float evo = ScaleEvolution.w;
    float seed = OffsetSeed.w;
    float3 q = localPos * ScaleEvolution.xyz + OffsetSeed.xyz + float3(evo * 0.37, evo * 0.53, evo * 0.71);

    float sum = 0.0;
    float norm = 0.0;
    float amp = 1.0;
    float freq = 1.0;
    int maxOct = (int)ceil(complexity);

    [loop]
    for (int o = 0; o < 8; o++)
    {
        if (o >= maxOct) break;
        float w = (o == maxOct - 1) ? frac(complexity - 1e-4) : 1.0;
        if (w <= 0.0) w = 1.0;
        float sampleVal = EvalBaseOctave(q * freq + float3(evo * 0.13 * (o + 1), 0.0, 0.0), nType, seed + o * 13.37);
        sum += sampleVal * amp * w;
        norm += amp * w;
        amp *= 0.5;
        freq *= 2.02;
    }

    float raw = norm > 1e-5 ? (sum / norm) : 0.5;
    // Restore multi-octave fBM dynamic range so [0, 1] threshold spans the full volume cleanly
    float contrast = 1.0 + 0.22 * (complexity - 1.0);
    float val = saturate((raw - 0.5) * contrast + 0.5);
    if (NoiseParams1.w > 0.5)
        val = 1.0 - val;
    return val;
}

// Single-[loop] forward gradient: keeps only 1 inlined copy of EvalNoise3D in the AST
float3 EvalNoiseGradFromCenter(float3 localPos, float vCenter, float eps)
{
    float3 g = float3(0.0, 0.0, 0.0);
    [loop]
    for (int a = 0; a < 3; a++)
    {
        float3 d = (a == 0) ? float3(eps, 0.0, 0.0) : ((a == 1) ? float3(0.0, eps, 0.0) : float3(0.0, 0.0, eps));
        float vp = EvalNoise3D(localPos + d) - vCenter;
        if (a == 0) g.x = vp;
        else if (a == 1) g.y = vp;
        else g.z = vp;
    }
    return g / eps;
}

float3 ShadeNoiseSurface(float3 nWorld, float3 worldPos, float3 baseCol, float emissionStrength)
{
    float3 keyDir = normalize(float3(0.45, -0.75, 0.48));
    float3 viewDir = normalize(float3(0.0, 0.0, 1000.0) - worldPos);
    float ndl = saturate(dot(nWorld, keyDir));
    float hemi = nWorld.y * -0.5 + 0.5;
    float3 amb = lerp(float3(0.16, 0.19, 0.24), float3(0.48, 0.54, 0.64), hemi);
    float3 h = normalize(keyDir + viewDir);
    float spec = pow(saturate(dot(nWorld, h)), 28.0) * 0.22;
    float rim = pow(1.0 - saturate(dot(nWorld, viewDir)), 3.0) * 0.16;
    return baseCol * (amb + ndl * 0.92) + spec + rim + baseCol * emissionStrength;
}

float4 PS_Noise3D_Surface(PSInput input) : SV_Target
{
    float v = EvalNoise3D(input.LocalPos);
    float thresh = NoiseParams0.z;
    float soft = max(NoiseParams0.w, 0.005);
    float band = smoothstep(thresh - soft * 0.5, thresh + soft * 0.5, v);
    float colAlpha = saturate(lerp(ColorA.a, ColorB.a, band));
    float alpha = saturate(ScreenAndMode.z) * colAlpha;
    if (alpha < 0.002)
        discard;

    float wA = ColorA.a * (1.0 - band);
    float wB = ColorB.a * band;
    float3 col = (wA + wB > 1e-5)
        ? ((ColorA.rgb * wA + ColorB.rgb * wB) / (wA + wB))
        : lerp(ColorA.rgb, ColorB.rgb, band);

    float3 n = normalize(input.WorldNormal);
    float3 grad = EvalNoiseGradFromCenter(input.LocalPos, v, 0.012) * NoiseParams1.y;
    float3 worldGrad = mul(grad, (float3x3)NormalMatrix);
    float3 perturbed = normalize(n - worldGrad);
    float3 lit = ShadeNoiseSurface(perturbed, input.WorldPos, col, BoundsMin.w);
    return float4(lit * alpha, alpha);
}

void GetRaySegment(PSInput input, out float3 rayStart, out float3 rayEnd)
{
    if (CameraLocalPos.w > 0.5)
    {
        rayStart = CameraLocalPos.xyz;
        rayEnd = input.LocalPos;
    }
    else
    {
        float4 backData = gBackPos.Load(int3((int2)input.Pos.xy, 0));
        if (backData.w < 0.5)
            discard;
        rayStart = input.LocalPos;
        rayEnd = backData.xyz;
    }
}

float4 PS_Noise3D_Volume(PSInput input) : SV_Target
{
    float3 rayStart, rayEnd;
    GetRaySegment(input, rayStart, rayEnd);

    float3 ray = rayEnd - rayStart;
    float rayLen = length(ray);
    if (rayLen < 1e-4)
        discard;

    int steps = clamp((int)NoiseParams1.z, 16, 128);
    float dt = rayLen / (float)steps;
    float jitter = frac(sin(dot(input.Pos.xy, float2(12.9898, 78.233))) * 43758.5453);

    float thresh = NoiseParams0.z;
    float soft = max(NoiseParams0.w, 0.005);
    float densityMul = NoiseParams1.x;
    float opacity = saturate(ScreenAndMode.z);
    float3 sunDir = normalize(float3(0.45, -0.75, 0.48));
    float3 boxSpan = max(BoundsMax.xyz - BoundsMin.xyz, float3(1e-4, 1e-4, 1e-4));

    float3 accumRgb = float3(0.0, 0.0, 0.0);
    float accumA = 0.0;
    float vPrev = thresh;

    [loop]
    for (int i = 0; i < 128; i++)
    {
        if (i >= steps || accumA > 0.992) break;
        float t = ((float)i + jitter) / (float)steps;
        float3 p = rayStart + ray * t;
        float v = EvalNoise3D(p);
        float shaped = smoothstep(thresh - soft * 0.5, thresh + soft * 0.5, v);
        if (shaped > 0.001)
        {
            float colAlpha = saturate(lerp(ColorA.a, ColorB.a, shaped));
            float sigma = shaped * densityMul * colAlpha;
            if (sigma > 0.0005)
            {
                float a = (1.0 - exp(-sigma * dt * 2.8)) * colAlpha;
                float3 normPos = (p - BoundsMin.xyz) / boxSpan - 0.5;
                float sunGrad = saturate(dot(normPos, sunDir) * 0.8 + 0.65);
                float stepSlope = saturate((vPrev - v) * 1.8 + 0.5);
                float lightBias = 0.52 + 0.48 * (0.65 * sunGrad + 0.35 * stepSlope);
                float wA = ColorA.a * (1.0 - shaped);
                float wB = ColorB.a * shaped;
                float3 mixedCol = (wA + wB > 1e-5)
                    ? ((ColorA.rgb * wA + ColorB.rgb * wB) / (wA + wB))
                    : lerp(ColorA.rgb, ColorB.rgb, shaped);
                float3 sampleCol = mixedCol * lightBias + mixedCol * (BoundsMin.w * shaped);
                accumRgb += (1.0 - accumA) * sampleCol * a;
                accumA += (1.0 - accumA) * a;
            }
        }
        vPrev = v;
    }

    if (accumA < 0.002)
        discard;

    return float4(accumRgb * opacity, accumA * opacity);
}

IsoPSOut PS_Noise3D_Iso(PSInput input)
{
    float3 rayStart, rayEnd;
    GetRaySegment(input, rayStart, rayEnd);

    float3 ray = rayEnd - rayStart;
    float rayLen = length(ray);
    if (rayLen < 1e-4)
        discard;

    int steps = clamp((int)NoiseParams1.z, 16, 128);
    float thresh = NoiseParams0.z;
    float jitter = frac(sin(dot(input.Pos.xy, float2(12.9898, 78.233))) * 43758.5453) * 0.5;

    bool hit = false;
    float tPrev = 0.0;
    float vPrev = thresh;
    float3 hitLocal = rayStart;
    float hitVal = thresh;

    [loop]
    for (int i = 0; i <= 128; i++)
    {
        if (i > steps) break;
        float tCur = (i == 0) ? 0.0 : saturate(((float)i - jitter) / (float)steps);
        float3 p = rayStart + ray * tCur;
        float vCur = EvalNoise3D(p);
        if (vCur >= thresh)
        {
            hit = true;
            if (i == 0)
            {
                hitLocal = rayStart;
                hitVal = vCur;
            }
            else
            {
                float fracHit = saturate((thresh - vPrev) / max(vCur - vPrev, 1e-4));
                float tHit = lerp(tPrev, tCur, fracHit);
                hitLocal = rayStart + ray * tHit;
                hitVal = thresh;
            }
            break;
        }
        tPrev = tCur;
        vPrev = vCur;
    }

    if (!hit)
        discard;

    float3 grad = EvalNoiseGradFromCenter(hitLocal, hitVal, 0.01);
    float3 wn = mul(-grad, (float3x3)NormalMatrix);
    float3 nWorld = dot(wn, wn) > 1e-8 ? normalize(wn) : normalize(input.WorldNormal);

    float3 worldHit = mul(float4(hitLocal, 1.0), WorldMatrix).xyz;
    float factor = saturate((hitLocal.y - BoundsMin.y) / max(BoundsMax.y - BoundsMin.y, 1e-4));
    float colAlpha = saturate(lerp(ColorA.a, ColorB.a, factor));
    float op = saturate(ScreenAndMode.z) * colAlpha;
    if (op < 0.002)
        discard;

    float wA = ColorA.a * (1.0 - factor);
    float wB = ColorB.a * factor;
    float3 baseCol = (wA + wB > 1e-5)
        ? ((ColorA.rgb * wA + ColorB.rgb * wB) / (wA + wB))
        : lerp(ColorA.rgb, ColorB.rgb, factor);
    float3 lit = ShadeNoiseSurface(nWorld, worldHit, baseCol, BoundsMin.w);

    float4 clipPos = ProjectWorldToClip(float4(worldHit, 1.0));
    float ndcDepth = clamp(clipPos.z / max(clipPos.w, 1e-4), 0.0001, 0.99999);

    IsoPSOut res;
    res.Color = float4(lit * op, op);
    res.Depth = ndcDepth;
    return res;
}
";

    public void Initialize(ID3D11Device device, ID3D11DeviceContext ctx)
    {
        if (_disposed) return;
        if (_initialized && _devicePointer == device.NativePointer) return;
        ReleaseResources();

        try
        {
            const Vortice.D3DCompiler.ShaderFlags compileFlags = Vortice.D3DCompiler.ShaderFlags.OptimizationLevel1;
            var vsBytes = ShaderBytecodeCache.GetOrCompile("Noise3D_VS_v3", HlslSource, "VS_Noise3D", "vs_5_0", "noise3d_shader", compileFlags);
            var psBackBytes = ShaderBytecodeCache.GetOrCompile("Noise3D_PS_Back_v3", HlslSource, "PS_NoiseBackPos", "ps_5_0", "noise3d_shader", compileFlags);
            var psSurfBytes = ShaderBytecodeCache.GetOrCompile("Noise3D_PS_Surf_v3", HlslSource, "PS_Noise3D_Surface", "ps_5_0", "noise3d_shader", compileFlags);
            var psVolBytes = ShaderBytecodeCache.GetOrCompile("Noise3D_PS_Vol_v3", HlslSource, "PS_Noise3D_Volume", "ps_5_0", "noise3d_shader", compileFlags);
            var psIsoBytes = ShaderBytecodeCache.GetOrCompile("Noise3D_PS_Iso_v3", HlslSource, "PS_Noise3D_Iso", "ps_5_0", "noise3d_shader", compileFlags);
            if (vsBytes == null || psBackBytes == null || psSurfBytes == null || psVolBytes == null || psIsoBytes == null)
                return;

            _vs = device.CreateVertexShader(vsBytes);
            _psBackPos = device.CreatePixelShader(psBackBytes);
            _psSurface = device.CreatePixelShader(psSurfBytes);
            _psVolume = device.CreatePixelShader(psVolBytes);
            _psIso = device.CreatePixelShader(psIsoBytes);

            var elements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,     0, 0),
                new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float,    12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,       24, 0),
                new InputElementDescription("COLOR",    0, Format.R32G32B32A32_Float, 32, 0),
            };
            _inputLayout = device.CreateInputLayout(elements, vsBytes);
            _cbBuffer = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbNoise3D>(), BindFlags.ConstantBuffer));

            _rsCullBackCcw = device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.Back, FrontCounterClockwise = true, DepthClipEnable = false });
            _rsCullBackCw = device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.Back, FrontCounterClockwise = false, DepthClipEnable = false });
            _rsCullFrontCcw = device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.Front, FrontCounterClockwise = true, DepthClipEnable = false });
            _rsCullFrontCw = device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.Front, FrontCounterClockwise = false, DepthClipEnable = false });

            var blendOp = new BlendDescription();
            blendOp.RenderTarget[0] = new RenderTargetBlendDescription { IsBlendEnabled = false, RenderTargetWriteMask = ColorWriteEnable.All };
            _blendOpaque = device.CreateBlendState(blendOp);

            var blendPr = new BlendDescription();
            blendPr.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = Blend.One,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            };
            _blendPremul = device.CreateBlendState(blendPr);

            _dsOpaque = device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = true, DepthWriteMask = DepthWriteMask.All, DepthFunc = ComparisonFunction.LessEqual });
            _dsReadOnly = device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = true, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunction.LessEqual });
            _dsDisabled = device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunction.Always });

            _devicePointer = device.NativePointer;
            _initialized = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] Noise3DVolumeRenderer init error: {ex.Message}");
            ReleaseResources();
        }
    }

    private void EnsureBackPosTarget(ID3D11Device device, int w, int h)
    {
        if (_backPosTex != null && _backTexW == w && _backTexH == h)
            return;

        _backPosSrv?.Dispose(); _backPosSrv = null;
        _backPosRtv?.Dispose(); _backPosRtv = null;
        _backPosTex?.Dispose(); _backPosTex = null;

        _backTexW = Math.Max(1, w);
        _backTexH = Math.Max(1, h);
        _backPosTex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = _backTexW,
            Height = _backTexH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32G32B32A32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _backPosRtv = device.CreateRenderTargetView(_backPosTex);
        _backPosSrv = device.CreateShaderResourceView(_backPosTex);
    }

    private static float SrgbToLin(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public void Render(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11RenderTargetView targetRtv,
        ID3D11DepthStencilView? targetDsv,
        int targetWidth,
        int targetHeight,
        ObjModelData model,
        in Matrix4x4 worldMatrix,
        in Matrix4x4 normalMatrix,
        in Matrix4x4 viewProjMatrix,
        Vector3 cameraWorldPos,
        float halfProjWidth,
        float halfProjHeight,
        float opacity)
    {
        var np = model.Noise3D;
        if (!np.Enabled || !_initialized || _vs == null || _cbBuffer == null)
            return;
        if (device.NativePointer != _devicePointer)
            return;
        if (model.VertexBuffer == null || model.IndexBuffer == null || model.IndexCount <= 0)
            return;

        if (!Matrix4x4.Invert(worldMatrix, out var invWorld))
            invWorld = Matrix4x4.Identity;

        Vector3 bMin = Vector3.DistanceSquared(model.BoundsMax, model.BoundsMin) > 1e-6f ? model.BoundsMin : np.BoundsMin;
        Vector3 bMax = Vector3.DistanceSquared(model.BoundsMax, model.BoundsMin) > 1e-6f ? model.BoundsMax : np.BoundsMax;
        Vector3 camLocal = Vector3.Transform(cameraWorldPos, invWorld);
        const float eps = 0.02f;
        bool camInside =
            camLocal.X >= bMin.X - eps && camLocal.X <= bMax.X + eps &&
            camLocal.Y >= bMin.Y - eps && camLocal.Y <= bMax.Y + eps &&
            camLocal.Z >= bMin.Z - eps && camLocal.Z <= bMax.Z + eps;

        var cb = new CbNoise3D
        {
            WorldMatrix = worldMatrix,
            ViewProjMatrix = viewProjMatrix,
            NormalMatrix = normalMatrix,
            InvWorldMatrix = invWorld,
            BoundsMin = new Vector4(bMin, np.EmissionStrength),
            BoundsMax = new Vector4(bMax, 0f),
            CameraLocalPos = new Vector4(camLocal, camInside ? 1f : 0f),
            ScaleEvolution = new Vector4(np.Scale, np.Evolution),
            OffsetSeed = new Vector4(np.Offset, np.Seed),
            NoiseParams0 = new Vector4((float)(int)np.NoiseType, np.Complexity, np.Threshold, np.Softness),
            NoiseParams1 = new Vector4(np.Density, np.BumpStrength, np.StepCount, np.Invert ? 1f : 0f),
            ColorA = new Vector4(SrgbToLin(np.ColorA.X), SrgbToLin(np.ColorA.Y), SrgbToLin(np.ColorA.Z), np.ColorA.W),
            ColorB = new Vector4(SrgbToLin(np.ColorB.X), SrgbToLin(np.ColorB.Y), SrgbToLin(np.ColorB.Z), np.ColorB.W),
            ScreenAndMode = new Vector4(halfProjWidth, halfProjHeight, opacity, (float)(int)np.RenderMode),
        };

        bool flipWinding = worldMatrix.GetDeterminant() > 0f;
        var rsBack = flipWinding ? _rsCullBackCw : _rsCullBackCcw;
        var rsFront = flipWinding ? _rsCullFrontCw : _rsCullFrontCcw;
        bool hasColorAlpha = np.ColorA.W < 0.999f || np.ColorB.W < 0.999f || opacity < 0.999f;

        ctx.UpdateSubresource(in cb, _cbBuffer);
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, model.VertexBuffer, ObjVertexStride, 0);
        ctx.IASetIndexBuffer(model.IndexBuffer, Format.R32_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.VSSetConstantBuffer(0, _cbBuffer);
        ctx.PSSetConstantBuffer(0, _cbBuffer);

        if (np.RenderMode == D3DNoise3DRenderMode.Surface3D)
        {
            ctx.OMSetRenderTargets(targetRtv, targetDsv);
            ctx.RSSetState(rsBack);
            ctx.OMSetBlendState(hasColorAlpha ? _blendPremul : _blendOpaque, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(hasColorAlpha ? _dsReadOnly : _dsOpaque, 0);
            ctx.PSSetShader(_psSurface);
            foreach (var part in model.Parts)
            {
                if (part.IndexCount > 0)
                    ctx.DrawIndexed(part.IndexCount, part.IndexOffset, 0);
            }
            return;
        }

        // Volume3D / IsoSurface3D: 2-pass bounded raymarch
        if (!camInside)
        {
            EnsureBackPosTarget(device, targetWidth, targetHeight);
            if (_backPosRtv == null || _backPosSrv == null)
                return;

            ctx.ClearRenderTargetView(_backPosRtv, new Color4(0f, 0f, 0f, 0f));
            ctx.OMSetRenderTargets(_backPosRtv, (ID3D11DepthStencilView?)null);
            ctx.RSSetState(rsFront);
            ctx.OMSetBlendState(_blendOpaque, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_dsDisabled, 0);
            ctx.PSSetShader(_psBackPos);
            foreach (var part in model.Parts)
            {
                if (part.IndexCount > 0)
                    ctx.DrawIndexed(part.IndexCount, part.IndexOffset, 0);
            }
        }

        ctx.OMSetRenderTargets(targetRtv, targetDsv);
        ctx.RSSetState(camInside ? rsFront : rsBack);
        if (np.RenderMode == D3DNoise3DRenderMode.IsoSurface3D)
        {
            ctx.OMSetBlendState(hasColorAlpha ? _blendPremul : _blendOpaque, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(hasColorAlpha ? _dsReadOnly : _dsOpaque, 0);
            ctx.PSSetShader(_psIso);
        }
        else
        {
            ctx.OMSetBlendState(_blendPremul, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_dsReadOnly, 0);
            ctx.PSSetShader(_psVolume);
        }
        if (!camInside)
            ctx.PSSetShaderResource(0, _backPosSrv!);

        foreach (var part in model.Parts)
        {
            if (part.IndexCount > 0)
                ctx.DrawIndexed(part.IndexCount, part.IndexOffset, 0);
        }
        ctx.PSSetShaderResource(0, null!);
    }

    private void ReleaseResources()
    {
        _backPosSrv?.Dispose(); _backPosSrv = null;
        _backPosRtv?.Dispose(); _backPosRtv = null;
        _backPosTex?.Dispose(); _backPosTex = null;
        _dsDisabled?.Dispose(); _dsDisabled = null;
        _dsReadOnly?.Dispose(); _dsReadOnly = null;
        _dsOpaque?.Dispose(); _dsOpaque = null;
        _blendPremul?.Dispose(); _blendPremul = null;
        _blendOpaque?.Dispose(); _blendOpaque = null;
        _rsCullFrontCw?.Dispose(); _rsCullFrontCw = null;
        _rsCullFrontCcw?.Dispose(); _rsCullFrontCcw = null;
        _rsCullBackCw?.Dispose(); _rsCullBackCw = null;
        _rsCullBackCcw?.Dispose(); _rsCullBackCcw = null;
        _cbBuffer?.Dispose(); _cbBuffer = null;
        _inputLayout?.Dispose(); _inputLayout = null;
        _psIso?.Dispose(); _psIso = null;
        _psVolume?.Dispose(); _psVolume = null;
        _psSurface?.Dispose(); _psSurface = null;
        _psBackPos?.Dispose(); _psBackPos = null;
        _vs?.Dispose(); _vs = null;
        _devicePointer = IntPtr.Zero;
        _initialized = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
    }
}
