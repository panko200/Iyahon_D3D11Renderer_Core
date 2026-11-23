using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// D2D1Image → D3D11 ShaderResourceView ベイク用テクスチャプール。
/// 同サイズのテクスチャ・D2Dビットマップ・SRV・アイソレートD2Dコンテキストをフレーム間で再利用し、
/// 毎フレーム/毎アイテムのGPUテクスチャ生成・破棄および dc.Flush() によるパイプラインストールを排除する。
/// </summary>
internal sealed class BakedTexturePool : IDisposable
{
    private const long MaxPoolBytes = 256L * 1024L * 1024L;
    private const long UnusedEvictFrames = 60;

    private sealed class PooledEntry : IDisposable
    {
        public required ID3D11Texture2D Texture { get; init; }
        public required ID2D1Bitmap1 Bitmap { get; init; }
        public required ID3D11ShaderResourceView Srv { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public Format Format { get; init; }
        public long ByteSize => (long)Width * Height * (Format == Format.R16G16B16A16_Float ? 8L : 4L);
        public long LastUsedFrame { get; set; }
        public bool InUse { get; set; }

        public void Dispose()
        {
            Srv.Dispose();
            Bitmap.Dispose();
            Texture.Dispose();
        }
    }

    private readonly List<PooledEntry> _entries = new();
    private IntPtr _d3dDevicePtr = IntPtr.Zero;
    private IntPtr _d2dDevicePtr = IntPtr.Zero;
    private ID2D1DeviceContext? _isolatedDc;
    private long _currentFrame;
    private bool _fp16Fallback;
    private bool _disposed;

    public void BeginFrame(long frameNumber)
    {
        _currentFrame = frameNumber;
        for (int i = 0; i < _entries.Count; i++)
        {
            _entries[i].InUse = false;
        }
    }

