using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using Iyahon_D3D11Renderer_Core.Model3D.Parsers;
using Vortice.Direct3D11;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

internal sealed class CachedGpuModel : IDisposable
{
    private readonly List<ID3D11Texture2D> _ownedTextures;
    private readonly List<ID3D11ShaderResourceView> _ownedSrvs;
    private List<IDisposable> _pinnedNodeResources;
    private readonly ObjPartRenderInfo[] _unsortedParts;
    private readonly int[]? _partMaterialIndices;
    private bool _disposed;

    public IntPtr DevicePtr { get; }
    public ID3D11Buffer VertexBuffer { get; }
    public ID3D11Buffer IndexBuffer { get; }
    public ObjPartRenderInfo[] Parts { get; }
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    public Vector3[]? BoundingSupportPoints { get; init; }
    public Vector3 ModelCenter { get; }
    public float ModelScale { get; }
    public int IndexCount { get; }
    public long EstimatedGpuBytes { get; }
    public bool IsDisposed => _disposed;
    internal int[]? PartMaterialIndices => _partMaterialIndices;
    internal ObjPartRenderInfo[] UnsortedParts => _unsortedParts;
    internal ObjPartRenderInfo[]? BaseDonorParts { get; }
    internal bool HasTimeDependentMaterial { get; set; }
    internal Viewport3D.Modeling.EditableMesh? SourceEditableMesh { get; set; }
    internal long LastTimeKey { get; set; } = long.MinValue;
    internal object SyncRoot { get; } = new();

    public ObjPartRenderInfo? GetPartByOriginalIndex(int originalIndex)
        => (originalIndex >= 0 && originalIndex < _unsortedParts.Length) ? _unsortedParts[originalIndex] : null;

    public CachedGpuModel(
        IntPtr devicePtr,
        ID3D11Buffer vb,
        ID3D11Buffer ib,
        ObjPartRenderInfo[] parts,
        List<ID3D11Texture2D> ownedTextures,
        List<ID3D11ShaderResourceView> ownedSrvs,
        Vector3 boundsMin,
        Vector3 boundsMax,
        Vector3 modelCenter,
        float modelScale,
        int indexCount,
        long estimatedGpuBytes,
        ObjPartRenderInfo[]? unsortedParts = null,
        int[]? partMaterialIndices = null,
        List<IDisposable>? pinnedNodeResources = null,
        ObjPartRenderInfo[]? baseDonorParts = null)
    {
        DevicePtr = devicePtr;
        VertexBuffer = vb;
        IndexBuffer = ib;
        Parts = parts;
        _unsortedParts = unsortedParts ?? parts;
        _partMaterialIndices = partMaterialIndices;
        _ownedTextures = ownedTextures;
        _ownedSrvs = ownedSrvs;
        _pinnedNodeResources = pinnedNodeResources ?? new List<IDisposable>();
        BaseDonorParts = baseDonorParts;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        ModelCenter = modelCenter;
        ModelScale = modelScale;
        IndexCount = indexCount;
        EstimatedGpuBytes = estimatedGpuBytes;
    }

    internal void ReplacePinnedNodeResources(List<IDisposable> nextPins)
    {
        List<IDisposable> toDispose;
        lock (SyncRoot)
        {
            if (_disposed)
            {
                toDispose = nextPins;
            }
            else
            {
                toDispose = _pinnedNodeResources;
                _pinnedNodeResources = nextPins;
            }
        }
        foreach (var pin in toDispose)
        {
            try { pin.Dispose(); } catch { }
        }
    }

    internal bool HasDisposedResources()
    {
        if (_disposed || VertexBuffer.NativePointer == IntPtr.Zero || IndexBuffer.NativePointer == IntPtr.Zero)
            return true;
        return ObjModelData.HasDisposedPartResources(Parts) || ObjModelData.HasDisposedPartResources(_unsortedParts);
    }

    private ID3D11Buffer? _smoothNormalBuffer;
    private readonly object _smoothNormalLock = new();
    internal Vector3[]? CpuPositions;
    internal Vector3[]? CpuNormals;

    public ID3D11Buffer? GetOrCreateSmoothNormalBuffer(ID3D11Device? device, ID3D11DeviceContext? ctx = null)
    {
        if (_disposed || device == null) return null;
        var existing = _smoothNormalBuffer;
        if (existing != null && existing.NativePointer != IntPtr.Zero)
            return existing;

        lock (_smoothNormalLock)
        {
            if (_disposed) return null;
            if (_smoothNormalBuffer != null && _smoothNormalBuffer.NativePointer != IntPtr.Zero)
                return _smoothNormalBuffer;

            if (CpuPositions != null && CpuNormals != null)
            {
                _smoothNormalBuffer = ModelGpuCache.CreateSmoothNormalBufferFromCpu(device, CpuPositions, CpuNormals);
            }
            else if (ctx != null && VertexBuffer != null && VertexBuffer.NativePointer != IntPtr.Zero)
            {
                _smoothNormalBuffer = ModelGpuCache.CreateSmoothNormalBufferFromGpuVb(device, ctx, VertexBuffer);
            }
            return _smoothNormalBuffer;
        }
    }

    public ObjModelData CreateRenderData(
        bool normalizeSize,
        bool groundToOrigin,
        float unitScale,
        Vector3 localScale,
        Vector3 localRotationDeg,
        Vector4 tintColor,
        bool overrideMaterial,
        float overrideMetallic,
        float overrideRoughness,
        float emissiveMultiplier,
        bool cullBackface)
    {
        return new ObjModelData
        {
            VertexBuffer = VertexBuffer,
            IndexBuffer = IndexBuffer,
            Parts = Parts,
            BoundsMin = BoundsMin,
            BoundsMax = BoundsMax,
            BoundingSupportPoints = BoundingSupportPoints,
            ModelCenter = ModelCenter,
            ModelScale = ModelScale,
            IndexCount = IndexCount,
            NormalizeSize = normalizeSize,
            GroundToOrigin = groundToOrigin,
            UnitScale = unitScale,
            LocalScale = localScale,
            LocalRotationDeg = localRotationDeg,
            TintColor = tintColor,
            OverrideMaterial = overrideMaterial,
            OverrideMetallic = overrideMetallic,
            OverrideRoughness = overrideRoughness,
            EmissiveMultiplier = emissiveMultiplier,
            CullBackface = cullBackface,
            SmoothNormalBuffer = _smoothNormalBuffer,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<IDisposable> pinsToDispose;
        lock (SyncRoot)
        {
            pinsToDispose = _pinnedNodeResources;
            _pinnedNodeResources = new List<IDisposable>();
        }
        foreach (var pin in pinsToDispose)
        {
            try { pin.Dispose(); } catch { }
        }

        foreach (var srv in _ownedSrvs)
        {
            try { srv.Dispose(); } catch { }
        }
        _ownedSrvs.Clear();

        foreach (var tex in _ownedTextures)
        {
            try { tex.Dispose(); } catch { }
        }
        _ownedTextures.Clear();

        try { _smoothNormalBuffer?.Dispose(); _smoothNormalBuffer = null; } catch { }
        try { IndexBuffer.Dispose(); } catch { }
        try { VertexBuffer.Dispose(); } catch { }
    }
}

internal static class ModelGpuCache
{
    private const int MaxCachedModelsPerDevice = 24;

    private readonly record struct CacheKey(
        IntPtr DevicePtr,
        string Path,
        long LastWriteUtcTicks,
        long FileLength,
        int MaxTextureSize);

    private readonly record struct EmbeddedCacheKey(
        IntPtr DevicePtr,
        int DataHash,
        int DataLength,
        string BaseFilePathLower,
        int MaxTextureSize);

    private readonly record struct ModifiedCacheKey(
        IntPtr DevicePtr,
        string BaseSourceKey,
        string ModifierKey,
        string SceneTargetsKey,
        int MaxTextureSize);

