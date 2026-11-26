namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// D3Dエフェクト・メインレンダラー・OBJレンダラー用の共通リニアPBR・IBL・ライティング＆シャドウHLSLコード。
/// StructuredBuffer (t3: Lights, t4: Shadows) と Texture2DArray (t2) を使用し、
/// 光源数・影数の上限を撤廃しつつ Cook-Torrance GGX PBR + 手続き型スタジオIBL + CSM/PCSS/Vogel PCF を実行する。
/// </summary>
internal static class LightingShaderCode
{
    public const string HlslCode = @"
// --- Constant Buffer for Lighting Header (b1, 176 bytes) ---
cbuffer CbLgt : register(b1)
{
    int    LightCount;
    int    ShadowEntryCount;
    float  UseSimpleLight;
    float  EnableShadow;

    float  AmbientIntensity;
    float  EnableSoftShadow;
    int    ShadowQuality;
    float  ShadowSoftness;

    float4 AmbientColor;
    float4 CameraWorldPosAndPageSize;   // xyz = camWorldPos, w = pageSize
    float4 CameraForwardAndRcpPageSize; // xyz = camForward,  w = 1.0 / pageSize

    float  EnablePbr;
    float  IblIntensity;
    float  DefaultRoughness;
    float  ShadowNoiseScale;

    float4 SkyColorAndGroundMix;        // rgb = linear sky tint, w = ground mix

    float4 CamBasisX;                   // xyz = camera-normal row0, w = HasEnvMap
    float4 CamBasisY;                   // xyz = camera-normal row1, w = EnvRotationRad
    float4 CamBasisZ;                   // xyz = camera-normal row2, w = EnvMaxMip
    float4 EnvParams;                   // x = EnvExposure, yzw = reserved
};

struct GpuLightData
{
    float4 PositionAndType;       // xyz = pos, w = type (0=Dir, 1=Point, 2=Spot, 3=Area)
    float4 DirectionAndIntensity; // xyz = normDir, w = intensity
    float4 ColorAndRange;         // rgb = linear color, w = range
    float4 SpotAndAreaParams;     // x = cosInner, y = cosOuter, z = areaW, w = areaH
    int4   ShadowMeta;            // x = shadowStartIndex (-1 if none), y = shadowCount, z = shadowMode (0=Spot,1=Point,2=AreaPCSS,3=DirCSM), w = pad
    float4 AreaRightAndSoftness;  // xyz = areaRight, w = perLightShadowSoftness
};

struct GpuShadowEntry
{
    float4 LvpRow0;
    float4 LvpRow1;
    float4 LvpRow2;
    float4 LvpRow3;
    float4 AtlasUvRect;   // xy = offset, zw = scale
    float4 BiasAndParams; // x = depthBias, y = normalBiasScale, z = shadowIntensity, w = sliceIndex
    float4 ExtraParams;   // DirCSM: (splitFar, blendWidth, worldUnitsPerTexel, tileRes)
                          // Spot/Point: (nearPlane, farPlane, tanHalfFov, tileRes)
                          // Area: (nearPlane, farPlane, tanHalfFov, lightRadiusWorld)
};

Texture2DArray<float>            gShadowArray        : register(t2);
StructuredBuffer<GpuLightData>   gLights             : register(t3);
StructuredBuffer<GpuShadowEntry> gShadows            : register(t4);
Texture2D                        gEnvMap             : register(t5);
SamplerComparisonState           gShadowCmpSampler   : register(s1);
SamplerState                     gShadowPointSampler : register(s2);
SamplerState                     gEnvSampler         : register(s3);

static const float PI_LGT = 3.14159265;

float SrgbToLinearChannelUnclamped(float x)
{
    x = max(x, 0.0);
    return (x <= 0.04045) ? (x / 12.92) : pow((x + 0.055) / 1.055, 2.4);
}

float3 SrgbToLinearUnclamped(float3 c)
{
    return float3(
        SrgbToLinearChannelUnclamped(c.r),
        SrgbToLinearChannelUnclamped(c.g),
        SrgbToLinearChannelUnclamped(c.b)
    );
}

float LinearToSrgbChannelUnclamped(float x)
{
    x = max(x, 0.0);
    return (x <= 0.0031308) ? (12.92 * x) : (1.055 * pow(x, 1.0 / 2.4) - 0.055);
}

float3 LinearToSrgbUnclamped(float3 c)
{
    return float3(
        LinearToSrgbChannelUnclamped(c.r),
        LinearToSrgbChannelUnclamped(c.g),
        LinearToSrgbChannelUnclamped(c.b)
    );
}

float4 TransformShadowLvp(float3 worldPos, GpuShadowEntry e)
{
    return worldPos.x * e.LvpRow0
         + worldPos.y * e.LvpRow1
         + worldPos.z * e.LvpRow2
         + e.LvpRow3;
}

float2 LocalToPageUV(float2 localUV, float4 uvRect, float rcpPageSize)
{
    float2 pad = float2(
        min(rcpPageSize, uvRect.z * 0.25),
        min(rcpPageSize, uvRect.w * 0.25)
    );
    float2 minUV = uvRect.xy + pad;
    float2 maxUV = uvRect.xy + uvRect.zw - pad;
    float2 pageUV = uvRect.xy + saturate(localUV) * uvRect.zw;
    return clamp(pageUV, minUV, maxUV);
}

float InterleavedGradientNoise(float2 screenPos)
{
    float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(screenPos, magic.xy)));
}

float2 VogelDiskSample(int sampleIndex, int samplesCount, float phi)
{
    float goldenAngle = 2.39996323;
    float r = sqrt((float(sampleIndex) + 0.5) / max(float(samplesCount), 1.0));
    float theta = float(sampleIndex) * goldenAngle + phi;
    float s, c;
    sincos(theta, s, c);
    return float2(r * c, r * s);
}

int GetShadowTapCount()
{
    if (ShadowQuality <= 0) return 4;
    if (ShadowQuality == 1) return 12;
    if (ShadowQuality == 2) return 20;
    return 32;
}

float LinearizeDepthRH(float ndcDepth, float nearPlane, float farPlane)
{
    return (nearPlane * farPlane) / max(farPlane - ndcDepth * (farPlane - nearPlane), 1e-5);
}

float SampleShadowTilePCF(GpuShadowEntry e, float2 localUV, float compareDepth, float filterRadiusLocalUV, float phi, int taps)
{
    float rcpPage = CameraForwardAndRcpPageSize.w;
    float slice = e.BiasAndParams.w;

    if (EnableSoftShadow < 0.5 || taps <= 1 || filterRadiusLocalUV <= 0.0)
    {
        float2 puv = LocalToPageUV(localUV, e.AtlasUvRect, rcpPage);
        return gShadowArray.SampleCmpLevelZero(gShadowCmpSampler, float3(puv, slice), compareDepth);
    }

    float sum = 0.0;
    [loop]
    for (int i = 0; i < taps; i++)
    {
        float2 offset = VogelDiskSample(i, taps, phi) * filterRadiusLocalUV;
        float2 puv = LocalToPageUV(localUV + offset, e.AtlasUvRect, rcpPage);
        sum += gShadowArray.SampleCmpLevelZero(gShadowCmpSampler, float3(puv, slice), compareDepth);
    }
    return sum / float(taps);
}

float SampleDirectionalCSM(int startIdx, int cascadeCount, float3 worldPos, float3 N, float ndl, float2 screenPos, float lightSoftness)
{
    if (startIdx < 0 || cascadeCount <= 0 || startIdx >= ShadowEntryCount) return 1.0;

    float viewDist = dot(worldPos - CameraWorldPosAndPageSize.xyz, CameraForwardAndRcpPageSize.xyz);
    int cIdx = cascadeCount - 1;
    [unroll]
    for (int c = 0; c < 4; c++)
    {
        if (c < cascadeCount && (startIdx + c) < ShadowEntryCount && viewDist <= gShadows[startIdx + c].ExtraParams.x)
        {
            cIdx = c;
            break;
        }
    }

    int entryIdx = startIdx + cIdx;
    if (entryIdx >= ShadowEntryCount) return 1.0;
    GpuShadowEntry e = gShadows[entryIdx];
    if (e.BiasAndParams.w < 0.0) return 1.0;

    float tileRes = max(e.ExtraParams.w, 1.0);
    float worldPerTexel = max(e.ExtraParams.z, 0.001);
    float3 samplePos = worldPos + N * (e.BiasAndParams.y * worldPerTexel * saturate(1.0 - ndl * 0.7));

    float4 sp = TransformShadowLvp(samplePos, e);
    if (abs(sp.w) < 1e-6) return 1.0;
    sp.xyz /= sp.w;

    float2 uv = float2(sp.x * 0.5 + 0.5, -sp.y * 0.5 + 0.5);
    float depth = sp.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || depth < 0.0 || depth > 1.0)
        return 1.0;

    float bias = e.BiasAndParams.x * (0.25 + 0.75 * (1.0 - ndl));
    float phi = InterleavedGradientNoise(screenPos) * 6.2831853;
    int taps = GetShadowTapCount();
    float baseWorldBlur = 4.0 * ShadowSoftness * lightSoftness;
    float filterUV = clamp(baseWorldBlur / max(worldPerTexel * tileRes, 1.0), 1.0 / tileRes, 0.035);

    float shadow = SampleShadowTilePCF(e, uv, depth - bias, filterUV, phi, taps);

    float splitFar = e.ExtraParams.x;
    float blendWidth = e.ExtraParams.y;
    if (cIdx + 1 < cascadeCount && (entryIdx + 1) < ShadowEntryCount && blendWidth > 0.001 && viewDist > splitFar - blendWidth)
    {
        GpuShadowEntry ne = gShadows[entryIdx + 1];
        if (ne.BiasAndParams.w >= 0.0)
        {
            float t = saturate((viewDist - (splitFar - blendWidth)) / blendWidth);
            float nWorldPerTexel = max(ne.ExtraParams.z, 0.001);
            float nTileRes = max(ne.ExtraParams.w, 1.0);
            float3 nSamplePos = worldPos + N * (ne.BiasAndParams.y * nWorldPerTexel * saturate(1.0 - ndl * 0.7));
            float4 nsp = TransformShadowLvp(nSamplePos, ne);
            if (abs(nsp.w) > 1e-6)
            {
                nsp.xyz /= nsp.w;
                float2 nuv = float2(nsp.x * 0.5 + 0.5, -nsp.y * 0.5 + 0.5);
                if (nuv.x >= 0.0 && nuv.x <= 1.0 && nuv.y >= 0.0 && nuv.y <= 1.0 && nsp.z >= 0.0 && nsp.z <= 1.0)
                {
                    float nFilterUV = clamp(baseWorldBlur / max(nWorldPerTexel * nTileRes, 1.0), 1.0 / nTileRes, 0.035);
                    float nShadow = SampleShadowTilePCF(ne, nuv, nsp.z - bias, nFilterUV, phi, min(taps, 8));
                    shadow = lerp(shadow, nShadow, t);
                }
            }
        }
    }

    return shadow;
}

