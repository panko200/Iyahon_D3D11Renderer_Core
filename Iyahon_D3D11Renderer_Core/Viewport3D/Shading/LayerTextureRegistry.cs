using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D.Shading;

/// <summary>
/// タイムライン再生・アニメーション時間情報（TimeInfo ノード評価用）。
/// </summary>
public readonly record struct ShaderEvalTime(long Frame, long Length, int Fps)
{
    public static readonly ShaderEvalTime Default = new(0, 60, 60);

    public float Seconds => Fps > 0 ? (float)Frame / Fps : 0f;

    public float Progress => Length > 1
        ? Math.Clamp((float)Frame / (Length - 1), 0f, 1f)
        : 0f;
}

/// <summary>
/// メインプレビューおよび 3D ビューポートで現在のフレームにレンダリングされた
/// 他レイヤーのテクスチャ（ID3D11ShaderResourceView）を保持・供給するレジストリ。
/// </summary>
internal static class LayerTextureRegistry
{
    private sealed class CpuSampleEntry
    {
        public long Sequence;
        public int Width;
        public int Height;
        public byte[] Pixels = Array.Empty<byte>();
    }

    private sealed class DeviceLayerState
    {
        public long Sequence;
        public readonly Dictionary<int, ID3D11ShaderResourceView> Srvs = new();
        public readonly Dictionary<int, CpuSampleEntry> CpuCache = new();
    }

    private static readonly object s_lock = new();
    private static readonly Dictionary<IntPtr, DeviceLayerState> s_states = new();

    public static void BeginFrame(ID3D11Device? device)
    {
        if (device == null || device.NativePointer == IntPtr.Zero) return;
        IntPtr devPtr = device.NativePointer;
        lock (s_lock)
        {
            if (!s_states.TryGetValue(devPtr, out var st))
            {
                st = new DeviceLayerState();
                s_states[devPtr] = st;
            }
            st.Sequence++;
            st.Srvs.Clear();
        }
    }

    public static void Register(ID3D11Device? device, int layer, ID3D11ShaderResourceView? srv)
    {
        if (device == null || device.NativePointer == IntPtr.Zero || srv == null || srv.NativePointer == IntPtr.Zero)
            return;
        if (layer < 0) return;

        IntPtr devPtr = device.NativePointer;
        lock (s_lock)
        {
            if (!s_states.TryGetValue(devPtr, out var st))
            {
                st = new DeviceLayerState();
                s_states[devPtr] = st;
            }
            st.Srvs[layer] = srv;
        }
    }

    public static long GetFrameSequence(ID3D11Device? device)
    {
        if (device == null || device.NativePointer == IntPtr.Zero) return 0;
        lock (s_lock)
        {
            return s_states.TryGetValue(device.NativePointer, out var st) ? st.Sequence : 0;
        }
    }

    public static ID3D11ShaderResourceView? TryGetSrv(ID3D11Device? device, int layer)
    {
        if (device == null || device.NativePointer == IntPtr.Zero || layer < 0) return null;
        IntPtr devPtr = device.NativePointer;
        lock (s_lock)
        {
            if (!s_states.TryGetValue(devPtr, out var st)) return null;
            if (!st.Srvs.TryGetValue(layer, out var srv)) return null;
            if (srv == null || srv.NativePointer == IntPtr.Zero) return null;
            return srv;
        }
    }

