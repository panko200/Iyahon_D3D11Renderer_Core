// Portions adapted from YMM4-ObjLoader (Copyright (c) 2025 routersys, MIT License)
// with added native BCn GPU block-compressed DDS upload and pooled zero-cache decoding.

using System;
using System.Buffers;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

internal static class ModelTextureLoader
{
    private const int MaxGpuDim = 16384;
    private const uint DdsMagic = 0x20534444; // "DDS "
    private const uint DdsFourCcDx10 = 0x30315844; // "DX10"

    public static (ID3D11Texture2D Texture, ID3D11ShaderResourceView Srv, long EstimatedBytes)? LoadFromFile(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        string path,
        int maxTextureSize)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);

            if (ext == ".dds")
                return TryLoadDds(device, ctx, fs, maxTextureSize);

            if (ext is ".tga" or ".targa")
                return TryLoadTga(device, ctx, fs, maxTextureSize);

            // マジックバイトでDDS判定（拡張子誤認対策）
            if (fs.Length >= 4)
            {
                Span<byte> head = stackalloc byte[4];
                if (fs.Read(head) == 4 && BitConverter.ToUInt32(head) == DdsMagic)
                {
                    fs.Position = 0;
                    return TryLoadDds(device, ctx, fs, maxTextureSize);
                }
                fs.Position = 0;
            }

            return TryLoadWic(device, ctx, fs, maxTextureSize);
        }
        catch (Exception ex)
        {
            ModelLoadHelper.Log($"テクスチャ読み込み失敗 ({path}): {ex.Message}");
            return null;
        }
    }

    public static (ID3D11Texture2D Texture, ID3D11ShaderResourceView Srv, long EstimatedBytes)? LoadFromBytes(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ReadOnlyMemory<byte> data,
        string? hintExt,
        int maxTextureSize)
    {
        if (data.Length < 4) return null;

        try
        {
            string ext = (hintExt ?? string.Empty).ToLowerInvariant();
            ReadOnlySpan<byte> span = data.Span;

            if (ext == ".dds" || BitConverter.ToUInt32(span[..4]) == DdsMagic)
            {
                using var ms = new MemoryStream(data.ToArray(), writable: false);
                return TryLoadDds(device, ctx, ms, maxTextureSize);
            }

            using var stream = new MemoryStream(data.ToArray(), writable: false);
            if (ext is ".tga" or ".targa")
                return TryLoadTga(device, ctx, stream, maxTextureSize);

            return TryLoadWic(device, ctx, stream, maxTextureSize);
        }
        catch (Exception ex)
        {
            ModelLoadHelper.Log($"埋め込みテクスチャ読み込み失敗: {ex.Message}");
            return null;
        }
    }

    private static (ID3D11Texture2D Texture, ID3D11ShaderResourceView Srv, long EstimatedBytes)? TryLoadWic(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        Stream stream,
        int maxTextureSize)
    {
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0) return null;
        BitmapSource src = decoder.Frames[0];

        int origW = src.PixelWidth;
        int origH = src.PixelHeight;
        if (origW <= 0 || origH <= 0) return null;

        int limit = maxTextureSize > 0 ? Math.Min(maxTextureSize, MaxGpuDim) : MaxGpuDim;
        int maxDim = Math.Max(origW, origH);

        if (src.Format != PixelFormats.Bgra32)
        {
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        }

        if (maxDim > limit)
        {
            double scale = (double)limit / maxDim;
            src = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        }

        int width = Math.Clamp(src.PixelWidth, 1, MaxGpuDim);
        int height = Math.Clamp(src.PixelHeight, 1, MaxGpuDim);
        int stride = width * 4;
        int byteCount = stride * height;

        byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            src.CopyPixels(pixels, stride, 0);
            return UploadBgraWithMips(device, ctx, pixels.AsSpan(0, byteCount), width, height);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    internal static (ID3D11Texture2D Texture, ID3D11ShaderResourceView Srv, long EstimatedBytes)? UploadBgraBytes(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height)
        => UploadBgraWithMips(device, ctx, bgraPixels, width, height);

    private static unsafe (ID3D11Texture2D Texture, ID3D11ShaderResourceView Srv, long EstimatedBytes)? UploadBgraWithMips(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0 || width > MaxGpuDim || height > MaxGpuDim)
            return null;

        int rowPitch = width * 4;
        if (bgraPixels.Length < rowPitch * height)
            return null;

        var texDesc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 0,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        };

        var tex = device.CreateTexture2D(texDesc);
        try
        {
            fixed (byte* p = bgraPixels)
            {
                ctx.UpdateSubresource(tex, 0, null, (IntPtr)p, rowPitch, 0);
            }
            var srv = device.CreateShaderResourceView(tex);
            ctx.GenerateMips(srv);
            long estBytes = (long)width * height * 4L * 4L / 3L;
            return (tex, srv, estBytes);
        }
        catch
        {
            tex.Dispose();
            throw;
        }
    }

    private static unsafe (ID3D11Texture2D Texture, ID3D11ShaderResourceView Srv, long EstimatedBytes)? TryLoadDds(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        Stream stream,
        int maxTextureSize)
    {
        using var br = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (stream.Length - stream.Position < 128) return null;

        uint magic = br.ReadUInt32();
        if (magic != DdsMagic) return null;

        uint headerSize = br.ReadUInt32();
        if (headerSize != 124) return null;

        uint flags = br.ReadUInt32();
        int height = (int)br.ReadUInt32();
        int width = (int)br.ReadUInt32();
        uint pitchOrLinearSize = br.ReadUInt32();
        uint depth = br.ReadUInt32();
        int mipCount = Math.Max(1, (int)br.ReadUInt32());
        br.BaseStream.Seek(44, SeekOrigin.Current); // dwReserved1[11]

        uint pfSize = br.ReadUInt32();
        uint pfFlags = br.ReadUInt32();
        uint pfFourCC = br.ReadUInt32();
        uint pfRgbBitCount = br.ReadUInt32();
        uint pfRMask = br.ReadUInt32();
        uint pfGMask = br.ReadUInt32();
        uint pfBMask = br.ReadUInt32();
        uint pfAMask = br.ReadUInt32();

        uint caps1 = br.ReadUInt32();
        uint caps2 = br.ReadUInt32();
        br.BaseStream.Seek(12, SeekOrigin.Current); // caps3, caps4, reserved2

        // キューブマップ・ボリュームテクスチャの単体読み込み保護（slice 0 だけ読む場合は 2D 配列のみ許容）
        bool isCubemap = (caps2 & 0x200) != 0;
        if (isCubemap || depth > 1) return null;
        if (width <= 0 || height <= 0 || width > MaxGpuDim || height > MaxGpuDim) return null;

        bool hasFourCc = (pfFlags & 0x4) != 0;
        bool isRgb = (pfFlags & 0x40) != 0;
        bool isLum = (pfFlags & 0x20000) != 0;

        Format dxgiFormat = Format.Unknown;
        bool isDx10 = false;

        if (hasFourCc && pfFourCC == DdsFourCcDx10)
        {
            if (stream.Length - stream.Position < 20) return null;
            isDx10 = true;
            uint rawDxgi = br.ReadUInt32();
            uint resDim = br.ReadUInt32();
            uint miscFlag = br.ReadUInt32();
            uint arraySize = br.ReadUInt32();
            br.ReadUInt32(); // miscFlags2

            if (resDim != 3 || (miscFlag & 0x4) != 0 || arraySize == 0)
                return null;

            dxgiFormat = StripSrgb((Format)rawDxgi);
        }
        else if (hasFourCc)
        {
            dxgiFormat = FourCcToDxgiFormat(pfFourCC);
        }

        // ブロック圧縮またはネイティブDX10非圧縮フォーマットの直接GPUアップロード
        if (dxgiFormat != Format.Unknown && TryGetFormatMetrics(dxgiFormat, out int blockBytes, out bool isCompressed, out int rawBpp))
        {
            if (isCompressed && ((width % 4) != 0 || (height % 4) != 0) && (width > 4 || height > 4))
            {
                // 非4の倍数のBCnでもmipCount==1や小サイズを許容するドライバはあるが念のため通常処理
            }

            long dataStart = stream.Position;
            int mipToKeepStart = 0;
            long skipBytes = 0;
            int w = width;
            int h = height;

            int limit = maxTextureSize > 0 ? Math.Min(maxTextureSize, MaxGpuDim) : MaxGpuDim;
            if (mipCount > 1 && Math.Max(w, h) > limit)
            {
                for (int m = 0; m < mipCount - 1; m++)
                {
                    int curW = Math.Max(1, w >> m);
                    int curH = Math.Max(1, h >> m);
                    if (Math.Max(curW, curH) <= limit) break;

                    int nextW = Math.Max(1, w >> (m + 1));
                    int nextH = Math.Max(1, h >> (m + 1));
                    if (isCompressed && (nextW < 4 || nextH < 4 || (nextW % 4) != 0 || (nextH % 4) != 0))
                        break;

                    skipBytes += ComputeSurfaceSize(curW, curH, blockBytes, isCompressed, rawBpp);
                    mipToKeepStart = m + 1;
                }
            }

            int finalMips = mipCount - mipToKeepStart;
            int finalW = Math.Max(1, w >> mipToKeepStart);
            int finalH = Math.Max(1, h >> mipToKeepStart);

            // 残りのmipチェーンの合計バイト数を計算
            long totalUploadBytes = 0;
            var mipSizes = new int[finalMips];
            var mipPitches = new int[finalMips];
            for (int m = 0; m < finalMips; m++)
            {
                int mw = Math.Max(1, finalW >> m);
                int mh = Math.Max(1, finalH >> m);
                int rowPitch = isCompressed
                    ? Math.Max(1, (mw + 3) / 4) * blockBytes
                    : mw * rawBpp;
                int slicePitch = ComputeSurfaceSize(mw, mh, blockBytes, isCompressed, rawBpp);
                mipPitches[m] = rowPitch;
                mipSizes[m] = slicePitch;
                totalUploadBytes += slicePitch;
            }

            if (totalUploadBytes <= 0 || totalUploadBytes > int.MaxValue) return null;
            if (stream.Length - (dataStart + skipBytes) < totalUploadBytes)
                return null;

            stream.Seek(dataStart + skipBytes, SeekOrigin.Begin);

            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)totalUploadBytes);
            try
            {
                int read = 0;
                while (read < totalUploadBytes)
                {
                    int chunk = stream.Read(buffer, read, (int)totalUploadBytes - read);
                    if (chunk <= 0) return null;
                    read += chunk;
                }

                var texDesc = new Texture2DDescription
                {
                    Width = finalW,
                    Height = finalH,
                    MipLevels = finalMips,
                    ArraySize = 1,
                    Format = dxgiFormat,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                };

                fixed (byte* pBase = buffer)
                {
                    var subresources = new SubresourceData[finalMips];
                    long off = 0;
                    for (int m = 0; m < finalMips; m++)
                    {
                        subresources[m] = new SubresourceData((IntPtr)(pBase + off), mipPitches[m], mipSizes[m]);
                        off += mipSizes[m];
                    }

                    var tex = device.CreateTexture2D(texDesc, subresources);
                    var srv = device.CreateShaderResourceView(tex);
                    return (tex, srv, totalUploadBytes);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // フォールバック: 非DX10 非圧縮 RGB / RGBA / Grayscale DDS
        if (!isDx10 && (isRgb || isLum) && (pfRgbBitCount == 8 || pfRgbBitCount == 24 || pfRgbBitCount == 32))
        {
            int srcBpp = (int)(pfRgbBitCount / 8);
            bool hasPitch = (flags & 0x8) != 0;
            int fileStride = hasPitch && pitchOrLinearSize > 0
                ? Math.Max((int)pitchOrLinearSize, width * srcBpp)
                : width * srcBpp;

            long reqBytes = (long)fileStride * height;
            if (stream.Length - stream.Position < reqBytes) return null;

            int dstBytes = width * height * 4;
            byte[] rowBuf = ArrayPool<byte>.Shared.Rent(fileStride);
            byte[] dstBuf = ArrayPool<byte>.Shared.Rent(dstBytes);
            try
            {
                int rShift = GetMaskShift(pfRMask);
                int gShift = GetMaskShift(pfGMask);
                int bShift = GetMaskShift(pfBMask);
                int aShift = GetMaskShift(pfAMask);

                for (int y = 0; y < height; y++)
                {
                    int rRead = stream.Read(rowBuf, 0, fileStride);
                    if (rRead < width * srcBpp) return null;

                    int dstRow = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int sIdx = x * srcBpp;
                        byte r, g, b, a;
                        if (srcBpp == 1)
                        {
                            r = g = b = rowBuf[sIdx];
                            a = 255;
                        }
                        else if (srcBpp == 3)
                        {
                            uint v = (uint)(rowBuf[sIdx] | (rowBuf[sIdx + 1] << 8) | (rowBuf[sIdx + 2] << 16));
                            r = (byte)((v & pfRMask) >> rShift);
                            g = (byte)((v & pfGMask) >> gShift);
                            b = (byte)((v & pfBMask) >> bShift);
                            a = 255;
                        }
                        else
                        {
                            uint v = BitConverter.ToUInt32(rowBuf, sIdx);
                            r = (byte)((v & pfRMask) >> rShift);
                            g = (byte)((v & pfGMask) >> gShift);
                            b = (byte)((v & pfBMask) >> bShift);
                            a = pfAMask != 0 ? (byte)((v & pfAMask) >> aShift) : (byte)255;
                        }

                        int dIdx = dstRow + x * 4;
                        dstBuf[dIdx] = b;
                        dstBuf[dIdx + 1] = g;
                        dstBuf[dIdx + 2] = r;
                        dstBuf[dIdx + 3] = a;
                    }
                }

                // maxTextureSize超過時は縮小アップロード
                int limit = maxTextureSize > 0 ? Math.Min(maxTextureSize, MaxGpuDim) : MaxGpuDim;
                if (Math.Max(width, height) > limit)
                {
                    var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, dstBuf, width * 4);
                    double scale = (double)limit / Math.Max(width, height);
                    var scaled = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                    int sw = Math.Max(1, scaled.PixelWidth);
                    int sh = Math.Max(1, scaled.PixelHeight);
                    int sBytes = sw * sh * 4;
                    byte[] scaledBuf = ArrayPool<byte>.Shared.Rent(sBytes);
                    try
                    {
                        scaled.CopyPixels(scaledBuf, sw * 4, 0);
                        return UploadBgraWithMips(device, ctx, scaledBuf.AsSpan(0, sBytes), sw, sh);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(scaledBuf);
                    }
                }

                return UploadBgraWithMips(device, ctx, dstBuf.AsSpan(0, dstBytes), width, height);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rowBuf);
                ArrayPool<byte>.Shared.Return(dstBuf);
            }
        }

        return null;
    }

    private static (ID3D11Texture2D Texture, ID3D11ShaderResourceView Srv, long EstimatedBytes)? TryLoadTga(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        Stream stream,
        int maxTextureSize)
    {
        if (stream.Length - stream.Position < 18) return null;
        using var br = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        byte idLength = br.ReadByte();
        byte colorMapType = br.ReadByte();
        byte imageType = br.ReadByte();
        br.ReadUInt16(); // colorMapOrigin
        ushort colorMapLength = br.ReadUInt16();
        byte colorMapEntrySize = br.ReadByte();
        br.ReadInt16(); // xOrigin
        br.ReadInt16(); // yOrigin
        int width = br.ReadUInt16();
        int height = br.ReadUInt16();
        byte pixelDepth = br.ReadByte();
        byte imageDescriptor = br.ReadByte();

        if (width <= 0 || height <= 0 || width > MaxGpuDim || height > MaxGpuDim) return null;
        if (idLength > 0) stream.Seek(idLength, SeekOrigin.Current);
        if (colorMapType == 1)
        {
            int skip = colorMapLength * ((colorMapEntrySize + 7) / 8);
            stream.Seek(skip, SeekOrigin.Current);
        }

        if (imageType is not (2 or 3 or 10)) return null;
        if (imageType == 3 && pixelDepth != 8) return null;
        if (imageType is (2 or 10) && pixelDepth is not (24 or 32)) return null;

        int stride = width * 4;
        int totalBytes = stride * height;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(totalBytes);
        byte[]? swapRow = null;
        try
        {
            int pixelCount = width * height;
            int dstIdx = 0;

            if (imageType == 2)
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    pixels[dstIdx++] = br.ReadByte();
                    pixels[dstIdx++] = br.ReadByte();
                    pixels[dstIdx++] = br.ReadByte();
                    pixels[dstIdx++] = pixelDepth == 32 ? br.ReadByte() : (byte)255;
                }
            }
            else if (imageType == 3)
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    byte g = br.ReadByte();
                    pixels[dstIdx++] = g;
                    pixels[dstIdx++] = g;
                    pixels[dstIdx++] = g;
                    pixels[dstIdx++] = 255;
                }
            }
            else if (imageType == 10)
            {
                int currentPixel = 0;
                while (currentPixel < pixelCount)
                {
                    byte packet = br.ReadByte();
                    int count = (packet & 0x7F) + 1;
                    if ((packet & 0x80) != 0)
                    {
                        byte b = br.ReadByte();
                        byte g = br.ReadByte();
                        byte r = br.ReadByte();
                        byte a = pixelDepth == 32 ? br.ReadByte() : (byte)255;
                        for (int i = 0; i < count && currentPixel < pixelCount; i++)
                        {
                            pixels[dstIdx++] = b;
                            pixels[dstIdx++] = g;
                            pixels[dstIdx++] = r;
                            pixels[dstIdx++] = a;
                            currentPixel++;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < count && currentPixel < pixelCount; i++)
                        {
                            pixels[dstIdx++] = br.ReadByte();
                            pixels[dstIdx++] = br.ReadByte();
                            pixels[dstIdx++] = br.ReadByte();
                            pixels[dstIdx++] = pixelDepth == 32 ? br.ReadByte() : (byte)255;
                            currentPixel++;
                        }
                    }
                }
            }

            bool isTopLeft = (imageDescriptor & 0x20) != 0;
            if (!isTopLeft)
            {
                swapRow = ArrayPool<byte>.Shared.Rent(stride);
                for (int y = 0; y < height / 2; y++)
                {
                    int topOff = y * stride;
                    int botOff = (height - 1 - y) * stride;
                    Buffer.BlockCopy(pixels, topOff, swapRow, 0, stride);
                    Buffer.BlockCopy(pixels, botOff, pixels, topOff, stride);
                    Buffer.BlockCopy(swapRow, 0, pixels, botOff, stride);
                }
            }

            int limit = maxTextureSize > 0 ? Math.Min(maxTextureSize, MaxGpuDim) : MaxGpuDim;
            if (Math.Max(width, height) > limit)
            {
                var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                double scale = (double)limit / Math.Max(width, height);
                var scaled = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                int sw = Math.Max(1, scaled.PixelWidth);
                int sh = Math.Max(1, scaled.PixelHeight);
                int sBytes = sw * sh * 4;
                byte[] scaledBuf = ArrayPool<byte>.Shared.Rent(sBytes);
                try
                {
                    scaled.CopyPixels(scaledBuf, sw * 4, 0);
                    return UploadBgraWithMips(device, ctx, scaledBuf.AsSpan(0, sBytes), sw, sh);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scaledBuf);
                }
            }

            return UploadBgraWithMips(device, ctx, pixels.AsSpan(0, totalBytes), width, height);
        }
        finally
        {
            if (swapRow != null) ArrayPool<byte>.Shared.Return(swapRow);
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static Format StripSrgb(Format fmt) => fmt switch
    {
        Format.BC1_UNorm_SRgb => Format.BC1_UNorm,
        Format.BC2_UNorm_SRgb => Format.BC2_UNorm,
        Format.BC3_UNorm_SRgb => Format.BC3_UNorm,
        Format.BC7_UNorm_SRgb => Format.BC7_UNorm,
        Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_UNorm,
        Format.B8G8R8A8_UNorm_SRgb => Format.B8G8R8A8_UNorm,
        Format.B8G8R8X8_UNorm_SRgb => Format.B8G8R8X8_UNorm,
        _ => fmt,
    };

    private static Format FourCcToDxgiFormat(uint fourCc) => fourCc switch
    {
        0x31545844 => Format.BC1_UNorm, // "DXT1"
        0x32545844 => Format.BC2_UNorm, // "DXT2"
        0x33545844 => Format.BC2_UNorm, // "DXT3"
        0x34545844 => Format.BC3_UNorm, // "DXT4"
        0x35545844 => Format.BC3_UNorm, // "DXT5"
        0x31495441 => Format.BC4_UNorm, // "ATI1"
        0x55344342 => Format.BC4_UNorm, // "BC4U"
        0x53344342 => Format.BC4_SNorm, // "BC4S"
        0x32495441 => Format.BC5_UNorm, // "ATI2"
        0x55354342 => Format.BC5_UNorm, // "BC5U"
        0x53354342 => Format.BC5_SNorm, // "BC5S"
        _ => Format.Unknown,
    };

    private static bool TryGetFormatMetrics(Format fmt, out int blockBytes, out bool isCompressed, out int rawBpp)
    {
        switch (fmt)
        {
            case Format.BC1_UNorm:
            case Format.BC4_UNorm:
            case Format.BC4_SNorm:
                blockBytes = 8; isCompressed = true; rawBpp = 0; return true;

            case Format.BC2_UNorm:
            case Format.BC3_UNorm:
            case Format.BC5_UNorm:
            case Format.BC5_SNorm:
            case Format.BC6H_Uf16:
            case Format.BC6H_Sf16:
            case Format.BC7_UNorm:
                blockBytes = 16; isCompressed = true; rawBpp = 0; return true;

            case Format.R8G8B8A8_UNorm:
            case Format.B8G8R8A8_UNorm:
            case Format.B8G8R8X8_UNorm:
                blockBytes = 0; isCompressed = false; rawBpp = 4; return true;

            default:
                blockBytes = 0; isCompressed = false; rawBpp = 0; return false;
        }
    }

    private static int ComputeSurfaceSize(int width, int height, int blockBytes, bool isCompressed, int rawBpp)
    {
        if (isCompressed)
        {
            int bw = Math.Max(1, (width + 3) / 4);
            int bh = Math.Max(1, (height + 3) / 4);
            return bw * bh * blockBytes;
        }
        return width * height * rawBpp;
    }

    private static int GetMaskShift(uint mask)
    {
        if (mask == 0) return 0;
        int shift = 0;
        while ((mask & 1) == 0)
        {
            mask >>= 1;
            shift++;
        }
        return shift;
    }
}