float SampleSpotShadow(int startIdx, float3 worldPos, float3 lightPos, float3 N, float ndl, float2 screenPos, float lightSoftness)
{
    if (startIdx < 0 || startIdx >= ShadowEntryCount) return 1.0;
    GpuShadowEntry e = gShadows[startIdx];
    if (e.BiasAndParams.w < 0.0) return 1.0;

    float dist = max(length(worldPos - lightPos), 1.0);
    float tanHalfFov = max(e.ExtraParams.z, 0.01);
    float tileRes = max(e.ExtraParams.w, 1.0);
    float worldPerTexel = (2.0 * dist * tanHalfFov) / tileRes;

    float3 samplePos = worldPos + N * (e.BiasAndParams.y * worldPerTexel * saturate(1.0 - ndl * 0.65));
    float4 sp = TransformShadowLvp(samplePos, e);
    if (sp.w <= 0.0001) return 1.0;
    sp.xyz /= sp.w;

    float2 uv = float2(sp.x * 0.5 + 0.5, -sp.y * 0.5 + 0.5);
    float depth = sp.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || depth <= 0.0 || depth >= 1.0)
        return 1.0;

    float bias = e.BiasAndParams.x * saturate(400.0 / max(dist, 50.0)) * (0.3 + 0.7 * (1.0 - ndl));
    float phi = InterleavedGradientNoise(screenPos) * 6.2831853;
    int taps = GetShadowTapCount();
    float worldBlur = 3.5 * ShadowSoftness * lightSoftness;
    float filterUV = clamp(worldBlur / max(2.0 * dist * tanHalfFov, 1.0), 1.0 / tileRes, 0.035);

    return SampleShadowTilePCF(e, uv, depth - bias, filterUV, phi, taps);
}

float SampleAreaShadowPCSS(int startIdx, float3 worldPos, float3 lightPos, float3 N, float ndl, float2 screenPos, float lightSoftness)
{
    if (startIdx < 0 || startIdx >= ShadowEntryCount) return 1.0;
    GpuShadowEntry e = gShadows[startIdx];
    if (e.BiasAndParams.w < 0.0) return 1.0;

    float nearP = max(e.ExtraParams.x, 1.0);
    float farP = max(e.ExtraParams.y, nearP + 10.0);
    float tanHalfFov = max(e.ExtraParams.z, 0.1);
    float lightRadiusWorld = max(e.ExtraParams.w, 1.0);
    float pageSize = max(CameraWorldPosAndPageSize.w, 128.0);
    float tileRes = max(e.AtlasUvRect.z * pageSize, 1.0);

    float distApprox = max(length(worldPos - lightPos) + nearP, nearP);
    float worldPerTexel = (2.0 * distApprox * tanHalfFov) / tileRes;

    float3 samplePos = worldPos + N * (e.BiasAndParams.y * worldPerTexel * saturate(1.0 - ndl * 0.65));
    float4 sp = TransformShadowLvp(samplePos, e);
    if (sp.w <= 0.0001) return 1.0;
    sp.xyz /= sp.w;

    float2 uv = float2(sp.x * 0.5 + 0.5, -sp.y * 0.5 + 0.5);
    float depth = sp.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || depth <= 0.0 || depth >= 1.0)
        return 1.0;

    float edgeFade = smoothstep(0.0, 0.03, min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y)));
    float bias = e.BiasAndParams.x * saturate(400.0 / max(distApprox, 50.0)) * (0.3 + 0.7 * (1.0 - ndl));

    if (EnableSoftShadow < 0.5)
    {
        float hardShadow = SampleShadowTilePCF(e, uv, depth - bias, 0.0, 0.0, 1);
        return lerp(1.0, hardShadow, edgeFade);
    }

    float rcpPage = CameraForwardAndRcpPageSize.w;
    float slice = e.BiasAndParams.w;
    float phi = InterleavedGradientNoise(screenPos) * 6.2831853;

    float receiverLin = LinearizeDepthRH(depth, nearP, farP);
    float searchRadiusUV = clamp(
        (lightRadiusWorld * max(receiverLin - nearP, 0.0) / max(receiverLin, nearP))
        / max(2.0 * receiverLin * tanHalfFov, 1.0)
        * ShadowSoftness * lightSoftness,
        2.0 / tileRes,
        0.06);

    int searchTaps = (ShadowQuality <= 1) ? 8 : 16;
    float blockerSum = 0.0;
    float blockerCount = 0.0;
    float searchBias = bias * 1.5;

    [loop]
    for (int i = 0; i < searchTaps; i++)
    {
        float2 offset = VogelDiskSample(i, searchTaps, phi) * searchRadiusUV;
        float2 puv = LocalToPageUV(uv + offset, e.AtlasUvRect, rcpPage);
        float sampleDepth = gShadowArray.SampleLevel(gShadowPointSampler, float3(puv, slice), 0).r;
        if (sampleDepth < depth - searchBias)
        {
            blockerSum += sampleDepth;
            blockerCount += 1.0;
        }
    }

    if (blockerCount < 0.5)
        return 1.0;

    float avgBlockerDepth = blockerSum / blockerCount;
    float blockerLin = LinearizeDepthRH(avgBlockerDepth, nearP, farP);
    float penumbraWorld = lightRadiusWorld * max(receiverLin - blockerLin, 0.0) / max(blockerLin, nearP);
    float filterUV = clamp(
        (penumbraWorld * ShadowSoftness * lightSoftness) / max(2.0 * receiverLin * tanHalfFov, 1.0),
        1.0 / tileRes,
        0.06);

    int pcfTaps = GetShadowTapCount();
    float softShadow = SampleShadowTilePCF(e, uv, depth - bias, filterUV, phi, pcfTaps);
    return lerp(1.0, softShadow, edgeFade);
}

int GetCubeFaceIndex(float3 d)
{
    float3 absD = abs(d);
    if (absD.x >= absD.y && absD.x >= absD.z)
        return (d.x >= 0.0) ? 0 : 1;
    if (absD.y >= absD.x && absD.y >= absD.z)
        return (d.y >= 0.0) ? 2 : 3;
    return (d.z >= 0.0) ? 4 : 5;
}

