using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

internal readonly struct ShadowTileAllocation
{
    public int SliceIndex { get; init; }
    public int PixelX { get; init; }
    public int PixelY { get; init; }
    public int TileRes { get; init; }
    public Vector4 UvRect { get; init; } // x = uOffset, y = vOffset, z = uScale, w = vScale
}

/// <summary>
/// 動的ページ式・タイル分割 Texture2DArray シャドウマッププール (D32_Float 深度専用)。
/// </summary>
internal sealed class ShadowMapPool : IDisposable
{
    private const int GridDim = 8;
    private const int UnusedReleaseDelayFrames = 60;
    private const int ShrinkDelayFrames = 120;

    private ID3D11Texture2D? _textureArray;
    private ID3D11DepthStencilView[]? _sliceDsvs;
    private ID3D11ShaderResourceView? _arraySrv;

    private IntPtr _devicePointer = IntPtr.Zero;
    private int _pageSize;
    private int _allocatedPages;
    private int _unusedFrames;
    private int _lowUsageFrames;
    private bool _disposed;

    public ID3D11ShaderResourceView? ArraySrv => _arraySrv;
    public ID3D11DepthStencilView[]? SliceDsvs => _sliceDsvs;
    public int PageSize => _pageSize;
    public int AllocatedPages => _allocatedPages;

    public static int GetMaxTier(int pageSize)
    {
        if (pageSize >= 1024) return 3; // 1/8 (min 128px)
        if (pageSize >= 512) return 2;  // 1/4 (128px)
        return 1;
    }

    /// <summary>
    /// リクエストされた Tier (0=1x1, 1=1/2, 2=1/4, 3=1/8) のタイル列を 8x8 バディページへパックする。
    /// VRAM 予算超過時は最も高解像度な要求から段階的にダウンスケールし、全ライトにタイルを配分する。
    /// </summary>
    public static ShadowTileAllocation[] PackTiles(
        int pageSize,
        long maxVramBytes,
        IReadOnlyList<int> desiredTiers,
        IReadOnlyList<int>? priorities,
        out int requiredPages)
    {
        int count = desiredTiers.Count;
        var results = new ShadowTileAllocation[count];
        if (count == 0)
        {
            requiredPages = 0;
            return results;
        }

        long effectiveBudget = pageSize >= 4096
            ? Math.Max(maxVramBytes, 512L * 1024L * 1024L)
            : maxVramBytes;
        long bytesPerPage = (long)pageSize * pageSize * 4L;
        int maxPages = Math.Clamp((int)(effectiveBudget / Math.Max(1L, bytesPerPage)), 2, 256);
        int maxTier = GetMaxTier(pageSize);

        var workingTiers = new int[count];
        for (int i = 0; i < count; i++)
        {
            workingTiers[i] = Math.Clamp(desiredTiers[i], 0, maxTier);
        }

        var pageMasks = new List<ulong>();

        while (true)
        {
            pageMasks.Clear();
            bool overflow = false;

            // 大タイル優先でソートして詰める（断片化ゼロ）
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int cmp = workingTiers[a].CompareTo(workingTiers[b]);
                if (cmp != 0) return cmp;
                return a.CompareTo(b);
            });

            for (int oi = 0; oi < count; oi++)
            {
                int idx = order[oi];
                int tier = workingTiers[idx];
                if (!TryAllocateCell(pageMasks, maxPages, tier, pageSize, out var alloc))
                {
                    overflow = true;
                    break;
                }
                results[idx] = alloc;
            }

            if (!overflow)
            {
                requiredPages = pageMasks.Count;
                return results;
            }

