namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// D3Dエフェクト用の共通ライティングHLSLコード。
/// </summary>
internal static class LightingShaderCode
{
    public const string HlslCode = @"
// --- Constant Buffer for Light (b1) ---
#define MAX_LIGHTS 8

struct LightData
{
    float4 PositionAndType;
    float4 DirectionAndIntensity;
    float4 ColorAndRange;
    float4 SpotParams;
};

struct ShadowData
{
    row_major float4x4 LightViewProj0;
    float4 ShadowParams;
    float4 AtlasParams;
    float4 DepthParams;
};

cbuffer CbLgt : register(b1)
{
    int   LightCount;
    float UseSimpleLight;
    float EnableShadow;
    float AmbientIntensity;
    float4 AmbientColor;
    
    ShadowData Shadows[8]; 
    
    int ShadowCount;
    float EnableSoftShadow; 
    float2 _padShadow;      
    
    LightData Lights[MAX_LIGHTS];
};

Texture2D shadowAtlasTex : register(t2);
SamplerState shadowAtlasSampler : register(s1);

float2 GetAtlasUV(int tileIdx, float2 localUV, float scale, float texelSize)
{
    int tileX = tileIdx % 8;
    int tileY = tileIdx / 8;
    float2 tileOffset = float2(tileX, tileY);
    
    float padding = 1.5 * texelSize;
    float2 guardedUV = lerp(padding, scale - padding, saturate(localUV));
    
    return (tileOffset + guardedUV) * float2(0.125, 0.16666667);
}

float SampleAtlas2D_PCF(int startTile, float2 localUV, float currentDepth, float bias, float texelSize, float scale)
{
    if (EnableSoftShadow < 0.5)
    {
        float2 atlasUV = GetAtlasUV(startTile, localUV, scale, texelSize);
        float depth = shadowAtlasTex.SampleLevel(shadowAtlasSampler, atlasUV, 0).r;
        return (currentDepth - bias < depth) ? 1.0 : 0.0;
    }

    float shadow = 0.0;
    [unroll]
    for (int x = -1; x <= 1; x++)
    {
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            float2 offset = float2(x, y) * texelSize;
            float2 atlasUV = GetAtlasUV(startTile, localUV + offset, scale, texelSize);
            float depth = shadowAtlasTex.SampleLevel(shadowAtlasSampler, atlasUV, 0).r;
            shadow += (currentDepth - bias < depth) ? 1.0 : 0.0;
        }
    }
    return shadow / 9.0;
}

float SampleAtlas2D_PCF_Scaled(int startTile, float2 localUV, float currentDepth, float bias, float texelSize, float scale, float kernelScale)
{
    if (EnableSoftShadow < 0.5)
    {
        float2 atlasUV = GetAtlasUV(startTile, localUV, scale, texelSize);
        float depth = shadowAtlasTex.SampleLevel(shadowAtlasSampler, atlasUV, 0).r;
        return (currentDepth - bias < depth) ? 1.0 : 0.0;
    }

    float effectiveTexel = texelSize * max(kernelScale, 1.0);
    effectiveTexel = min(effectiveTexel, scale * 0.175);

    float adaptiveBias = bias * max(sqrt(kernelScale), 1.0);

    float shadow = 0.0;
    [unroll]
    for (int x = -2; x <= 2; x++)
    {
        [unroll]
        for (int y = -2; y <= 2; y++)
        {
            float2 offset = float2(x, y) * effectiveTexel;
            float2 atlasUV = GetAtlasUV(startTile, localUV + offset, scale, texelSize);
            float depth = shadowAtlasTex.SampleLevel(shadowAtlasSampler, atlasUV, 0).r;
            shadow += (currentDepth - adaptiveBias < depth) ? 1.0 : 0.0;
        }
    }
    return shadow / 25.0;
}