float SamplePointShadow(int startIdx, int count, float3 worldPos, float3 lightPos, float3 N, float ndl, float2 screenPos, float lightSoftness)
{
    if (startIdx < 0 || count < 6 || (startIdx + 5) >= ShadowEntryCount) return 1.0;

    float3 d0 = worldPos - lightPos;
    float dist = max(length(d0), 1.0);
    int face0 = GetCubeFaceIndex(d0);
    GpuShadowEntry e0 = gShadows[startIdx + face0];
    if (e0.BiasAndParams.w < 0.0) return 1.0;

    float tileRes = max(e0.ExtraParams.w, 1.0);
    float worldPerTexel = (2.0 * dist) / tileRes;
    float3 samplePos = worldPos + N * (e0.BiasAndParams.y * worldPerTexel * saturate(1.0 - ndl * 0.65));

    float3 d = samplePos - lightPos;
    int face = GetCubeFaceIndex(d);
    GpuShadowEntry e = gShadows[startIdx + face];
    if (e.BiasAndParams.w < 0.0) return 1.0;

    float4 sp = TransformShadowLvp(samplePos, e);
    if (sp.w <= 0.0001) return 1.0;
    sp.xyz /= sp.w;

    float2 uv = float2(sp.x * 0.5 + 0.5, -sp.y * 0.5 + 0.5);
    float depth = sp.z;
    if (depth <= 0.0 || depth >= 1.0) return 1.0;

    float bias = e.BiasAndParams.x * saturate(400.0 / max(dist, 50.0)) * (0.3 + 0.7 * (1.0 - ndl));
    int taps = GetShadowTapCount();
    float phi = InterleavedGradientNoise(screenPos) * 6.2831853;

    if (EnableSoftShadow < 0.5 || taps <= 1)
    {
        return SampleShadowTilePCF(e, uv, depth - bias, 0.0, phi, 1);
    }

    float worldBlurRadius = 3.5 * ShadowSoftness * lightSoftness;
    float uvRadius = clamp(worldBlurRadius / max(2.0 * dist, 1.0), 1.0 / tileRes, 0.04);

    if (uv.x > 0.05 && uv.x < 0.95 && uv.y > 0.05 && uv.y < 0.95)
    {
        return SampleShadowTilePCF(e, uv, depth - bias, uvRadius, phi, taps);
    }

    float3 dirN = normalize(d);
    float3 upRef = abs(dirN.y) > 0.9 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
    float3 tangent = normalize(cross(upRef, dirN));
    float3 bitangent = cross(dirN, tangent);
    float rcpPage = CameraForwardAndRcpPageSize.w;

    float sum = 0.0;
    [loop]
    for (int i = 0; i < taps; i++)
    {
        float2 disk = VogelDiskSample(i, taps, phi);
        float3 offsetWorld = samplePos + (tangent * disk.x + bitangent * disk.y) * worldBlurRadius;
        int f = GetCubeFaceIndex(offsetWorld - lightPos);
        GpuShadowEntry fe = gShadows[startIdx + f];
        if (fe.BiasAndParams.w < 0.0)
        {
            sum += 1.0;
            continue;
        }
        float4 fsp = TransformShadowLvp(offsetWorld, fe);
        if (fsp.w <= 0.0001)
        {
            sum += 1.0;
            continue;
        }
        fsp.xyz /= fsp.w;
        float2 fuv = float2(fsp.x * 0.5 + 0.5, -fsp.y * 0.5 + 0.5);
        float2 puv = LocalToPageUV(fuv, fe.AtlasUvRect, rcpPage);
        sum += gShadowArray.SampleCmpLevelZero(gShadowCmpSampler, float3(puv, fe.BiasAndParams.w), fsp.z - bias);
    }
    return sum / float(taps);
}

float GetLightShadowFactor(int lightIdx, float3 worldPos, float3 N, float ndl, float2 screenPos)
{
    if (EnableShadow < 0.5) return 1.0;

    int startIdx = gLights[lightIdx].ShadowMeta.x;
    int count = gLights[lightIdx].ShadowMeta.y;
    int mode = gLights[lightIdx].ShadowMeta.z;
    if (startIdx < 0 || count <= 0 || startIdx >= ShadowEntryCount) return 1.0;

    float shadowIntensity = gShadows[startIdx].BiasAndParams.z;
    if (shadowIntensity <= 0.0001) return 1.0;

    float3 lightPos = gLights[lightIdx].PositionAndType.xyz;
    float lightSoftness = max(gLights[lightIdx].AreaRightAndSoftness.w, 0.0);

    float shadow = 1.0;
    if (mode == 3)
    {
        shadow = SampleDirectionalCSM(startIdx, count, worldPos, N, ndl, screenPos, lightSoftness);
    }
    else if (mode == 2)
    {
        shadow = SampleAreaShadowPCSS(startIdx, worldPos, lightPos, N, ndl, screenPos, lightSoftness);
    }
    else if (mode == 1)
    {
        shadow = SamplePointShadow(startIdx, count, worldPos, lightPos, N, ndl, screenPos, lightSoftness);
    }
    else
    {
        shadow = SampleSpotShadow(startIdx, worldPos, lightPos, N, ndl, screenPos, lightSoftness);
    }

    return lerp(1.0 - shadowIntensity, 1.0, shadow);
}

// --- Cook-Torrance GGX PBR + Procedural Studio/Room IBL ---

float DistributionGGX(float ndh, float roughness)
{
    float a = max(roughness * roughness, 0.0025);
    float a2 = a * a;
    float denom = (ndh * ndh) * (a2 - 1.0) + 1.0;
    return a2 / max(PI_LGT * denom * denom, 1e-6);
}

float GeometrySchlickGGX(float ndv, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) * 0.125;
    return ndv / max(ndv * (1.0 - k) + k, 1e-5);
}

float GeometrySmith(float ndv, float ndl, float roughness)
{
    return GeometrySchlickGGX(ndv, roughness) * GeometrySchlickGGX(ndl, roughness);
}

float3 FresnelSchlick(float cosTheta, float3 F0)
{
    float x = saturate(1.0 - cosTheta);
    float x2 = x * x;
    return F0 + (1.0 - F0) * (x2 * x2 * x);
}

float3 FresnelSchlickRoughness(float cosTheta, float3 F0, float roughness)
{
    float x = saturate(1.0 - cosTheta);
    float x2 = x * x;
    return F0 + (max(float3(1.0 - roughness, 1.0 - roughness, 1.0 - roughness), F0) - F0) * (x2 * x2 * x);
}

float3 EnvBRDFApprox(float3 F0, float roughness, float ndv)
{
    const float4 c0 = float4(-1.0, -0.0275, -0.572, 0.022);
    const float4 c1 = float4( 1.0,  0.0425,  1.04, -0.04);
    float4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * ndv)) * r.x + r.y;
    float2 AB = float2(-1.04, 1.04) * a004 + r.zw;
    return F0 * AB.x + AB.y;
}

float3 EvaluateHemisphereAmbient(float3 n)
{
    // YMM4 world convention: -Y is UP, +Y is DOWN
    float upFactor = -n.y;
    float3 groundBounce = AmbientColor.rgb;
    float3 horizon = lerp(groundBounce, SkyColorAndGroundMix.rgb, 0.45);
    float3 topSky = SkyColorAndGroundMix.rgb;
    if (upFactor >= 0.0)
        return lerp(horizon, topSky, upFactor);
    return lerp(horizon, groundBounce * 0.72, -upFactor);
}

float3 EvaluateProceduralEnv(float3 R, float roughness)
{
    float3 d = normalize(R);
    // YMM4: -Y is UP
    float up = -d.y;

    float3 groundCol = AmbientColor.rgb * 0.85;
    float3 horizonCol = float3(0.45, 0.47, 0.50);
    float3 skyCol = SkyColorAndGroundMix.rgb * 1.15;

    float3 sharpEnv = up >= 0.0
        ? lerp(horizonCol, skyCol, pow(up, 0.6))
        : lerp(horizonCol, groundCol, pow(-up, 0.7));

    // Studio softbox highlights (overhead softbox + subtle side rim window)
    float3 softboxDir = normalize(float3(0.35, -0.82, 0.45));
    float softboxSpec = smoothstep(0.65, 0.97, dot(d, softboxDir));
    float windowPanel = smoothstep(0.55, 0.92, dot(d, normalize(float3(-0.78, -0.35, 0.52))));

    float gloss = pow(1.0 - roughness, 2.0);
    sharpEnv += float3(1.55, 1.50, 1.42) * softboxSpec * (0.25 + 0.75 * gloss);
    sharpEnv += float3(0.65, 0.78, 0.95) * windowPanel * (0.2 + 0.6 * gloss);

    float3 diffuseBlur = EvaluateHemisphereAmbient(d);
    return lerp(sharpEnv, diffuseBlur, saturate(roughness * roughness * 0.92));
}

float3 WorldToCamNormal(float3 worldNormal)
{
    float3 nCam = worldNormal.x * CamBasisX.xyz
                + worldNormal.y * CamBasisY.xyz
                + worldNormal.z * CamBasisZ.xyz;
    float lenSq = dot(nCam, nCam);
    return lenSq > 1e-8 ? (nCam * rsqrt(lenSq)) : float3(0.0, 0.0, 1.0);
}

float2 DirToEquirectUV(float3 dir, float rotRad)
{
    float3 d = normalize(dir);
    float s, c;
    sincos(rotRad, s, c);
    float3 r = float3(d.x * c + d.z * s, d.y, -d.x * s + d.z * c);
    float u = atan2(r.x, -r.z) * (1.0 / (2.0 * PI_LGT)) + 0.5;
    float v = acos(clamp(-r.y, -1.0, 1.0)) * (1.0 / PI_LGT);
    return float2(u, v);
}

float3 EvaluateEnv(float3 R, float roughness)
{
    if (CamBasisX.w > 0.5)
    {
        float2 uv = DirToEquirectUV(R, CamBasisY.w);
        float rMip = saturate((roughness - 0.04) / 0.96);
        float mip = clamp(pow(rMip, 0.85) * CamBasisZ.w, 0.0, CamBasisZ.w);
        return min(gEnvMap.SampleLevel(gEnvSampler, uv, mip).rgb * max(EnvParams.x, 0.0), 256.0);
    }
    return EvaluateProceduralEnv(R, roughness);
}

float3 EvaluateDiffuseEnv(float3 n)
{
    if (CamBasisX.w > 0.5)
    {
        float2 uv = DirToEquirectUV(n, CamBasisY.w);
        return min(gEnvMap.SampleLevel(gEnvSampler, uv, CamBasisZ.w).rgb * max(EnvParams.x, 0.0), 64.0);
    }
    return EvaluateHemisphereAmbient(n);
}

