using System;
using System.Collections.Generic;
using System.IO;
using Vortice.Direct3D11;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

/// <summary>
/// 「D3Dマテリアル」等で個別指定されたノーマルマップ／ラフネスマップ用の GPU テクスチャキャッシュ。
/// </summary>
internal static class MaterialTextureCache
{
    private const int MaxEntries = 32;
    private const int MaxTexDim = 2048;

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

    private sealed class Entry : IDisposable
    {
        public required ID3D11Texture2D Texture;
        public required ID3D11ShaderResourceView Srv;
        public void Dispose()
        {
            Srv.Dispose();
            Texture.Dispose();
        }
    }

    private static readonly object s_lock = new();
    private static readonly List<(CacheKey Key, Entry? Value)> s_lru = new();

    public static ID3D11ShaderResourceView? GetOrLoadSrv(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || device.NativePointer == IntPtr.Zero)
            return null;

        CacheKey key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            key = new CacheKey(device.NativePointer, info.FullName, info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch
        {
            return null;
        }

        lock (s_lock)
        {
            for (int i = 0; i < s_lru.Count; i++)
            {
                if (s_lru[i].Key.Equals(key))
                {
                    var hit = s_lru[i];
                    s_lru.RemoveAt(i);
                    s_lru.Add(hit);
                    return hit.Value?.Srv;
                }
            }
        }

        var loaded = ModelTextureLoader.LoadFromFile(device, ctx, key.NormalizedPath, MaxTexDim);
        Entry? entry = loaded is { } texRes
            ? new Entry { Texture = texRes.Texture, Srv = texRes.Srv }
            : null;

        lock (s_lock)
        {
            for (int i = 0; i < s_lru.Count; i++)
            {
                if (s_lru[i].Key.Equals(key))
                {
                    entry?.Dispose();
                    var existing = s_lru[i];
                    s_lru.RemoveAt(i);
                    s_lru.Add(existing);
                    return existing.Value?.Srv;
                }
            }

            s_lru.Add((key, entry));
            while (s_lru.Count > MaxEntries)
            {
                var old = s_lru[0].Value;
                s_lru.RemoveAt(0);
                old?.Dispose();
            }

            return entry?.Srv;
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
                    var e = s_lru[i].Value;
                    s_lru.RemoveAt(i);
                    e?.Dispose();
                }
            }
        }
    }

    public static void ClearAll()
    {
        lock (s_lock)
        {
            for (int i = 0; i < s_lru.Count; i++)
            {
                s_lru[i].Value?.Dispose();
            }
            s_lru.Clear();
        }
    }
}
