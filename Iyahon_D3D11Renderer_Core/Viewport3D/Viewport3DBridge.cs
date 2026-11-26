using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Iyahon_D3D11Renderer_Core.D3DEffect;
using Iyahon_D3D11Renderer_Core.Lighting;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal enum ViewportItemKind
{
    BuiltInModel,
    D3DEffect,
    Quad2D,
    Light,
}

/// <summary>
/// YMM4デバイス上でベイクされた SharedHandle 付きテクスチャ（参照カウント管理）。
/// 複数ビューポート同時表示やスナップショット差し替え時の解放競合を防ぐ。
/// </summary>
internal sealed class ViewportSharedTexture
{
    private int _refCount = 1;

    public ID3D11Texture2D Texture { get; }
    public IntPtr SharedHandle { get; }
    public int Width { get; }
    public int Height { get; }
    public bool HasKeyedMutex { get; }
    public IntPtr SourceDevicePtr { get; }

    public ViewportSharedTexture(
        ID3D11Texture2D texture,
        IntPtr sharedHandle,
        int width,
        int height,
        bool hasKeyedMutex = false,
        IntPtr sourceDevicePtr = default)
    {
        Texture = texture;
        SharedHandle = sharedHandle;
        Width = width;
        Height = height;
        HasKeyedMutex = hasKeyedMutex;
        SourceDevicePtr = sourceDevicePtr;
    }

    public bool TryAddRef()
    {
        while (true)
        {
            int cur = Volatile.Read(ref _refCount);
            if (cur <= 0) return false;
            if (Interlocked.CompareExchange(ref _refCount, cur + 1, cur) == cur)
                return true;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            try { Texture.Dispose(); } catch { }
        }
    }
}

internal sealed class ViewportModelSpec
{
    public string FilePath { get; init; } = string.Empty;
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public Vector3 ModelCenter { get; init; }
    public float ModelScale { get; init; } = 1f;
    public bool NormalizeSize { get; init; } = true;
    public bool GroundToOrigin { get; init; } = false;
    public float UnitScale { get; init; } = 100f;
    public Vector3 LocalScale { get; init; } = Vector3.One;
    public Vector3 LocalRotationDeg { get; init; } = Vector3.Zero;
    public Vector4 TintColor { get; init; } = Vector4.One;
    public bool OverrideMaterial { get; init; } = false;
    public float OverrideMetallic { get; init; } = 0f;
    public float OverrideRoughness { get; init; } = 0.5f;
    public float EmissiveMultiplier { get; init; } = 1f;
    public bool CullBackface { get; init; } = false;
    public string EmbeddedMeshData { get; init; } = string.Empty;
    public string ModifierStackData { get; init; } = string.Empty;
    public string ProceduralCacheKey { get; init; } = string.Empty;
    public Func<Modeling.EditableMesh>? ProceduralMeshFactory { get; init; }
    public Model3D.Noise3DParameters Noise3D { get; init; }

    public ObjModelData AttachToCachedModel(Model3D.CachedGpuModel cached)
    {
        var copy = cached.CreateRenderData(
            NormalizeSize,
            GroundToOrigin,
            UnitScale,
            LocalScale,
            LocalRotationDeg,
            TintColor,
            OverrideMaterial,
            OverrideMetallic,
            OverrideRoughness,
            EmissiveMultiplier,
            CullBackface);
        var np = Noise3D;
        if (np.Enabled)
        {
            np.BoundsMin = cached.BoundsMin;
            np.BoundsMax = cached.BoundsMax;
        }
        copy.Noise3D = np;
        return copy;
    }

    public static ViewportModelSpec FromObjModelData(string filePath, ObjModelData data)
        => FromObjModelDataWithMesh(filePath, data, string.Empty);

    public static ViewportModelSpec FromObjModelDataWithMesh(string filePath, ObjModelData data, string embeddedMeshData) => new()
    {
        FilePath = filePath,
        EmbeddedMeshData = embeddedMeshData ?? string.Empty,
        ModifierStackData = data.ModifierStackData ?? string.Empty,
        BoundsMin = data.BoundsMin,
        BoundsMax = data.BoundsMax,
        ModelCenter = data.ModelCenter,
        ModelScale = data.ModelScale,
        NormalizeSize = data.NormalizeSize,
        GroundToOrigin = data.GroundToOrigin,
        UnitScale = data.UnitScale,
        LocalScale = data.LocalScale,
        LocalRotationDeg = data.LocalRotationDeg,
        TintColor = data.TintColor,
        OverrideMaterial = data.OverrideMaterial,
        OverrideMetallic = data.OverrideMetallic,
        OverrideRoughness = data.OverrideRoughness,
        EmissiveMultiplier = data.EmissiveMultiplier,
        CullBackface = data.CullBackface,
        ProceduralCacheKey = data.ProceduralCacheKey ?? string.Empty,
        ProceduralMeshFactory = data.ProceduralMeshFactory,
        Noise3D = data.Noise3D,
    };
}

