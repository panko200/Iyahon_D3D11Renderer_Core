using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iyahon_D3D11Renderer_Core.Model3D;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// HDRI 360° 環境マップ（Radiance .hdr / .dds / .png / .jpg 等）のデコード、
/// Equirectangular Wrap-U 対応プレフィルタリング、GPU キャッシュを担当するマネージャ。
/// </summary>
internal static class EnvironmentMapManager
{
    private const int MaxEnvWidth = 2048;

    [StructLayout(LayoutKind.Sequential)]
    private struct CbEnvDownsample
    {
        public float RcpSrcWidth;
        public float RcpSrcHeight;
        public float MipLevel;
        public float TotalMips;
    }

    internal const string EnvDownsampleShaderSource = @"
cbuffer CbEnvDownsample : register(b0)
{
    float RcpSrcWidth;
    float RcpSrcHeight;
    float MipLevel;
    float TotalMips;
};

Texture2D    gSrcEnv : register(t0);
SamplerState gWrapU  : register(s0);

struct PSInput
{
    float4 Pos : SV_POSITION;
    float2 UV  : TEXCOORD0;
};

PSInput VS_EnvDownsample(uint vid : SV_VertexID)
{
    PSInput o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.5, 1.0);
    o.UV = uv;
    return o;
}

float4 PS_EnvDownsample(PSInput input) : SV_Target
{
    // Wrap-U, Clamp-V separable-like 13-tap kernel for smooth glossy->diffuse IBL mips
    float spread = 1.0 + MipLevel * 0.45;
    float2 texel = float2(RcpSrcWidth, RcpSrcHeight) * spread;
    float latCos = max(sin(clamp(input.UV.y, 0.04, 0.96) * 3.14159265), 0.25);
    float2 hStep = float2(texel.x / latCos, 0.0);
    float2 vStep = float2(0.0, texel.y);

    float3 sum = gSrcEnv.SampleLevel(gWrapU, input.UV, 0).rgb * 0.22;
    sum += gSrcEnv.SampleLevel(gWrapU, input.UV + hStep, 0).rgb * 0.11;
    sum += gSrcEnv.SampleLevel(gWrapU, input.UV - hStep, 0).rgb * 0.11;
    sum += gSrcEnv.SampleLevel(gWrapU, input.UV + vStep, 0).rgb * 0.11;
    sum += gSrcEnv.SampleLevel(gWrapU, input.UV - vStep, 0).rgb * 0.11;

    sum += gSrcEnv.SampleLevel(gWrapU, input.UV + hStep + vStep, 0).rgb * 0.065;
    sum += gSrcEnv.SampleLevel(gWrapU, input.UV - hStep + vStep, 0).rgb * 0.065;
    sum += gSrcEnv.SampleLevel(gWrapU, input.UV + hStep - vStep, 0).rgb * 0.065;
    sum += gSrcEnv.SampleLevel(gWrapU, input.UV - hStep - vStep, 0).rgb * 0.065;

    sum += gSrcEnv.SampleLevel(gWrapU, input.UV + hStep * 2.0, 0).rgb * 0.02;
    sum += gSrcEnv.SampleLevel(gWrapU, input.UV - hStep * 2.0, 0).rgb * 0.02;
    sum += gSrcEnv.SampleLevel(gWrapU, input.UV + vStep * 2.0, 0).rgb * 0.02;
    sum += gSrcEnv.SampleLevel(gWrapU, input.UV - vStep * 2.0, 0).rgb * 0.02;

    return float4(min(sum, 65000.0), 1.0);
}
";

    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        public readonly IntPtr DevicePtr;
        public readonly string NormalizedPath;
        public readonly long LastWriteTicks;
        public readonly long FileLength;

        public CacheKey(IntPtr dev, string path, long ticks, long len)
        {
            DevicePtr = dev;
            NormalizedPath = path;
            LastWriteTicks = ticks;
            FileLength = len;
        }

        public bool Equals(CacheKey other) =>
            DevicePtr == other.DevicePtr &&
            LastWriteTicks == other.LastWriteTicks &&
            FileLength == other.FileLength &&
            string.Equals(NormalizedPath, other.NormalizedPath, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);
        public override int GetHashCode() =>
            HashCode.Combine(DevicePtr, NormalizedPath.GetHashCode(StringComparison.OrdinalIgnoreCase), LastWriteTicks, FileLength);
    }

    private sealed class EnvEntry : IDisposable
    {
        public ID3D11Texture2D? Texture;
        public ID3D11ShaderResourceView? Srv;
        public float MaxMip;
        public void Dispose()
        {
            Srv?.Dispose();
            Texture?.Dispose();
        }
    }

    private sealed class DevicePipeline : IDisposable
    {
        public ID3D11VertexShader? Vs;
        public ID3D11PixelShader? Ps;
        public ID3D11Buffer? Cb;
        public ID3D11SamplerState? EnvSampler;
        public ID3D11BlendState? BlendDisabled;
        public ID3D11DepthStencilState? DepthDisabled;
        public ID3D11RasterizerState? RasterizerNoCull;

        public void Dispose()
        {
            Vs?.Dispose();
            Ps?.Dispose();
            Cb?.Dispose();
            EnvSampler?.Dispose();
            BlendDisabled?.Dispose();
            DepthDisabled?.Dispose();
            RasterizerNoCull?.Dispose();
        }
    }

    private static readonly object s_lock = new();
    private static readonly Dictionary<CacheKey, EnvEntry?> s_cache = new();
    private static readonly Dictionary<IntPtr, DevicePipeline> s_pipelines = new();

    public static bool TryGetEnvMap(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        string? path,
        out ID3D11ShaderResourceView? srv,
        out float maxMip)
    {
        srv = null;
        maxMip = 0f;
        if (string.IsNullOrWhiteSpace(path) || device.NativePointer == IntPtr.Zero)
            return false;

        CacheKey key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            key = new CacheKey(device.NativePointer, info.FullName, info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch
        {
            return false;
        }

        lock (s_lock)
        {
            if (s_cache.TryGetValue(key, out var hit))
            {
                if (hit?.Srv != null)
                {
                    srv = hit.Srv;
                    maxMip = hit.MaxMip;
                    return true;
                }
                return false;
            }
        }

        EnvEntry? created = LoadAndPrefilter(device, ctx, key.NormalizedPath);

        lock (s_lock)
        {
            if (s_cache.TryGetValue(key, out var existing))
            {
                created?.Dispose();
                if (existing?.Srv != null)
                {
                    srv = existing.Srv;
                    maxMip = existing.MaxMip;
                    return true;
                }
                return false;
            }

            if (s_cache.Count >= 8)
            {
                foreach (var kv in s_cache)
                {
                    kv.Value?.Dispose();
                }
                s_cache.Clear();
            }

            s_cache[key] = created;
            if (created?.Srv != null)
            {
                srv = created.Srv;
                maxMip = created.MaxMip;
                return true;
            }
            return false;
        }
    }

    public static ID3D11SamplerState? GetOrCreateEnvSampler(ID3D11Device device)
    {
        if (device.NativePointer == IntPtr.Zero) return null;
        lock (s_lock)
        {
            return GetOrCreatePipelineLocked(device).EnvSampler;
        }
    }

    public static void ClearForDevice(IntPtr devicePtr)
    {
        if (devicePtr == IntPtr.Zero) return;
        lock (s_lock)
        {
            var toRemove = new List<CacheKey>();
            foreach (var kv in s_cache)
            {
                if (kv.Key.DevicePtr == devicePtr)
                {
                    kv.Value?.Dispose();
                    toRemove.Add(kv.Key);
                }
            }
            foreach (var k in toRemove) s_cache.Remove(k);

            if (s_pipelines.TryGetValue(devicePtr, out var pipe))
            {
                pipe.Dispose();
                s_pipelines.Remove(devicePtr);
            }
        }
    }

    public static void ClearAll()
    {
        lock (s_lock)
        {
            foreach (var kv in s_cache) kv.Value?.Dispose();
            s_cache.Clear();
            foreach (var kv in s_pipelines) kv.Value.Dispose();
            s_pipelines.Clear();
        }
    }

    private static DevicePipeline GetOrCreatePipelineLocked(ID3D11Device device)
    {
        if (s_pipelines.TryGetValue(device.NativePointer, out var existing))
            return existing;

        var pipe = new DevicePipeline();
        try
        {
            pipe.EnvSampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });

            var vsBytes = ShaderBytecodeCache.GetOrCompile("EnvDown_VS", EnvDownsampleShaderSource, "VS_EnvDownsample", "vs_5_0", "env_downsample");
            var psBytes = ShaderBytecodeCache.GetOrCompile("EnvDown_PS", EnvDownsampleShaderSource, "PS_EnvDownsample", "ps_5_0", "env_downsample");
            if (vsBytes != null && psBytes != null)
            {
                pipe.Vs = device.CreateVertexShader(vsBytes);
                pipe.Ps = device.CreatePixelShader(psBytes);
                pipe.Cb = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbEnvDownsample>(), BindFlags.ConstantBuffer));

                var bd = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
                bd.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = false,
                    RenderTargetWriteMask = ColorWriteEnable.All,
                };
                pipe.BlendDisabled = device.CreateBlendState(bd);
                pipe.DepthDisabled = device.CreateDepthStencilState(new DepthStencilDescription
                {
                    DepthEnable = false,
                    DepthWriteMask = DepthWriteMask.Zero,
                    StencilEnable = false,
                });
                pipe.RasterizerNoCull = device.CreateRasterizerState(new RasterizerDescription
                {
                    FillMode = FillMode.Solid,
                    CullMode = CullMode.None,
                    DepthClipEnable = false,
                });
            }
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"EnvironmentMapManager パイプライン初期化エラー: {ex.Message}");
        }

        s_pipelines[device.NativePointer] = pipe;
        return pipe;
    }

    private static unsafe EnvEntry? LoadAndPrefilter(ID3D11Device device, ID3D11DeviceContext ctx, string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dds")
            {
                var ddsLoaded = ModelTextureLoader.LoadFromFile(device, ctx, path, MaxEnvWidth);
                if (ddsLoaded is { } ddsRes)
                {
                    var ddsDesc = ddsRes.Texture.Description;
                    return new EnvEntry
                    {
                        Texture = ddsRes.Texture,
                        Srv = ddsRes.Srv,
                        MaxMip = MathF.Max(0f, ddsDesc.MipLevels - 1),
                    };
                }
                return null;
            }

            int width, height;
            Half[]? rgba16f;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            {
                if (ext == ".hdr" || ext == ".pic")
                {
                    if (!TryDecodeRadianceHdr(fs, out width, out height, out rgba16f) || rgba16f == null)
                        return null;
                }
                else
                {
                    if (!TryDecodeStandardImageAsLinearHdr(fs, out width, out height, out rgba16f) || rgba16f == null)
                        return null;
                }
            }

            if (width <= 0 || height <= 0) return null;

            var texDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 0,
                ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            };

            var tex = device.CreateTexture2D(texDesc);
            ID3D11ShaderResourceView? fullSrv = null;
            try
            {
                fixed (Half* pHalf = rgba16f)
                {
                    ctx.UpdateSubresource(tex, 0, null, (IntPtr)pHalf, width * 8, 0);
                }

                fullSrv = device.CreateShaderResourceView(tex);
                int mipCount = tex.Description.MipLevels;

                DevicePipeline pipe;
                lock (s_lock)
                {
                    pipe = GetOrCreatePipelineLocked(device);
                }

                if (mipCount > 1 && pipe.Vs != null && pipe.Ps != null && pipe.Cb != null && pipe.EnvSampler != null)
                {
                    PrefilterEnvMips(device, ctx, tex, width, height, mipCount, pipe);
                }
                else if (mipCount > 1)
                {
                    ctx.GenerateMips(fullSrv);
                }

                return new EnvEntry
                {
                    Texture = tex,
                    Srv = fullSrv,
                    MaxMip = MathF.Max(0f, mipCount - 1),
                };
            }
            catch
            {
                fullSrv?.Dispose();
                tex.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"HDRI環境マップ読み込み失敗 ({path}): {ex.Message}");
            return null;
        }
    }

    private static void PrefilterEnvMips(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11Texture2D tex,
        int baseWidth,
        int baseHeight,
        int mipCount,
        DevicePipeline pipe)
    {
        using var stateSaver = new D3D11StateSaver(ctx);

        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(pipe.Vs);
        ctx.PSSetShader(pipe.Ps);
        ctx.PSSetSampler(0, pipe.EnvSampler);
        ctx.PSSetConstantBuffer(0, pipe.Cb);
        ctx.OMSetBlendState(pipe.BlendDisabled, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(pipe.DepthDisabled, 0);
        ctx.RSSetState(pipe.RasterizerNoCull);

        for (int m = 1; m < mipCount; m++)
        {
            int srcW = Math.Max(1, baseWidth >> (m - 1));
            int srcH = Math.Max(1, baseHeight >> (m - 1));
            int dstW = Math.Max(1, baseWidth >> m);
            int dstH = Math.Max(1, baseHeight >> m);

            using var mipSrcSrv = device.CreateShaderResourceView(tex, new ShaderResourceViewDescription
            {
                Format = Format.R16G16B16A16_Float,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MostDetailedMip = m - 1, MipLevels = 1 },
            });
            using var mipDstRtv = device.CreateRenderTargetView(tex, new RenderTargetViewDescription
            {
                Format = Format.R16G16B16A16_Float,
                ViewDimension = RenderTargetViewDimension.Texture2D,
                Texture2D = new Texture2DRenderTargetView { MipSlice = m },
            });

            var cb = new CbEnvDownsample
            {
                RcpSrcWidth = 1f / srcW,
                RcpSrcHeight = 1f / srcH,
                MipLevel = m,
                TotalMips = mipCount - 1,
            };
            ctx.UpdateSubresource(in cb, pipe.Cb!);

            ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, dstW, dstH, 0f, 1f));
            ctx.OMSetRenderTargets(mipDstRtv, (ID3D11DepthStencilView?)null);
            ctx.PSSetShaderResource(0, mipSrcSrv);
            ctx.Draw(3, 0);

            ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView)null!);
            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        }
    }

    private static bool TryDecodeStandardImageAsLinearHdr(
        Stream stream,
        out int width,
        out int height,
        out Half[]? rgba16f)
    {
        width = 0;
        height = 0;
        rgba16f = null;

        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return false;

        BitmapSource src = decoder.Frames[0];
        if (src.PixelWidth <= 0 || src.PixelHeight <= 0) return false;

        if (src.Format != PixelFormats.Bgra32)
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        if (src.PixelWidth > MaxEnvWidth)
        {
            double scale = (double)MaxEnvWidth / src.PixelWidth;
            src = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        }

        width = Math.Max(1, src.PixelWidth);
        height = Math.Max(1, src.PixelHeight);

        int stride = width * 4;
        byte[] bgra = new byte[stride * height];
        src.CopyPixels(bgra, stride, 0);

        rgba16f = new Half[width * height * 4];
        Half one = (Half)1.0f;

        for (int i = 0, p = 0; i < bgra.Length; i += 4, p += 4)
        {
            float b = bgra[i] / 255f;
            float g = bgra[i + 1] / 255f;
            float r = bgra[i + 2] / 255f;

            rgba16f[p] = (Half)SrgbToLinear(r);
            rgba16f[p + 1] = (Half)SrgbToLinear(g);
            rgba16f[p + 2] = (Half)SrgbToLinear(b);
            rgba16f[p + 3] = one;
        }
        return true;
    }

    internal static bool TryDecodeRadianceHdr(
        Stream stream,
        out int width,
        out int height,
        out Half[]? rgba16f)
    {
        width = 0;
        height = 0;
        rgba16f = null;

        // Read header line-by-line (ASCII LF-terminated)
        string? resLine = null;
        while (true)
        {
            string? line = ReadAsciiLine(stream);
            if (line == null) return false;
            if (line.Length == 0)
            {
                resLine = ReadAsciiLine(stream);
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(resLine)) return false;
        if (!TryParseHdrResolution(resLine, out width, out height, out bool flipY, out bool flipX))
            return false;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            return false;

        float[] rgbFloat = new float[width * height * 3];
        byte[] scanline = new byte[width * 4];

        for (int y = 0; y < height; y++)
        {
            int dstY = flipY ? (height - 1 - y) : y;
            if (!ReadHdrScanline(stream, scanline, width))
                return false;

            for (int x = 0; x < width; x++)
            {
                int dstX = flipX ? (width - 1 - x) : x;
                byte r = scanline[x];
                byte g = scanline[width + x];
                byte b = scanline[width * 2 + x];
                byte e = scanline[width * 3 + x];

                int dstIdx = (dstY * width + dstX) * 3;
                if (e == 0)
                {
                    rgbFloat[dstIdx] = 0f;
                    rgbFloat[dstIdx + 1] = 0f;
                    rgbFloat[dstIdx + 2] = 0f;
                }
                else
                {
                    float scale = MathF.ScaleB(1.0f, e - 136);
                    rgbFloat[dstIdx] = Math.Clamp((r + 0.5f) * scale, 0f, 65000f);
                    rgbFloat[dstIdx + 1] = Math.Clamp((g + 0.5f) * scale, 0f, 65000f);
                    rgbFloat[dstIdx + 2] = Math.Clamp((b + 0.5f) * scale, 0f, 65000f);
                }
            }
        }

        // Downsample CPU-side if wider than MaxEnvWidth
        if (width > MaxEnvWidth)
        {
            int newW = MaxEnvWidth;
            int newH = Math.Max(1, (int)Math.Round((double)height * newW / width));
            var down = new Half[newW * newH * 4];
            Half one = (Half)1.0f;

            float sx = (float)width / newW;
            float sy = (float)height / newH;
            for (int ny = 0; ny < newH; ny++)
            {
                int y0 = Math.Clamp((int)(ny * sy), 0, height - 1);
                int y1 = Math.Clamp((int)((ny + 1) * sy), y0 + 1, height);
                for (int nx = 0; nx < newW; nx++)
                {
                    int x0 = Math.Clamp((int)(nx * sx), 0, width - 1);
                    int x1 = Math.Clamp((int)((nx + 1) * sx), x0 + 1, width);
                    float ar = 0f, ag = 0f, ab = 0f;
                    int cnt = 0;
                    for (int syIdx = y0; syIdx < y1; syIdx++)
                    {
                        for (int sxIdx = x0; sxIdx < x1; sxIdx++)
                        {
                            int sIdx = (syIdx * width + sxIdx) * 3;
                            ar += rgbFloat[sIdx];
                            ag += rgbFloat[sIdx + 1];
                            ab += rgbFloat[sIdx + 2];
                            cnt++;
                        }
                    }
                    float inv = cnt > 0 ? 1f / cnt : 1f;
                    int p = (ny * newW + nx) * 4;
                    down[p] = (Half)Math.Clamp(ar * inv, 0f, 65000f);
                    down[p + 1] = (Half)Math.Clamp(ag * inv, 0f, 65000f);
                    down[p + 2] = (Half)Math.Clamp(ab * inv, 0f, 65000f);
                    down[p + 3] = one;
                }
            }

            width = newW;
            height = newH;
            rgba16f = down;
            return true;
        }

        rgba16f = new Half[width * height * 4];
        Half alphaOne = (Half)1.0f;
        for (int s = 0, d = 0; s < rgbFloat.Length; s += 3, d += 4)
        {
            rgba16f[d] = (Half)rgbFloat[s];
            rgba16f[d + 1] = (Half)rgbFloat[s + 1];
            rgba16f[d + 2] = (Half)rgbFloat[s + 2];
            rgba16f[d + 3] = alphaOne;
        }
        return true;
    }

    private static string? ReadAsciiLine(Stream s)
    {
        var sb = new StringBuilder(64);
        int b;
        bool any = false;
        while ((b = s.ReadByte()) >= 0)
        {
            any = true;
            if (b == '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        return any ? sb.ToString() : null;
    }

    private static bool TryParseHdrResolution(
        string line,
        out int width,
        out int height,
        out bool flipY,
        out bool flipX)
    {
        width = 0;
        height = 0;
        flipY = false;
        flipX = false;

        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4) return false;

        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            string axis = tokens[i];
            if (!int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                return false;

            if (axis.Equals("-Y", StringComparison.Ordinal)) { height = val; flipY = false; }
            else if (axis.Equals("+Y", StringComparison.Ordinal)) { height = val; flipY = true; }
            else if (axis.Equals("+X", StringComparison.Ordinal)) { width = val; flipX = false; }
            else if (axis.Equals("-X", StringComparison.Ordinal)) { width = val; flipX = true; }
        }

        return width > 0 && height > 0;
    }

    private static bool ReadHdrScanline(Stream s, byte[] planarOut, int width)
    {
        int b0 = s.ReadByte();
        int b1 = s.ReadByte();
        int b2 = s.ReadByte();
        int b3 = s.ReadByte();
        if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0) return false;

        // New adaptive RLE format
        if (b0 == 2 && b1 == 2 && (b2 & 0x80) == 0 && width >= 8 && width <= 32767 && (((b2 << 8) | b3) == width))
        {
            for (int ch = 0; ch < 4; ch++)
            {
                int offset = ch * width;
                int x = 0;
                while (x < width)
                {
                    int code = s.ReadByte();
                    if (code < 0) return false;
                    if (code > 128)
                    {
                        int count = code - 128;
                        if (x + count > width) return false;
                        int val = s.ReadByte();
                        if (val < 0) return false;
                        byte bv = (byte)val;
                        for (int k = 0; k < count; k++)
                            planarOut[offset + x++] = bv;
                    }
                    else
                    {
                        int count = code;
                        if (count == 0 || x + count > width) return false;
                        for (int k = 0; k < count; k++)
                        {
                            int val = s.ReadByte();
                            if (val < 0) return false;
                            planarOut[offset + x++] = (byte)val;
                        }
                    }
                }
            }
            return true;
        }

        // Uncompressed or old RLE: convert interleaved RGBE into planar layout
        int px = 0;
        byte r = (byte)b0, g = (byte)b1, b = (byte)b2, e = (byte)b3;
        int rshift = 0;
        while (true)
        {
            if (r == 1 && g == 1 && b == 1 && px > 0)
            {
                int rep = e << rshift;
                rshift += 8;
                byte pr = planarOut[px - 1];
                byte pg = planarOut[width + px - 1];
                byte pb = planarOut[width * 2 + px - 1];
                byte pe = planarOut[width * 3 + px - 1];
                for (int k = 0; k < rep && px < width; k++)
                {
                    planarOut[px] = pr;
                    planarOut[width + px] = pg;
                    planarOut[width * 2 + px] = pb;
                    planarOut[width * 3 + px] = pe;
                    px++;
                }
            }
            else
            {
                rshift = 0;
                planarOut[px] = r;
                planarOut[width + px] = g;
                planarOut[width * 2 + px] = b;
                planarOut[width * 3 + px] = e;
                px++;
            }

            if (px >= width) break;
            int n0 = s.ReadByte();
            int n1 = s.ReadByte();
            int n2 = s.ReadByte();
            int n3 = s.ReadByte();
            if (n0 < 0 || n1 < 0 || n2 < 0 || n3 < 0) return false;
            r = (byte)n0; g = (byte)n1; b = (byte)n2; e = (byte)n3;
        }
        return true;
    }

    private static float SrgbToLinear(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }
}