float3 PerturbNormalCotangent(float3 N, float3 worldPos, float2 uv, float4 mapSample, float strength)
{
    float3 n = normalize(N);
    if (strength <= 0.0001) return n;

    float2 xy = mapSample.xy * 2.0 - 1.0;
    float z = (mapSample.z > 0.05) ? (mapSample.z * 2.0 - 1.0) : sqrt(saturate(1.0 - dot(xy, xy)));
    float3 mapN = normalize(float3(xy.x * strength, xy.y * strength, max(z, 0.001)));

    float3 dp1 = ddx(worldPos);
    float3 dp2 = ddy(worldPos);
    float2 duv1 = ddx(uv);
    float2 duv2 = ddy(uv);

    float3 dp2perp = cross(dp2, n);
    float3 dp1perp = cross(n, dp1);
    float3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    float3 B = dp2perp * duv1.y + dp1perp * duv2.y;
    float invmax = max(dot(T, T), dot(B, B));
    if (invmax < 1e-10) return n;

    float s = rsqrt(invmax);
    float3x3 TBN = float3x3(T * s, B * s, n);
    return normalize(mul(mapN, TBN));
}

void ApplyRoughMetalSample(float4 rmSample, float mode, inout float roughness, inout float metallic)
{
    if (mode < 0.5) return;
    if (mode < 1.5)
    {
        // glTF ORM: G = Roughness, B = Metallic
        roughness = clamp(roughness * rmSample.g, 0.04, 1.0);
        metallic = saturate(metallic * rmSample.b);
    }
    else if (mode < 2.5)
    {
        // Grayscale roughness
        roughness = clamp(rmSample.r, 0.04, 1.0);
    }
    else if (mode < 3.5)
    {
        // Grayscale metallic
        metallic = saturate(rmSample.r);
    }
    else
    {
        // Specular/Glossiness map
        roughness = clamp(1.0 - rmSample.r, 0.04, 1.0);
    }
}

struct OpaqueMrtOutput
{
    float4 Color       : SV_Target0;
    float4 NormalRough : SV_Target1;
    float4 SpecMeta    : SV_Target2;
};

OpaqueMrtOutput PackOpaqueMrt(
    float4 premulColor,
    float3 worldNormal,
    float3 albedoLinear,
    float roughness,
    float metallic,
    float clearcoat)
{
    OpaqueMrtOutput o;
    o.Color = premulColor;
    float r = clamp(roughness >= 0.0 ? roughness : DefaultRoughness, 0.04, 1.0);
    float cc = saturate(clearcoat);
    float effRough = lerp(r, 0.08, cc * 0.85);
    float3 nCam = WorldToCamNormal(worldNormal);
    o.NormalRough = float4(nCam, effRough);

    float m = saturate(metallic);
    float3 f0 = lerp(float3(0.04, 0.04, 0.04), saturate(albedoLinear), m);
    f0 = lerp(f0, float3(0.04, 0.04, 0.04), cc * 0.5);
    o.SpecMeta = float4(saturate(f0), max(m, cc * 0.5));
    return o;
}

void EvalPbrDirectSplit(
    float3 albedo,
    float3 N,
    float3 V,
    float3 L_diff,
    float3 L_spec,
    float metallic,
    float roughness,
    float clearcoat,
    float dielectricF0,
    out float3 outDiff,
    out float3 outSpec)
{
    float ndl = saturate(dot(N, L_diff));
    float ndlSpec = saturate(dot(N, L_spec));
    float ndv = max(dot(N, V), 0.001);

    float3 H = normalize(V + L_spec);
    float ndh = saturate(dot(N, H));
    float vdh = saturate(dot(V, H));

    float3 F0 = lerp(float3(dielectricF0, dielectricF0, dielectricF0), albedo, metallic);
    float3 F = FresnelSchlick(vdh, F0);
    float D = DistributionGGX(ndh, roughness);
    float G = GeometrySmith(ndv, max(ndlSpec, 0.001), roughness);

    float3 spec = (D * G * F) / max(4.0 * ndv * max(ndlSpec, 0.001), 1e-4);
    float directMatteFade = lerp(1.0 - smoothstep(0.80, 1.0, roughness) * 0.75, 1.0, metallic);
    spec = min(spec * PI_LGT, float3(32.0, 32.0, 32.0)) * directMatteFade;

    float3 kD = (1.0 - F) * (1.0 - metallic);
    outDiff = (kD * albedo) * ndl;
    outSpec = spec * ndlSpec;

    if (clearcoat > 0.001)
    {
        float cc = saturate(clearcoat);
        float ccRough = 0.08;
        float Fc = FresnelSchlick(vdh, float3(0.04, 0.04, 0.04)).r * cc;
        float Dc = DistributionGGX(ndh, ccRough);
        float Gc = GeometrySmith(ndv, max(ndlSpec, 0.001), ccRough);
        float ccSpec = min((Dc * Gc * Fc) / max(4.0 * ndv * max(ndlSpec, 0.001), 1e-4) * PI_LGT, 32.0);
        outDiff *= (1.0 - Fc);
        outSpec = outSpec * (1.0 - Fc) + float3(ccSpec, ccSpec, ccSpec) * ndlSpec;
    }
}

float3 EvalPbrDirect(
    float3 albedo,
    float3 N,
    float3 V,
    float3 L_diff,
    float3 L_spec,
    float metallic,
    float roughness,
    float clearcoat)
{
    float3 d, s;
    EvalPbrDirectSplit(albedo, N, V, L_diff, L_spec, metallic, roughness, clearcoat, 0.04, d, s);
    return d + s;
}

void CalcSimpleLgtPbrSplit(
    float3 albedoLinear,
    float3 normal,
    float3 worldPos,
    float metallic,
    float roughness,
    float clearcoat,
    float lightIntensityParam,
    float dielectricF0,
    float glassEnvMode,
    out float3 outDiff,
    out float3 outSpec,
    out float3 outF)
{
    float3 n = normalize(normal);
    float m = saturate(metallic);
    float r = clamp(roughness >= 0.0 ? roughness : DefaultRoughness, 0.04, 1.0);
    float cc = saturate(clearcoat);

    float3 keyDir = -normalize(float3(0.3, -0.5, -1.0));
    float3 V = normalize(CameraWorldPosAndPageSize.xyz - worldPos);
    if (dot(V, V) < 1e-6) V = float3(0.0, 0.0, 1.0);

    if (EnablePbr < 0.5)
    {
        float ndlSimple = saturate(dot(n, keyDir));
        float fillSimple = lerp(1.0, 0.5 + 0.5 * ndlSimple, lightIntensityParam);
        outDiff = albedoLinear * fillSimple;
        outSpec = float3(0.0, 0.0, 0.0);
        outF = float3(0.0, 0.0, 0.0);
        return;
    }

    float3 hemi = EvaluateDiffuseEnv(n);
    float ndv = max(dot(n, V), 0.001);
    float3 R = reflect(-V, n);
    float3 F0 = lerp(float3(dielectricF0, dielectricF0, dielectricF0), albedoLinear, m);
    float3 kS = FresnelSchlickRoughness(ndv, F0, r);
    float3 kD = (1.0 - kS) * (1.0 - m);
    float matteSpecFade = lerp(1.0 - smoothstep(0.75, 1.0, r) * 0.92, 1.0, m);

    float envReflScale = (EnvParams.z > 0.5) ? max(EnvParams.y, 0.0) : 1.0;
    float baseSpecIbl = ((CamBasisX.w > 0.5) ? IblIntensity : (IblIntensity * lerp(0.75, 1.0, m))) * envReflScale;
    float specIblScale = (glassEnvMode > 0.5)
        ? ((CamBasisX.w > 0.5) ? 1.0 : max(IblIntensity, 0.75))
        : baseSpecIbl;
    float3 envDiff = kD * albedoLinear * hemi * (0.45 + 0.40 * IblIntensity);
    float3 envSpec = EvaluateEnv(R, r) * EnvBRDFApprox(F0, r, ndv) * specIblScale * matteSpecFade;
    if (cc > 0.001)
    {
        float ccFres = FresnelSchlick(ndv, float3(0.04, 0.04, 0.04)).r * cc;
        float3 ccEnv = EvaluateEnv(R, 0.08) * EnvBRDFApprox(float3(0.04, 0.04, 0.04), 0.08, ndv) * specIblScale * cc;
        envDiff *= (1.0 - ccFres);
        envSpec = envSpec * (1.0 - ccFres) + ccEnv;
    }

    float3 keyDiff, keySpec;
    EvalPbrDirectSplit(albedoLinear, n, V, keyDir, keyDir, m, r, cc, dielectricF0, keyDiff, keySpec);
    float3 rimDir = normalize(float3(-0.6, -0.3, 0.4));
    float rimNdl = saturate(dot(n, rimDir));
    float3 rimLight = (kD * albedoLinear) * float3(0.22, 0.25, 0.32) * rimNdl;

    float pbrWeight = (CamBasisX.w > 0.5) ? 1.0 : lerp(saturate(lightIntensityParam * 1.15), 1.0, m);
    bool hasCustomEnv = (CamBasisX.w > 0.5 || glassEnvMode > 0.5);
    float3 simpleDiff = hasCustomEnv
        ? (envDiff + keyDiff * 0.85 + rimLight)
        : (envDiff * 0.72 + keyDiff * 0.62 + rimLight * 0.50);
    float envSpecMult = hasCustomEnv ? 1.0 : lerp(0.42, 1.0, m);
    float keySpecMult = hasCustomEnv ? 0.85 : lerp(0.30, 0.85, m);
    outDiff = lerp(albedoLinear * (1.0 - m), simpleDiff, pbrWeight);
    outSpec = (envSpec * envSpecMult + keySpec * keySpecMult) * pbrWeight;
    outF = kS;
}

// Simple light in Linear HDR PBR (fallback when no D3D11 lights exist)
float3 CalcSimpleLgtPbrEx(
    float3 albedoLinear,
    float3 normal,
    float3 worldPos,
    float metallic,
    float roughness,
    float clearcoat,
    float lightIntensityParam)
{
    float3 d, s, f;
    CalcSimpleLgtPbrSplit(albedoLinear, normal, worldPos, metallic, roughness, clearcoat, lightIntensityParam, 0.04, 0.0, d, s, f);
    return d + s;
}

