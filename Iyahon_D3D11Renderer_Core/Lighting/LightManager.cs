using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

internal static class LightManager
{
    private static readonly ConcurrentBag<LightData> _currentFrameLights = new();
    private static readonly object _swapLock = new();

    private static List<LightData> _activeLights = new();

    public static void BeginFrame()
    {
        while (_currentFrameLights.TryTake(out _)) { }
    }

    public static void AddLight(LightData light)
    {
        _currentFrameLights.Add(light);
    }

    public static void EndFrame()
    {
        lock (_swapLock)
        {
            _activeLights = _currentFrameLights.ToList();
        }
    }

    public static int ActiveLightCount
    {
        get
        {
            lock (_swapLock) { return _activeLights.Count; }
        }
    }

    public static List<LightData> GetActiveLights()
    {
        lock (_swapLock)
        {
            return new List<LightData>(_activeLights);
        }
    }

    private static (Vector3 center, float size) CalculateSceneBounds(List<RenderItem> items)
    {
        Vector3 target = Vector3.Zero;

        if (items != null && items.Count > 0)
        {
            var firstItem = items.FirstOrDefault();
            if (firstItem != null)
            {
                var cameraMatrix = firstItem.DrawDescription.Camera;
                if (Matrix4x4.Invert(cameraMatrix, out var invCam))
                {
                    target = Vector3.Transform(new Vector3(0, 0, -1000f), invCam);
                }
            }
        }

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        foreach (var item in items)
        {
            Vector3 pos = item.DrawDescription.Draw;
            float sizeEstimate = Math.Max(item.PixelWidth, item.PixelHeight);

            if (sizeEstimate <= 0)
            {
                if (item.ObjModels != null && item.ObjModels.Count > 0)
                {
                    float zoom = (MathF.Abs((float)item.DrawDescription.Zoom.X) + MathF.Abs((float)item.DrawDescription.Zoom.Y)) / 2f;
                    sizeEstimate = 200f * zoom * 2f;
                }
                else
                {
                    sizeEstimate = 500f;
                }
            }

            minX = MathF.Min(minX, pos.X - sizeEstimate);
            minY = MathF.Min(minY, pos.Y - sizeEstimate);
            minZ = MathF.Min(minZ, pos.Z - sizeEstimate);
            maxX = MathF.Max(maxX, pos.X + sizeEstimate);
            maxY = MathF.Max(maxY, pos.Y + sizeEstimate);
            maxZ = MathF.Max(maxZ, pos.Z + sizeEstimate);
        }

        float sizeX = maxX - minX;
        float sizeY = maxY - minY;
        float sizeZ = maxZ - minZ;

        float size = MathF.Max(sizeX, MathF.Max(sizeY, sizeZ));
        size = Math.Clamp(size, 18000f, 50000f);

        return (target, size);
    }

    private static (Vector3 min, Vector3 max) CalculateWorldAABB(List<RenderItem> items)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        if (items == null || items.Count == 0)
        {
            return (new Vector3(-1000f), new Vector3(1000f));
        }

        foreach (var item in items)
        {
            Vector3 pos = item.DrawDescription.Draw;
            float sizeEstimate = Math.Max(item.PixelWidth, item.PixelHeight);

            if (sizeEstimate <= 0)
            {
                if (item.ObjModels != null && item.ObjModels.Count > 0)
                {
                    float zoom = (MathF.Abs((float)item.DrawDescription.Zoom.X) + MathF.Abs((float)item.DrawDescription.Zoom.Y)) / 2f;
                    sizeEstimate = 200f * zoom * 2f;
                }
                else
                {
                    sizeEstimate = 500f;
                }
            }

            minX = MathF.Min(minX, pos.X - sizeEstimate);
            minY = MathF.Min(minY, pos.Y - sizeEstimate);
            minZ = MathF.Min(minZ, pos.Z - sizeEstimate);
            maxX = MathF.Max(maxX, pos.X + sizeEstimate);
            maxY = MathF.Max(maxY, pos.Y + sizeEstimate);
            maxZ = MathF.Max(maxZ, pos.Z + sizeEstimate);
        }