            // 予算超過: まだ下げられるタイルの中で「優先度が低い、かつ解像度が高い」ものを1段階下げる
            int candidate = -1;
            int bestScore = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (workingTiers[i] >= maxTier) continue;
                int prio = priorities != null && i < priorities.Count ? priorities[i] : 0;
                // 優先度が低く、Tier数値が小さい(高解像度な)ものを優先的に落とす
                int score = (workingTiers[i] * 1000) + prio;
                if (score < bestScore)
                {
                    bestScore = score;
                    candidate = i;
                }
            }

            if (candidate < 0)
            {
                // 全タイルが最小解像度でも maxPages を超える場合、収まる範囲だけ割り当て残りは無効(-1)とする
                pageMasks.Clear();
                for (int oi = 0; oi < count; oi++)
                {
                    int idx = order[oi];
                    if (TryAllocateCell(pageMasks, maxPages, workingTiers[idx], pageSize, out var alloc))
                    {
                        results[idx] = alloc;
                    }
                    else
                    {
                        results[idx] = new ShadowTileAllocation { SliceIndex = -1 };
                    }
                }
                requiredPages = pageMasks.Count;
                return results;
            }

            // 同優先度・同Tierのライト群をまとめて1段下げると収束が速い
            int targetTier = workingTiers[candidate];
            int targetPrio = priorities != null && candidate < priorities.Count ? priorities[candidate] : 0;
            for (int i = 0; i < count; i++)
            {
                int p = priorities != null && i < priorities.Count ? priorities[i] : 0;
                if (workingTiers[i] == targetTier && p <= targetPrio && workingTiers[i] < maxTier)
                {
                    workingTiers[i]++;
                }
            }
        }
    }

    private static bool TryAllocateCell(List<ulong> pageMasks, int maxPages, int tier, int pageSize, out ShadowTileAllocation alloc)
    {
        int blockCells = GridDim >> tier; // Tier0=8, Tier1=4, Tier2=2, Tier3=1
        int tileRes = Math.Max(1, pageSize >> tier);
        float uvScale = 1f / (1 << tier);

        for (int p = 0; p < pageMasks.Count; p++)
        {
            if (TryAllocateInPage(pageMasks, p, blockCells, tileRes, pageSize, uvScale, out alloc))
                return true;
        }

        if (pageMasks.Count < maxPages)
        {
            pageMasks.Add(0UL);
            int newPage = pageMasks.Count - 1;
            if (TryAllocateInPage(pageMasks, newPage, blockCells, tileRes, pageSize, uvScale, out alloc))
                return true;
        }

        alloc = default;
        return false;
    }

    private static bool TryAllocateInPage(
        List<ulong> pageMasks,
        int pageIndex,
        int blockCells,
        int tileRes,
        int pageSize,
        float uvScale,
        out ShadowTileAllocation alloc)
    {
        ulong currentMask = pageMasks[pageIndex];

        for (int cy = 0; cy < GridDim; cy += blockCells)
        {
            for (int cx = 0; cx < GridDim; cx += blockCells)
            {
                ulong regionMask = BuildRegionMask(cx, cy, blockCells);
                if ((currentMask & regionMask) == 0)
                {
                    pageMasks[pageIndex] = currentMask | regionMask;
                    int px = (cx * pageSize) / GridDim;
                    int py = (cy * pageSize) / GridDim;
                    alloc = new ShadowTileAllocation
                    {
                        SliceIndex = pageIndex,
                        PixelX = px,
                        PixelY = py,
                        TileRes = tileRes,
                        UvRect = new Vector4(
                            (float)cx / GridDim,
                            (float)cy / GridDim,
                            uvScale,
                            uvScale),
                    };
                    return true;
                }
            }
        }

        alloc = default;
        return false;
    }

    private static ulong BuildRegionMask(int startX, int startY, int size)
    {
        ulong mask = 0;
        for (int y = startY; y < startY + size; y++)
        {
            int rowShift = y * GridDim;
            for (int x = startX; x < startX + size; x++)
            {
                mask |= 1UL << (rowShift + x);
            }
        }
        return mask;
    }

    public void EnsureAllocated(ID3D11Device device, int pageSize, int requiredPages)
    {
        if (_disposed) return;

        if (requiredPages <= 0)
        {
            OnFrameUnused();
            return;
        }

        _unusedFrames = 0;

        bool needRecreate = _textureArray == null
            || _devicePointer != device.NativePointer
            || _pageSize != pageSize
            || requiredPages > _allocatedPages;

        if (!needRecreate && _allocatedPages > 2 && requiredPages * 2 <= _allocatedPages)
        {
            _lowUsageFrames++;
            if (_lowUsageFrames >= ShrinkDelayFrames)
            {
                needRecreate = true;
            }
        }
        else
        {
            _lowUsageFrames = 0;
        }

        if (!needRecreate) return;

        ReleaseAll();
        _lowUsageFrames = 0;

        var texDesc = new Texture2DDescription
        {
            Width = pageSize,
            Height = pageSize,
            MipLevels = 1,
            ArraySize = requiredPages,
            Format = Format.R32_Typeless,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _textureArray = device.CreateTexture2D(texDesc);
        _sliceDsvs = new ID3D11DepthStencilView[requiredPages];

        for (int i = 0; i < requiredPages; i++)
        {
            _sliceDsvs[i] = device.CreateDepthStencilView(_textureArray, new DepthStencilViewDescription
            {
                Format = Format.D32_Float,
                ViewDimension = DepthStencilViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayDepthStencilView
                {
                    ArraySize = 1,
                    FirstArraySlice = i,
                    MipSlice = 0,
                },
            });
        }

        _arraySrv = device.CreateShaderResourceView(_textureArray, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float,
            ViewDimension = ShaderResourceViewDimension.Texture2DArray,
            Texture2DArray = new Texture2DArrayShaderResourceView
            {
                ArraySize = requiredPages,
                FirstArraySlice = 0,
                MipLevels = 1,
                MostDetailedMip = 0,
            },
        });

        _devicePointer = device.NativePointer;
        _pageSize = pageSize;
        _allocatedPages = requiredPages;
    }

    public void OnFrameUnused()
    {
        if (_textureArray == null) return;
        _unusedFrames++;
        if (_unusedFrames >= UnusedReleaseDelayFrames)
        {
            ReleaseAll();
        }
    }

    public void ReleaseAll()
    {
        _arraySrv?.Dispose();
        _arraySrv = null;

        if (_sliceDsvs != null)
        {
            foreach (var dsv in _sliceDsvs)
            {
                dsv?.Dispose();
            }
            _sliceDsvs = null;
        }

        _textureArray?.Dispose();
        _textureArray = null;

        _devicePointer = IntPtr.Zero;
        _pageSize = 0;
        _allocatedPages = 0;
        _unusedFrames = 0;
        _lowUsageFrames = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseAll();
    }
}
