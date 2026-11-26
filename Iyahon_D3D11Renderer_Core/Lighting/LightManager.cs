using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

internal sealed class ShadowRenderJob
{
    public int LightIndex { get; init; }
    public int ShadowEntryIndex { get; init; }
    public ShadowTileAllocation Tile { get; init; }
    public Matrix4x4 LightViewProj { get; init; }
    public Vector3 ShadowCameraPos { get; init; }
    public bool IsOrthographic { get; init; }
    public FrustumPlanes Frustum { get; init; }
}

internal sealed class LightingFramePlan
{
    public CbLightingHeader Header { get; init; }
    public GpuLightData[] Lights { get; init; } = Array.Empty<GpuLightData>();
    public GpuShadowEntry[] ShadowEntries { get; init; } = Array.Empty<GpuShadowEntry>();
    public List<ShadowRenderJob> ShadowJobs { get; init; } = new();
    public int RequiredPages { get; init; }
    public int PageSize { get; init; }
}

internal static class LightManager
{
    private readonly struct ShadowRequest
    {
        public int LightIndex { get; init; }
        public int ShadowMode { get; init; }
        public int EntryCount { get; init; }
        public int DesiredTier { get; init; }
        public int Priority { get; init; }
        public Vector3 CameraPos { get; init; }
        public float NearPlane { get; init; }
        public float FarPlane { get; init; }
        public float TanHalfFov { get; init; }
        public float ExtraScalar { get; init; }
        public float[] CascadeNear { get; init; }
        public float[] CascadeFar { get; init; }
    }

    public static (Vector3 forward, Vector3 right, Vector3 up) ComputeAreaBasis(LightData light)
    {
        Vector3 forward = light.Direction.LengthSquared() > 1e-6f
            ? Vector3.Normalize(light.Direction)
            : new Vector3(0f, -1f, 0f);

        Vector3 rightBasis;
        if (light.HasExplicitAreaRight)
        {
            Vector3 proj = light.AreaRight - forward * Vector3.Dot(light.AreaRight, forward);
            if (proj.LengthSquared() > 1e-6f)
            {
                rightBasis = Vector3.Normalize(proj);
                Vector3 upExplicit = Vector3.Normalize(Vector3.Cross(rightBasis, forward));
                return (forward, rightBasis, upExplicit);
            }
        }

        Vector3 upBasis = MathF.Abs(forward.Y) > 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        rightBasis = Vector3.Normalize(Vector3.Cross(forward, upBasis));
        upBasis = Vector3.Normalize(Vector3.Cross(rightBasis, forward));
        return (forward, rightBasis, upBasis);
    }

    public static float ComputeAreaLightSize(LightData light)
    {
        float halfW = light.AreaWidth * 0.5f;
        float halfH = light.AreaHeight * 0.5f;
        return MathF.Sqrt(halfW * halfW + halfH * halfH);
    }

    public static LightingFramePlan BuildFramePlan(
        IReadOnlyList<LightData> activeLights,
        IReadOnlyList<PreparedRenderItem> preparedItems,
        int screenWidth,
        int screenHeight)
    {
        return BuildFramePlan(activeLights, preparedItems, screenWidth, screenHeight, default, Matrix4x4.Identity);
    }