        return (new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));
    }

    public static CbLighting BuildConstantBuffer(int width, int height, List<RenderItem> items)
    {
        var settings = D3D11RendererSettings.Default;
        var lights = GetActiveLights();
        int targetRes = (int)settings.ShadowResolution;

        int maxTileSize = 16384 / 4;
        int actualTileSize = Math.Min(targetRes, maxTileSize);

        Vector3 cameraTarget = Vector3.Zero;
        if (items != null && items.Count > 0)
        {
            var firstItem = items.FirstOrDefault();
            if (firstItem != null)
            {
                if (Matrix4x4.Invert(firstItem.DrawDescription.Camera, out var invCam))
                {
                    cameraTarget = Vector3.Transform(Vector3.Zero, invCam);
                }
            }
        }

        var cb = new CbLighting
        {
            LightCount = Math.Min(lights.Count, CbLighting.MAX_LIGHTS),
            UseSimpleLight = lights.Count > 0 ? 0f : 1f,
            EnableShadow = settings.EnableShadow ? 1f : 0f,
            AmbientIntensity = (float)settings.AmbientIntensity,
            AmbientColor = new Vector4(0.3f, 0.3f, 0.35f, 1.0f),
            ShadowCount = 0,
            EnableSoftShadow = settings.EnableSoftShadow ? 1f : 0f, // ★追加：設定画面のフラグをGPU定数バッファに伝達
        };

        var shadowLights = new List<(LightData light, int index)>();
        for (int i = 0; i < cb.LightCount; i++)
        {
            if (lights[i].CastShadow && shadowLights.Count < 8)
            {
                shadowLights.Add((lights[i], i));
            }
        }

        cb.ShadowCount = shadowLights.Count;

        int currentTile = 0;

        for (int i = 0; i < 8; i++)
        {
            var shadowData = new GpuShadowData
            {
                LightViewProj0 = Matrix4x4.Identity,
                ShadowParams = new Vector4(0.0005f, 0.0f, -1f, 0f),
                AtlasParams = new Vector4(-1f, (float)actualTileSize, 1.0f, 0f),
                DepthParams = Vector4.Zero
            };

            if (i < shadowLights.Count)
            {
                var sLight = shadowLights[i].light;
                int lIdx = shadowLights[i].index;

                // shadowMode: 0=単一2D(Directional/Spot), 1=Cube(Point), 2=Area(PCSS、単一視点+可変PCF)
                bool isCube = sLight.Type == LightType.Point;
                bool isArea = sLight.Type == LightType.Area;
                float shadowMode = isCube ? 1f : (isArea ? 2f : 0f);
                int tilesNeeded = isCube ? 6 : 1;

                if (currentTile + tilesNeeded <= 48)
                {
                    float lightSize = isArea ? ComputeAreaLightSize(sLight) : 0f;

                    shadowData.ShadowParams = new Vector4(
                        sLight.ShadowBias,
                        sLight.ShadowIntensity,
                        (float)lIdx,
                        shadowMode
                    );

                    // ★ 補正：ライトがカメラ中心から離れたら影描画解像度を動的に削減 (1/2, 1/4)
                    float scaleFactor = 1.0f;
                    if (sLight.Type != LightType.Directional)
                    {
                        float dist = Vector3.Distance(cameraTarget, sLight.Position);
                        if (dist > 8000f) scaleFactor = 0.25f; // 8000px超で 1/4 (超軽量)
                        else if (dist > 4000f) scaleFactor = 0.5f;  // 4000px超で 1/2
                    }

                    shadowData.AtlasParams = new Vector4(
                        (float)currentTile,
                        (float)actualTileSize,
                        scaleFactor,
                        lightSize
                    );

                    if (isArea)
                    {
                        shadowData.LightViewProj0 = BuildAreaSingleViewProj(sLight, items);
                        var (nearPlane, farPlane) = ComputeAreaNearFar(sLight);
                        shadowData.DepthParams = new Vector4(nearPlane, farPlane, 0f, 0f);
                    }
                    else if (!isCube)
                    {
                        shadowData.LightViewProj0 = BuildLightViewProj(sLight, width, height, items);
                    }

                    currentTile += tilesNeeded;
                }
            }

            switch (i)
            {
                case 0: cb.Shadow0 = shadowData; break;
                case 1: cb.Shadow1 = shadowData; break;
                case 2: cb.Shadow2 = shadowData; break;
                case 3: cb.Shadow3 = shadowData; break;
                case 4: cb.Shadow4 = shadowData; break;
                case 5: cb.Shadow5 = shadowData; break;
                case 6: cb.Shadow6 = shadowData; break;
                case 7: cb.Shadow7 = shadowData; break;
            }
        }

        for (int i = 0; i < cb.LightCount; i++)
        {
            cb.SetLight(i, GpuLightData.FromLightData(lights[i]));
        }

        return cb;
    }

    private static Vector3 ComputeMainCameraUp(List<RenderItem> items)
    {
        Matrix4x4 cameraMatrix = Matrix4x4.Identity;
        if (items != null && items.Count > 0)
        {
            var firstItem = items.FirstOrDefault();
            if (firstItem != null)
            {
                cameraMatrix = firstItem.DrawDescription.Camera;
            }
        }

        if (!Matrix4x4.Invert(cameraMatrix, out var invCam))
        {
            invCam = Matrix4x4.Identity;
        }

        Vector3 mainCameraUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, invCam));
        if (mainCameraUp.LengthSquared() < 1e-6f)
        {
            mainCameraUp = Vector3.UnitY;
        }
        return mainCameraUp;
    }

    /// <summary>
    /// Area光源の面の基底ベクトル(forward/right/up)を計算する。
    /// HLSL側(CalcDynamicLgtEff)のロジックと必ず一致させること。
    /// </summary>
    private static (Vector3 forward, Vector3 right, Vector3 up) ComputeAreaBasis(LightData light)
    {
        Vector3 forward = Vector3.Normalize(light.Direction);
        Vector3 upBasis = Vector3.UnitY;
        if (MathF.Abs(Vector3.Dot(forward, upBasis)) > 0.9f)
        {
            upBasis = Vector3.UnitZ;
        }
        Vector3 rightBasis = Vector3.Normalize(Vector3.Cross(forward, upBasis));
        upBasis = Vector3.Normalize(Vector3.Cross(rightBasis, forward));
        return (forward, rightBasis, upBasis);
    }

    /// <summary>
    /// Area光源の視点をエリア面の中心から「後ろ」(Directionの逆方向、つまり物理的には
    /// 照射方向と逆向き=天井側など)に後退させる距離。
    /// 視点を面そのものに置くと、面のすぐ近くにある物体が視野角のギリギリ端に来て
    /// 精度低下やクリッピングを起こし、「光源直下の影が切れる」原因になっていたため、
    /// 後退を導入した。ただし、エリアサイズに比例して後退量を大きくする設計だと、
    /// エリアが大きいほど視点が照射方向と逆側(天井など)に大きく入り込んでしまい、
    /// 実際の部屋のジオメトリ(天井)を突き抜けてシャドウマップ自体が破綻する、という
    /// より深刻な問題を引き起こすことが判明した。そのため、エリアサイズに依存しない
    /// 小さな固定値に留め、近接クリッピングの最低限の緩和にとどめる。
    /// BuildAreaSingleViewProj と ComputeAreaNearFar で同じ値を使うこと。
    /// </summary>
    public static float ComputeAreaViewBackOffset(LightData light)
    {
        return 15f;
    }

    /// <summary>
    /// Area光源シャドウマップのnear/farプレーン値。BuildAreaSingleViewProjと必ず一致させること
    /// (シェーダー側でNDC深度→線形深度変換するために、同じ値をDepthParamsとして渡す必要がある)。
    /// </summary>
    public static (float near, float far) ComputeAreaNearFar(LightData light)
    {
        float backOffset = ComputeAreaViewBackOffset(light);
        float nearPlane = 4f;
        float farPlane = Math.Max(100f, light.Range) + backOffset;
        return (nearPlane, farPlane);
    }

    /// <summary>
    /// Area光源の中心位置から、Direction方向を向く単一の透視投影を構築する。
    /// シャドウマップ自体は1枚だけ(=1パス)で済ませ、ソフトシャドウ表現は
    /// シェーダー側のPCSS(ブロッカーサーチ+可変PCFカーネル)に任せる。
    /// これにより、Area光源1つあたりのシーン再描画回数を1回に抑える。
    /// </summary>
    public static Matrix4x4 BuildAreaSingleViewProj(LightData light, List<RenderItem> items)
    {
        var (forward, _, _) = ComputeAreaBasis(light);

        Vector3 mainCameraUp = ComputeMainCameraUp(items);
        Vector3 viewUp = mainCameraUp;
        if (MathF.Abs(Vector3.Dot(forward, viewUp)) > 0.9f)
        {
            viewUp = Vector3.UnitZ;
        }

        // 視点をエリア面の中心そのものではなく、少し後ろに下げる(理由は上記コメント参照)
        float backOffset = ComputeAreaViewBackOffset(light);
        Vector3 viewPos = light.Position - forward * backOffset;

        var view = Matrix4x4.CreateLookAt(viewPos, viewPos + forward * 1000f, viewUp);

        // 面光源は前方半球に広く放射するため、画角を広めに確保しておく
        float fov = 150f * MathF.PI / 180f;
        var (nearPlane, farPlane) = ComputeAreaNearFar(light);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, 1f, nearPlane, farPlane);

        return view * proj;
    }

    /// <summary>
    /// Area光源のおおよそのワールド単位サイズ(対角線の半分)。PCFカーネル拡大の参考値として使用。
    /// </summary>
    public static float ComputeAreaLightSize(LightData light)
    {
        float halfW = light.AreaWidth * 0.5f;
        float halfH = light.AreaHeight * 0.5f;
        return MathF.Sqrt(halfW * halfW + halfH * halfH);
    }

    public static Matrix4x4 BuildCubeLightViewProj(Vector3 lightPos, int face, float range)
    {
        Vector3 target = Vector3.Zero;
        Vector3 up = Vector3.Zero;

        switch (face)
        {
            case 0:
                target = new Vector3(1f, 0f, 0f);
                up = new Vector3(0f, -1f, 0f);
                break;
            case 1:
                target = new Vector3(-1f, 0f, 0f);
                up = new Vector3(0f, -1f, 0f);
                break;
            case 2:
                target = new Vector3(0f, -1f, 0f);
                up = new Vector3(0f, 0f, 1f);
                break;
            case 3:
                target = new Vector3(0f, 1f, 0f);
                up = new Vector3(0f, 0f, -1f);
                break;
            case 4:
                target = new Vector3(0f, 0f, -1f);
                up = new Vector3(0f, -1f, 0f);
                break;
            case 5:
                target = new Vector3(0f, 0f, 1f);
                up = new Vector3(0f, -1f, 0f);
                break;
        }

        var view = Matrix4x4.CreateLookAtLeftHanded(lightPos, lightPos + target, up);
        var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(MathF.PI * 0.5f, 1f, 10f, Math.Max(100f, range));
        return view * proj;
    }

    public static Matrix4x4 BuildLightViewProj(LightData light, int width, int height, List<RenderItem> items)
    {
        Matrix4x4 cameraMatrix = Matrix4x4.Identity;
        if (items != null && items.Count > 0)
        {
            var firstItem = items.FirstOrDefault();
            if (firstItem != null)
            {
                cameraMatrix = firstItem.DrawDescription.Camera;
            }
        }

        if (!Matrix4x4.Invert(cameraMatrix, out var invCam))
        {
            invCam = Matrix4x4.Identity;
        }

        Vector3 mainCameraUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, invCam));
        if (mainCameraUp.LengthSquared() < 1e-6f)
        {
            mainCameraUp = Vector3.UnitY;
        }

        Vector3 target = Vector3.Transform(Vector3.Zero, invCam);

        Matrix4x4 view;
        Matrix4x4 proj;

        if (light.Type == LightType.Directional)
        {
            Vector3 L = Vector3.Normalize(light.Direction);
            if (L.LengthSquared() < 1e-6f) L = new Vector3(0, -1, 0);

            Vector3 up = mainCameraUp;
            if (MathF.Abs(Vector3.Dot(L, up)) > 0.9f)
            {
                up = Vector3.UnitZ;
            }

            Vector3 position = target;
            view = Matrix4x4.CreateLookAt(position, position + L * 1000f, up);

            float w = width;
            float h = height;

            float nearZ = 1000f;
            float farZ = -20000f;

            float nearW = w * (MathF.Abs(nearZ) / 1000f);
            float nearH = h * (MathF.Abs(nearZ) / 1000f);
            float farW = w * (MathF.Abs(farZ) / 1000f);
            float farH = h * (MathF.Abs(farZ) / 1000f);

            Vector3[] frustumCorners = new Vector3[8]
            {
                new Vector3(-nearW / 2f, -nearH / 2f, nearZ),
                new Vector3( nearW / 2f, -nearH / 2f, nearZ),
                new Vector3(-nearW / 2f,  nearH / 2f, nearZ),
                new Vector3( nearW / 2f,  nearH / 2f, nearZ),
                new Vector3(-farW / 2f, -farH / 2f, farZ),
                new Vector3( farW / 2f, -farH / 2f, farZ),
                new Vector3(-farW / 2f,  farH / 2f, farZ),
                new Vector3( farW / 2f,  farH / 2f, farZ)
            };

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            for (int i = 0; i < 8; i++)
            {
                Vector3 worldPos = Vector3.Transform(frustumCorners[i], invCam);
                Vector3 shadowViewPos = Vector3.Transform(worldPos, view);

                minX = MathF.Min(minX, shadowViewPos.X);
                minY = MathF.Min(minY, shadowViewPos.Y);
                minZ = MathF.Min(minZ, shadowViewPos.Z);
                maxX = MathF.Max(maxX, shadowViewPos.X);
                maxY = MathF.Max(maxY, shadowViewPos.Y);
                maxZ = MathF.Max(maxZ, shadowViewPos.Z);
            }

            float sizeX = maxX - minX;
            float sizeY = maxY - minY;
            float sizeZ = maxZ - minZ;
            float maxSpan = MathF.Max(sizeX, MathF.Max(sizeY, sizeZ));

            proj = Matrix4x4.CreateOrthographic(maxSpan, maxSpan, -maxSpan * 2f, maxSpan * 2f);
        }
        else if (light.Type == LightType.Spot)
        {
            Vector3 L = Vector3.Normalize(light.Direction);

            Vector3 up = mainCameraUp;
            if (MathF.Abs(Vector3.Dot(L, up)) > 0.9f)
            {
                up = Vector3.UnitZ;
            }

            view = Matrix4x4.CreateLookAt(light.Position, light.Position + L * 1000f, up);

            // 【修正】SpotOuterAngle は半角のため、視野角（FOV）には全角（2倍）を指定します
            float fov = Math.Clamp(light.SpotOuterAngle * 2f, 1f, 179f) * MathF.PI / 180f;

            // 【改善】遠クリップ面をライトのRangeに合わせることで、デプスの精度を最適化します
            float nearPlane = 10f;
            float farPlane = Math.Max(100f, light.Range);

            proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, 1f, nearPlane, farPlane);
        }
        else
        {
            // 注: Point Lightはキューブシャドウマップ(BuildCubeLightViewProj)を使うため、
            // 通常はこの分岐に到達しない。Area Lightも BuildAreaSingleViewProj を使うため
            // ここには来ない。未知のライトタイプに対するフォールバックとして維持。
            var (aabbMin, aabbMax) = CalculateWorldAABB(items);
            Vector3 sceneCenter = (aabbMin + aabbMax) * 0.5f;
            Vector3 toCenter = sceneCenter - light.Position;
            Vector3 dir = toCenter.LengthSquared() > 1f ? Vector3.Normalize(toCenter) : new Vector3(0, -1, 0);

            Vector3 up = mainCameraUp;
            if (MathF.Abs(Vector3.Dot(dir, up)) > 0.9f)
            {
                up = Vector3.UnitZ;
            }

            view = Matrix4x4.CreateLookAt(light.Position, light.Position + dir * 1000f, up);
            proj = Matrix4x4.CreatePerspectiveFieldOfView(150f * MathF.PI / 180f, 1f, 10f, Math.Max(100f, light.Range));
        }

        return view * proj;
    }
}