float SampleAtlasCube_PCF(int startTile, float3 dir, float currentDepth, float bias, float texelSize, float scale)
{
    float3 absDir = abs(dir);
    float maxAxis = max(absDir.x, max(absDir.y, absDir.z));
    float u = 0.0;
    float v = 0.0;
    int faceIdx = 0;

    if (maxAxis == absDir.x) {
        if (dir.x > 0.0) { u = -dir.z; v = -dir.y; faceIdx = 0; }
        else             { u =  dir.z; v = -dir.y; faceIdx = 1; }
    } else if (maxAxis == absDir.y) {
        if (dir.y > 0.0) { u =  dir.x; v =  dir.z; faceIdx = 2; }
        else             { u =  dir.x; v = -dir.z; faceIdx = 3; }
    } else {
        if (dir.z > 0.0) { u =  dir.x; v = -dir.y; faceIdx = 4; }
        else             { u = -dir.x; v = -dir.y; faceIdx = 5; }
    }

    float2 localUV = float2(u / maxAxis, v / maxAxis) * 0.5 + 0.5;

    if (EnableSoftShadow < 0.5)
    {
        float2 atlasUV = GetAtlasUV(startTile + faceIdx, localUV, scale, texelSize);
        float depth = shadowAtlasTex.SampleLevel(shadowAtlasSampler, atlasUV, 0).r;
        return (currentDepth - bias < depth) ? 1.0 : 0.0;
    }

    float shadow = 0.0;
    [unroll]
    for (int x = -1; x <= 1; x++)
    {
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            float2 offset = float2(x, y) * texelSize;
            float2 atlasUV = GetAtlasUV(startTile + faceIdx, localUV + offset, scale, texelSize);
            float depth = shadowAtlasTex.SampleLevel(shadowAtlasSampler, atlasUV, 0).r;
            shadow += (currentDepth - bias < depth) ? 1.0 : 0.0;
        }
    }
    return shadow / 9.0;
}

static const float2 PCSS_PoissonDisk[16] =
{
    float2(-0.94201624, -0.39906216),
    float2( 0.94558609, -0.76890725),
    float2(-0.09418410, -0.92938870),
    float2( 0.34495938,  0.29387760),
    float2(-0.91588581,  0.45771432),
    float2(-0.81544232, -0.87912464),
    float2(-0.38277543,  0.27676845),
    float2( 0.97484398,  0.75648379),
    float2( 0.44323325, -0.97511554),
    float2( 0.53742981, -0.47373420),
    float2(-0.26496911, -0.41893023),
    float2( 0.79197514,  0.19090188),
    float2(-0.24188840,  0.99706507),
    float2(-0.81409955,  0.91437590),
    float2( 0.19984126,  0.78641367),
    float2( 0.14383161, -0.14100790)
};

float LinearizeDepth(float ndcDepth, float nearPlane, float farPlane)
{
    return (nearPlane * farPlane) / (farPlane - ndcDepth * (farPlane - nearPlane));
}