    /// <summary>
    /// CPU 評価チェーン（ColorRamp / Bump 等）向けのフォールバック用レイヤーテクスチャ読み戻し。
    /// 同一フレーム内はキャッシュされ、最大 256px へダウンサンプルして返す。
    /// </summary>
    public static bool TryGetCpuSample(
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        int layer,
        out int width,
        out int height,
        out byte[]? bgraPixels)
    {
        width = 0;
        height = 0;
        bgraPixels = null;
        if (device == null || ctx == null || device.NativePointer == IntPtr.Zero || layer < 0)
            return false;

        IntPtr devPtr = device.NativePointer;
        ID3D11ShaderResourceView? srv = null;
        long seq = 0;
        lock (s_lock)
        {
            if (!s_states.TryGetValue(devPtr, out var st)) return false;
            seq = st.Sequence;
            if (st.CpuCache.TryGetValue(layer, out var cached) && cached.Sequence == seq)
            {
                width = cached.Width;
                height = cached.Height;
                bgraPixels = cached.Pixels;
                return width > 0 && height > 0 && bgraPixels.Length >= width * height * 4;
            }
            if (!st.Srvs.TryGetValue(layer, out srv) || srv == null || srv.NativePointer == IntPtr.Zero)
                return false;
        }

        try
        {
            using var res = srv.Resource;
            using var tex2d = res.QueryInterfaceOrNull<ID3D11Texture2D>();
            if (tex2d == null) return false;

            var desc = tex2d.Description;
            int srcW = desc.Width;
            int srcH = desc.Height;
            if (srcW <= 0 || srcH <= 0) return false;

            bool isBgra = desc.Format == Format.B8G8R8A8_UNorm || desc.Format == Format.B8G8R8A8_UNorm_SRgb;
            bool isRgba = desc.Format == Format.R8G8B8A8_UNorm || desc.Format == Format.R8G8B8A8_UNorm_SRgb;
            bool isHalf4 = desc.Format == Format.R16G16B16A16_Float;
            if (!isBgra && !isRgba && !isHalf4) return false;

            using var staging = device.CreateTexture2D(new Texture2DDescription
            {
                Width = srcW,
                Height = srcH,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            });

            ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, tex2d, 0, null);
            var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int dstMax = 256;
                float scale = Math.Max(srcW, srcH) > dstMax ? (float)dstMax / Math.Max(srcW, srcH) : 1f;
                int dstW = Math.Max(1, (int)MathF.Round(srcW * scale));
                int dstH = Math.Max(1, (int)MathF.Round(srcH * scale));
                byte[] outBytes = new byte[dstW * dstH * 4];
                int rowPitch = (int)mapped.RowPitch;

                if (isBgra || isRgba)
                {
                    byte[] rowBuf = new byte[srcW * 4];
                    for (int y = 0; y < dstH; y++)
                    {
                        int sy = Math.Min(srcH - 1, (y * srcH) / dstH);
                        Marshal.Copy(mapped.DataPointer + sy * rowPitch, rowBuf, 0, srcW * 4);
                        int dstRowOff = y * dstW * 4;
                        for (int x = 0; x < dstW; x++)
                        {
                            int sx = Math.Min(srcW - 1, (x * srcW) / dstW);
                            int si = sx * 4;
                            int di = dstRowOff + x * 4;
                            if (isBgra)
                            {
                                outBytes[di] = rowBuf[si];
                                outBytes[di + 1] = rowBuf[si + 1];
                                outBytes[di + 2] = rowBuf[si + 2];
                                outBytes[di + 3] = rowBuf[si + 3];
                            }
                            else
                            {
                                outBytes[di] = rowBuf[si + 2];
                                outBytes[di + 1] = rowBuf[si + 1];
                                outBytes[di + 2] = rowBuf[si];
                                outBytes[di + 3] = rowBuf[si + 3];
                            }
                        }
                    }
                }
                else
                {
                    short[] rowHalf = new short[srcW * 4];
                    for (int y = 0; y < dstH; y++)
                    {
                        int sy = Math.Min(srcH - 1, (y * srcH) / dstH);
                        Marshal.Copy(mapped.DataPointer + sy * rowPitch, rowHalf, 0, srcW * 4);
                        int dstRowOff = y * dstW * 4;
                        for (int x = 0; x < dstW; x++)
                        {
                            int sx = Math.Min(srcW - 1, (x * srcW) / dstW);
                            int si = sx * 4;
                            int di = dstRowOff + x * 4;
                            float r = (float)BitConverter.UInt16BitsToHalf((ushort)rowHalf[si]);
                            float g = (float)BitConverter.UInt16BitsToHalf((ushort)rowHalf[si + 1]);
                            float b = (float)BitConverter.UInt16BitsToHalf((ushort)rowHalf[si + 2]);
                            float a = (float)BitConverter.UInt16BitsToHalf((ushort)rowHalf[si + 3]);
                            outBytes[di] = LinearToSrgbByte(b);
                            outBytes[di + 1] = LinearToSrgbByte(g);
                            outBytes[di + 2] = LinearToSrgbByte(r);
                            outBytes[di + 3] = (byte)Math.Clamp((int)MathF.Round(a * 255f), 0, 255);
                        }
                    }
                }

                width = dstW;
                height = dstH;
                bgraPixels = outBytes;
                lock (s_lock)
                {
                    if (s_states.TryGetValue(devPtr, out var st))
                    {
                        st.CpuCache[layer] = new CpuSampleEntry
                        {
                            Sequence = seq,
                            Width = dstW,
                            Height = dstH,
                            Pixels = outBytes
                        };
                    }
                }
                return true;
            }
            finally
            {
                ctx.Unmap(staging, 0);
            }
        }
        catch
        {
            return false;
        }
    }

    public static void ClearForDevice(IntPtr devicePtr)
    {
        if (devicePtr == IntPtr.Zero) return;
        lock (s_lock)
        {
            s_states.Remove(devicePtr);
        }
    }

    public static void ClearAll()
    {
        lock (s_lock)
        {
            s_states.Clear();
        }
    }

    private static byte LinearToSrgbByte(float lin)
    {
        lin = MathF.Max(0f, MathF.Min(1f, lin));
        float srgb = lin <= 0.0031308f ? 12.92f * lin : 1.055f * MathF.Pow(lin, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(srgb * 255f), 0, 255);
    }
}