float3 CalcSimpleLgtPbr(
    float3 albedoLinear,
    float3 normal,
    float3 worldPos,
    float metallic,
    float roughness,
    float lightIntensityParam)
{
    return CalcSimpleLgtPbrEx(albedoLinear, normal, worldPos, metallic, roughness, 0.0, lightIntensityParam);
}

void CalcDynamicPbrPartsEx(
    float3 albedoLinear,
    float3 normal,
    float3 worldPos,
    float2 screenPos,
    float metallic,
    float roughness,
    float clearcoat,
    float dielectricF0,
    float glassEnvMode,
    out float3 outDiff,
    out float3 outSpec,
    out float3 outF)
{
    float3 n = normalize(normal);
    float m = saturate(metallic);
    float r = clamp(roughness >= 0.0 ? roughness : DefaultRoughness, 0.04, 1.0);
    float cc = saturate(clearcoat);

    float3 V = normalize(CameraWorldPosAndPageSize.xyz - worldPos);
    if (dot(V, V) < 1e-6) V = float3(0.0, 0.0, 1.0);

    float3 hemi = EvaluateDiffuseEnv(n);
    outDiff = float3(0.0, 0.0, 0.0);
    outSpec = float3(0.0, 0.0, 0.0);
    outF = float3(0.0, 0.0, 0.0);

    if (EnablePbr >= 0.5)
    {
        float ndv = max(dot(n, V), 0.001);
        float3 R = reflect(-V, n);
        float3 F0 = lerp(float3(dielectricF0, dielectricF0, dielectricF0), albedoLinear, m);
        float3 kS = FresnelSchlickRoughness(ndv, F0, r);
        float3 kD = (1.0 - kS) * (1.0 - m);
        float matteSpecFade = lerp(1.0 - smoothstep(0.75, 1.0, r) * 0.92, 1.0, m);

        float envReflScale = (EnvParams.z > 0.5) ? max(EnvParams.y, 0.0) : 1.0;
        float baseSpecIbl = ((CamBasisX.w > 0.5)
            ? IblIntensity
            : (IblIntensity * lerp(max(AmbientIntensity, 0.2), 0.72, m))) * envReflScale;
        float specIblScale = (glassEnvMode > 0.5)
            ? ((CamBasisX.w > 0.5) ? 1.0 : max(IblIntensity, 0.75))
            : baseSpecIbl;
        float3 envDiff = kD * albedoLinear * hemi * AmbientIntensity;
        float3 envSpec = EvaluateEnv(R, r) * EnvBRDFApprox(F0, r, ndv) * specIblScale * matteSpecFade;
        if (cc > 0.001)
        {
            float ccFres = FresnelSchlick(ndv, float3(0.04, 0.04, 0.04)).r * cc;
            float3 ccEnv = EvaluateEnv(R, 0.08) * EnvBRDFApprox(float3(0.04, 0.04, 0.04), 0.08, ndv) * specIblScale * cc;
            envDiff *= (1.0 - ccFres);
            envSpec = envSpec * (1.0 - ccFres) + ccEnv;
        }
        outDiff += envDiff;
        outSpec += envSpec;
        outF = kS;
    }
    else
    {
        outDiff += albedoLinear * hemi * AmbientIntensity;
    }

    [loop]
    for (int i = 0; i < LightCount; i++)
    {
        float lightType = gLights[i].PositionAndType.w;
        float3 lightPos = gLights[i].PositionAndType.xyz;
        float3 lightDir = gLights[i].DirectionAndIntensity.xyz;
        float  intensity = gLights[i].DirectionAndIntensity.w;
        float3 lightColor = gLights[i].ColorAndRange.xyz;
        float  range = gLights[i].ColorAndRange.w;

        if (intensity <= 0.0001) continue;

        float3 L;
        float3 L_spec;
        float specEnergyScale = 1.0;
        float attenuation = 1.0;

        if (lightType < 0.5)
        {
            L = -normalize(lightDir);
            L_spec = L;
        }
        else if (lightType > 2.5)
        {
            float areaWidth = gLights[i].SpotAndAreaParams.z;
            float areaHeight = gLights[i].SpotAndAreaParams.w;

            float3 forward = normalize(lightDir);
            float3 rightBasis = gLights[i].AreaRightAndSoftness.xyz;
            if (dot(rightBasis, rightBasis) < 0.001)
            {
                float3 upFallback = abs(forward.y) > 0.9 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
                rightBasis = normalize(cross(forward, upFallback));
            }
            else
            {
                rightBasis = normalize(rightBasis);
            }
            float3 upBasis = normalize(cross(rightBasis, forward));

            float3 toPixel = worldPos - lightPos;
            float distForward = dot(toPixel, forward);
            if (distForward <= 0.0) continue;

            float distRight = dot(toPixel, rightBasis);
            float distUp = dot(toPixel, upBasis);
            float halfW = areaWidth * 0.5;
            float halfH = areaHeight * 0.5;

            float3 closestPoint = lightPos
                + rightBasis * clamp(distRight, -halfW, halfW)
                + upBasis * clamp(distUp, -halfH, halfH);

            float3 toLight = closestPoint - worldPos;
            float dist = length(toLight);
            L = toLight / max(dist, 0.001);

            float falloff = saturate(1.0 - dist / max(range, 0.001));
            attenuation = falloff * falloff;

            float emitCos = saturate(dot(-L, forward));
            attenuation *= lerp(0.25, 1.0, emitCos);

            // Karis Representative Point Specular on Area Light plane
            float3 Rray = reflect(-V, n);
            float denom = dot(Rray, forward);
            float tHit = (denom < -1e-4) ? ((dot(lightPos - worldPos, forward)) / denom) : -1.0;
            if (tHit > 0.0)
            {
                float3 hitPt = worldPos + Rray * tHit - lightPos;
                float3 specPt = lightPos
                    + rightBasis * clamp(dot(hitPt, rightBasis), -halfW, halfW)
                    + upBasis * clamp(dot(hitPt, upBasis), -halfH, halfH);
                float3 toSpec = specPt - worldPos;
                float distSpec = max(length(toSpec), 0.001);
                L_spec = toSpec / distSpec;
                float a = max(r * r, 0.0025);
                float coneRadius = max(areaWidth, areaHeight) * 0.25;
                float aPrime = saturate(a + coneRadius / (2.0 * distSpec));
                specEnergyScale = (a * a) / max(aPrime * aPrime, 1e-4);
            }
            else
            {
                L_spec = L;
            }
        }
        else
        {
            float3 toLight = lightPos - worldPos;
            float dist = length(toLight);
            if (dist >= range) continue;

            L = toLight / max(dist, 0.001);
            L_spec = L;
            float falloff = saturate(1.0 - dist / max(range, 0.001));
            attenuation = falloff * falloff;

            if (lightType > 1.5)
            {
                float cosInner = gLights[i].SpotAndAreaParams.x;
                float cosOuter = gLights[i].SpotAndAreaParams.y;
                float spotCos = dot(-L, normalize(lightDir));
                if (spotCos <= cosOuter) continue;
                float spotFade = saturate((spotCos - cosOuter) / max(cosInner - cosOuter, 0.001));
                attenuation *= spotFade;
            }
        }

        if (attenuation <= 0.0001) continue;
        float ndl = saturate(dot(n, L));
        if (ndl <= 0.0001 && (EnablePbr < 0.5 || lightType <= 2.5)) continue;

        float shadowNdl = max(ndl, 0.001);
        float lightShadow = GetLightShadowFactor(i, worldPos, n, shadowNdl, screenPos);
        float3 lightRad = lightColor * intensity * attenuation * lightShadow;

        if (EnablePbr >= 0.5)
        {
            float rSpec = lightType > 2.5 ? clamp(r + 0.08 * (1.0 - specEnergyScale), 0.06, 1.0) : r;
            float3 bDiff, bSpec;
            EvalPbrDirectSplit(albedoLinear, n, V, L, L_spec, m, rSpec, cc, dielectricF0, bDiff, bSpec);
            outDiff += lightRad * bDiff;
            outSpec += lightRad * bSpec;
        }
        else
        {
            outDiff += albedoLinear * lightRad * ndl;
        }
    }
}

// Dynamic light in Linear HDR PBR using unlimited D3D11 light sources + Shadows + IBL + Clearcoat
float3 CalcDynamicLgtPbrEx(
    float3 albedoLinear,
    float3 normal,
    float3 worldPos,
    float2 screenPos,
    float metallic,
    float roughness,
    float clearcoat)
{
    float3 d, s, f;
    CalcDynamicPbrPartsEx(albedoLinear, normal, worldPos, screenPos, metallic, roughness, clearcoat, 0.04, 0.0, d, s, f);
    return d + s;
}

float3 ComputeSolidRefractDir(float3 V, float3 N, float ior)
{
    float effIor = clamp(ior, 1.01, 3.0);
    float eta = 1.0 / effIor;
    float3 I = -V;
    float3 T1 = refract(I, N, eta);
    if (dot(T1, T1) < 1e-5) return reflect(I, N);
    T1 = normalize(T1);
    float3 Nexit = normalize(N - 2.0 * dot(N, T1) * T1);
    float3 T2 = refract(T1, -Nexit, effIor);
    if (dot(T2, T2) < 1e-5) return reflect(I, N);
    return normalize(T2);
}

