using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
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
    private const int MaxEntriesPerDevice = 128;
    private const int MaxLutEntriesPerDevice = 128;
    private const int MaxCpuEntries = 48;

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
        public int PinCount;
        public bool Evicted;
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

    private sealed class CachedLutEntry : IDisposable
    {
        public readonly ID3D11Texture2D Tex;
        public readonly ID3D11ShaderResourceView Srv;
        public int PinCount;
        public bool Evicted;
        public bool IsDisposed { get; private set; }

        public CachedLutEntry(ID3D11Texture2D tex, ID3D11ShaderResourceView srv)
        {
            Tex = tex;
            Srv = srv;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            try { Srv.Dispose(); } catch { }
            try { Tex.Dispose(); } catch { }
        }
    }

    private sealed class DeviceCache
    {
        public readonly List<(string Hash, CachedGpuEntry Entry)> SpatialLru = new();
        public readonly List<(string Hash, CachedLutEntry Entry)> LutLru = new();
        public readonly HashSet<CachedGpuEntry> EvictedPinnedSpatial = new();
        public readonly HashSet<CachedLutEntry> EvictedPinnedLut = new();
    }

    private sealed class NodeTexturePinLease : IDisposable
    {
        private readonly IntPtr _devicePtr;
        private readonly CachedGpuEntry? _spatial;
        private readonly CachedLutEntry? _lut;
        private int _disposed;

        public NodeTexturePinLease(IntPtr devicePtr, CachedGpuEntry? spatial, CachedLutEntry? lut)
        {
            _devicePtr = devicePtr;
            _spatial = spatial;
            _lut = lut;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleasePin(_devicePtr, _spatial, _lut);
            }
        }
    }

    private static readonly object s_lock = new();
    private static readonly Dictionary<IntPtr, DeviceCache> s_deviceCaches = new();
    private static readonly List<(string Hash, BakedNodeMaterial Baked)> s_cpuLru = new();

    private static DeviceCache GetOrCreateDeviceCacheLocked(IntPtr devPtr)
    {
        if (!s_deviceCaches.TryGetValue(devPtr, out var cache))
        {
            cache = new DeviceCache();
            s_deviceCaches[devPtr] = cache;
        }
        return cache;
    }

    private static void ReleasePin(IntPtr devicePtr, CachedGpuEntry? spatial, CachedLutEntry? lut)
    {
        lock (s_lock)
        {
            s_deviceCaches.TryGetValue(devicePtr, out var devCache);
            if (spatial != null)
            {
                spatial.PinCount = Math.Max(0, spatial.PinCount - 1);
                if (spatial.Evicted && spatial.PinCount <= 0)
                {
                    devCache?.EvictedPinnedSpatial.Remove(spatial);
                    spatial.Dispose();
                }
            }
            if (lut != null)
            {
                lut.PinCount = Math.Max(0, lut.PinCount - 1);
                if (lut.Evicted && lut.PinCount <= 0)
                {
                    devCache?.EvictedPinnedLut.Remove(lut);
                    lut.Dispose();
                }
            }
        }
    }

    private static void EvictExcessSpatialLocked(DeviceCache devCache)
    {
        while (devCache.SpatialLru.Count > MaxEntriesPerDevice)
        {
            var oldest = devCache.SpatialLru[0].Entry;
            devCache.SpatialLru.RemoveAt(0);
            oldest.Evicted = true;
            if (oldest.PinCount <= 0)
                oldest.Dispose();
            else
                devCache.EvictedPinnedSpatial.Add(oldest);
        }
    }

    private static void EvictExcessLutLocked(DeviceCache devCache)
    {
        while (devCache.LutLru.Count > MaxLutEntriesPerDevice)
        {
            var oldest = devCache.LutLru[0].Entry;
            devCache.LutLru.RemoveAt(0);
            oldest.Evicted = true;
            if (oldest.PinCount <= 0)
                oldest.Dispose();
            else
                devCache.EvictedPinnedLut.Add(oldest);
        }
    }

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
        var result = GetOrBakePinnedAtTime(device, ctx, graph, resolutionOverride, evalTime, out var pinLease);
        pinLease?.Dispose();
        return result;
    }

    public static BakedGpuMaterialTextures? GetOrBakePinnedAtTime(
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        MaterialNodeGraph? graph,
        int? resolutionOverride,
        ShaderEvalTime evalTime,
        out IDisposable? pinLease)
    {
        pinLease = null;
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

        ID3D11ShaderResourceView? lutSrv = GetOrUploadLutPinned(
            device, ctx, scalars.ShaderLutHash, scalars.ShaderLutBgra, out CachedLutEntry? pinnedLut);

        try
        {
            string? spatialHash = ShaderNodeEvaluator.ComputeSpatialTextureHashEx(
                graph, effectiveRes, effTime, device, out bool usesLayerCpu);
            if (spatialHash == null)
            {
                pinLease = pinnedLut != null ? new NodeTexturePinLease(devPtr, null, pinnedLut) : null;
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
                var devCache = GetOrCreateDeviceCacheLocked(devPtr);
                for (int i = devCache.SpatialLru.Count - 1; i >= 0; i--)
                {
                    var e = devCache.SpatialLru[i];
                    if (string.Equals(e.Hash, spatialHash, StringComparison.Ordinal))
                    {
                        if (e.Entry.IsDisposed)
                        {
                            devCache.SpatialLru.RemoveAt(i);
                            continue;
                        }
                        devCache.SpatialLru.RemoveAt(i);
                        devCache.SpatialLru.Add(e);
                        e.Entry.PinCount++;
                        pinLease = new NodeTexturePinLease(devPtr, e.Entry, pinnedLut);
                        return e.Entry.CreateView(in scalars, lutSrv);
                    }
                }

                if (!usesLayerCpu)
                {
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
            }

            if (cpuBaked == null)
            {
                cpuBaked = ShaderNodeEvaluator.EvaluateAndBakeAtTime(graph, effectiveRes, effTime, device, ctx);
                if (!usesLayerCpu)
                {
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
                if (pinnedLut != null)
                {
                    ReleasePin(devPtr, null, pinnedLut);
                    pinnedLut = null;
                }
                return null;
            }

            lock (s_lock)
            {
                var devCache = GetOrCreateDeviceCacheLocked(devPtr);
                for (int i = devCache.SpatialLru.Count - 1; i >= 0; i--)
                {
                    var existing = devCache.SpatialLru[i];
                    if (string.Equals(existing.Hash, spatialHash, StringComparison.Ordinal))
                    {
                        if (existing.Entry.IsDisposed)
                        {
                            devCache.SpatialLru.RemoveAt(i);
                            continue;
                        }
                        entry.Dispose();
                        devCache.SpatialLru.RemoveAt(i);
                        devCache.SpatialLru.Add(existing);
                        existing.Entry.PinCount++;
                        pinLease = new NodeTexturePinLease(devPtr, existing.Entry, pinnedLut);
                        return existing.Entry.CreateView(in scalars, lutSrv);
                    }
                }

                entry.PinCount++;
                devCache.SpatialLru.Add((spatialHash, entry));
                EvictExcessSpatialLocked(devCache);
                pinLease = new NodeTexturePinLease(devPtr, entry, pinnedLut);
                return entry.CreateView(in scalars, lutSrv);
            }
        }
        catch
        {
            if (pinnedLut != null)
                ReleasePin(devPtr, null, pinnedLut);
            throw;
        }
    }

    private static ID3D11ShaderResourceView? GetOrUploadLutPinned(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        string? lutHash,
        byte[]? lutBgra,
        out CachedLutEntry? pinnedEntry)
    {
        pinnedEntry = null;
        const int lutWidth = ShaderNodeEvaluator.ShaderLutWidth;
        if (string.IsNullOrEmpty(lutHash) || lutBgra == null || lutBgra.Length < lutWidth * 2 * 4)
            return null;

        IntPtr devPtr = device.NativePointer;
        lock (s_lock)
        {
            EvictLostLocked(device);
            var devCache = GetOrCreateDeviceCacheLocked(devPtr);
            for (int i = devCache.LutLru.Count - 1; i >= 0; i--)
            {
                var e = devCache.LutLru[i];
                if (string.Equals(e.Hash, lutHash, StringComparison.Ordinal))
                {
                    if (e.Entry.IsDisposed || e.Entry.Srv.NativePointer == IntPtr.Zero)
                    {
                        devCache.LutLru.RemoveAt(i);
                        continue;
                    }
                    devCache.LutLru.RemoveAt(i);
                    devCache.LutLru.Add(e);
                    e.Entry.PinCount++;
                    pinnedEntry = e.Entry;
                    return e.Entry.Srv;
                }
            }
        }

        var uploaded = ModelTextureLoader.UploadBgraBytes(device, ctx, lutBgra, lutWidth, 2);
        if (uploaded is not { } res) return null;

        var newEntry = new CachedLutEntry(res.Texture, res.Srv);
        lock (s_lock)
        {
            var devCache = GetOrCreateDeviceCacheLocked(devPtr);
            for (int i = devCache.LutLru.Count - 1; i >= 0; i--)
            {
                var existing = devCache.LutLru[i];
                if (string.Equals(existing.Hash, lutHash, StringComparison.Ordinal))
                {
                    if (existing.Entry.IsDisposed || existing.Entry.Srv.NativePointer == IntPtr.Zero)
                    {
                        devCache.LutLru.RemoveAt(i);
                        continue;
                    }
                    newEntry.Dispose();
                    devCache.LutLru.RemoveAt(i);
                    devCache.LutLru.Add(existing);
                    existing.Entry.PinCount++;
                    pinnedEntry = existing.Entry;
                    return existing.Entry.Srv;
                }
            }

            newEntry.PinCount++;
            devCache.LutLru.Add((lutHash, newEntry));
            EvictExcessLutLocked(devCache);
            pinnedEntry = newEntry;
            return newEntry.Srv;
        }
    }

    private static void DisposeDeviceCacheLocked(DeviceCache devCache)
    {
        foreach (var item in devCache.SpatialLru)
            item.Entry.Dispose();
        devCache.SpatialLru.Clear();

        foreach (var item in devCache.EvictedPinnedSpatial)
            item.Dispose();
        devCache.EvictedPinnedSpatial.Clear();

        foreach (var item in devCache.LutLru)
            item.Entry.Dispose();
        devCache.LutLru.Clear();

        foreach (var item in devCache.EvictedPinnedLut)
            item.Dispose();
        devCache.EvictedPinnedLut.Clear();
    }

    public static void ClearForDevice(IntPtr devicePtr)
    {
        if (devicePtr == IntPtr.Zero) return;
        lock (s_lock)
        {
            if (s_deviceCaches.TryGetValue(devicePtr, out var devCache))
            {
                DisposeDeviceCacheLocked(devCache);
                s_deviceCaches.Remove(devicePtr);
            }
        }
    }

    public static void ClearAll()
    {
        lock (s_lock)
        {
            foreach (var devCache in s_deviceCaches.Values)
                DisposeDeviceCacheLocked(devCache);
            s_deviceCaches.Clear();
            s_cpuLru.Clear();
        }
        ShaderNodeEvaluator.ClearCaches();
    }

    internal static (int SpatialCount, int LutCount, int EvictedPinnedSpatialCount, int EvictedPinnedLutCount)
        GetDeviceCacheStatsForTests(IntPtr devicePtr)
    {
        lock (s_lock)
        {
            if (!s_deviceCaches.TryGetValue(devicePtr, out var devCache))
                return (0, 0, 0, 0);
            return (
                devCache.SpatialLru.Count,
                devCache.LutLru.Count,
                devCache.EvictedPinnedSpatial.Count,
                devCache.EvictedPinnedLut.Count);
        }
    }

    private static void EvictLostLocked(ID3D11Device dev)
    {
        IntPtr ptr = dev.NativePointer;
        bool lost = false;
        try { lost = dev.DeviceRemovedReason.Failure; } catch { lost = true; }
        if (!lost) return;

        if (s_deviceCaches.TryGetValue(ptr, out var devCache))
        {
            DisposeDeviceCacheLocked(devCache);
            s_deviceCaches.Remove(ptr);
        }
    }
}
