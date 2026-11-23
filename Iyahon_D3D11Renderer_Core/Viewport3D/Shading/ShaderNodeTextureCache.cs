using System;
using System.Collections.Generic;
using System.Numerics;
using Iyahon_D3D11Renderer_Core.Model3D;
using Vortice.Direct3D11;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D.Shading;

internal sealed class BakedGpuMaterialTextures : IDisposable
{
    public ID3D11ShaderResourceView? AlbedoSrv;
    public ID3D11ShaderResourceView? NormalSrv;
    public ID3D11ShaderResourceView? OrmSrv;
    public ID3D11ShaderResourceView? EmissiveSrv;
    public ID3D11ShaderResourceView? TransmissionSrv;
    public ID3D11ShaderResourceView? ShaderLutSrv;

    public Vector4 BaseColor = Vector4.One;
    public Vector4 EmissiveColor = Vector4.Zero;
    public float Metallic;
    public float Roughness = 0.5f;
    public float Transmission;
    public float Ior = 1.5f;
    public float NormalScale = 1f;
    public bool ForceOpaque;

    public float CoatWeight;
    public float CoatRoughness = 0.05f;
    public float CoatIor = 1.5f;
    public float DiffuseRoughness;
    public float ThinFilmThickness;
    public float ThinFilmIor = 1.33f;
    public float Anisotropic;
    public float AnisotropicRotation;
    public float SubsurfaceWeight;
    public float SubsurfaceScale = 0.25f;
    public Vector3 SubsurfaceColor = new(1f, 0.35f, 0.25f);
    public float SheenWeight;
    public float SheenRoughness = 0.5f;
    public Vector3 SheenTint = Vector3.One;
    public bool IsDiffuseOnly;

    public int ShaderLutMode;
    public float ShaderLutParamA;
    public float ShaderLutParamB;
    public float ShaderLutEmissiveMax;

    public int WireframeMode;
    public float WireframeSize = 1.5f;
    public bool WireframePixelSize = true;
    public Vector4 WireframeBaseColor;
    public Vector4 WireframeEmissiveColor;

    public int LayerTextureAlbedoLayerPlusOne;
    public int LayerTextureEmissiveLayerPlusOne;
    public Vector4 LayerUvTransform = new(1f, 1f, 0f, 0f);
    public float LayerUvRotationRad;
    public bool LayerFlipV;

    // 返却ビューはキャッシュエントリ所有 SRV への参照のみを保持するため、ここでは Dispose しない
    public void Dispose() { }
}

internal static class ShaderNodeTextureCache
{
    private const int MaxEntries = 96;
    private const int MaxLutEntries = 64;
    private const int MaxCpuEntries = 32;

    private sealed class CachedGpuEntry : IDisposable
    {
        private readonly List<ID3D11Texture2D> _textures = new();
        private readonly List<ID3D11ShaderResourceView> _srvs = new();

        public ID3D11ShaderResourceView? AlbedoSrv;
        public ID3D11ShaderResourceView? NormalSrv;
        public ID3D11ShaderResourceView? OrmSrv;
        public ID3D11ShaderResourceView? EmissiveSrv;
        public ID3D11ShaderResourceView? TransmissionSrv;
        public ID3D11ShaderResourceView? ShaderLutSrv;
        public Vector4 BakedEmissiveColor = Vector4.Zero;
        public float NormalScale = 1f;
        public bool IsDisposed { get; private set; }

        public ID3D11ShaderResourceView? UploadChannel(
            ID3D11Device device, ID3D11DeviceContext ctx, byte[]? bgra, int w, int h)
        {
            if (bgra == null || w <= 0 || h <= 0) return null;
            var res = ModelTextureLoader.UploadBgraBytes(device, ctx, bgra, w, h);
            if (res is { } t)
            {
                _textures.Add(t.Texture);
                _srvs.Add(t.Srv);
                return t.Srv;
            }
            return null;
        }

