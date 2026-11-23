using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// 要素数に応じて2の冪で自動拡張する D3D11 Dynamic StructuredBuffer + SRV ラッパー。
/// </summary>
internal sealed class DynamicStructuredBuffer<T> : IDisposable where T : unmanaged
{
    private const int MinCapacity = 16;

    private ID3D11Buffer? _buffer;
    private ID3D11ShaderResourceView? _srv;
    private IntPtr _devicePointer = IntPtr.Zero;
    private int _capacity;
    private bool _disposed;

    public ID3D11ShaderResourceView? ShaderResourceView => _srv;
    public int Capacity => _capacity;

    public unsafe void Update(ID3D11Device device, ID3D11DeviceContext ctx, ReadOnlySpan<T> data)
    {
        if (_disposed) return;

        int required = Math.Max(MinCapacity, data.Length);
        if (_buffer == null || _srv == null || _devicePointer != device.NativePointer || required > _capacity)
        {
            int newCap = Math.Max(MinCapacity, _capacity > 0 ? _capacity : MinCapacity);
            while (newCap < required)
            {
                newCap *= 2;
            }
            Recreate(device, newCap);
        }

        if (_buffer == null) return;

        if (data.Length == 0)
        {
            // 0件時でも先頭1要素をゼロクリアして安全にバインド可能な状態を維持
            ctx.Map(_buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var mappedEmpty);
            try
            {
                Unsafe.InitBlockUnaligned(mappedEmpty.DataPointer.ToPointer(), 0, (uint)Unsafe.SizeOf<T>());
            }
            finally
            {
                ctx.Unmap(_buffer, 0);
            }
            return;
        }

        ctx.Map(_buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var mapped);
        try
        {
            var srcBytes = MemoryMarshal.AsBytes(data);
            var dstSpan = new Span<byte>(mapped.DataPointer.ToPointer(), _capacity * Unsafe.SizeOf<T>());
            srcBytes.CopyTo(dstSpan);
        }
        finally
        {
            ctx.Unmap(_buffer, 0);
        }
    }

    private void Recreate(ID3D11Device device, int capacity)
    {
        ReleaseResources();

        int stride = Unsafe.SizeOf<T>();
        var bufferDesc = new BufferDescription
        {
            ByteWidth = stride * capacity,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = stride,
        };

        _buffer = device.CreateBuffer(bufferDesc);
        _srv = device.CreateShaderResourceView(_buffer, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView
            {
                FirstElement = 0,
                NumElements = capacity,
            },
        });

        _capacity = capacity;
        _devicePointer = device.NativePointer;
    }

    public void Release()
    {
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        _srv?.Dispose();
        _srv = null;
        _buffer?.Dispose();
        _buffer = null;
        _capacity = 0;
        _devicePointer = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
    }
}