void CalcGlassSurfaceLighting(
    float3 albedoLinear,
    float3 normal,
    float3 worldPos,
    float2 screenPos,
    float metallic,
    float roughness,
    float clearcoat,
    float ior,
    out float3 outSpecReflect,
    out float3 outDiffuseLit,
    out float3 outFresnel)
{
    float f0Sqrt = clamp((ior - 1.0) / (ior + 1.0), 0.0, 1.0);
    float dielectricF0 = f0Sqrt * f0Sqrt;
    if (UseSimpleLight > 0.5)
    {
        CalcSimpleLgtPbrSplit(albedoLinear, normal, worldPos, metallic, roughness, clearcoat, 0.75, dielectricF0, 1.0, outDiffuseLit, outSpecReflect, outFresnel);
    }
    else
    {
        CalcDynamicPbrPartsEx(albedoLinear, normal, worldPos, screenPos, metallic, roughness, clearcoat, dielectricF0, 1.0, outDiffuseLit, outSpecReflect, outFresnel);
    }
}

float3 CalcDynamicLgtPbr(
    float3 albedoLinear,
    float3 normal,
    float3 worldPos,
    float2 screenPos,
    float metallic,
    float roughness)
{
    return CalcDynamicLgtPbrEx(albedoLinear, normal, worldPos, screenPos, metallic, roughness, 0.0);
}

// Legacy compatibility wrappers (Linear sRGB-agnostic multipliers)
float CalcSimpleLgtEff(float3 normal, float lightIntensityParam)
{
    float3 lightDir = normalize(float3(0.3, -0.5, -1.0));
    float3 n = normalize(normal);
    float ndl = saturate(dot(n, -lightDir));
    return lerp(1.0, 0.5 + 0.5 * ndl, lightIntensityParam);
}

float3 CalcDynamicLgtEff(float3 normal, float3 worldPos, float2 screenPos)
{
    return CalcDynamicLgtPbr(float3(1.0, 1.0, 1.0), normal, worldPos, screenPos, 0.0, 0.85);
}

float3 CalcDynamicLgtEff(float3 normal, float3 worldPos)
{
    return CalcDynamicLgtEff(normal, worldPos, worldPos.xy);
}

// --- Extended Principled BSDF PBR (Thin Film, Coat IOR/Roughness, Anisotropy, Subsurface, Sheen, Oren-Nayar) ---

struct ExtendedPbrParams
{
    float  CoatWeight;
    float  CoatRoughness;
    float  CoatIor;
    float  DiffuseRoughness;
    float  ThinFilmThickness; // nm (0 = disabled)
    float  ThinFilmIor;
    float  Anisotropic;
    float  AnisoRotationRad;
    float  SubsurfaceWeight;
    float  SubsurfaceScale;
    float3 SubsurfaceColorLinear;
    float  SheenWeight;
    float  SheenRoughness;
    float3 SheenTintLinear;
    float  IsDiffuseOnly;
    float3 Tangent;
    float3 Bitangent;
};

void ComputeAnisoTangentFrame(float3 worldPos, float3 N, float2 uv, float rotRad, out float3 T, out float3 B)
{
    float3 n = normalize(N);
    float3 dp1 = ddx(worldPos);
    float3 dp2 = ddy(worldPos);
    float2 duv1 = ddx(uv);
    float2 duv2 = ddy(uv);

    float3 dp2perp = cross(dp2, n);
    float3 dp1perp = cross(n, dp1);
    float3 rawT = dp2perp * duv1.x + dp1perp * duv2.x;
    float lenSq = dot(rawT, rawT);
    if (lenSq > 1e-8)
    {
        rawT = normalize(rawT - n * dot(n, rawT));
    }
    else
    {
        float3 upRef = abs(n.y) > 0.9 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
        rawT = normalize(cross(upRef, n));
    }
    float3 rawB = normalize(cross(n, rawT));

    float s, c;
    sincos(rotRad, s, c);
    T = rawT * c + rawB * s;
    B = cross(n, T);
}

float3 EvalThinFilmIridescence(float cosTheta, float thicknessNm, float filmIor, float3 baseF0)
{
    float weight = saturate(thicknessNm / 45.0);
    if (weight <= 0.001) return FresnelSchlick(cosTheta, baseF0);

    float nFilm = clamp(filmIor, 1.01, 2.8);
    float sin2Theta = saturate(1.0 - cosTheta * cosTheta);
    float cosTheta2 = sqrt(max(1.0 - sin2Theta / (nFilm * nFilm), 0.001));
    float opd = 2.0 * nFilm * thicknessNm * cosTheta2;

    // Coherence attenuation as film thickness increases
    float damping = exp(-thicknessNm * 0.00055);
    float3 wavelengths = float3(650.0, 532.0, 460.0);
    float3 phase = (2.0 * PI_LGT * opd) / wavelengths + PI_LGT;
    float3 spectral = saturate(0.5 + 0.5 * cos(phase) * damping);

    float3 baseF = FresnelSchlick(cosTheta, baseF0);
    float r0 = (nFilm - 1.0) / (nFilm + 1.0);
    float3 filmF = saturate(spectral * (0.28 + 0.72 * baseF + r0 * r0));
    float3 boost = lerp(baseF, max(baseF, filmF), weight);
    return boost;
}

float D_GGX_Aniso(float ndh, float tdh, float bdh, float ax, float ay)
{
    float a2 = ax * ay;
    float3 v = float3(ay * tdh, ax * bdh, a2 * ndh);
    float v2 = dot(v, v);
    float w2 = a2 / max(v2, 1e-6);
    return a2 * w2 * w2 * (1.0 / PI_LGT);
}

float V_SmithGGXCorrelated_Aniso(float ndv, float ndl, float tdv, float bdv, float tdl, float bdl, float ax, float ay)
{
    float lambdaV = ndl * length(float3(ax * tdv, ay * bdv, ndv));
    float lambdaL = ndv * length(float3(ax * tdl, ay * bdl, ndl));
    return 0.5 / max(lambdaV + lambdaL, 1e-5);
}

float D_Charlie(float roughness, float ndh)
{
    float invR = 1.0 / max(roughness * roughness, 0.005);
    float cos2h = ndh * ndh;
    float sin2h = max(1.0 - cos2h, 0.005);
    return (2.0 + invR) * pow(sin2h, invR * 0.5) / (2.0 * PI_LGT);
}

float V_Ashikhmin(float ndv, float ndl)
{
    return 1.0 / max(4.0 * (ndl + ndv - ndl * ndv), 1e-4);
}

float EvalOrenNayar(float ndl, float ndv, float vdl, float rough)
{
    float s2 = rough * rough;
    float A = 1.0 - 0.5 * (s2 / (s2 + 0.33));
    float B = 0.45 * (s2 / (s2 + 0.09));
    float s = vdl - ndl * ndv;
    float t = (s > 0.0) ? max(ndl, ndv) : 1.0;
    return ndl * max(0.0, A + B * max(s, 0.0) / max(t, 1e-4));
}

float3 SafeNormalizeLgt(float3 v, float3 fallback)
{
    float lenSq = dot(v, v);
    return lenSq > 1e-8 ? (v * rsqrt(lenSq)) : fallback;
}

float3 ComputeAnisoBentNormal(float3 n, float3 T, float3 V, float aniso)
{
    float absA = saturate(abs(aniso) * 0.5);
    if (absA <= 0.001) return n;
    float3 c1 = cross(T, V);
    if (dot(c1, c1) < 1e-7) return n;
    float3 bent = cross(c1, T);
    if (dot(bent, bent) < 1e-7) return n;
    return SafeNormalizeLgt(lerp(n, normalize(bent), absA), n);
}