internal sealed class ViewportItemEntry
{
    public required IVideoItem Item { get; init; }
    public required ViewportItemKind Kind { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public required DrawDescription DrawDescription { get; init; }
    public int Layer { get; init; }
    public long ItemFrame { get; init; }
    public long ItemLength { get; init; }
    public int Fps { get; init; } = 60;
    public float Opacity { get; init; } = 1f;

    public float PixelWidth { get; init; }
    public float PixelHeight { get; init; }
    public float BoundsCenterX { get; init; }
    public float BoundsCenterY { get; init; }

    public ViewportModelSpec? ModelSpec { get; init; }
    public MaterialOverrideParameters Material { get; init; }
    public D3DEffect.Effects.OutlineParameters Outline { get; set; }
    public List<DrawDescription>? RandomInstanceDrawDescs { get; set; }

    public string? D3DEffectId { get; init; }
    public ID3DVideoEffect? D3DVideoEffect { get; init; }

    public LightData? Light { get; init; }
    public ViewportSharedTexture? SharedTexture { get; init; }
    public int PartIndex { get; init; }
    public bool UsesBlenderAxis { get; init; }

    public Vector3 CapturedAnimPos { get; init; }
    public float CapturedAnimZoom { get; init; } = 100f;
    public float CapturedAnimRotZ { get; init; }
    public Vector3 CapturedRotateEffectDeg { get; init; }
    public bool HasCapturedRotateEffect { get; init; }
    public bool HasCapturedAnimBaseline { get; init; }
}

internal sealed class ViewportSceneSnapshot
{
    private int _refCount = 1;

    public long SequenceId { get; init; }
    public int VideoWidth { get; init; } = 1920;
    public int VideoHeight { get; init; } = 1080;
    public int Fps { get; init; } = 60;
    public long CurrentFrame { get; init; }
    public Matrix4x4 SceneCameraMatrix { get; init; } = Matrix4x4.Identity;
    public EnvironmentParameters Environment { get; init; }
    public List<ViewportItemEntry> Items { get; } = new();
    public List<LightData> Lights { get; } = new();

    public bool TryAddRef()
    {
        while (true)
        {
            int cur = Volatile.Read(ref _refCount);
            if (cur <= 0) return false;
            if (Interlocked.CompareExchange(ref _refCount, cur + 1, cur) == cur)
                return true;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].SharedTexture?.Release();
            }
            Items.Clear();
        }
    }
}

internal static class Viewport3DBridge
{
    private static readonly object _snapshotLock = new();
    private static int _activeViewports;
    private static long _sequenceCounter;
    private static ViewportSceneSnapshot? _latestSnapshot;

    private static readonly object _bakeLock = new();
    private static ID2D1DeviceContext? _bakeDc;
    private static IntPtr _bakeD2dDevicePtr;