    public static LightingFramePlan BuildFramePlan(
        IReadOnlyList<LightData> activeLights,
        IReadOnlyList<PreparedRenderItem> preparedItems,
        int screenWidth,
        int screenHeight,
        in EnvironmentParameters environment,
        in Matrix4x4 fallbackCameraMatrix)
    {
        var settings = D3D11RendererSettings.Default;
        int pageSize = (int)settings.ShadowResolution;
        long maxVramBytes = (long)settings.ShadowMemoryBudget * 1024L * 1024L;

        // 1. カメラ情報の正確な抽出 (YMM4 カメラ位置: ビュー空間 (0,0,+1000) が視点、+Z方向がカメラ手前)
        Matrix4x4 cameraMatrix = preparedItems.Count > 0
            ? preparedItems[0].Item.DrawDescription.Camera
            : (fallbackCameraMatrix.M44 != 0f ? fallbackCameraMatrix : Matrix4x4.Identity);
        if (!Matrix4x4.Invert(cameraMatrix, out var invCam))
        {
            invCam = Matrix4x4.Identity;
        }

        Vector3 camPosWorld = Vector3.Transform(new Vector3(0f, 0f, 1000f), invCam);
        Vector3 camLookAtWorld = Vector3.Transform(Vector3.Zero, invCam);
        Vector3 camForwardWorld = camLookAtWorld - camPosWorld;
        camForwardWorld = camForwardWorld.LengthSquared() > 1e-6f
            ? Vector3.Normalize(camForwardWorld)
            : new Vector3(0f, 0f, -1f);

        // 2. 全アイテムのワールドAABBとカメラ視点からの深度範囲を計算
        ComputeSceneBounds(preparedItems, camPosWorld, camForwardWorld,
            out Vector3 sceneMin, out Vector3 sceneMax,
            out float sceneNearDist, out float sceneFarDist);

        int lightCount = activeLights.Count;
        var gpuLights = new GpuLightData[lightCount];

        // 3. 影を必要とする光源のタイルリクエストを収集
        var requests = new List<ShadowRequest>();
        var flatTiers = new List<int>();
        var flatPriorities = new List<int>();

        bool shadowsEnabled = settings.EnableShadow && lightCount > 0 && preparedItems.Count > 0;
        int requestedCascades = (int)settings.ShadowCascadeCount;

        for (int i = 0; i < lightCount; i++)
        {
            var light = activeLights[i];
            var (_, areaRight, _) = ComputeAreaBasis(light);

            gpuLights[i] = GpuLightData.FromLightData(
                light,
                areaRight,
                shadowStartIndex: -1,
                shadowCount: 0,
                shadowMode: 0);

            if (!shadowsEnabled || !light.CastShadow || light.ShadowIntensity <= 0.0001f || light.Intensity <= 0.0001f)
            {
                continue;
            }

            if (light.Type == LightType.Directional)
            {
                float depthSpan = sceneFarDist - sceneNearDist;
                int cascadeCount = depthSpan < 800f
                    ? 1
                    : (depthSpan < 1500f ? Math.Min(requestedCascades, 2) : requestedCascades);

                ComputeCascadeSplits(sceneNearDist, sceneFarDist, cascadeCount, out var cNear, out var cFar);

                requests.Add(new ShadowRequest
                {
                    LightIndex = i,
                    ShadowMode = 3,
                    EntryCount = cascadeCount,
                    DesiredTier = 0,
                    Priority = 100,
                    CascadeNear = cNear,
                    CascadeFar = cFar,
                });

                for (int c = 0; c < cascadeCount; c++)
                {
                    flatTiers.Add(0);
                    flatPriorities.Add(100 - c * 5);
                }
            }
            else if (light.Type == LightType.Point)
            {
                float range = MathF.Max(50f, light.Range);
                if (!FrustumPlanes.IntersectsAabb(sceneMin, sceneMax, new BoundingSphere3D(light.Position, range)))
                    continue;

                float dist = MathF.Min(
                    Vector3.Distance(camPosWorld, light.Position),
                    Vector3.Distance(camLookAtWorld, light.Position));

                int tier = dist <= 4000f ? 1 : (dist <= 8000f ? 2 : 3);
                int prio = dist <= 4000f ? 50 : (dist <= 8000f ? 30 : 10);
                float nearP = Math.Clamp(range * 0.005f, 10f, 50f);

                requests.Add(new ShadowRequest
                {
                    LightIndex = i,
                    ShadowMode = 1,
                    EntryCount = 6,
                    DesiredTier = tier,
                    Priority = prio,
                    CameraPos = light.Position,
                    NearPlane = nearP,
                    FarPlane = range,
                    TanHalfFov = 1.0f,
                });

                for (int f = 0; f < 6; f++)
                {
                    flatTiers.Add(tier);
                    flatPriorities.Add(prio);
                }
            }
            else if (light.Type == LightType.Area)
            {
                float areaSize = ComputeAreaLightSize(light);
                float range = MathF.Max(100f, light.Range);
                if (!FrustumPlanes.IntersectsAabb(sceneMin, sceneMax, new BoundingSphere3D(light.Position, range + areaSize)))
                    continue;

                float dist = MathF.Min(
                    Vector3.Distance(camPosWorld, light.Position),
                    Vector3.Distance(camLookAtWorld, light.Position));

                int tier = dist <= 3500f ? 0 : (dist <= 7000f ? 1 : 2);
                int prio = dist <= 3500f ? 60 : (dist <= 7000f ? 40 : 20);

                float fovRad = 130f * MathF.PI / 180f;
                float tanHalfFov = MathF.Tan(fovRad * 0.5f);
                float halfW = MathF.Max(1f, light.AreaWidth * 0.5f);
                float halfH = MathF.Max(1f, light.AreaHeight * 0.5f);
                float backOffset = MathF.Max(MathF.Max(halfW, halfH) / tanHalfFov, 15f);
                float nearP = backOffset;
                float farP = backOffset + range;

                requests.Add(new ShadowRequest
                {
                    LightIndex = i,
                    ShadowMode = 2,
                    EntryCount = 1,
                    DesiredTier = tier,
                    Priority = prio,
                    CameraPos = light.Position - (light.Direction.LengthSquared() > 1e-6f ? Vector3.Normalize(light.Direction) : new Vector3(0, -1, 0)) * backOffset,
                    NearPlane = nearP,
                    FarPlane = farP,
                    TanHalfFov = tanHalfFov,
                    ExtraScalar = areaSize,
                });

                flatTiers.Add(tier);
                flatPriorities.Add(prio);
            }
            else // Spot
            {
                float range = MathF.Max(50f, light.Range);
                if (!FrustumPlanes.IntersectsAabb(sceneMin, sceneMax, new BoundingSphere3D(light.Position, range)))
                    continue;

                float dist = MathF.Min(
                    Vector3.Distance(camPosWorld, light.Position),
                    Vector3.Distance(camLookAtWorld, light.Position));

                int tier = dist <= 3500f ? 0 : (dist <= 7000f ? 1 : 2);
                int prio = dist <= 3500f ? 60 : (dist <= 7000f ? 40 : 20);

                float outerDeg = Math.Clamp(light.SpotOuterAngle, 1f, 89f);
                float fovRad = outerDeg * 2f * (MathF.PI / 180f);
                float tanHalfFov = MathF.Tan(fovRad * 0.5f);
                float nearP = Math.Clamp(range * 0.005f, 10f, 50f);

                requests.Add(new ShadowRequest
                {
                    LightIndex = i,
                    ShadowMode = 0,
                    EntryCount = 1,
                    DesiredTier = tier,
                    Priority = prio,
                    CameraPos = light.Position,
                    NearPlane = nearP,
                    FarPlane = range,
                    TanHalfFov = tanHalfFov,
                });

                flatTiers.Add(tier);
                flatPriorities.Add(prio);
            }
        }

        // 4. タイルパッキング（VRAM予算超過時は自動ダウンスケールして全光源を維持）
        var allocations = ShadowMapPool.PackTiles(pageSize, maxVramBytes, flatTiers, flatPriorities, out int requiredPages);

        var shadowEntries = new List<GpuShadowEntry>(flatTiers.Count);
        var shadowJobs = new List<ShadowRenderJob>(flatTiers.Count);

        Vector3 sceneCenter = (sceneMin + sceneMax) * 0.5f;
        float sceneRadius = Vector3.Distance(sceneCenter, sceneMax) + 50f;
        Span<Vector3> sceneCorners = stackalloc Vector3[8];
        FillAabbCorners(sceneMin, sceneMax, sceneCorners);

        int allocCursor = 0;
        for (int r = 0; r < requests.Count; r++)
        {
            var req = requests[r];
            var light = activeLights[req.LightIndex];

            bool allValid = true;
            for (int sub = 0; sub < req.EntryCount; sub++)
            {
                if (allocations[allocCursor + sub].SliceIndex < 0)
                {
                    allValid = false;
                    break;
                }
            }

            if (!allValid)
            {
                allocCursor += req.EntryCount;
                continue;
            }

            int startEntryIdx = shadowEntries.Count;
            gpuLights[req.LightIndex].ShadowStartIndex = startEntryIdx;
            gpuLights[req.LightIndex].ShadowCount = req.EntryCount;
            gpuLights[req.LightIndex].ShadowMode = req.ShadowMode;

            if (req.ShadowMode == 3) // Directional CSM
            {
                Vector3 dir = light.Direction.LengthSquared() > 1e-6f
                    ? Vector3.Normalize(light.Direction)
                    : new Vector3(0f, -1f, 0f);
                Vector3 up = MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;
                Matrix4x4 lightView = Matrix4x4.CreateLookAt(Vector3.Zero, dir, up);

                // 全シーンAABBの8角点からライト空間Z範囲をタイトに決定
                float minZ_LS = float.MaxValue;
                float maxZ_LS = float.MinValue;
                for (int k = 0; k < 8; k++)
                {
                    float tz = Vector3.Transform(sceneCorners[k], lightView).Z;
                    if (tz < minZ_LS) minZ_LS = tz;
                    if (tz > maxZ_LS) maxZ_LS = tz;
                }
                float zMargin = MathF.Max(200f, (maxZ_LS - minZ_LS) * 0.1f);
                float zNear = -maxZ_LS - zMargin;
                float zFar = -minZ_LS + zMargin;

                for (int c = 0; c < req.EntryCount; c++)
                {
                    var tile = allocations[allocCursor++];
                    float dNear = req.CascadeNear[c];
                    float dFar = req.CascadeFar[c];

                    var lvp = BuildDirectionalCascadeMatrix(
                        lightView, invCam, screenWidth, screenHeight,
                        dNear, dFar, zNear, zFar,
                        sceneCenter, sceneRadius,
                        tile.TileRes, out float worldUnitsPerTexel);

                    float blendWidth = (c < req.EntryCount - 1) ? (dFar - dNear) * 0.10f : 0f;

                    var entry = new GpuShadowEntry
                    {
                        AtlasUvRect = tile.UvRect,
                        BiasAndParams = new Vector4(light.ShadowBias, light.ShadowNormalBias, light.ShadowIntensity, tile.SliceIndex),
                        ExtraParams = new Vector4(dFar, blendWidth, worldUnitsPerTexel, tile.TileRes),
                    };
                    entry.SetLightViewProj(lvp);

                    int entryIdx = shadowEntries.Count;
                    shadowEntries.Add(entry);
                    shadowJobs.Add(new ShadowRenderJob
                    {
                        LightIndex = req.LightIndex,
                        ShadowEntryIndex = entryIdx,
                        Tile = tile,
                        LightViewProj = lvp,
                        ShadowCameraPos = camPosWorld - dir * 10000f,
                        IsOrthographic = true,
                        Frustum = FrustumPlanes.ExtractPlanes(lvp),
                    });
                }
            }
            else if (req.ShadowMode == 1) // Point Cube (6 faces)
            {
                for (int face = 0; face < 6; face++)
                {
                    var tile = allocations[allocCursor++];
                    var lvp = BuildPointFaceViewProj(light.Position, face, req.NearPlane, req.FarPlane);

                    var entry = new GpuShadowEntry
                    {
                        AtlasUvRect = tile.UvRect,
                        BiasAndParams = new Vector4(light.ShadowBias, light.ShadowNormalBias, light.ShadowIntensity, tile.SliceIndex),
                        ExtraParams = new Vector4(req.NearPlane, req.FarPlane, 1.0f, tile.TileRes),
                    };
                    entry.SetLightViewProj(lvp);

                    int entryIdx = shadowEntries.Count;
                    shadowEntries.Add(entry);
                    shadowJobs.Add(new ShadowRenderJob
                    {
                        LightIndex = req.LightIndex,
                        ShadowEntryIndex = entryIdx,
                        Tile = tile,
                        LightViewProj = lvp,
                        ShadowCameraPos = light.Position,
                        IsOrthographic = false,
                        Frustum = FrustumPlanes.ExtractPlanes(lvp),
                    });
                }
            }
            else if (req.ShadowMode == 2) // Area PCSS
            {
                var tile = allocations[allocCursor++];
                var lvp = BuildAreaViewProj(light, req.CameraPos, req.NearPlane, req.FarPlane, 130f * MathF.PI / 180f);

                var entry = new GpuShadowEntry
                {
                    AtlasUvRect = tile.UvRect,
                    BiasAndParams = new Vector4(light.ShadowBias, light.ShadowNormalBias, light.ShadowIntensity, tile.SliceIndex),
                    ExtraParams = new Vector4(req.NearPlane, req.FarPlane, req.TanHalfFov, req.ExtraScalar),
                };
                entry.SetLightViewProj(lvp);

                int entryIdx = shadowEntries.Count;
                shadowEntries.Add(entry);
                shadowJobs.Add(new ShadowRenderJob
                {
                    LightIndex = req.LightIndex,
                    ShadowEntryIndex = entryIdx,
                    Tile = tile,
                    LightViewProj = lvp,
                    ShadowCameraPos = req.CameraPos,
                    IsOrthographic = false,
                    Frustum = FrustumPlanes.ExtractPlanes(lvp),
                });
            }
            else // Spot
            {
                var tile = allocations[allocCursor++];
                float outerDeg = Math.Clamp(light.SpotOuterAngle, 1f, 89f);
                var lvp = BuildSpotViewProj(light, outerDeg * 2f * (MathF.PI / 180f), req.NearPlane, req.FarPlane);

                var entry = new GpuShadowEntry
                {
                    AtlasUvRect = tile.UvRect,
                    BiasAndParams = new Vector4(light.ShadowBias, light.ShadowNormalBias, light.ShadowIntensity, tile.SliceIndex),
                    ExtraParams = new Vector4(req.NearPlane, req.FarPlane, req.TanHalfFov, tile.TileRes),
                };
                entry.SetLightViewProj(lvp);

                int entryIdx = shadowEntries.Count;
                shadowEntries.Add(entry);
                shadowJobs.Add(new ShadowRenderJob
                {
                    LightIndex = req.LightIndex,
                    ShadowEntryIndex = entryIdx,
                    Tile = tile,
                    LightViewProj = lvp,
                    ShadowCameraPos = light.Position,
                    IsOrthographic = false,
                    Frustum = FrustumPlanes.ExtractPlanes(lvp),
                });
            }
        }

        var camNormMat = MatrixTransformHelper.ComputeNormalMatrix(cameraMatrix);

        float ambientInt = environment.OverrideIblAndAmbient ? environment.AmbientIntensity : (float)settings.AmbientIntensity;
        float iblInt = environment.OverrideIblAndAmbient ? environment.IblIntensity : (float)settings.IblIntensity;
        Vector3 skyColorLin = environment.OverrideIblAndAmbient ? environment.SkyColorLinear : new Vector3(0.68f, 0.76f, 0.90f);
        float envExposure = environment.OverrideEnvironmentMap ? environment.Exposure : (float)settings.EnvironmentExposure;
        float envRefl = environment.OverrideEnvironmentMap
            ? (environment.ReflectionIntensity ?? 1.0f)
            : (float)settings.EnvironmentReflectionIntensity;

        var header = new CbLightingHeader
        {
            LightCount = lightCount,
            ShadowEntryCount = shadowEntries.Count,
            UseSimpleLight = lightCount > 0 ? 0f : 1f,
            EnableShadow = (settings.EnableShadow && shadowEntries.Count > 0) ? 1f : 0f,
            AmbientIntensity = ambientInt,
            EnableSoftShadow = settings.EnableSoftShadow ? 1f : 0f,
            ShadowQuality = (int)settings.ShadowQuality,
            ShadowSoftness = (float)settings.ShadowSoftness,
            AmbientColor = new Vector4(0.28f, 0.27f, 0.25f, 1.0f),
            CameraWorldPosAndPageSize = new Vector4(camPosWorld, pageSize),
            CameraForwardAndRcpPageSize = new Vector4(camForwardWorld, 1f / Math.Max(1, pageSize)),
            EnablePbr = settings.EnablePbr ? 1f : 0f,
            IblIntensity = iblInt,
            DefaultRoughness = (float)settings.DefaultRoughness,
            ShadowNoiseScale = 1f,
            SkyColorAndGroundMix = new Vector4(skyColorLin, 1.0f),
            CamBasisX = new Vector4(camNormMat.M11, camNormMat.M12, camNormMat.M13, 0f),
            CamBasisY = new Vector4(camNormMat.M21, camNormMat.M22, camNormMat.M23, 0f),
            CamBasisZ = new Vector4(camNormMat.M31, camNormMat.M32, camNormMat.M33, 0f),
            EnvParams = new Vector4(envExposure, MathF.Max(envRefl, 0f), 1f, 0f),
        };

        return new LightingFramePlan
        {
            Header = header,
            Lights = gpuLights,
            ShadowEntries = shadowEntries.ToArray(),
            ShadowJobs = shadowJobs,
            RequiredPages = requiredPages,
            PageSize = pageSize,
        };
    }