    public ID3D11ShaderResourceView? Bake(
        ID2D1Image image,
        IGraphicsDevicesAndContext devices,
        int width,
        int height,
        float offsetX = 0f,
        float offsetY = 0f,
        bool useFp16 = false)
    {
        if (_disposed || width <= 0 || height <= 0) return null;
        if (devices?.D3D?.Device == null || devices.D3D.Device.NativePointer == IntPtr.Zero) return null;
        if (devices.DeviceContext == null || devices.DeviceContext.NativePointer == IntPtr.Zero) return null;

        try
        {
            using var d2dDevice = devices.DeviceContext.Device;
            if (d2dDevice == null || d2dDevice.NativePointer == IntPtr.Zero) return null;

            IntPtr d3dPtr = devices.D3D.Device.NativePointer;
            IntPtr d2dPtr = d2dDevice.NativePointer;

            if (_isolatedDc == null || _d3dDevicePtr != d3dPtr || _d2dDevicePtr != d2dPtr)
            {
                ClearAll();
                _isolatedDc = d2dDevice.CreateDeviceContext(DeviceContextOptions.EnableMultithreadedOptimizations);
                _isolatedDc.Dpi = devices.DeviceContext.Dpi;
                _d3dDevicePtr = d3dPtr;
                _d2dDevicePtr = d2dPtr;
            }

            Format desiredFormat = (useFp16 && !_fp16Fallback) ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;

            PooledEntry? entry = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                var candidate = _entries[i];
                if (!candidate.InUse && candidate.Width == width && candidate.Height == height && candidate.Format == desiredFormat)
                {
                    entry = candidate;
                    break;
                }
            }

            if (entry == null)
            {
                entry = CreateEntry(devices, _isolatedDc, width, height, desiredFormat, out bool fellBackToBgra);
                if (fellBackToBgra) _fp16Fallback = true;
                if (entry == null) return null;
                _entries.Add(entry);
            }

            entry.InUse = true;
            entry.LastUsedFrame = _currentFrame;

            var interpMode = D3D11RendererSettings.Default.TransparencyMode == TransparencyMode.Standard
                ? InterpolationMode.Linear
                : InterpolationMode.NearestNeighbor;

            _isolatedDc.Target = entry.Bitmap;
            _isolatedDc.BeginDraw();
            _isolatedDc.Clear(new Color4(0f, 0f, 0f, 0f));
            _isolatedDc.DrawImage(image, new Vector2(offsetX, offsetY), null, interpMode, CompositeMode.SourceOver);
            var hr = _isolatedDc.EndDraw();
            _isolatedDc.Target = null;
            if (hr.Failure)
            {
                Iyahon_D3D11Renderer_CorePlugin.Log($"BakedTexturePool.Bake EndDraw 失敗: HRESULT=0x{hr.Code:X8}");
                return null;
            }

            return entry.Srv;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"BakedTexturePool.Bake 例外: {ex.Message}");
            if (_isolatedDc != null)
            {
                try { _isolatedDc.Target = null; } catch { }
            }
            return null;
        }
    }

    public void EndFrame(long frameNumber)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (!e.InUse && (frameNumber - e.LastUsedFrame) > UnusedEvictFrames)
            {
                e.Dispose();
                _entries.RemoveAt(i);
            }
        }

        long totalBytes = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            totalBytes += _entries[i].ByteSize;
        }

        while (totalBytes > MaxPoolBytes)
        {
            int oldestIdx = -1;
            long oldestFrame = long.MaxValue;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (!e.InUse && e.LastUsedFrame < oldestFrame)
                {
                    oldestFrame = e.LastUsedFrame;
                    oldestIdx = i;
                }
            }

            if (oldestIdx < 0) break;

            totalBytes -= _entries[oldestIdx].ByteSize;
            _entries[oldestIdx].Dispose();
            _entries.RemoveAt(oldestIdx);
        }
    }

    private static PooledEntry? CreateEntry(
        IGraphicsDevicesAndContext devices,
        ID2D1DeviceContext dc,
        int width,
        int height,
        Format requestedFormat,
        out bool fellBackToBgra)
    {
        fellBackToBgra = false;

        PooledEntry? TryCreate(Format fmt)
        {
            ID3D11Texture2D? texture = null;
            ID2D1Bitmap1? bitmap = null;
            ID3D11ShaderResourceView? srv = null;

            try
            {
                var texDesc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = fmt,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    MiscFlags = ResourceOptionFlags.Shared,
                };

                texture = devices.D3D.Device.CreateTexture2D(texDesc);
                using var surface = texture.QueryInterface<IDXGISurface>();

                var dpi = dc.Dpi;
                var bitmapProps = new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(fmt, Vortice.DCommon.AlphaMode.Premultiplied),
                    dpi.Width, dpi.Height, BitmapOptions.Target);

                bitmap = dc.CreateBitmapFromDxgiSurface(surface, bitmapProps);
                srv = D2DD3DBridge.CreateSrv(texture, devices.D3D.Device);
                if (srv == null)
                {
                    bitmap.Dispose();
                    texture.Dispose();
                    return null;
                }

                return new PooledEntry
                {
                    Texture = texture,
                    Bitmap = bitmap,
                    Srv = srv,
                    Width = width,
                    Height = height,
                    Format = fmt,
                };
            }
            catch (Exception ex)
            {
                Iyahon_D3D11Renderer_CorePlugin.Log($"BakedTexturePool.CreateEntry({fmt}) 例外: {ex.Message}");
                srv?.Dispose();
                bitmap?.Dispose();
                texture?.Dispose();
                return null;
            }
        }

        var entry = TryCreate(requestedFormat);
        if (entry == null && requestedFormat == Format.R16G16B16A16_Float)
        {
            fellBackToBgra = true;
            entry = TryCreate(Format.B8G8R8A8_UNorm);
        }
        return entry;
    }

    public void ClearAll()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            try { _entries[i].Dispose(); } catch { }
        }
        _entries.Clear();

        try { _isolatedDc?.Dispose(); } catch { }
        _isolatedDc = null;
        _d3dDevicePtr = IntPtr.Zero;
        _d2dDevicePtr = IntPtr.Zero;
        _fp16Fallback = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearAll();
    }
}