void EvalPbrDirectExtended(
    float3 albedoLinear,
    float3 N,
    float3 V,
    float3 L_diff,
    float3 L_spec,
    float metallic,
    float roughness,
    float dielectricF0,
    ExtendedPbrParams ext,
    out float3 outDiff,
    out float3 outSpec)
{
    float rawNdl = dot(N, L_diff);
    float ndl = saturate(rawNdl);
    float ndlSpec = saturate(dot(N, L_spec));
    float ndv = max(dot(N, V), 0.001);

    float3 H = normalize(V + L_spec);
    float ndh = saturate(dot(N, H));
    float vdh = saturate(dot(V, H));

    float3 F0 = lerp(float3(dielectricF0, dielectricF0, dielectricF0), albedoLinear, metallic);
    float3 F = (ext.ThinFilmThickness > 1.0)
        ? EvalThinFilmIridescence(vdh, ext.ThinFilmThickness, ext.ThinFilmIor, F0)
        : FresnelSchlick(vdh, F0);

    float3 spec;
    if (ext.IsDiffuseOnly > 0.5)
    {
        spec = float3(0.0, 0.0, 0.0);
    }
    else
    {
        float specTerm;
        if (abs(ext.Anisotropic) > 0.001)
        {
            float a = max(roughness * roughness, 0.004);
            float aspect = sqrt(1.0 - clamp(abs(ext.Anisotropic), 0.0, 0.96) * 0.9);
            float ax = max(0.0025, a / aspect);
            float ay = max(0.0025, a * aspect);
            float tdh = dot(ext.Tangent, H);
            float bdh = dot(ext.Bitangent, H);
            float tdv = dot(ext.Tangent, V);
            float bdv = dot(ext.Bitangent, V);
            float tdl = dot(ext.Tangent, L_spec);
            float bdl = dot(ext.Bitangent, L_spec);
            float D = D_GGX_Aniso(ndh, tdh, bdh, ax, ay);
            float Vis = V_SmithGGXCorrelated_Aniso(ndv, max(ndlSpec, 0.001), tdv, bdv, tdl, bdl, ax, ay);
            specTerm = D * Vis;
        }
        else
        {
            float D = DistributionGGX(ndh, roughness);
            float G = GeometrySmith(ndv, max(ndlSpec, 0.001), roughness);
            specTerm = (D * G) / max(4.0 * ndv * max(ndlSpec, 0.001), 1e-4);
        }

        float directMatteFade = lerp(1.0 - smoothstep(0.80, 1.0, roughness) * 0.75, 1.0, metallic);
        spec = min(specTerm * F * PI_LGT, float3(32.0, 32.0, 32.0)) * directMatteFade * ndlSpec;
    }

    float3 kD = (1.0 - saturate(F)) * (1.0 - metallic);
    float diffFactor = (ext.DiffuseRoughness > 0.001)
        ? EvalOrenNayar(ndl, ndv, dot(V, L_diff), ext.DiffuseRoughness)
        : ndl;
    outDiff = (kD * albedoLinear) * diffFactor;

    // Subsurface Scattering (Wrap Diffuse + Chromatic Terminator + Backlit View Translucency)
    if (ext.SubsurfaceWeight > 0.001)
    {
        float sssW = saturate(ext.SubsurfaceWeight) * (1.0 - metallic);
        float wrap = clamp(0.35 + ext.SubsurfaceScale * 0.45, 0.15, 1.0);
        float wrappedNdl = saturate((rawNdl + wrap) / ((1.0 + wrap) * (1.0 + wrap)));
        float shadowBand = saturate(wrappedNdl - ndl * 0.65);

        float3 Hback = SafeNormalizeLgt(L_diff + N * clamp(0.35 + ext.SubsurfaceScale * 0.25, 0.2, 0.85), L_diff);
        float vBack = pow(saturate(dot(V, -Hback)), lerp(4.0, 1.8, saturate(ext.SubsurfaceScale * 0.5)));

        float3 sssScatter = albedoLinear * ext.SubsurfaceColorLinear * (shadowBand * 0.75 + vBack * (0.45 + 0.55 * ext.SubsurfaceScale));
        outDiff = lerp(outDiff, outDiff * 0.55 + sssScatter, sssW);
    }

    // Velvet / Fabric Sheen Lobe
    if (ext.SheenWeight > 0.001 && ext.IsDiffuseOnly < 0.5)
    {
        float sheenR = clamp(ext.SheenRoughness, 0.05, 1.0);
        float Dsh = D_Charlie(sheenR, ndh);
        float Vsh = V_Ashikhmin(ndv, max(ndlSpec, 0.001));
        float3 sheenSpec = ext.SheenTintLinear * min(Dsh * Vsh * PI_LGT, 12.0) * ndlSpec * saturate(ext.SheenWeight);
        spec += sheenSpec;
    }

    outSpec = spec;

    // Extended Clearcoat with variable Roughness & IOR
    if (ext.CoatWeight > 0.001 && ext.IsDiffuseOnly < 0.5)
    {
        float cc = saturate(ext.CoatWeight);
        float ccRough = clamp(ext.CoatRoughness, 0.03, 1.0);
        float cSqrt = clamp((ext.CoatIor - 1.0) / (ext.CoatIor + 1.0), 0.0, 1.0);
        float coatF0 = cSqrt * cSqrt;
        float Fc = FresnelSchlick(vdh, float3(coatF0, coatF0, coatF0)).r * cc;
        float Dc = DistributionGGX(ndh, ccRough);
        float Gc = GeometrySmith(ndv, max(ndlSpec, 0.001), ccRough);
        float ccSpec = min((Dc * Gc * Fc) / max(4.0 * ndv * max(ndlSpec, 0.001), 1e-4) * PI_LGT, 32.0);
        outDiff *= (1.0 - Fc);
        outSpec = outSpec * (1.0 - Fc) + float3(ccSpec, ccSpec, ccSpec) * ndlSpec;
    }
}

void CalcSimpleLgtPbrExtended(
    float3 albedoLinear,
    float3 normal,
    float3 worldPos,
    float metallic,
    float roughness,
    float lightIntensityParam,
    float dielectricF0,
    ExtendedPbrParams ext,
    out float3 outDiff,
    out float3 outSpec)
{
    float3 n = normalize(normal);
    float m = saturate(metallic);
    float r = clamp(roughness >= 0.0 ? roughness : DefaultRoughness, 0.04, 1.0);

    float3 keyDir = -normalize(float3(0.3, -0.5, -1.0));
    float3 V = normalize(CameraWorldPosAndPageSize.xyz - worldPos);
    if (dot(V, V) < 1e-6) V = float3(0.0, 0.0, 1.0);

    if (EnablePbr < 0.5)
    {
        float ndlSimple = saturate(dot(n, keyDir));
        outDiff = albedoLinear * lerp(1.0, 0.5 + 0.5 * ndlSimple, lightIntensityParam);
        outSpec = float3(0.0, 0.0, 0.0);
        return;
    }

    float3 hemi = EvaluateDiffuseEnv(n);
    float ndv = max(dot(n, V), 0.001);

    float3 anisoDir = ext.Tangent;
    float3 bentN = ComputeAnisoBentNormal(n, anisoDir, V, ext.Anisotropic);
    float3 R = reflect(-V, bentN);

    float3 F0 = lerp(float3(dielectricF0, dielectricF0, dielectricF0), albedoLinear, m);
    float3 kS = (ext.ThinFilmThickness > 1.0)
        ? EvalThinFilmIridescence(ndv, ext.ThinFilmThickness, ext.ThinFilmIor, F0)
        : FresnelSchlickRoughness(ndv, F0, r);
    float3 kD = (1.0 - saturate(kS)) * (1.0 - m);
    float matteSpecFade = (ext.IsDiffuseOnly > 0.5) ? 0.0 : lerp(1.0 - smoothstep(0.75, 1.0, r) * 0.92, 1.0, m);

    float envReflScale = (EnvParams.z > 0.5) ? max(EnvParams.y, 0.0) : 1.0;
    float specIblScale = ((CamBasisX.w > 0.5) ? IblIntensity : (IblIntensity * lerp(0.75, 1.0, m))) * envReflScale;
    float3 envDiff = kD * albedoLinear * hemi * (0.45 + 0.40 * IblIntensity);
    if (ext.SubsurfaceWeight > 0.001)
    {
        float3 oppHemi = EvaluateDiffuseEnv(-n);
        envDiff += albedoLinear * ext.SubsurfaceColorLinear * oppHemi * saturate(ext.SubsurfaceWeight) * 0.38;
    }

    float3 envBrdf = EnvBRDFApprox(F0, r, ndv);
    if (ext.ThinFilmThickness > 1.0)
        envBrdf *= lerp(float3(1.0, 1.0, 1.0), kS / max(FresnelSchlick(ndv, F0), 0.04), 0.85);
    float3 envSpec = EvaluateEnv(R, r) * envBrdf * specIblScale * matteSpecFade;

    if (ext.SheenWeight > 0.001 && ext.IsDiffuseOnly < 0.5)
    {
        float sheenRim = pow(1.0 - ndv, lerp(5.0, 1.8, clamp(ext.SheenRoughness, 0.05, 1.0)));
        envDiff += ext.SheenTintLinear * saturate(ext.SheenWeight) * sheenRim * hemi * 0.65;
    }

    if (ext.CoatWeight > 0.001 && ext.IsDiffuseOnly < 0.5)
    {
        float cc = saturate(ext.CoatWeight);
        float ccRough = clamp(ext.CoatRoughness, 0.03, 1.0);
        float cSqrt = clamp((ext.CoatIor - 1.0) / (ext.CoatIor + 1.0), 0.0, 1.0);
        float coatF0 = cSqrt * cSqrt;
        float ccFres = FresnelSchlick(ndv, float3(coatF0, coatF0, coatF0)).r * cc;
        float3 ccEnv = EvaluateEnv(reflect(-V, n), ccRough) * EnvBRDFApprox(float3(coatF0, coatF0, coatF0), ccRough, ndv) * specIblScale * cc;
        envDiff *= (1.0 - ccFres);
        envSpec = envSpec * (1.0 - ccFres) + ccEnv;
    }

    float3 keyDiff, keySpec;
    EvalPbrDirectExtended(albedoLinear, n, V, keyDir, keyDir, m, r, dielectricF0, ext, keyDiff, keySpec);
    float3 rimDir = normalize(float3(-0.6, -0.3, 0.4));
    float rimNdl = saturate(dot(n, rimDir));
    float3 rimLight = (kD * albedoLinear) * float3(0.22, 0.25, 0.32) * rimNdl;

    float pbrWeight = (CamBasisX.w > 0.5) ? 1.0 : lerp(saturate(lightIntensityParam * 1.15), 1.0, m);
    bool hasCustomEnv = (CamBasisX.w > 0.5);
    float3 simpleDiff = hasCustomEnv
        ? (envDiff + keyDiff * 0.85 + rimLight)
        : (envDiff * 0.72 + keyDiff * 0.62 + rimLight * 0.50);
    float envSpecMult = hasCustomEnv ? 1.0 : lerp(0.42, 1.0, m);
    float keySpecMult = hasCustomEnv ? 0.85 : lerp(0.30, 0.85, m);
    outDiff = lerp(albedoLinear * (1.0 - m), simpleDiff, pbrWeight);
    outSpec = (envSpec * envSpecMult + keySpec * keySpecMult) * pbrWeight;
}