float SampleAreaShadow(ShadowData sd, float3 worldPos, float bias, float texelSize, float scale, float lightSize)
{
    int startTile = int(sd.AtlasParams.x);

    float4 shadowPos = mul(float4(worldPos, 1.0), sd.LightViewProj0);
    shadowPos.xyz /= shadowPos.w;

    float2 shadowUV = shadowPos.xy * 0.5 + 0.5;
    shadowUV.y = 1.0 - shadowUV.y;

    float currentDepth = shadowPos.z;

    if (shadowUV.x < 0.0 || shadowUV.x > 1.0 || shadowUV.y < 0.0 || shadowUV.y > 1.0 ||
        currentDepth < 0.0 || currentDepth > 1.0)
    {
        return 1.0;
    }

    if (EnableSoftShadow < 0.5)
    {
        float2 atlasUV = GetAtlasUV(startTile, shadowUV, scale, texelSize);
        float depth = shadowAtlasTex.SampleLevel(shadowAtlasSampler, atlasUV, 0).r;
        return (currentDepth - bias < depth) ? 1.0 : 0.0;
    }

    float searchRadiusUV = saturate(lightSize / 1200.0) * 0.22 + 0.012;
    searchRadiusUV = min(searchRadiusUV, scale * 0.35);

    float avgBlockerDepth = 0.0;
    float blockerCount = 0.0;

    float searchBias = bias * 4.0;

    [unroll]
    for (int i = 0; i < 16; i++)
    {
        float2 offset = PCSS_PoissonDisk[i] * searchRadiusUV;
        float2 atlasUV = GetAtlasUV(startTile, shadowUV + offset, scale, texelSize);
        float sampleDepth = shadowAtlasTex.SampleLevel(shadowAtlasSampler, atlasUV, 0).r;
        if (sampleDepth < currentDepth - searchBias)
        {
            avgBlockerDepth += sampleDepth;
            blockerCount += 1.0;
        }
    }

    if (blockerCount < 0.5)
    {
        return 1.0;
    }

    avgBlockerDepth /= blockerCount;

    float nearPlane = sd.DepthParams.x;
    float farPlane = sd.DepthParams.y;
    float receiverLinear = LinearizeDepth(currentDepth, nearPlane, farPlane);
    float blockerLinear = LinearizeDepth(avgBlockerDepth, nearPlane, farPlane);

    float penumbraRatio = saturate((receiverLinear - blockerLinear) / max(blockerLinear, 1.0));
    float sizeFactor = saturate(lightSize / 600.0);
    float kernelScale = 1.0 + penumbraRatio * sizeFactor * 24.0;

    return SampleAtlas2D_PCF_Scaled(startTile, shadowUV, currentDepth, bias, texelSize, scale, kernelScale);
}

float GetLightShadowFactor(int lightIdx, float3 worldPos)
{
    if (EnableShadow < 0.5) return 1.0;

    for (int s = 0; s < ShadowCount && s < 8; s++)
    {
        float shadowLightIdx = Shadows[s].ShadowParams.z;
        if (lightIdx == int(shadowLightIdx))
        {
            float bias = Shadows[s].ShadowParams.x;
            float intensity = Shadows[s].ShadowParams.y;
            float shadowMode = Shadows[s].ShadowParams.w;
            int startTile = int(Shadows[s].AtlasParams.x);
            float targetRes = Shadows[s].AtlasParams.y;
            float scale = Shadows[s].AtlasParams.z;

            if (startTile < 0) return 1.0; 

            float3 lightPos = Lights[lightIdx].PositionAndType.xyz;
            float lightRange = Lights[lightIdx].ColorAndRange.w;
            float texelSize = 1.0 / targetRes;

            if (shadowMode > 1.5)
            {
                float lightSize = Shadows[s].AtlasParams.w;
                float shadow = SampleAreaShadow(Shadows[s], worldPos, bias, texelSize, scale, lightSize);
                return lerp(1.0 - intensity, 1.0, shadow);
            }
            else if (shadowMode > 0.5)
            {
                float3 toPixel = worldPos - lightPos;
                float dist = length(toPixel);
                float3 dir = toPixel / max(dist, 0.001);

                float3 sampleDir = float3(dir.x, -dir.y, -dir.z);
                float shadowDepth = SampleAtlasCube_PCF(startTile, sampleDir, dist / max(lightRange, 1.0), bias, texelSize, scale);
                return lerp(1.0 - intensity, 1.0, shadowDepth);
            }
            else
            {
                float4 shadowPos = mul(float4(worldPos, 1.0), Shadows[s].LightViewProj0);
                shadowPos.xyz /= shadowPos.w;

                float2 shadowUV = shadowPos.xy * 0.5 + 0.5;
                shadowUV.y = 1.0 - shadowUV.y;

                if (shadowUV.x < 0.0 || shadowUV.x > 1.0 || shadowUV.y < 0.0 || shadowUV.y > 1.0)
                    return 1.0;

                float currentDepth = shadowPos.z;
                if (currentDepth < 0.0 || currentDepth > 1.0)
                    return 1.0;

                float shadow = SampleAtlas2D_PCF(startTile, shadowUV, currentDepth, bias, texelSize, scale);
                return lerp(1.0 - intensity, 1.0, shadow);
            }
        }
    }

    return 1.0;
}