    static Viewport3DBridge()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ClearAll();
    }

    public static bool HasActiveViewports => Volatile.Read(ref _activeViewports) > 0;
    public static event Action? FrameUpdated;

    public static void RegisterViewport()
    {
        Interlocked.Increment(ref _activeViewports);
    }

    public static void UnregisterViewport()
    {
        if (Interlocked.Decrement(ref _activeViewports) <= 0)
        {
            Interlocked.Exchange(ref _activeViewports, 0);
            ClearLatestSnapshot();
        }
    }

    public static long NextSequenceId() => Interlocked.Increment(ref _sequenceCounter);

    public static void PublishFrame(ViewportSceneSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            _latestSnapshot?.Release();
            _latestSnapshot = snapshot;
        }
        FrameUpdated?.Invoke();
    }

    public static ViewportSceneSnapshot? TryAcquireLatestSnapshot()
    {
        lock (_snapshotLock)
        {
            if (_latestSnapshot != null && _latestSnapshot.TryAddRef())
                return _latestSnapshot;
            return null;
        }
    }

    public static void ClearLatestSnapshot()
    {
        lock (_snapshotLock)
        {
            _latestSnapshot?.Release();
            _latestSnapshot = null;
        }
    }

    private static readonly object _luidLock = new();
    private static long? _ymmAdapterLuid;

    public static void RecordYmmAdapterLuid(ID3D11Device? ymmDevice)
    {
        if (ymmDevice == null || ymmDevice.NativePointer == IntPtr.Zero) return;
        try
        {
            using var dxgiDev = ymmDevice.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDev.GetAdapter();
            var luid = adapter.Description.Luid;
            long packed = PackLuid(luid.HighPart, luid.LowPart);
            lock (_luidLock)
            {
                _ymmAdapterLuid = packed;
            }
        }
        catch { }
    }

    public static bool TryGetYmmAdapterLuid(out long luidPacked)
    {
        lock (_luidLock)
        {
            if (_ymmAdapterLuid.HasValue)
            {
                luidPacked = _ymmAdapterLuid.Value;
                return true;
            }
            luidPacked = 0;
            return false;
        }
    }

    internal static long PackLuid(int highPart, uint lowPart)
        => ((long)highPart << 32) | lowPart;

    public static void ClearAll()
    {
        ClearLatestSnapshot();
        lock (_luidLock)
        {
            _ymmAdapterLuid = null;
        }
        lock (_bakeLock)
        {
            try { _bakeDc?.Dispose(); } catch { }
            _bakeDc = null;
            _bakeD2dDevicePtr = IntPtr.Zero;
        }
    }

    public static bool TryBakeImage(
        ID2D1Image image,
        IGraphicsDevicesAndContext devices,
        out ViewportSharedTexture? sharedTex,
        out int texW,
        out int texH,
        out float boundsCenterX,
        out float boundsCenterY)
    {
        sharedTex = null;
        texW = 0;
        texH = 0;
        boundsCenterX = 0f;
        boundsCenterY = 0f;

        if (devices?.D3D?.Device == null || devices.DeviceContext == null)
            return false;

        lock (_bakeLock)
        {
            var bakeDc = EnsureBakeDcLocked(devices.DeviceContext);
            if (bakeDc == null) return false;

            RawRectF bounds;
            try
            {
                bakeDc.Dpi = devices.DeviceContext.Dpi;
                bakeDc.UnitMode = devices.DeviceContext.UnitMode;
                bakeDc.Transform = Matrix3x2.Identity;
                bounds = bakeDc.GetImageLocalBounds(image);
            }
            catch
            {
                return false;
            }

            if (float.IsInfinity(bounds.Left) || float.IsInfinity(bounds.Top) ||
                float.IsInfinity(bounds.Right) || float.IsInfinity(bounds.Bottom) ||
                float.IsNaN(bounds.Left) || float.IsNaN(bounds.Top) ||
                float.IsNaN(bounds.Right) || float.IsNaN(bounds.Bottom))
                return false;

            int left = (int)MathF.Floor(bounds.Left);
            int top = (int)MathF.Floor(bounds.Top);
            int right = (int)MathF.Ceiling(bounds.Right);
            int bottom = (int)MathF.Ceiling(bounds.Bottom);

            float pw = right - left;
            float ph = bottom - top;
            if (pw <= 0f || ph <= 0f) return false;

            const int MaxViewportTexSize = 4096;
            texW = Math.Min((int)pw, MaxViewportTexSize);
            texH = Math.Min((int)ph, MaxViewportTexSize);
            if (texW <= 0 || texH <= 0) return false;

            sharedTex = BakeSharedTextureLocked(image, devices, bakeDc, texW, texH, -left, -top);
            if (sharedTex == null) return false;

            boundsCenterX = left + texW / 2f;
            boundsCenterY = top + texH / 2f;
            return true;
        }
    }

    public static ViewportSharedTexture? BakeSharedTexture(
        ID2D1Image image,
        IGraphicsDevicesAndContext devices,
        int width,
        int height,
        float offsetX,
        float offsetY)
    {
        if (width <= 0 || height <= 0) return null;
        if (devices?.D3D?.Device == null || devices.DeviceContext == null) return null;

        lock (_bakeLock)
        {
            var bakeDc = EnsureBakeDcLocked(devices.DeviceContext);
            if (bakeDc == null) return null;

            bakeDc.Dpi = devices.DeviceContext.Dpi;
            bakeDc.UnitMode = devices.DeviceContext.UnitMode;
            bakeDc.Transform = Matrix3x2.Identity;
            return BakeSharedTextureLocked(image, devices, bakeDc, width, height, offsetX, offsetY);
        }
    }

    private static ID2D1DeviceContext? EnsureBakeDcLocked(ID2D1DeviceContext6 sourceDc)
    {
        using var d2dDev = sourceDc.Device;
        if (d2dDev == null || d2dDev.NativePointer == IntPtr.Zero)
            return null;

        if (_bakeDc == null || _bakeD2dDevicePtr != d2dDev.NativePointer)
        {
            try { _bakeDc?.Dispose(); } catch { }
            _bakeDc = d2dDev.CreateDeviceContext(DeviceContextOptions.EnableMultithreadedOptimizations);
            _bakeD2dDevicePtr = d2dDev.NativePointer;
        }
        return _bakeDc;
    }

    private static ViewportSharedTexture? BakeSharedTextureLocked(
        ID2D1Image image,
        IGraphicsDevicesAndContext devices,
        ID2D1DeviceContext bakeDc,
        int width,
        int height,
        float offsetX,
        float offsetY)
    {
        var d3dDev = devices.D3D.Device;
        RecordYmmAdapterLuid(d3dDev);

        ID3D11Texture2D? texture = null;
        ID2D1Bitmap1? bitmap = null;
        IDXGIKeyedMutex? writeMutex = null;
        bool useKeyedMutex = false;
        bool mutexAcquired = false;

        try
        {
            var texDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
            };

            try
            {
                texture = d3dDev.CreateTexture2D(texDesc);
                useKeyedMutex = true;
            }
            catch
            {
                texDesc.MiscFlags = ResourceOptionFlags.Shared;
                texture = d3dDev.CreateTexture2D(texDesc);
                useKeyedMutex = false;
            }

            using var dxgiRes = texture.QueryInterface<IDXGIResource>();
            IntPtr sharedHandle = dxgiRes.SharedHandle;
            if (sharedHandle == IntPtr.Zero)
            {
                texture.Dispose();
                return null;
            }

            if (useKeyedMutex)
            {
                writeMutex = texture.QueryInterface<IDXGIKeyedMutex>();
                try
                {
                    writeMutex.AcquireSync(0, 5000);
                    mutexAcquired = true;
                }
                catch (Exception ex)
                {
                    Iyahon_D3D11Renderer_CorePlugin.Log($"Viewport3DBridge WriteMutex AcquireSync 失敗: {ex.Message}");
                    writeMutex.Dispose();
                    texture.Dispose();
                    return null;
                }
            }

            bool drawSucceeded = false;
            try
            {
                using var surface = texture.QueryInterface<IDXGISurface>();
                var dpi = bakeDc.Dpi;
                var bitmapProps = new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    dpi.Width, dpi.Height, BitmapOptions.Target);

                bitmap = bakeDc.CreateBitmapFromDxgiSurface(surface, bitmapProps);
                bakeDc.Target = bitmap;
                bakeDc.BeginDraw();
                bakeDc.Clear(new Color4(0f, 0f, 0f, 0f));
                bakeDc.DrawImage(image, new Vector2(offsetX, offsetY), null, InterpolationMode.Linear, CompositeMode.SourceOver);
                var hr = bakeDc.EndDraw();
                bakeDc.Target = null;
                bitmap.Dispose();
                bitmap = null;

                var flushCtx = devices.D3D.DeviceContext ?? d3dDev.ImmediateContext;
                if (hr.Success)
                {
                    flushCtx?.Flush();
                    if (!useKeyedMutex && flushCtx != null)
                        WaitForGpuFinish(d3dDev, flushCtx);
                    drawSucceeded = true;
                }
                else
                {
                    Iyahon_D3D11Renderer_CorePlugin.Log($"Viewport3DBridge BakeSharedTexture EndDraw 失敗: 0x{hr.Code:X8}");
                }
            }
            finally
            {
                if (mutexAcquired && writeMutex != null)
                {
                    try { writeMutex.ReleaseSync(1); } catch { }
                    mutexAcquired = false;
                }
                writeMutex?.Dispose();
                writeMutex = null;
            }

            if (!drawSucceeded)
            {
                texture.Dispose();
                return null;
            }

            return new ViewportSharedTexture(texture, sharedHandle, width, height, useKeyedMutex, d3dDev.NativePointer);
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"Viewport3DBridge BakeSharedTexture 例外: {ex.Message}");
            try { bakeDc.Target = null; } catch { }
            bitmap?.Dispose();
            if (mutexAcquired && writeMutex != null)
            {
                try { writeMutex.ReleaseSync(1); } catch { }
            }
            writeMutex?.Dispose();
            texture?.Dispose();
            return null;
        }
    }

    private static void WaitForGpuFinish(ID3D11Device device, ID3D11DeviceContext ctx)
    {
        try
        {
            using var query = device.CreateQuery(new QueryDescription(QueryType.Event));
            ctx.End(query);
            ctx.Flush();
            long deadline = Environment.TickCount64 + 200;
            while (Environment.TickCount64 < deadline)
            {
                if (ctx.GetData<int>(query, AsyncGetDataFlags.DoNotFlush) != 0)
                    break;
                Thread.SpinWait(64);
            }
        }
        catch { }
    }
}