    public static void ComputeSceneBounds(
        IReadOnlyList<PreparedRenderItem> items,
        Vector3 camPosWorld,
        Vector3 camForwardWorld,
        out Vector3 sceneMin,
        out Vector3 sceneMax,
        out float sceneNearDist,
        out float sceneFarDist)
    {
        if (items.Count == 0)
        {
            sceneMin = new Vector3(-1000f);
            sceneMax = new Vector3(1000f);
            sceneNearDist = 100f;
            sceneFarDist = 3000f;
            return;
        }

        sceneMin = new Vector3(float.MaxValue);
        sceneMax = new Vector3(float.MinValue);

        for (int i = 0; i < items.Count; i++)
        {
            var b = items[i].Bounds;
            sceneMin = Vector3.Min(sceneMin, b.Min);
            sceneMax = Vector3.Max(sceneMax, b.Max);
        }

        Span<Vector3> corners = stackalloc Vector3[8];
        FillAabbCorners(sceneMin, sceneMax, corners);

        float minProj = float.MaxValue;
        float maxProj = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            float d = Vector3.Dot(corners[i] - camPosWorld, camForwardWorld);
            if (d < minProj) minProj = d;
            if (d > maxProj) maxProj = d;
        }

        sceneNearDist = MathF.Max(50f, minProj - 100f);
        sceneFarDist = Math.Clamp(maxProj + 100f, sceneNearDist + 200f, 30000f);
    }

    private static void ComputeCascadeSplits(
        float nearDist, float farDist, int cascadeCount,
        out float[] cNear, out float[] cFar)
    {
        cNear = new float[cascadeCount];
        cFar = new float[cascadeCount];
        const float lambda = 0.65f;

        float prev = nearDist;
        for (int i = 0; i < cascadeCount; i++)
        {
            float p = (float)(i + 1) / cascadeCount;
            float logSplit = nearDist * MathF.Pow(farDist / nearDist, p);
            float uniSplit = nearDist + (farDist - nearDist) * p;
            float curr = lambda * logSplit + (1f - lambda) * uniSplit;
            cNear[i] = prev;
            cFar[i] = curr;
            prev = curr;
        }
    }

    public static Matrix4x4 BuildDirectionalCascadeMatrix(
        in Matrix4x4 lightView,
        in Matrix4x4 invCam,
        int screenWidth,
        int screenHeight,
        float dNear,
        float dFar,
        float zNear,
        float zFar,
        Vector3 sceneCenter,
        float sceneRadius,
        int tileRes,
        out float worldUnitsPerTexel)
    {
        float halfScreenW = screenWidth * 0.5f;
        float halfScreenH = screenHeight * 0.5f;

        float zCamNear = 1000f - dNear;
        float zCamFar = 1000f - dFar;
        float hwNear = halfScreenW * (dNear / 1000f);
        float hhNear = halfScreenH * (dNear / 1000f);
        float hwFar = halfScreenW * (dFar / 1000f);
        float hhFar = halfScreenH * (dFar / 1000f);

        Span<Vector3> frustumWorld = stackalloc Vector3[8];
        frustumWorld[0] = Vector3.Transform(new Vector3(-hwNear, -hhNear, zCamNear), invCam);
        frustumWorld[1] = Vector3.Transform(new Vector3( hwNear, -hhNear, zCamNear), invCam);
        frustumWorld[2] = Vector3.Transform(new Vector3(-hwNear,  hhNear, zCamNear), invCam);
        frustumWorld[3] = Vector3.Transform(new Vector3( hwNear,  hhNear, zCamNear), invCam);
        frustumWorld[4] = Vector3.Transform(new Vector3(-hwFar, -hhFar, zCamFar), invCam);
        frustumWorld[5] = Vector3.Transform(new Vector3( hwFar, -hhFar, zCamFar), invCam);
        frustumWorld[6] = Vector3.Transform(new Vector3(-hwFar,  hhFar, zCamFar), invCam);
        frustumWorld[7] = Vector3.Transform(new Vector3( hwFar,  hhFar, zCamFar), invCam);

        Vector3 frustumCenter = Vector3.Zero;
        for (int i = 0; i < 8; i++) frustumCenter += frustumWorld[i];
        frustumCenter /= 8f;

        float frustumRadius = 0f;
        for (int i = 0; i < 8; i++)
        {
            float d = Vector3.Distance(frustumCenter, frustumWorld[i]);
            if (d > frustumRadius) frustumRadius = d;
        }

        Vector3 center = frustumCenter;
        float radius = MathF.Max(frustumRadius, 100f);
        if (sceneRadius < radius)
        {
            center = sceneCenter;
            radius = MathF.Max(sceneRadius, 100f);
        }

        float halfExtent = radius * (1f + 4f / Math.Max(1, tileRes));
        worldUnitsPerTexel = (halfExtent * 2f) / Math.Max(1, tileRes);

        Vector3 centerLS = Vector3.Transform(center, lightView);
        float snappedX = MathF.Floor(centerLS.X / worldUnitsPerTexel) * worldUnitsPerTexel;
        float snappedY = MathF.Floor(centerLS.Y / worldUnitsPerTexel) * worldUnitsPerTexel;

        var lightProj = Matrix4x4.CreateOrthographicOffCenter(
            snappedX - halfExtent, snappedX + halfExtent,
            snappedY - halfExtent, snappedY + halfExtent,
            zNear, zFar);

        return lightView * lightProj;
    }

    public static Matrix4x4 BuildAreaViewProj(LightData light, Vector3 viewPos, float nearPlane, float farPlane, float fovRad)
    {
        var (forward, _, up) = ComputeAreaBasis(light);
        var view = Matrix4x4.CreateLookAt(viewPos, light.Position + forward * 100f, up);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fovRad, 1f, nearPlane, farPlane);
        return view * proj;
    }

    public static Matrix4x4 BuildSpotViewProj(LightData light, float fovRad, float nearPlane, float farPlane)
    {
        Vector3 dir = light.Direction.LengthSquared() > 1e-6f
            ? Vector3.Normalize(light.Direction)
            : new Vector3(0f, -1f, 0f);
        Vector3 up = MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;

        var view = Matrix4x4.CreateLookAt(light.Position, light.Position + dir * 100f, up);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fovRad, 1f, nearPlane, farPlane);
        return view * proj;
    }

    public static Matrix4x4 BuildPointFaceViewProj(Vector3 lightPos, int face, float nearPlane, float farPlane)
    {
        Vector3 target;
        Vector3 up;
        switch (face)
        {
            case 0: target = new Vector3(1f, 0f, 0f); up = new Vector3(0f, 1f, 0f); break;
            case 1: target = new Vector3(-1f, 0f, 0f); up = new Vector3(0f, 1f, 0f); break;
            case 2: target = new Vector3(0f, 1f, 0f); up = new Vector3(0f, 0f, -1f); break;
            case 3: target = new Vector3(0f, -1f, 0f); up = new Vector3(0f, 0f, 1f); break;
            case 4: target = new Vector3(0f, 0f, 1f); up = new Vector3(0f, 1f, 0f); break;
            default: target = new Vector3(0f, 0f, -1f); up = new Vector3(0f, 1f, 0f); break;
        }

        var view = Matrix4x4.CreateLookAt(lightPos, lightPos + target, up);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * 0.5f, 1f, nearPlane, farPlane);
        return view * proj;
    }

    private static void FillAabbCorners(in Vector3 min, in Vector3 max, Span<Vector3> dst)
    {
        dst[0] = new Vector3(min.X, min.Y, min.Z);
        dst[1] = new Vector3(max.X, min.Y, min.Z);
        dst[2] = new Vector3(min.X, max.Y, min.Z);
        dst[3] = new Vector3(max.X, max.Y, min.Z);
        dst[4] = new Vector3(min.X, min.Y, max.Z);
        dst[5] = new Vector3(max.X, min.Y, max.Z);
        dst[6] = new Vector3(min.X, max.Y, max.Z);
        dst[7] = new Vector3(max.X, max.Y, max.Z);
    }

    public static void ComputeDominantLightScreenAndDir(
        IReadOnlyList<LightData> lights,
        in Matrix4x4 cameraMatrix,
        int screenWidth,
        int screenHeight,
        out Vector3 dirCam,
        out bool hasLight,
        out Vector2 lightScreenUv,
        out Vector3 lightColorLinear,
        out float lightVisibility)
    {
        var camNormMat = MatrixTransformHelper.ComputeNormalMatrix(cameraMatrix);
        LightData? best = null;
        float bestScore = -1f;

        for (int i = 0; i < lights.Count; i++)
        {
            var l = lights[i];
            if (l.Intensity <= 0.001f) continue;
            float typeWeight = l.Type == LightType.Directional ? 2.5f : 1.0f;
            float score = l.Intensity * typeWeight;
            if (score > bestScore)
            {
                bestScore = score;
                best = l;
            }
        }

        if (!Matrix4x4.Invert(cameraMatrix, out var invCam))
            invCam = Matrix4x4.Identity;
        Vector3 camPosWorld = Vector3.Transform(new Vector3(0f, 0f, 1000f), invCam);

        Vector3 toLightWorld;
        Vector3 lightOriginWorld;
        if (best == null)
        {
            // Fallback matches CalcSimpleLgtPbr keyDir = -normalize(0.3, -0.5, -1.0)
            toLightWorld = Vector3.Normalize(new Vector3(-0.3f, 0.5f, 1.0f));
            lightOriginWorld = camPosWorld + toLightWorld * 50000f;
            hasLight = false;
            lightColorLinear = new Vector3(1f, 0.95f, 0.88f);
        }
        else if (best.Type == LightType.Directional)
        {
            Vector3 d = best.Direction.LengthSquared() > 1e-6f ? Vector3.Normalize(best.Direction) : new Vector3(0f, -1f, 0f);
            toLightWorld = -d;
            lightOriginWorld = camPosWorld + toLightWorld * 50000f;
            hasLight = true;
            lightColorLinear = new Vector3(SrgbToLinear(best.Color.X), SrgbToLinear(best.Color.Y), SrgbToLinear(best.Color.Z));
        }
        else
        {
            toLightWorld = Vector3.Normalize(best.Position - camPosWorld + new Vector3(0f, -0.001f, 0f));
            lightOriginWorld = best.Position;
            hasLight = true;
            lightColorLinear = new Vector3(SrgbToLinear(best.Color.X), SrgbToLinear(best.Color.Y), SrgbToLinear(best.Color.Z));
        }

        Vector3 dirC = Vector3.TransformNormal(toLightWorld, camNormMat);
        dirCam = dirC.LengthSquared() > 1e-8f ? Vector3.Normalize(dirC) : new Vector3(0f, -0.5f, 0.866f);

        // Project lightOriginWorld to screen UV for crepuscular rays
        var vp = DepthSortRenderer.BuildViewProjMatrix(cameraMatrix);
        var clip = Vector4.Transform(new Vector4(lightOriginWorld, 1f), vp);
        if (clip.W > 0.01f && screenWidth > 0 && screenHeight > 0)
        {
            float sx = clip.X / clip.W;
            float sy = clip.Y / clip.W;
            float u = sx / screenWidth + 0.5f;
            float v = sy / screenHeight + 0.5f;
            float radialDist = MathF.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f));
            lightVisibility = Math.Clamp(1.0f - MathF.Max(0f, radialDist - 0.85f) * 1.1f, 0f, 1f);
            lightScreenUv = new Vector2(u, v);
        }
        else
        {
            lightScreenUv = new Vector2(0.5f, 0.25f);
            lightVisibility = 0f;
        }
    }

    private static float SrgbToLinear(float x)
    {
        x = MathF.Max(0f, x);
        return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }
}