    private const int MaxEmbeddedModelsPerDevice = 32;
    private const int MaxProceduralModelsPerDevice = 32;
    private const int MaxParsedModels = 6;
    private const int MaxModifiedModelsPerDevice = 32;

    private static readonly object s_lock = new();
    private static readonly List<(CacheKey Key, CachedGpuModel Model)> s_lru = new();
    private static readonly List<(EmbeddedCacheKey Key, string Data, CachedGpuModel Model)> s_embeddedLru = new();
    private static readonly List<(IntPtr DevicePtr, string Key, CachedGpuModel Model)> s_proceduralLru = new();
    private static readonly List<(string PathLower, long WriteTicks, long FileLength, ParsedModel Parsed, Viewport3D.Modeling.EditableMesh BaseEditable)> s_parsedModelLru = new();
    private static readonly List<(ModifiedCacheKey Key, string? EmbeddedData, CachedGpuModel Model)> s_modifiedLru = new();

    static ModelGpuCache()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ClearAll();
    }

    internal static string ComputeStringHash64(string text)
    {
        ulong h = 14695981039346656037UL;
        unchecked
        {
            for (int i = 0; i < text.Length; i++)
            {
                h ^= text[i];
                h *= 1099511628211UL;
            }
        }
        return h.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void EvictExcessForDeviceLocked<T>(
        List<T> list,
        int maxPerDevice,
        Func<T, IntPtr> getDevPtr,
        Func<T, CachedGpuModel> getModel,
        IntPtr devPtr)
    {
        int devCount = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (getDevPtr(list[i]) == devPtr)
                devCount++;
        }
        while (devCount > maxPerDevice)
        {
            int removeIdx = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (getDevPtr(list[i]) == devPtr)
                {
                    removeIdx = i;
                    break;
                }
            }
            if (removeIdx < 0) break;
            var oldModel = getModel(list[removeIdx]);
            list.RemoveAt(removeIdx);
            oldModel.Dispose();
            devCount--;
        }
    }

    public static CachedGpuModel? GetOrBuildProceduralMesh(
        ID3D11Device? device,
        string cacheKey,
        Func<Viewport3D.Modeling.EditableMesh> meshFactory)
    {
        if (device == null || string.IsNullOrWhiteSpace(cacheKey))
            return null;

        IntPtr devPtr = device.NativePointer;
        lock (s_lock)
        {
            EvictStaleEntriesLocked(device);
            for (int i = s_proceduralLru.Count - 1; i >= 0; i--)
            {
                var entry = s_proceduralLru[i];
                if (entry.DevicePtr == devPtr && string.Equals(entry.Key, cacheKey, StringComparison.Ordinal))
                {
                    if (entry.Model.HasDisposedResources())
                    {
                        s_proceduralLru.RemoveAt(i);
                        entry.Model.Dispose();
                        continue;
                    }
                    s_proceduralLru.RemoveAt(i);
                    s_proceduralLru.Add(entry);
                    return entry.Model;
                }
            }
        }

        var mesh = meshFactory();
        var parsed = mesh.ToParsedModel();
        ID3D11Buffer? vb = null;
        ID3D11Buffer? ib = null;
        CachedGpuModel? uploaded = null;
        try
        {
            UploadVertexAndIndexBuffers(device, parsed, out vb, out ib, out long gpuBytes);
            int partCount = parsed.Parts.Count;
            var gpuParts = new ObjPartRenderInfo[partCount];
            for (int i = 0; i < partCount; i++)
            {
                var p = parsed.Parts[i];
                gpuParts[i] = new ObjPartRenderInfo
                {
                    IndexOffset = p.IndexOffset,
                    IndexCount = p.IndexCount,
                    BaseColor = p.BaseColor,
                    EmissiveColor = p.EmissiveColor,
                    Metallic = p.Metallic,
                    Roughness = p.Roughness,
                    Transmission = p.Transmission,
                    Ior = p.Ior > 0f ? p.Ior : 1.5f,
                    ForceOpaque = p.ForceOpaque,
                    CullBackface = p.CullBackface,
                    DisableShadow = !p.CastShadow,
                };
            }

            var unsortedParts = (ObjPartRenderInfo[])gpuParts.Clone();
            StablePartitionOpaqueFirst(gpuParts);

            uploaded = new CachedGpuModel(
                devPtr,
                vb,
                ib,
                gpuParts,
                new List<ID3D11Texture2D>(),
                new List<ID3D11ShaderResourceView>(),
                parsed.BoundsMin,
                parsed.BoundsMax,
                parsed.ModelCenter,
                parsed.ModelScale,
                parsed.Indices.Length,
                gpuBytes,
                unsortedParts)
            {
                BoundingSupportPoints = ComputeBoundingSupportPoints(parsed),
            };
        }
        catch (Exception ex)
        {
            ModelLoadHelper.Log($"プロシージャル図形GPU生成失敗: {ex.Message}");
            try { ib?.Dispose(); } catch { }
            try { vb?.Dispose(); } catch { }
            return null;
        }

        lock (s_lock)
        {
            for (int i = s_proceduralLru.Count - 1; i >= 0; i--)
            {
                var existing = s_proceduralLru[i];
                if (existing.DevicePtr == devPtr && string.Equals(existing.Key, cacheKey, StringComparison.Ordinal))
                {
                    if (existing.Model.HasDisposedResources())
                    {
                        s_proceduralLru.RemoveAt(i);
                        existing.Model.Dispose();
                        continue;
                    }
                    uploaded.Dispose();
                    s_proceduralLru.RemoveAt(i);
                    s_proceduralLru.Add(existing);
                    return existing.Model;
                }
            }

            s_proceduralLru.Add((devPtr, cacheKey, uploaded));
            EvictExcessForDeviceLocked(s_proceduralLru, MaxProceduralModelsPerDevice, e => e.DevicePtr, e => e.Model, devPtr);
            return uploaded;
        }
    }

    public static CachedGpuModel? GetOrLoad(
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        string? filePath,
        int maxTextureSize)
    {
        if (device == null || ctx == null || string.IsNullOrWhiteSpace(filePath))
            return null;

        CacheKey key;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return null;
            key = new CacheKey(
                device.NativePointer,
                info.FullName.ToLowerInvariant(),
                info.LastWriteTimeUtc.Ticks,
                info.Length,
                maxTextureSize);
        }
        catch
        {
            return null;
        }

        lock (s_lock)
        {
            // 無効化・デバイスロストされたエントリを整理
            EvictStaleEntriesLocked(device);

            for (int i = s_lru.Count - 1; i >= 0; i--)
            {
                if (s_lru[i].Key.Equals(key))
                {
                    var hit = s_lru[i];
                    if (hit.Model.HasDisposedResources())
                    {
                        s_lru.RemoveAt(i);
                        hit.Model.Dispose();
                        continue;
                    }
                    s_lru.RemoveAt(i);
                    s_lru.Add(hit);
                    return hit.Model;
                }
            }
        }

        // ロック外でパース＆GPUアップロード（他フレーム・別モデルをブロックしない）
        var parsed = ModelParserDispatcher.Parse(key.Path);
        if (parsed == null || parsed.Vertices.Length == 0 || parsed.Indices.Length < 3)
            return null;

        var uploaded = UploadToGpu(device, ctx, parsed, maxTextureSize);
        if (uploaded == null)
            return null;

        lock (s_lock)
        {
            for (int i = s_lru.Count - 1; i >= 0; i--)
            {
                if (s_lru[i].Key.Equals(key))
                {
                    var existing = s_lru[i];
                    if (existing.Model.HasDisposedResources())
                    {
                        s_lru.RemoveAt(i);
                        existing.Model.Dispose();
                        continue;
                    }
                    // 競合で既に他スレッドが登録済みなら新しい方を破棄して既存を返す
                    uploaded.Dispose();
                    s_lru.RemoveAt(i);
                    s_lru.Add(existing);
                    return existing.Model;
                }
            }

            s_lru.Add((key, uploaded));
            EvictExcessForDeviceLocked(s_lru, MaxCachedModelsPerDevice, e => e.Key.DevicePtr, e => e.Model, key.DevicePtr);
            return uploaded;
        }
    }

    public static CachedGpuModel? GetOrBuildFromEditableMesh(
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        string? embeddedMeshData,
        string? baseFilePath,
        int maxTextureSize)
        => GetOrBuildFromEditableMeshAtTime(device, ctx, embeddedMeshData, baseFilePath, maxTextureSize, null);

    public static CachedGpuModel? GetOrBuildFromEditableMeshAtTime(
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        string? embeddedMeshData,
        string? baseFilePath,
        int maxTextureSize,
        Viewport3D.Shading.ShaderEvalTime? evalTime)
    {
        if (device == null || ctx == null || string.IsNullOrWhiteSpace(embeddedMeshData))
            return null;

        string baseLower = string.IsNullOrWhiteSpace(baseFilePath)
            ? string.Empty
            : baseFilePath.Trim().ToLowerInvariant();
        var key = new EmbeddedCacheKey(
            device.NativePointer,
            embeddedMeshData.GetHashCode(StringComparison.Ordinal),
            embeddedMeshData.Length,
            baseLower,
            maxTextureSize);

        CachedGpuModel? hitModel = null;
        lock (s_lock)
        {
            EvictStaleEntriesLocked(device);
            for (int i = s_embeddedLru.Count - 1; i >= 0; i--)
            {
                var entry = s_embeddedLru[i];
                if (entry.Key.Equals(key) && string.Equals(entry.Data, embeddedMeshData, StringComparison.Ordinal))
                {
                    if (entry.Model.HasDisposedResources())
                    {
                        s_embeddedLru.RemoveAt(i);
                        entry.Model.Dispose();
                        continue;
                    }
                    s_embeddedLru.RemoveAt(i);
                    s_embeddedLru.Add(entry);
                    hitModel = entry.Model;
                    break;
                }
            }
        }

        if (hitModel != null)
        {
            if (hitModel.HasTimeDependentMaterial && hitModel.SourceEditableMesh != null && evalTime.HasValue)
            {
                long tKey = ComputeCombinedTimeKey(hitModel.SourceEditableMesh, evalTime.Value);
                if (tKey != hitModel.LastTimeKey)
                {
                    lock (hitModel.SyncRoot)
                    {
                        if (tKey != hitModel.LastTimeKey)
                        {
                            RefreshTransientMaterials(device, ctx, hitModel, hitModel.SourceEditableMesh, null, null, evalTime);
                            hitModel.LastTimeKey = tKey;
                        }
                    }
                }
            }
            return hitModel;
        }

        if (!Viewport3D.Modeling.EditableMesh.TryDeserialize(embeddedMeshData, out var editable) || editable == null)
            return null;

        var parsed = editable.ToParsedModel();
        if (!string.IsNullOrWhiteSpace(baseFilePath))
        {
            var baseParsed = ModelParserDispatcher.Parse(baseFilePath);
            if (baseParsed != null)
            {
                CopyPartTexturesFromTemplate(parsed, baseParsed);
            }
        }

        var uploaded = UploadToGpu(device, ctx, parsed, maxTextureSize, evalTime);
        if (uploaded == null)
            return null;

        bool timeDep = HasAnyTimeDependency(editable);
        if (timeDep)
        {
            uploaded.HasTimeDependentMaterial = true;
            uploaded.SourceEditableMesh = editable;
            uploaded.LastTimeKey = evalTime.HasValue ? ComputeCombinedTimeKey(editable, evalTime.Value) : 0L;
        }

        lock (s_lock)
        {
            for (int i = s_embeddedLru.Count - 1; i >= 0; i--)
            {
                var existing = s_embeddedLru[i];
                if (existing.Key.Equals(key) && string.Equals(existing.Data, embeddedMeshData, StringComparison.Ordinal))
                {
                    if (existing.Model.HasDisposedResources())
                    {
                        s_embeddedLru.RemoveAt(i);
                        existing.Model.Dispose();
                        continue;
                    }
                    uploaded.Dispose();
                    s_embeddedLru.RemoveAt(i);
                    s_embeddedLru.Add(existing);
                    return existing.Model;
                }
            }

            s_embeddedLru.Add((key, embeddedMeshData, uploaded));
            EvictExcessForDeviceLocked(s_embeddedLru, MaxEmbeddedModelsPerDevice, e => e.Key.DevicePtr, e => e.Model, key.DevicePtr);
            return uploaded;
        }
    }

    private static bool TryGetParsedModelCached(
        string? filePath,
        out ParsedModel? parsed,
        out Viewport3D.Modeling.EditableMesh? baseEditable,
        out string fileKey)
    {
        parsed = null;
        baseEditable = null;
        fileKey = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        string fullLower;
        long writeTicks;
        long fileLength;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return false;
            fullLower = info.FullName.ToLowerInvariant();
            writeTicks = info.LastWriteTimeUtc.Ticks;
            fileLength = info.Length;
            fileKey = $"{fullLower}:{writeTicks}:{fileLength}";
        }
        catch
        {
            return false;
        }

        lock (s_lock)
        {
            for (int i = 0; i < s_parsedModelLru.Count; i++)
            {
                var e = s_parsedModelLru[i];
                if (e.WriteTicks == writeTicks && e.FileLength == fileLength &&
                    string.Equals(e.PathLower, fullLower, StringComparison.Ordinal))
                {
                    s_parsedModelLru.RemoveAt(i);
                    s_parsedModelLru.Add(e);
                    parsed = e.Parsed;
                    baseEditable = e.BaseEditable.Clone();
                    return true;
                }
            }
        }

        var newlyParsed = ModelParserDispatcher.Parse(fullLower);
        if (newlyParsed == null || newlyParsed.Vertices.Length == 0 || newlyParsed.Indices.Length < 3)
            return false;

        var newlyEdit = Viewport3D.Modeling.EditableMesh.FromParsedModel(newlyParsed);
        newlyEdit.SourceFilePath = filePath;

        lock (s_lock)
        {
            for (int i = 0; i < s_parsedModelLru.Count; i++)
            {
                var e = s_parsedModelLru[i];
                if (e.WriteTicks == writeTicks && e.FileLength == fileLength &&
                    string.Equals(e.PathLower, fullLower, StringComparison.Ordinal))
                {
                    s_parsedModelLru.RemoveAt(i);
                    s_parsedModelLru.Add(e);
                    parsed = e.Parsed;
                    baseEditable = e.BaseEditable.Clone();
                    return true;
                }
            }

            s_parsedModelLru.Add((fullLower, writeTicks, fileLength, newlyParsed, newlyEdit));
            while (s_parsedModelLru.Count > MaxParsedModels)
                s_parsedModelLru.RemoveAt(0);
        }

        parsed = newlyParsed;
        baseEditable = newlyEdit.Clone();
        return true;
    }

    internal static Viewport3D.Modeling.EditableMesh? GetOrLoadBaseEditableMesh(
        string? embeddedMeshData,
        string? filePath,
        Func<Viewport3D.Modeling.EditableMesh>? proceduralFactory,
        out ParsedModel? textureTemplate)
    {
        textureTemplate = null;
        if (!string.IsNullOrWhiteSpace(filePath))
            TryGetParsedModelCached(filePath, out textureTemplate, out _, out _);

        if (!string.IsNullOrWhiteSpace(embeddedMeshData) &&
            Viewport3D.Modeling.EditableMesh.TryDeserialize(embeddedMeshData, out var embMesh) &&
            embMesh != null)
        {
            embMesh.SourceFilePath ??= filePath;
            return embMesh;
        }

        if (proceduralFactory != null)
            return proceduralFactory();

        if (!string.IsNullOrWhiteSpace(filePath) &&
            TryGetParsedModelCached(filePath, out textureTemplate, out var fileEdit, out _))
            return fileEdit;

        return null;
    }

    public static CachedGpuModel? GetOrBuildWithModifiersAtTime(
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        string? embeddedMeshData,
        string? filePath,
        string? proceduralCacheKey,
        Func<Viewport3D.Modeling.EditableMesh>? proceduralFactory,
        string? modifierStackData,
        Viewport3D.Modifiers.SceneModifierContext? sceneCtx,
        int selfLayer,
        Matrix4x4 selfModelToWorld,
        int maxTextureSize,
        Viewport3D.Shading.ShaderEvalTime? evalTime)
    {
        if (device == null || ctx == null) return null;

        bool hasEnabledMods = Viewport3D.Modifiers.MeshModifierStack.TryDeserialize(modifierStackData, out var modStack) &&
                              modStack.HasEnabledModifiers;
        if (!hasEnabledMods)
        {
            if (!string.IsNullOrWhiteSpace(embeddedMeshData))
                return GetOrBuildFromEditableMeshAtTime(device, ctx, embeddedMeshData, filePath, maxTextureSize, evalTime);
            if (!string.IsNullOrWhiteSpace(proceduralCacheKey) && proceduralFactory != null)
                return GetOrBuildProceduralMesh(device, proceduralCacheKey, proceduralFactory);
            if (!string.IsNullOrWhiteSpace(filePath))
                return GetOrLoad(device, ctx, filePath, maxTextureSize);
            return null;
        }

        string baseLower = string.IsNullOrWhiteSpace(filePath) ? string.Empty : filePath.Trim().ToLowerInvariant();
        string baseSourceKey;
        if (!string.IsNullOrWhiteSpace(embeddedMeshData))
        {
            baseSourceKey = $"emb:{ComputeStringHash64(embeddedMeshData)}:{embeddedMeshData.Length}:{baseLower}";
        }
        else if (!string.IsNullOrWhiteSpace(proceduralCacheKey))
        {
            baseSourceKey = $"proc:{proceduralCacheKey}";
        }
        else if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists) return null;
                baseSourceKey = $"file:{info.FullName.ToLowerInvariant()}:{info.LastWriteTimeUtc.Ticks}:{info.Length}";
            }
            catch { return null; }
        }
        else
        {
            return null;
        }

        string modKey = modStack.ComputeEnabledContentKey();
        string sceneKey = (sceneCtx != null && modStack.HasSceneDependentEnabledModifiers)
            ? sceneCtx.ComputeSceneTargetsCacheKey(modStack, selfLayer, selfModelToWorld)
            : string.Empty;

        var cacheKey = new ModifiedCacheKey(device.NativePointer, baseSourceKey, modKey, sceneKey, maxTextureSize);

        CachedGpuModel? hitModel = null;
        lock (s_lock)
        {
            EvictStaleEntriesLocked(device);
            for (int i = s_modifiedLru.Count - 1; i >= 0; i--)
            {
                var entry = s_modifiedLru[i];
                if (entry.Key.Equals(cacheKey) &&
                    string.Equals(entry.EmbeddedData, embeddedMeshData, StringComparison.Ordinal))
                {
                    if (entry.Model.HasDisposedResources())
                    {
                        s_modifiedLru.RemoveAt(i);
                        entry.Model.Dispose();
                        continue;
                    }
                    s_modifiedLru.RemoveAt(i);
                    s_modifiedLru.Add(entry);
                    hitModel = entry.Model;
                    break;
                }
            }
        }

        if (hitModel != null)
        {
            if (hitModel.HasTimeDependentMaterial && hitModel.SourceEditableMesh != null && evalTime.HasValue)
            {
                long tKey = ComputeCombinedTimeKey(hitModel.SourceEditableMesh, evalTime.Value);
                if (tKey != hitModel.LastTimeKey)
                {
                    lock (hitModel.SyncRoot)
                    {
                        if (tKey != hitModel.LastTimeKey)
                        {
                            RefreshTransientMaterials(device, ctx, hitModel, hitModel.SourceEditableMesh, null, null, evalTime);
                            hitModel.LastTimeKey = tKey;
                        }
                    }
                }
            }
            return hitModel;
        }

        var baseMesh = GetOrLoadBaseEditableMesh(embeddedMeshData, filePath, proceduralFactory, out var textureTemplate);
        if (baseMesh == null) return null;

        var evalMesh = Viewport3D.Modifiers.MeshModifierEvaluator.Evaluate(
            baseMesh, modStack, sceneCtx, selfLayer, selfModelToWorld);

        var parsed = evalMesh.ToParsedModel();
        if (textureTemplate != null)
            CopyPartTexturesFromTemplate(parsed, textureTemplate);

        var uploaded = UploadToGpu(device, ctx, parsed, maxTextureSize, evalTime);
        if (uploaded == null) return null;

        if (HasAnyTimeDependency(evalMesh))
        {
            uploaded.HasTimeDependentMaterial = true;
            uploaded.SourceEditableMesh = evalMesh;
            uploaded.LastTimeKey = evalTime.HasValue ? ComputeCombinedTimeKey(evalMesh, evalTime.Value) : 0L;
        }

        lock (s_lock)
        {
            for (int i = s_modifiedLru.Count - 1; i >= 0; i--)
            {
                var existing = s_modifiedLru[i];
                if (existing.Key.Equals(cacheKey) &&
                    string.Equals(existing.EmbeddedData, embeddedMeshData, StringComparison.Ordinal))
                {
                    if (existing.Model.HasDisposedResources())
                    {
                        s_modifiedLru.RemoveAt(i);
                        existing.Model.Dispose();
                        continue;
                    }
                    uploaded.Dispose();
                    s_modifiedLru.RemoveAt(i);
                    s_modifiedLru.Add(existing);
                    return existing.Model;
                }
            }

            s_modifiedLru.Add((cacheKey, embeddedMeshData, uploaded));
            EvictExcessForDeviceLocked(s_modifiedLru, MaxModifiedModelsPerDevice, e => e.Key.DevicePtr, e => e.Model, cacheKey.DevicePtr);
            return uploaded;
        }
    }

    internal static bool HasAnyTimeDependency(Viewport3D.Modeling.EditableMesh? mesh)
    {
        if (mesh == null) return false;
        for (int i = 0; i < mesh.Materials.Count; i++)
        {
            if (Viewport3D.Shading.ShaderNodeEvaluator.IsGraphTimeDependent(mesh.Materials[i].NodeGraph))
                return true;
        }
        return false;
    }

    internal static long ComputeCombinedTimeKey(Viewport3D.Modeling.EditableMesh mesh, Viewport3D.Shading.ShaderEvalTime evalTime)
    {
        unchecked
        {
            long acc = 17;
            for (int i = 0; i < mesh.Materials.Count; i++)
            {
                var ng = mesh.Materials[i].NodeGraph;
                if (Viewport3D.Shading.ShaderNodeEvaluator.IsGraphTimeDependent(ng))
                {
                    long k = Viewport3D.Shading.ShaderNodeEvaluator.ComputeQuantizedTimeKey(ng, evalTime, out _, out _);
                    acc = acc * 31 + k;
                }
            }
            return acc;
        }
    }

    public static CachedGpuModel? BuildTransientGpuModel(
        ID3D11Device? device,
        Viewport3D.Modeling.EditableMesh? mesh,
        CachedGpuModel? textureDonor = null,
        ID3D11DeviceContext? ctx = null,
        int? nodeResolutionOverride = null,
        Viewport3D.Shading.ShaderEvalTime? evalTime = null)
    {
        if (device == null || mesh == null) return null;

        var parsed = mesh.ToParsedModel();
        ID3D11Buffer? vb = null;
        ID3D11Buffer? ib = null;
        var pinnedNodeResources = new List<IDisposable>();
        try
        {
            UploadVertexAndIndexBuffers(device, parsed, out vb, out ib, out long gpuBytes);
            bool canBorrow = textureDonor != null && !textureDonor.IsDisposed && textureDonor.DevicePtr == device.NativePointer;

            int partCount = parsed.Parts.Count;
            var gpuParts = new ObjPartRenderInfo[partCount];
            var partMatIndices = new int[partCount];
            for (int i = 0; i < partCount; i++)
            {
                var p = parsed.Parts[i];
                partMatIndices[i] = p.MaterialSlotIndex;
                int srcIdx = p.SourcePartIndex;
                ObjPartRenderInfo? donor = (canBorrow && srcIdx >= 0) ? textureDonor!.GetPartByOriginalIndex(srcIdx) : null;
                var info = new ObjPartRenderInfo
                {
                    IndexOffset = p.IndexOffset,
                    IndexCount = p.IndexCount,
                    BaseColor = p.BaseColor,
                    EmissiveColor = p.EmissiveColor,
                    Metallic = p.Metallic,
                    Roughness = p.Roughness,
                    Texture = donor is { } dt ? dt.Texture : null,
                    NormalTexture = donor is { } dn ? dn.NormalTexture : null,
                    NormalScale = donor is { } dns ? dns.NormalScale : p.NormalScale,
                    RoughnessMetallicTexture = donor is { } drm ? drm.RoughnessMetallicTexture : null,
                    RoughMetalPacking = donor is { } drmp ? drmp.RoughMetalPacking : 0,
                    EmissiveTexture = donor is { } de ? de.EmissiveTexture : null,
                    Transmission = p.Transmission,
                    Ior = p.Ior > 0f ? p.Ior : 1.5f,
                    TransmissionTexture = donor is { } dtr ? dtr.TransmissionTexture : null,
                    ForceOpaque = p.ForceOpaque,
                    CullBackface = p.CullBackface,
                    DisableShadow = !p.CastShadow,
                };
                ApplyNodeGraphToPart(device, ctx, p.NodeGraph, ref info, nodeResolutionOverride, evalTime, pinnedNodeResources);
                gpuParts[i] = info;
            }

            var unsortedParts = (ObjPartRenderInfo[])gpuParts.Clone();
            StablePartitionOpaqueFirst(gpuParts);

            ExtractPositionsAndNormals(parsed.Vertices, out var cpuPos, out var cpuNorm);
            return new CachedGpuModel(
                device.NativePointer,
                vb,
                ib,
                gpuParts,
                new List<ID3D11Texture2D>(),
                new List<ID3D11ShaderResourceView>(),
                parsed.BoundsMin,
                parsed.BoundsMax,
                parsed.ModelCenter,
                parsed.ModelScale,
                parsed.Indices.Length,
                gpuBytes,
                unsortedParts,
                partMatIndices,
                pinnedNodeResources)
            {
                CpuPositions = cpuPos,
                CpuNormals = cpuNorm,
                BoundingSupportPoints = ComputeBoundingSupportPoints(parsed),
                HasTimeDependentMaterial = HasAnyTimeDependency(mesh),
                LastTimeKey = evalTime.HasValue ? ComputeCombinedTimeKey(mesh, evalTime.Value) : 0L
            };
        }
        catch (Exception ex)
        {
            ModelLoadHelper.Log($"編集用GPUモデル構築失敗: {ex.Message}");
            foreach (var pin in pinnedNodeResources) { try { pin.Dispose(); } catch { } }
            try { ib?.Dispose(); } catch { }
            try { vb?.Dispose(); } catch { }
            return null;
        }
    }

    public static bool RefreshTransientMaterials(
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        CachedGpuModel? model,
        Viewport3D.Modeling.EditableMesh? mesh,
        CachedGpuModel? textureDonor,
        int? nodeResolutionOverride = null,
        Viewport3D.Shading.ShaderEvalTime? evalTime = null)
    {
        if (device == null || model == null || mesh == null || model.IsDisposed || model.DevicePtr != device.NativePointer)
            return false;

        int[]? slotIndices = model.PartMaterialIndices;
        var unsorted = model.UnsortedParts;
        int partCount = unsorted.Length;
        if (slotIndices == null || slotIndices.Length != partCount || model.Parts.Length != partCount)
            return false;

        bool canBorrow = textureDonor != null && !textureDonor.IsDisposed && textureDonor.DevicePtr == device.NativePointer;
        var baseDonor = model.BaseDonorParts;
        var nextUnsorted = new ObjPartRenderInfo[partCount];
        var newPins = new List<IDisposable>();

        try
        {
            for (int i = 0; i < partCount; i++)
            {
                int matIdx = slotIndices[i];
                if (matIdx < 0 || matIdx >= mesh.Materials.Count)
                {
                    foreach (var pin in newPins) { try { pin.Dispose(); } catch { } }
                    return false;
                }

                var mat = mesh.Materials[matIdx];
                ObjPartRenderInfo? donor = (canBorrow && mat.SourcePartIndex >= 0) ? textureDonor!.GetPartByOriginalIndex(mat.SourcePartIndex) : null;
                if (donor == null && baseDonor != null && i < baseDonor.Length)
                    donor = baseDonor[i];
                var prev = unsorted[i];

                var info = new ObjPartRenderInfo
                {
                    IndexOffset = prev.IndexOffset,
                    IndexCount = prev.IndexCount,
                    BaseColor = mat.BaseColor,
                    EmissiveColor = mat.EmissiveColor,
                    Metallic = Math.Clamp(mat.Metallic, 0f, 1f),
                    Roughness = Math.Clamp(mat.Roughness, 0.04f, 1f),
                    Texture = donor is { } dt ? dt.Texture : null,
                    NormalTexture = donor is { } dn ? dn.NormalTexture : null,
                    NormalScale = donor is { } dns ? dns.NormalScale : 1f,
                    RoughnessMetallicTexture = donor is { } drm ? drm.RoughnessMetallicTexture : null,
                    RoughMetalPacking = donor is { } drmp ? drmp.RoughMetalPacking : 0,
                    EmissiveTexture = donor is { } de ? de.EmissiveTexture : null,
                    Transmission = Math.Clamp(mat.Transmission, 0f, 1f),
                    Ior = mat.Ior > 0f ? mat.Ior : 1.5f,
                    TransmissionTexture = donor is { } dtr ? dtr.TransmissionTexture : null,
                    ForceOpaque = mat.ForceOpaque,
                    CullBackface = mat.CullBackface,
                    DisableShadow = !mat.CastShadow,
                };
                ApplyNodeGraphToPart(device, ctx, mat.NodeGraph, ref info, nodeResolutionOverride, evalTime, newPins);
                nextUnsorted[i] = info;
            }
        }
        catch
        {
            foreach (var pin in newPins) { try { pin.Dispose(); } catch { } }
            return false;
        }

        Array.Copy(nextUnsorted, unsorted, partCount);
        Array.Copy(unsorted, model.Parts, partCount);
        StablePartitionOpaqueFirst(model.Parts);
        model.ReplacePinnedNodeResources(newPins);
        model.HasTimeDependentMaterial = HasAnyTimeDependency(mesh);
        if (evalTime.HasValue)
            model.LastTimeKey = ComputeCombinedTimeKey(mesh, evalTime.Value);
        return true;
    }

    private static void CopyPartTexturesFromTemplate(ParsedModel target, ParsedModel template)
    {
        for (int i = 0; i < target.Parts.Count; i++)
        {
            var dst = target.Parts[i];
            int srcIdx = dst.SourcePartIndex;
            if (srcIdx < 0 || srcIdx >= template.Parts.Count) continue;

            var src = template.Parts[srcIdx];
            dst.TexturePath = src.TexturePath;
            dst.EmbeddedTextureBytes = src.EmbeddedTextureBytes;
            dst.EmbeddedTextureKey = src.EmbeddedTextureKey;
            dst.EmbeddedTextureExt = src.EmbeddedTextureExt;
            dst.NormalTexturePath = src.NormalTexturePath;
            dst.EmbeddedNormalBytes = src.EmbeddedNormalBytes;
            dst.EmbeddedNormalKey = src.EmbeddedNormalKey;
            dst.EmbeddedNormalExt = src.EmbeddedNormalExt;
            dst.NormalScale = src.NormalScale;
            dst.RoughnessMetallicTexturePath = src.RoughnessMetallicTexturePath;
            dst.EmbeddedRoughMetalBytes = src.EmbeddedRoughMetalBytes;
            dst.EmbeddedRoughMetalKey = src.EmbeddedRoughMetalKey;
            dst.EmbeddedRoughMetalExt = src.EmbeddedRoughMetalExt;
            dst.RoughMetalPacking = src.RoughMetalPacking;
            dst.EmissiveTexturePath = src.EmissiveTexturePath;
            dst.EmbeddedEmissiveBytes = src.EmbeddedEmissiveBytes;
            dst.EmbeddedEmissiveKey = src.EmbeddedEmissiveKey;
            dst.EmbeddedEmissiveExt = src.EmbeddedEmissiveExt;
            dst.TransmissionTexturePath = src.TransmissionTexturePath;
            dst.EmbeddedTransmissionBytes = src.EmbeddedTransmissionBytes;
            dst.EmbeddedTransmissionKey = src.EmbeddedTransmissionKey;
            dst.EmbeddedTransmissionExt = src.EmbeddedTransmissionExt;
        }
    }

    public static void ClearForDevice(IntPtr devicePtr)
    {
        if (devicePtr == IntPtr.Zero) return;
        lock (s_lock)
        {
            for (int i = s_lru.Count - 1; i >= 0; i--)
            {
                if (s_lru[i].Key.DevicePtr == devicePtr)
                {
                    var m = s_lru[i].Model;
                    s_lru.RemoveAt(i);
                    m.Dispose();
                }
            }
            for (int i = s_embeddedLru.Count - 1; i >= 0; i--)
            {
                if (s_embeddedLru[i].Key.DevicePtr == devicePtr)
                {
                    var m = s_embeddedLru[i].Model;
                    s_embeddedLru.RemoveAt(i);
                    m.Dispose();
                }
            }
            for (int i = s_proceduralLru.Count - 1; i >= 0; i--)
            {
                if (s_proceduralLru[i].DevicePtr == devicePtr)
                {
                    var m = s_proceduralLru[i].Model;
                    s_proceduralLru.RemoveAt(i);
                    m.Dispose();
                }
            }
            for (int i = s_modifiedLru.Count - 1; i >= 0; i--)
            {
                if (s_modifiedLru[i].Key.DevicePtr == devicePtr)
                {
                    var m = s_modifiedLru[i].Model;
                    s_modifiedLru.RemoveAt(i);
                    m.Dispose();
                }
            }
        }
        Viewport3D.Shading.ShaderNodeTextureCache.ClearForDevice(devicePtr);
        Viewport3D.Shading.LayerTextureRegistry.ClearForDevice(devicePtr);
    }

    public static void ClearAll()
    {
        lock (s_lock)
        {
            for (int i = 0; i < s_lru.Count; i++)
            {
                s_lru[i].Model.Dispose();
            }
            s_lru.Clear();

            for (int i = 0; i < s_embeddedLru.Count; i++)
            {
                s_embeddedLru[i].Model.Dispose();
            }
            s_embeddedLru.Clear();

            for (int i = 0; i < s_proceduralLru.Count; i++)
            {
                s_proceduralLru[i].Model.Dispose();
            }
            s_proceduralLru.Clear();

            for (int i = 0; i < s_modifiedLru.Count; i++)
            {
                s_modifiedLru[i].Model.Dispose();
            }
            s_modifiedLru.Clear();
            s_parsedModelLru.Clear();
        }
        Viewport3D.Shading.ShaderNodeTextureCache.ClearAll();
        Viewport3D.Shading.LayerTextureRegistry.ClearAll();
    }

    private static void EvictStaleEntriesLocked(ID3D11Device currentDevice)
    {
        IntPtr curPtr = currentDevice.NativePointer;
        bool curLost = false;
        try { curLost = currentDevice.DeviceRemovedReason.Failure; } catch { curLost = true; }

        for (int i = s_lru.Count - 1; i >= 0; i--)
        {
            if (curLost && s_lru[i].Key.DevicePtr == curPtr)
            {
                var m = s_lru[i].Model;
                s_lru.RemoveAt(i);
                m.Dispose();
            }
        }
        for (int i = s_embeddedLru.Count - 1; i >= 0; i--)
        {
            if (curLost && s_embeddedLru[i].Key.DevicePtr == curPtr)
            {
                var m = s_embeddedLru[i].Model;
                s_embeddedLru.RemoveAt(i);
                m.Dispose();
            }
        }
        for (int i = s_proceduralLru.Count - 1; i >= 0; i--)
        {
            if (curLost && s_proceduralLru[i].DevicePtr == curPtr)
            {
                var m = s_proceduralLru[i].Model;
                s_proceduralLru.RemoveAt(i);
                m.Dispose();
            }
        }
        for (int i = s_modifiedLru.Count - 1; i >= 0; i--)
        {
            if (curLost && s_modifiedLru[i].Key.DevicePtr == curPtr)
            {
                var m = s_modifiedLru[i].Model;
                s_modifiedLru.RemoveAt(i);
                m.Dispose();
            }
        }
    }

    private static unsafe void UploadVertexAndIndexBuffers(
        ID3D11Device device,
        ParsedModel model,
        out ID3D11Buffer vb,
        out ID3D11Buffer ib,
        out long gpuBytes)
    {
        var vbData = model.Vertices.Length > 0 ? model.Vertices : new ModelVertex[1];
        int vbSize = vbData.Length * Unsafe.SizeOf<ModelVertex>();
        var vDesc = new BufferDescription(vbSize, BindFlags.VertexBuffer, ResourceUsage.Immutable, CpuAccessFlags.None);
        fixed (ModelVertex* pVerts = vbData)
        {
            vb = device.CreateBuffer(vDesc, new SubresourceData((IntPtr)pVerts));
        }

        var ibData = model.Indices.Length > 0 ? model.Indices : new uint[3];
        int ibSize = ibData.Length * sizeof(uint);
        var iDesc = new BufferDescription(ibSize, BindFlags.IndexBuffer, ResourceUsage.Immutable, CpuAccessFlags.None);
        fixed (uint* pInds = ibData)
        {
            ib = device.CreateBuffer(iDesc, new SubresourceData((IntPtr)pInds));
        }

        gpuBytes = (long)vbSize + ibSize;
    }

    private static readonly Vector3[] s_supportDirs = BuildSupportDirections();

    private static Vector3[] BuildSupportDirections()
    {
        var dirs = new List<Vector3>(62);
        for (int ix = -1; ix <= 1; ix++)
        {
            for (int iy = -1; iy <= 1; iy++)
            {
                for (int iz = -1; iz <= 1; iz++)
                {
                    if (ix == 0 && iy == 0 && iz == 0) continue;
                    dirs.Add(Vector3.Normalize(new Vector3(ix, iy, iz)));
                }
            }
        }
        const int FibCount = 36;
        float goldenAngle = MathF.PI * (3f - MathF.Sqrt(5f));
        for (int i = 0; i < FibCount; i++)
        {
            float y = 1f - (2f * i + 1f) / FibCount;
            float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            float theta = goldenAngle * i;
            dirs.Add(Vector3.Normalize(new Vector3(MathF.Cos(theta) * r, y, MathF.Sin(theta) * r)));
        }
        return dirs.ToArray();
    }

    internal static Vector3[] ComputeBoundingSupportPoints(ParsedModel? model)
    {
        if (model == null || model.Vertices == null || model.Vertices.Length == 0)
            return Array.Empty<Vector3>();

        var verts = model.Vertices;
        if (verts.Length <= s_supportDirs.Length)
        {
            var unique = new List<Vector3>(verts.Length);
            var seen = new HashSet<Vector3>();
            for (int i = 0; i < verts.Length; i++)
            {
                var p = verts[i].Position;
                if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && seen.Add(p))
                    unique.Add(p);
            }
            return unique.ToArray();
        }

        int dirCount = s_supportDirs.Length;
        var bestIdx = new int[dirCount];
        var bestDot = new float[dirCount];
        for (int d = 0; d < dirCount; d++)
        {
            bestIdx[d] = -1;
            bestDot[d] = float.MinValue;
        }

        for (int i = 0; i < verts.Length; i++)
        {
            var p = verts[i].Position;
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                continue;

            for (int d = 0; d < dirCount; d++)
            {
                float dot = Vector3.Dot(p, s_supportDirs[d]);
                if (dot > bestDot[d])
                {
                    bestDot[d] = dot;
                    bestIdx[d] = i;
                }
            }
        }

        var result = new List<Vector3>(dirCount);
        var usedIndices = new HashSet<int>();
        var usedPos = new HashSet<Vector3>();
        for (int d = 0; d < dirCount; d++)
        {
            int idx = bestIdx[d];
            if (idx >= 0 && usedIndices.Add(idx))
            {
                var p = verts[idx].Position;
                if (usedPos.Add(p))
                    result.Add(p);
            }
        }
        return result.ToArray();
    }

    internal static void ExtractPositionsAndNormals(
        ModelVertex[] vertices,
        out Vector3[] positions,
        out Vector3[] normals)
    {
        positions = new Vector3[vertices.Length];
        normals = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            positions[i] = vertices[i].Position;
            normals[i] = vertices[i].Normal;
        }
    }

    internal static Vector3[] ComputeSmoothNormals(Vector3[] positions, Vector3[] normals)
    {
        int n = Math.Min(positions.Length, normals.Length);
        var result = new Vector3[n];
        if (n == 0) return result;

        var accum = new Dictionary<(long X, long Y, long Z), Vector3>(n);
        for (int i = 0; i < n; i++)
        {
            var p = positions[i];
            var key = (
                (long)MathF.Round(p.X * 10000f),
                (long)MathF.Round(p.Y * 10000f),
                (long)MathF.Round(p.Z * 10000f));
            var vNorm = normals[i];
            float lenSq = vNorm.LengthSquared();
            Vector3 unit = (lenSq > 1e-10f && float.IsFinite(lenSq))
                ? vNorm / MathF.Sqrt(lenSq)
                : Vector3.Zero;
            accum.TryGetValue(key, out var sum);
            accum[key] = sum + unit;
        }

        for (int i = 0; i < n; i++)
        {
            var p = positions[i];
            var key = (
                (long)MathF.Round(p.X * 10000f),
                (long)MathF.Round(p.Y * 10000f),
                (long)MathF.Round(p.Z * 10000f));
            Vector3 sum = accum.TryGetValue(key, out var s) ? s : normals[i];
            float lenSq = sum.LengthSquared();
            if (lenSq > 1e-10f && float.IsFinite(lenSq))
            {
                result[i] = sum / MathF.Sqrt(lenSq);
            }
            else
            {
                float fallbackSq = normals[i].LengthSquared();
                result[i] = (fallbackSq > 1e-10f && float.IsFinite(fallbackSq))
                    ? normals[i] / MathF.Sqrt(fallbackSq)
                    : Vector3.UnitY;
            }
        }
        return result;
    }

    internal static unsafe ID3D11Buffer? CreateSmoothNormalBufferFromCpu(
        ID3D11Device device,
        Vector3[]? positions,
        Vector3[]? normals)
    {
        if (device == null || positions == null || normals == null || positions.Length == 0 || positions.Length != normals.Length)
            return null;
        var smooth = ComputeSmoothNormals(positions, normals);
        int byteSize = smooth.Length * Unsafe.SizeOf<Vector3>();
        var desc = new BufferDescription(byteSize, BindFlags.VertexBuffer, ResourceUsage.Immutable, CpuAccessFlags.None);
        fixed (Vector3* p = smooth)
        {
            return device.CreateBuffer(desc, new SubresourceData((IntPtr)p));
        }
    }

    internal static unsafe ID3D11Buffer? CreateSmoothNormalBufferFromGpuVb(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11Buffer? vb)
    {
        if (device == null || ctx == null || vb == null || vb.NativePointer == IntPtr.Zero)
            return null;
        try
        {
            var desc = vb.Description;
            int vertStride = Unsafe.SizeOf<ModelVertex>();
            if (desc.ByteWidth < vertStride) return null;
            int vertCount = desc.ByteWidth / vertStride;

            using var staging = device.CreateBuffer(new BufferDescription(
                desc.ByteWidth, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            ctx.CopyResource(staging, vb);
            ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
            var positions = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            try
            {
                byte* basePtr = (byte*)mapped.DataPointer.ToPointer();
                for (int i = 0; i < vertCount; i++)
                {
                    var mv = Unsafe.ReadUnaligned<ModelVertex>(basePtr + i * vertStride);
                    positions[i] = mv.Position;
                    normals[i] = mv.Normal;
                }
            }
            finally
            {
                ctx.Unmap(staging, 0);
            }
            return CreateSmoothNormalBufferFromCpu(device, positions, normals);
        }
        catch
        {
            return null;
        }
    }

    private static CachedGpuModel? UploadToGpu(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ParsedModel model,
        int maxTextureSize,
        Viewport3D.Shading.ShaderEvalTime? evalTime = null)
    {
        ID3D11Buffer? vb = null;
        ID3D11Buffer? ib = null;
        var ownedTextures = new List<ID3D11Texture2D>();
        var ownedSrvs = new List<ID3D11ShaderResourceView>();
        var pinnedNodeResources = new List<IDisposable>();
        var srvDedup = new Dictionary<string, ID3D11ShaderResourceView?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            UploadVertexAndIndexBuffers(device, model, out vb, out ib, out long gpuBytes);

            int partCount = model.Parts.Count;
            var gpuParts = new ObjPartRenderInfo[partCount];
            var baseDonorParts = new ObjPartRenderInfo[partCount];
            var partMatIndices = new int[partCount];

            ID3D11ShaderResourceView? LoadOrDedupTex(
                string? filePath,
                ref byte[]? embBytes,
                string? embKey,
                string? embExt)
            {
                ID3D11ShaderResourceView? srvOut = null;
                if (!string.IsNullOrEmpty(filePath))
                {
                    string texKey = "file:" + filePath;
                    if (!srvDedup.TryGetValue(texKey, out srvOut))
                    {
                        var loaded = ModelTextureLoader.LoadFromFile(device, ctx, filePath, maxTextureSize);
                        if (loaded is { } texRes)
                        {
                            ownedTextures.Add(texRes.Texture);
                            ownedSrvs.Add(texRes.Srv);
                            gpuBytes += texRes.EstimatedBytes;
                            srvOut = texRes.Srv;
                        }
                        srvDedup[texKey] = srvOut!;
                    }
                }
                else if (embBytes != null && !string.IsNullOrEmpty(embKey))
                {
                    if (!srvDedup.TryGetValue(embKey, out srvOut))
                    {
                        var loaded = ModelTextureLoader.LoadFromBytes(
                            device, ctx, embBytes, embExt, maxTextureSize);
                        if (loaded is { } texRes)
                        {
                            ownedTextures.Add(texRes.Texture);
                            ownedSrvs.Add(texRes.Srv);
                            gpuBytes += texRes.EstimatedBytes;
                            srvOut = texRes.Srv;
                        }
                        srvDedup[embKey] = srvOut!;
                    }
                    embBytes = null;
                }
                return srvOut;
            }

            for (int i = 0; i < partCount; i++)
            {
                var p = model.Parts[i];
                partMatIndices[i] = p.MaterialSlotIndex >= 0 ? p.MaterialSlotIndex : i;
                var srv = LoadOrDedupTex(p.TexturePath, ref p.EmbeddedTextureBytes, p.EmbeddedTextureKey, p.EmbeddedTextureExt);
                var normSrv = LoadOrDedupTex(p.NormalTexturePath, ref p.EmbeddedNormalBytes, p.EmbeddedNormalKey, p.EmbeddedNormalExt);
                var rmSrv = LoadOrDedupTex(p.RoughnessMetallicTexturePath, ref p.EmbeddedRoughMetalBytes, p.EmbeddedRoughMetalKey, p.EmbeddedRoughMetalExt);
                var emSrv = LoadOrDedupTex(p.EmissiveTexturePath, ref p.EmbeddedEmissiveBytes, p.EmbeddedEmissiveKey, p.EmbeddedEmissiveExt);
                var trSrv = LoadOrDedupTex(p.TransmissionTexturePath, ref p.EmbeddedTransmissionBytes, p.EmbeddedTransmissionKey, p.EmbeddedTransmissionExt);

                var info = new ObjPartRenderInfo
                {
                    IndexOffset = p.IndexOffset,
                    IndexCount = p.IndexCount,
                    BaseColor = p.BaseColor,
                    EmissiveColor = p.EmissiveColor,
                    Metallic = p.Metallic,
                    Roughness = p.Roughness,
                    Texture = srv,
                    NormalTexture = normSrv,
                    NormalScale = p.NormalScale,
                    RoughnessMetallicTexture = rmSrv,
                    RoughMetalPacking = rmSrv != null ? p.RoughMetalPacking : 0,
                    EmissiveTexture = emSrv,
                    Transmission = p.Transmission,
                    Ior = p.Ior > 0f ? p.Ior : 1.5f,
                    TransmissionTexture = trSrv,
                    ForceOpaque = p.ForceOpaque,
                    CullBackface = p.CullBackface,
                    DisableShadow = !p.CastShadow,
                };
                baseDonorParts[i] = info;
                ApplyNodeGraphToPart(device, ctx, p, ref info, null, evalTime, pinnedNodeResources);
                gpuParts[i] = info;
            }

            var unsortedParts = (ObjPartRenderInfo[])gpuParts.Clone();
            // 不透明パーツを先頭に安定ソート（Standard / OIT 半透明パス高速化）
            StablePartitionOpaqueFirst(gpuParts);

            return new CachedGpuModel(
                device.NativePointer,
                vb,
                ib,
                gpuParts,
                ownedTextures,
                ownedSrvs,
                model.BoundsMin,
                model.BoundsMax,
                model.ModelCenter,
                model.ModelScale,
                model.Indices.Length,
                gpuBytes,
                unsortedParts,
                partMatIndices,
                pinnedNodeResources,
                baseDonorParts)
            {
                BoundingSupportPoints = ComputeBoundingSupportPoints(model),
            };
        }
        catch (Exception ex)
        {
            ModelLoadHelper.Log($"GPUリソース生成失敗: {ex.Message}");
            foreach (var pin in pinnedNodeResources) { try { pin.Dispose(); } catch { } }
            foreach (var srv in ownedSrvs) { try { srv.Dispose(); } catch { } }
            foreach (var tex in ownedTextures) { try { tex.Dispose(); } catch { } }
            try { ib?.Dispose(); } catch { }
            try { vb?.Dispose(); } catch { }
            return null;
        }
    }

    private static void ApplyNodeGraphToPart(
        ID3D11Device device,
        ID3D11DeviceContext? ctx,
        ParsedModelPart p,
        ref ObjPartRenderInfo info,
        int? resolutionOverride = null,
        Viewport3D.Shading.ShaderEvalTime? evalTime = null,
        List<IDisposable>? pinCollector = null)
        => ApplyNodeGraphToPart(device, ctx, p.NodeGraph, ref info, resolutionOverride, evalTime, pinCollector);

    private static void ApplyNodeGraphToPart(
        ID3D11Device device,
        ID3D11DeviceContext? ctx,
        Viewport3D.Shading.MaterialNodeGraph? nodeGraph,
        ref ObjPartRenderInfo info,
        int? resolutionOverride = null,
        Viewport3D.Shading.ShaderEvalTime? evalTime = null,
        List<IDisposable>? pinCollector = null)
    {
        if (nodeGraph == null || ctx == null) return;
        var baked = Viewport3D.Shading.ShaderNodeTextureCache.GetOrBakePinnedAtTime(
            device, ctx, nodeGraph, resolutionOverride, evalTime ?? Viewport3D.Shading.ShaderEvalTime.Default, out var pinLease);
        if (baked == null)
        {
            pinLease?.Dispose();
            return;
        }
        if (pinLease != null)
        {
            if (pinCollector != null)
                pinCollector.Add(pinLease);
            else
                pinLease.Dispose();
        }

        info.BaseColor = baked.BaseColor;
        info.Metallic = baked.Metallic;
        info.Roughness = baked.Roughness;
        info.Transmission = baked.Transmission;
        info.Ior = baked.Ior > 0f ? baked.Ior : 1.5f;
        info.EmissiveColor = baked.EmissiveColor;
        info.ForceOpaque = baked.ForceOpaque;

        info.CoatWeight = baked.CoatWeight;
        info.CoatRoughness = baked.CoatRoughness;
        info.CoatIor = baked.CoatIor;
        info.DiffuseRoughness = baked.DiffuseRoughness;
        info.ThinFilmThickness = baked.ThinFilmThickness;
        info.ThinFilmIor = baked.ThinFilmIor;
        info.Anisotropic = baked.Anisotropic;
        info.AnisotropicRotation = baked.AnisotropicRotation;
        info.SubsurfaceWeight = baked.SubsurfaceWeight;
        info.SubsurfaceScale = baked.SubsurfaceScale;
        info.SubsurfaceColor = baked.SubsurfaceColor;
        info.SheenWeight = baked.SheenWeight;
        info.SheenRoughness = baked.SheenRoughness;
        info.SheenTint = baked.SheenTint;
        info.IsDiffuseOnly = baked.IsDiffuseOnly;

        info.ShaderLutTexture = baked.ShaderLutSrv;
        info.ShaderLutMode = baked.ShaderLutMode;
        info.ShaderLutParamA = baked.ShaderLutParamA;
        info.ShaderLutParamB = baked.ShaderLutParamB;
        info.ShaderLutEmissiveMax = baked.ShaderLutEmissiveMax;

        info.WireframeMode = baked.WireframeMode;
        info.WireframeSize = baked.WireframeSize;
        info.WireframePixelSize = baked.WireframePixelSize;
        info.WireframeBaseColor = baked.WireframeBaseColor;
        info.WireframeEmissiveColor = baked.WireframeEmissiveColor;

        info.LayerTextureAlbedoLayerPlusOne = baked.LayerTextureAlbedoLayerPlusOne;
        info.LayerTextureEmissiveLayerPlusOne = baked.LayerTextureEmissiveLayerPlusOne;
        info.LayerUvTransform = baked.LayerUvTransform;
        info.LayerUvRotationRad = baked.LayerUvRotationRad;
        info.LayerFlipV = baked.LayerFlipV;

        if (baked.LayerTextureAlbedoLayerPlusOne > 0)
            info.Texture = null;
        else if (baked.AlbedoSrv != null)
            info.Texture = baked.AlbedoSrv;

        if (baked.NormalSrv != null)
        {
            info.NormalTexture = baked.NormalSrv;
            info.NormalScale = baked.NormalScale;
        }
        if (baked.OrmSrv != null)
        {
            info.RoughnessMetallicTexture = baked.OrmSrv;
            info.RoughMetalPacking = 1;
        }
        if (baked.EmissiveSrv != null) info.EmissiveTexture = baked.EmissiveSrv;
        if (baked.TransmissionSrv != null) info.TransmissionTexture = baked.TransmissionSrv;
    }

    private static void StablePartitionOpaqueFirst(ObjPartRenderInfo[] parts)
    {
        if (parts.Length <= 1) return;

        var opaque = new List<ObjPartRenderInfo>(parts.Length);
        var trans = new List<ObjPartRenderInfo>(parts.Length);

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].IsOpaque) opaque.Add(parts[i]);
            else trans.Add(parts[i]);
        }

        if (opaque.Count == 0 || trans.Count == 0) return;

        opaque.CopyTo(parts, 0);
        trans.CopyTo(parts, opaque.Count);
    }
}