void CalcDynamicPbrPartsExtended(
    float3 albedoLinear,
    float3 normal,
    float3 worldPos,
    float2 screenPos,
    float metallic,
    float roughness,
    float dielectricF0,
    ExtendedPbrParams ext,
    out float3 outDirectDiff,
    out float3 outDirectSpec,
    out float3 outAmbient)
{
    float3 n = normalize(normal);
    float m = saturate(metallic);
    float r = clamp(roughness >= 0.0 ? roughness : DefaultRoughness, 0.04, 1.0);

    float3 V = normalize(CameraWorldPosAndPageSize.xyz - worldPos);
    if (dot(V, V) < 1e-6) V = float3(0.0, 0.0, 1.0);

    float3 hemi = EvaluateDiffuseEnv(n);
    outDirectDiff = float3(0.0, 0.0, 0.0);
    outDirectSpec = float3(0.0, 0.0, 0.0);
    outAmbient = float3(0.0, 0.0, 0.0);

    if (EnablePbr >= 0.5)
    {
        float ndv = max(dot(n, V), 0.001);
        float3 bentN = ComputeAnisoBentNormal(n, ext.Tangent, V, ext.Anisotropic);
        float3 R = reflect(-V, bentN);

        float3 F0 = lerp(float3(dielectricF0, dielectricF0, dielectricF0), albedoLinear, m);
        float3 kS = (ext.ThinFilmThickness > 1.0)
            ? EvalThinFilmIridescence(ndv, ext.ThinFilmThickness, ext.ThinFilmIor, F0)
            : FresnelSchlickRoughness(ndv, F0, r);
        float3 kD = (1.0 - saturate(kS)) * (1.0 - m);
        float matteSpecFade = (ext.IsDiffuseOnly > 0.5) ? 0.0 : lerp(1.0 - smoothstep(0.75, 1.0, r) * 0.92, 1.0, m);

        float envReflScale = (EnvParams.z > 0.5) ? max(EnvParams.y, 0.0) : 1.0;
        float specIblScale = ((CamBasisX.w > 0.5)
            ? IblIntensity
            : (IblIntensity * lerp(max(AmbientIntensity, 0.2), 0.72, m))) * envReflScale;
        float3 envDiff = kD * albedoLinear * hemi * AmbientIntensity;

        if (ext.SubsurfaceWeight > 0.001)
        {
            float3 oppHemi = EvaluateDiffuseEnv(-n);
            envDiff += albedoLinear * ext.SubsurfaceColorLinear * oppHemi * saturate(ext.SubsurfaceWeight) * AmbientIntensity * 0.55;
        }

        float3 envBrdf = EnvBRDFApprox(F0, r, ndv);
        if (ext.ThinFilmThickness > 1.0)
            envBrdf *= lerp(float3(1.0, 1.0, 1.0), kS / max(FresnelSchlick(ndv, F0), 0.04), 0.85);
        float3 envSpec = EvaluateEnv(R, r) * envBrdf * specIblScale * matteSpecFade;

        if (ext.SheenWeight > 0.001 && ext.IsDiffuseOnly < 0.5)
        {
            float sheenRim = pow(1.0 - ndv, lerp(5.0, 1.8, clamp(ext.SheenRoughness, 0.05, 1.0)));
            envDiff += ext.SheenTintLinear * saturate(ext.SheenWeight) * sheenRim * hemi * max(AmbientIntensity, 0.25) * 0.8;
        }

        if (ext.CoatWeight > 0.001 && ext.IsDiffuseOnly < 0.5)
        {
            float cc = saturate(ext.CoatWeight);
            float ccRough = clamp(ext.CoatRoughness, 0.03, 1.0);
            float cSqrt = clamp((ext.CoatIor - 1.0) / (ext.CoatIor + 1.0), 0.0, 1.0);
            float coatF0 = cSqrt * cSqrt;
            float ccFres = FresnelSchlick(ndv, float3(coatF0, coatF0, coatF0)).r * cc;
            float3 ccEnv = EvaluateEnv(reflect(-V, n), ccRough) * EnvBRDFApprox(float3(coatF0, coatF0, coatF0), ccRough, ndv) * specIblScale * cc;
            envDiff *= (1.0 - ccFres);
            envSpec = envSpec * (1.0 - ccFres) + ccEnv;
        }
        outAmbient = envDiff + envSpec;
    }
    else
    {
        outAmbient = albedoLinear * hemi * AmbientIntensity;
    }

    [loop]
    for (int i = 0; i < LightCount; i++)
    {
        float lightType = gLights[i].PositionAndType.w;
        float3 lightPos = gLights[i].PositionAndType.xyz;
        float3 lightDir = gLights[i].DirectionAndIntensity.xyz;
        float  intensity = gLights[i].DirectionAndIntensity.w;
        float3 lightColor = gLights[i].ColorAndRange.xyz;
        float  range = gLights[i].ColorAndRange.w;

        if (intensity <= 0.0001) continue;

        float3 L;
        float3 L_spec;
        float specEnergyScale = 1.0;
        float attenuation = 1.0;

        if (lightType < 0.5)
        {
            L = -normalize(lightDir);
            L_spec = L;
        }
        else if (lightType > 2.5)
        {
            float areaWidth = gLights[i].SpotAndAreaParams.z;
            float areaHeight = gLights[i].SpotAndAreaParams.w;

            float3 forward = normalize(lightDir);
            float3 rightBasis = gLights[i].AreaRightAndSoftness.xyz;
            if (dot(rightBasis, rightBasis) < 0.001)
            {
                float3 upFallback = abs(forward.y) > 0.9 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
                rightBasis = normalize(cross(forward, upFallback));
            }
            else
            {
                rightBasis = normalize(rightBasis);
            }
            float3 upBasis = normalize(cross(rightBasis, forward));

            float3 toPixel = worldPos - lightPos;
            float distForward = dot(toPixel, forward);
            if (distForward <= 0.0) continue;

            float distRight = dot(toPixel, rightBasis);
            float distUp = dot(toPixel, upBasis);
            float halfW = areaWidth * 0.5;
            float halfH = areaHeight * 0.5;

            float3 closestPoint = lightPos
                + rightBasis * clamp(distRight, -halfW, halfW)
                + upBasis * clamp(distUp, -halfH, halfH);

            float3 toLight = closestPoint - worldPos;
            float dist = length(toLight);
            L = toLight / max(dist, 0.001);

            float falloff = saturate(1.0 - dist / max(range, 0.001));
            attenuation = falloff * falloff;

            float emitCos = saturate(dot(-L, forward));
            attenuation *= lerp(0.25, 1.0, emitCos);

            float3 Rray = reflect(-V, n);
            float denom = dot(Rray, forward);
            float tHit = (denom < -1e-4) ? ((dot(lightPos - worldPos, forward)) / denom) : -1.0;
            if (tHit > 0.0)
            {
                float3 hitPt = worldPos + Rray * tHit - lightPos;
                float3 specPt = lightPos
                    + rightBasis * clamp(dot(hitPt, rightBasis), -halfW, halfW)
                    + upBasis * clamp(dot(hitPt, upBasis), -halfH, halfH);
                float3 toSpec = specPt - worldPos;
                float distSpec = max(length(toSpec), 0.001);
                L_spec = toSpec / distSpec;
                float a = max(r * r, 0.0025);
                float coneRadius = max(areaWidth, areaHeight) * 0.25;
                float aPrime = saturate(a + coneRadius / (2.0 * distSpec));
                specEnergyScale = (a * a) / max(aPrime * aPrime, 1e-4);
            }
            else
            {
                L_spec = L;
            }
        }
        else
        {
            float3 toLight = lightPos - worldPos;
            float dist = length(toLight);
            if (dist >= range) continue;

            L = toLight / max(dist, 0.001);
            L_spec = L;
            float falloff = saturate(1.0 - dist / max(range, 0.001));
            attenuation = falloff * falloff;

            if (lightType > 1.5)
            {
                float cosInner = gLights[i].SpotAndAreaParams.x;
                float cosOuter = gLights[i].SpotAndAreaParams.y;
                float spotCos = dot(-L, normalize(lightDir));
                if (spotCos <= cosOuter) continue;
                float spotFade = saturate((spotCos - cosOuter) / max(cosInner - cosOuter, 0.001));
                attenuation *= spotFade;
            }
        }

        if (attenuation <= 0.0001) continue;
        float ndl = saturate(dot(n, L));
        if (ndl <= 0.0001 && ext.SubsurfaceWeight <= 0.001 && (EnablePbr < 0.5 || lightType <= 2.5)) continue;

        float shadowNdl = max(ndl, 0.001);
        float lightShadow = GetLightShadowFactor(i, worldPos, n, shadowNdl, screenPos);
        float3 lightRad = lightColor * intensity * attenuation * lightShadow;

        if (EnablePbr >= 0.5)
        {
            float rSpec = lightType > 2.5 ? clamp(r + 0.08 * (1.0 - specEnergyScale), 0.06, 1.0) : r;
            float3 bDiff, bSpec;
            EvalPbrDirectExtended(albedoLinear, n, V, L, L_spec, m, rSpec, dielectricF0, ext, bDiff, bSpec);
            outDirectDiff += lightRad * bDiff;
            outDirectSpec += lightRad * bSpec;
        }
        else
        {
            outDirectDiff += albedoLinear * lightRad * ndl;
        }
    }
}
";
}