internal static class D2DD3DBridge
{
    public static (ID3D11Texture2D texture, ID2D1Bitmap1 bitmap)? CreateSharedTexture(
        IGraphicsDevicesAndContext devices, int width, int height)
    {
        ID3D11Texture2D? texture = null;
        try
        {
            if (devices == null || devices.D3D == null || devices.D3D.Device == null) return null;
            if (devices.D3D.Device.NativePointer == IntPtr.Zero) return null;

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
                MiscFlags = ResourceOptionFlags.Shared,
            };

            texture = devices.D3D.Device.CreateTexture2D(texDesc);
            using var surface = texture.QueryInterface<IDXGISurface>();

            float dpiX = devices.DeviceContext.Dpi.Width;
            float dpiY = devices.DeviceContext.Dpi.Height;

            var bitmapProps = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                dpiX, dpiY, BitmapOptions.Target
            );

            var bitmap = devices.DeviceContext.CreateBitmapFromDxgiSurface(surface, bitmapProps);
            return (texture, bitmap);
        }
        catch
        {
            texture?.Dispose();
            return null;
        }
    }

    public static ID3D11Texture2D? BakeToD3DTexture(
        ID2D1Image image, IGraphicsDevicesAndContext devices, int width, int height, float offsetX = 0f, float offsetY = 0f)
    {
        ID3D11Texture2D? texture = null;
        ID2D1Bitmap1? bitmap = null;
        ID2D1Image? prevTarget = null;
        try
        {
            if (devices == null || devices.D3D == null || devices.D3D.Device == null) return null;
            if (devices.D3D.Device.NativePointer == IntPtr.Zero) return null;
            if (devices.DeviceContext == null || devices.DeviceContext.NativePointer == IntPtr.Zero) return null;

            var ymmCtx = devices.D3D.DeviceContext;
            using var stateSaver = ymmCtx != null ? new D3D11StateSaver(ymmCtx) : null;

            var result = CreateSharedTexture(devices, width, height);
            if (result == null) return null;
            texture = result.Value.texture;
            bitmap = result.Value.bitmap;

            var dc = devices.DeviceContext;
            prevTarget = dc.Target;

            dc.Target = bitmap;
            dc.BeginDraw();
            dc.Clear(new Color4(0f, 0f, 0f, 0f));

            var interpMode = D3D11RendererSettings.Default.TransparencyMode == TransparencyMode.Standard
                ? InterpolationMode.Linear
                : InterpolationMode.NearestNeighbor;
            dc.DrawImage(image, new Vector2(offsetX, offsetY), null, interpMode, CompositeMode.SourceOver);

            dc.EndDraw();
            dc.Target = prevTarget;

            return texture;
        }
        catch
        {
            texture?.Dispose();
            return null;
        }
        finally
        {
            prevTarget?.Dispose();
            bitmap?.Dispose();
        }
    }

    public static ID3D11ShaderResourceView? CreateSrv(ID3D11Texture2D texture, ID3D11Device d3dDevice)
    {
        try
        {
            if (d3dDevice == null || d3dDevice.NativePointer == IntPtr.Zero) return null;
            if (texture == null || texture.NativePointer == IntPtr.Zero) return null;

            var srvDesc = new ShaderResourceViewDescription
            {
                Format = texture.Description.Format,
                ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
            };
            return d3dDevice.CreateShaderResourceView(texture, srvDesc);
        }
        catch { return null; }
    }

    public static ID2D1Bitmap1? GetD2DBitmapFromD3DTexture(ID3D11Texture2D renderTexture, IGraphicsDevicesAndContext devices)
    {
        try
        {
            if (devices == null || devices.DeviceContext == null || devices.DeviceContext.NativePointer == IntPtr.Zero) return null;
            if (renderTexture == null || renderTexture.NativePointer == IntPtr.Zero) return null;

            using var surface = renderTexture.QueryInterface<IDXGISurface>();
            if (surface == null) return null;

            float dpiX = devices.DeviceContext.Dpi.Width;
            float dpiY = devices.DeviceContext.Dpi.Height;
            Format texFmt = renderTexture.Description.Format;

            var bitmapProps = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(texFmt, Vortice.DCommon.AlphaMode.Premultiplied),
                dpiX, dpiY, BitmapOptions.None
            );
            return devices.DeviceContext.CreateBitmapFromDxgiSurface(surface, bitmapProps);
        }
        catch { return null; }
    }
}