        public BakedGpuMaterialTextures CreateView(in EvaluatedMaterialScalars scalars, ID3D11ShaderResourceView? lutSrvOverride = null) => new()
        {
            AlbedoSrv = AlbedoSrv,
            NormalSrv = NormalSrv,
            OrmSrv = OrmSrv,
            EmissiveSrv = EmissiveSrv,
            TransmissionSrv = TransmissionSrv,
            ShaderLutSrv = lutSrvOverride ?? ShaderLutSrv,
            BaseColor = scalars.BaseColor,
            EmissiveColor = (EmissiveSrv != null && scalars.ShaderLutMode != 1 && !(scalars.ShaderLutMode == 2 && scalars.ShaderLutEmissiveMax > 1e-4f))
                ? BakedEmissiveColor
                : scalars.EmissiveColor,
            Metallic = scalars.Metallic,
            Roughness = scalars.Roughness,
            Transmission = scalars.Transmission,
            Ior = scalars.Ior,
            NormalScale = NormalScale,
            ForceOpaque = scalars.ForceOpaque,
            CoatWeight = scalars.CoatWeight,
            CoatRoughness = scalars.CoatRoughness,
            CoatIor = scalars.CoatIor,
            DiffuseRoughness = scalars.DiffuseRoughness,
            ThinFilmThickness = scalars.ThinFilmThickness,
            ThinFilmIor = scalars.ThinFilmIor,
            Anisotropic = scalars.Anisotropic,
            AnisotropicRotation = scalars.AnisotropicRotation,
            SubsurfaceWeight = scalars.SubsurfaceWeight,
            SubsurfaceScale = scalars.SubsurfaceScale,
            SubsurfaceColor = scalars.SubsurfaceColor,
            SheenWeight = scalars.SheenWeight,
            SheenRoughness = scalars.SheenRoughness,
            SheenTint = scalars.SheenTint,
            IsDiffuseOnly = scalars.IsDiffuseOnly,
            ShaderLutMode = scalars.ShaderLutMode,
            ShaderLutParamA = scalars.ShaderLutParamA,
            ShaderLutParamB = scalars.ShaderLutParamB,
            ShaderLutEmissiveMax = scalars.ShaderLutEmissiveMax,
            WireframeMode = scalars.WireframeMode,
            WireframeSize = scalars.WireframeSize,
            WireframePixelSize = scalars.WireframePixelSize,
            WireframeBaseColor = scalars.WireframeBaseColor,
            WireframeEmissiveColor = scalars.WireframeEmissiveColor,
            LayerTextureAlbedoLayerPlusOne = scalars.LayerTextureAlbedoLayerPlusOne,
            LayerTextureEmissiveLayerPlusOne = scalars.LayerTextureEmissiveLayerPlusOne,
            LayerUvTransform = scalars.LayerUvTransform,
            LayerUvRotationRad = scalars.LayerUvRotationRad,
            LayerFlipV = scalars.LayerFlipV
        };

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            foreach (var srv in _srvs) { try { srv.Dispose(); } catch { } }
            foreach (var tex in _textures) { try { tex.Dispose(); } catch { } }
            _srvs.Clear();
            _textures.Clear();
            AlbedoSrv = null;
            NormalSrv = null;
            OrmSrv = null;
            EmissiveSrv = null;
            TransmissionSrv = null;
            ShaderLutSrv = null;
        }
    }

    private static readonly object s_lock = new();
    private static readonly List<(IntPtr DevicePtr, string Hash, CachedGpuEntry Entry)> s_lru = new();
    private static readonly List<(IntPtr DevicePtr, string Hash, ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv)> s_lutLru = new();
    private static readonly List<(string Hash, BakedNodeMaterial Baked)> s_cpuLru = new();

    public static BakedGpuMaterialTextures? GetOrBake(
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        MaterialNodeGraph? graph)
        => GetOrBakeAtTime(device, ctx, graph, null, ShaderEvalTime.Default);

    public static BakedGpuMaterialTextures? GetOrBakeAtResolution(
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        MaterialNodeGraph? graph,
        int? resolutionOverride)
        => GetOrBakeAtTime(device, ctx, graph, resolutionOverride, ShaderEvalTime.Default);

    public static BakedGpuMaterialTextures? GetOrBakeAtTime(
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        MaterialNodeGraph? graph,
        int? resolutionOverride,
        ShaderEvalTime evalTime)
    {
        if (device == null || ctx == null || graph == null)
            return null;

        IntPtr devPtr = device.NativePointer;
        if (devPtr == IntPtr.Zero) return null;

        int? effectiveOverride = resolutionOverride;
        ShaderEvalTime effTime = evalTime;
        if (ShaderNodeEvaluator.IsGraphTimeDependent(graph))
        {
            ShaderNodeEvaluator.ComputeQuantizedTimeKey(graph, evalTime, out effTime, out int? timeMaxRes);
            if (timeMaxRes.HasValue)
            {
                effectiveOverride = effectiveOverride.HasValue
                    ? Math.Min(effectiveOverride.Value, timeMaxRes.Value)
                    : timeMaxRes.Value;
            }
        }

        var scalars = ShaderNodeEvaluator.EvaluateScalars(graph, effTime, device, ctx);
        int effectiveRes = effectiveOverride.HasValue
            ? Math.Clamp(Math.Min(effectiveOverride.Value, graph.BakeResolution), 64, 1024)
            : Math.Clamp(graph.BakeResolution, 64, 1024);

        ID3D11ShaderResourceView? lutSrv = GetOrUploadLut(device, ctx, scalars.ShaderLutHash, scalars.ShaderLutBgra);

        string? spatialHash = ShaderNodeEvaluator.ComputeSpatialTextureHash(graph, effectiveRes, effTime, device);
        if (spatialHash == null)
        {
            return new BakedGpuMaterialTextures
            {
                BaseColor = scalars.BaseColor,
                EmissiveColor = scalars.EmissiveColor,
                Metallic = scalars.Metallic,
                Roughness = scalars.Roughness,
                Transmission = scalars.Transmission,
                Ior = scalars.Ior,
                NormalScale = scalars.NormalScale,
                ForceOpaque = scalars.ForceOpaque,
                CoatWeight = scalars.CoatWeight,
                CoatRoughness = scalars.CoatRoughness,
                CoatIor = scalars.CoatIor,
                DiffuseRoughness = scalars.DiffuseRoughness,
                ThinFilmThickness = scalars.ThinFilmThickness,
                ThinFilmIor = scalars.ThinFilmIor,
                Anisotropic = scalars.Anisotropic,
                AnisotropicRotation = scalars.AnisotropicRotation,
                SubsurfaceWeight = scalars.SubsurfaceWeight,
                SubsurfaceScale = scalars.SubsurfaceScale,
                SubsurfaceColor = scalars.SubsurfaceColor,
                SheenWeight = scalars.SheenWeight,
                SheenRoughness = scalars.SheenRoughness,
                SheenTint = scalars.SheenTint,
                IsDiffuseOnly = scalars.IsDiffuseOnly,
                ShaderLutSrv = lutSrv,
                ShaderLutMode = scalars.ShaderLutMode,
                ShaderLutParamA = scalars.ShaderLutParamA,
                ShaderLutParamB = scalars.ShaderLutParamB,
                ShaderLutEmissiveMax = scalars.ShaderLutEmissiveMax,
                WireframeMode = scalars.WireframeMode,
                WireframeSize = scalars.WireframeSize,
                WireframePixelSize = scalars.WireframePixelSize,
                WireframeBaseColor = scalars.WireframeBaseColor,
                WireframeEmissiveColor = scalars.WireframeEmissiveColor,
                LayerTextureAlbedoLayerPlusOne = scalars.LayerTextureAlbedoLayerPlusOne,
                LayerTextureEmissiveLayerPlusOne = scalars.LayerTextureEmissiveLayerPlusOne,
                LayerUvTransform = scalars.LayerUvTransform,
                LayerUvRotationRad = scalars.LayerUvRotationRad,
                LayerFlipV = scalars.LayerFlipV
            };
        }

        BakedNodeMaterial? cpuBaked = null;
        lock (s_lock)
        {
            EvictLostLocked(device);
            for (int i = 0; i < s_lru.Count; i++)
            {
                var e = s_lru[i];
                if (e.DevicePtr == devPtr && string.Equals(e.Hash, spatialHash, StringComparison.Ordinal))
                {
                    s_lru.RemoveAt(i);
                    s_lru.Add(e);
                    return e.Entry.CreateView(in scalars, lutSrv);
                }
            }

            for (int i = 0; i < s_cpuLru.Count; i++)
            {
                if (string.Equals(s_cpuLru[i].Hash, spatialHash, StringComparison.Ordinal))
                {
                    var ce = s_cpuLru[i];
                    s_cpuLru.RemoveAt(i);
                    s_cpuLru.Add(ce);
                    cpuBaked = ce.Baked;
                    break;
                }
            }
        }

        if (cpuBaked == null)
        {
            cpuBaked = ShaderNodeEvaluator.EvaluateAndBakeAtTime(graph, effectiveRes, effTime, device, ctx);
            lock (s_lock)
            {
                bool exists = false;
                for (int i = 0; i < s_cpuLru.Count; i++)
                {
                    if (string.Equals(s_cpuLru[i].Hash, spatialHash, StringComparison.Ordinal))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    s_cpuLru.Add((spatialHash, cpuBaked));
                    while (s_cpuLru.Count > MaxCpuEntries)
                        s_cpuLru.RemoveAt(0);
                }
            }
        }

        var entry = new CachedGpuEntry
        {
            BakedEmissiveColor = cpuBaked.EmissiveColor,
            NormalScale = cpuBaked.NormalScale
        };

        try
        {
            entry.AlbedoSrv = entry.UploadChannel(device, ctx, cpuBaked.BaseColorBgra, cpuBaked.Width, cpuBaked.Height);
            entry.NormalSrv = entry.UploadChannel(device, ctx, cpuBaked.NormalBgra, cpuBaked.Width, cpuBaked.Height);
            entry.OrmSrv = entry.UploadChannel(device, ctx, cpuBaked.OrmBgra, cpuBaked.Width, cpuBaked.Height);
            entry.EmissiveSrv = entry.UploadChannel(device, ctx, cpuBaked.EmissiveBgra, cpuBaked.Width, cpuBaked.Height);
            entry.TransmissionSrv = entry.UploadChannel(device, ctx, cpuBaked.TransmissionBgra, cpuBaked.Width, cpuBaked.Height);
        }
        catch
        {
            entry.Dispose();
            return null;
        }

        lock (s_lock)
        {
            for (int i = 0; i < s_lru.Count; i++)
            {
                var existing = s_lru[i];
                if (existing.DevicePtr == devPtr && string.Equals(existing.Hash, spatialHash, StringComparison.Ordinal))
                {
                    entry.Dispose();
                    s_lru.RemoveAt(i);
                    s_lru.Add(existing);
                    return existing.Entry.CreateView(in scalars, lutSrv);
                }
            }

            s_lru.Add((devPtr, spatialHash, entry));
            while (s_lru.Count > MaxEntries)
            {
                var oldest = s_lru[0].Entry;
                s_lru.RemoveAt(0);
                oldest.Dispose();
            }
            return entry.CreateView(in scalars, lutSrv);
        }
    }

    private static ID3D11ShaderResourceView? GetOrUploadLut(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        string? lutHash,
        byte[]? lutBgra)
    {
        const int lutWidth = ShaderNodeEvaluator.ShaderLutWidth;
        if (string.IsNullOrEmpty(lutHash) || lutBgra == null || lutBgra.Length < lutWidth * 2 * 4)
            return null;

        IntPtr devPtr = device.NativePointer;
        lock (s_lock)
        {
            EvictLostLocked(device);
            for (int i = 0; i < s_lutLru.Count; i++)
            {
                var e = s_lutLru[i];
                if (e.DevicePtr == devPtr && string.Equals(e.Hash, lutHash, StringComparison.Ordinal))
                {
                    s_lutLru.RemoveAt(i);
                    s_lutLru.Add(e);
                    return e.Srv;
                }
            }
        }

        var uploaded = ModelTextureLoader.UploadBgraBytes(device, ctx, lutBgra, lutWidth, 2);
        if (uploaded is not { } res) return null;

        lock (s_lock)
        {
            for (int i = 0; i < s_lutLru.Count; i++)
            {
                var existing = s_lutLru[i];
                if (existing.DevicePtr == devPtr && string.Equals(existing.Hash, lutHash, StringComparison.Ordinal))
                {
                    res.Srv.Dispose();
                    res.Texture.Dispose();
                    s_lutLru.RemoveAt(i);
                    s_lutLru.Add(existing);
                    return existing.Srv;
                }
            }

            s_lutLru.Add((devPtr, lutHash, res.Texture, res.Srv));
            while (s_lutLru.Count > MaxLutEntries)
            {
                var old = s_lutLru[0];
                s_lutLru.RemoveAt(0);
                try { old.Srv.Dispose(); } catch { }
                try { old.Tex.Dispose(); } catch { }
            }
            return res.Srv;
        }
    }

    public static void ClearForDevice(IntPtr devicePtr)
    {
        if (devicePtr == IntPtr.Zero) return;
        lock (s_lock)
        {
            for (int i = s_lru.Count - 1; i >= 0; i--)
            {
                if (s_lru[i].DevicePtr == devicePtr)
                {
                    s_lru[i].Entry.Dispose();
                    s_lru.RemoveAt(i);
                }
            }
            for (int i = s_lutLru.Count - 1; i >= 0; i--)
            {
                if (s_lutLru[i].DevicePtr == devicePtr)
                {
                    try { s_lutLru[i].Srv.Dispose(); } catch { }
                    try { s_lutLru[i].Tex.Dispose(); } catch { }
                    s_lutLru.RemoveAt(i);
                }
            }
        }
    }

    public static void ClearAll()
    {
        lock (s_lock)
        {
            for (int i = 0; i < s_lru.Count; i++)
                s_lru[i].Entry.Dispose();
            s_lru.Clear();
            for (int i = 0; i < s_lutLru.Count; i++)
            {
                try { s_lutLru[i].Srv.Dispose(); } catch { }
                try { s_lutLru[i].Tex.Dispose(); } catch { }
            }
            s_lutLru.Clear();
            s_cpuLru.Clear();
        }
        ShaderNodeEvaluator.ClearCaches();
    }

    private static void EvictLostLocked(ID3D11Device dev)
    {
        IntPtr ptr = dev.NativePointer;
        bool lost = false;
        try { lost = dev.DeviceRemovedReason.Failure; } catch { lost = true; }
        if (!lost) return;

        for (int i = s_lru.Count - 1; i >= 0; i--)
        {
            if (s_lru[i].DevicePtr == ptr)
            {
                s_lru[i].Entry.Dispose();
                s_lru.RemoveAt(i);
            }
        }
        for (int i = s_lutLru.Count - 1; i >= 0; i--)
        {
            if (s_lutLru[i].DevicePtr == ptr)
            {
                try { s_lutLru[i].Srv.Dispose(); } catch { }
                try { s_lutLru[i].Tex.Dispose(); } catch { }
                s_lutLru.RemoveAt(i);
            }
        }
    }
}