// Simple light (fallback)
float CalcSimpleLgtEff(float3 normal, float lightIntensityParam)
{
    float3 lightDir = normalize(float3(0.3, -0.5, -1.0));
    float3 n = normalize(normal);
    float ndl = saturate(dot(n, -lightDir));
    return lerp(1.0, 0.5 + 0.5 * ndl, lightIntensityParam);
}

// Dynamic light using D3D11 light sources
float3 CalcDynamicLgtEff(float3 normal, float3 worldPos)
{
    float3 result = AmbientColor.rgb * AmbientIntensity;

    for (int i = 0; i < LightCount && i < MAX_LIGHTS; i++)
    {
        float lightType = Lights[i].PositionAndType.w;
        float3 lightPos = Lights[i].PositionAndType.xyz;
        float3 lightDir = Lights[i].DirectionAndIntensity.xyz;
        float  intensity = Lights[i].DirectionAndIntensity.w;
        float3 lightColor = Lights[i].ColorAndRange.xyz;
        float  range = Lights[i].ColorAndRange.w;
        float  cosInner = Lights[i].SpotParams.x;
        float  cosOuter = Lights[i].SpotParams.y;

        float3 L;
        float attenuation = 1.0;

        if (lightType < 0.5)
        {
            L = -normalize(lightDir);
        }
        else
        {
            if (lightType > 2.5)
            {
                float areaWidth = Lights[i].SpotParams.z;
                float areaHeight = Lights[i].SpotParams.w;

                float3 forward = normalize(lightDir);
                float3 upBasis = float3(0.0, 1.0, 0.0);
                if (abs(dot(forward, upBasis)) > 0.9)
                {
                    upBasis = float3(0.0, 0.0, 1.0);
                }
                float3 rightBasis = normalize(cross(forward, upBasis));
                upBasis = normalize(cross(rightBasis, forward));

                float3 toPixel = worldPos - lightPos;
                float distRight = dot(toPixel, rightBasis);
                float distUp = dot(toPixel, upBasis);
                float distForward = dot(toPixel, forward);

                float halfW = areaWidth * 0.5;
                float halfH = areaHeight * 0.5;

                float clampedRight = clamp(distRight, -halfW, halfW);
                float clampedUp = clamp(distUp, -halfH, halfH);

                float3 closestPoint = lightPos + rightBasis * clampedRight + upBasis * clampedUp;

                float3 toLight = closestPoint - worldPos;
                float dist = length(toLight);
                L = toLight / max(dist, 0.001);

                float falloff = saturate(1.0 - dist / max(range, 0.001));
                attenuation = falloff * falloff;

                float transitionRange = max(halfW, halfH) * 0.1 + 8.0;
                float emissionCos = smoothstep(-transitionRange, transitionRange, distForward);
                attenuation *= emissionCos;
            }
            else // Point Light / Spot Light
            {
                float3 toLight = lightPos - worldPos;
                float dist = length(toLight);
                L = toLight / max(dist, 0.001);
                
                float falloff = saturate(1.0 - dist / max(range, 0.001));
                attenuation = falloff * falloff;

                if (lightType > 1.5) // Spot Light (1.5 < lightType <= 2.5)
                {
                    float spotCos = dot(-L, normalize(lightDir));
                    float spotFade = saturate((spotCos - cosOuter) / max(cosInner - cosOuter, 0.001));
                    attenuation *= spotFade;
                }
            }
        }

        float ndl = saturate(dot(normalize(normal), L));
        
        float lightShadow = GetLightShadowFactor(i, worldPos);
        
        result += lightColor * intensity * ndl * attenuation * lightShadow;
    }

    return result;
}
";
}