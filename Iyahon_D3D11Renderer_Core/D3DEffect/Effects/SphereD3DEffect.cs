using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect.Effects;

/// <summary>
/// 球エフェクト。
/// 入力テクスチャを球体にマッピングして描画する。
/// </summary>
public sealed class SphereD3DEffect : ID3DEffect
{
    public string Name => "球";
    public string Category => "3Dオブジェクト";

    // ── エフェクト固有パラメータ ──
    public float DepthScale { get; set; } = 1f;
    public float LightIntensity { get; set; } = 0.5f;

    [StructLayout(LayoutKind.Sequential)]
    private struct SphereVertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;
        public Vector3 Normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SphereCb
    {
        public Matrix4x4 WorldMatrix;   // 64
        public float HalfWidth;         // 4
        public float HalfHeight;        // 4
        public float Opacity;           // 4
        public float AlphaThreshold;    // 4
        public float DepthScale;        // 4
        public float LightIntensity;    // 4
        public float TexWidth;          // 4
        public float TexHeight;         // 4  → 96 bytes
    }

    private bool _initialized;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private ID3D11Buffer? _cbBuffer;
    private int _indexCount;

    private const int Stacks = 32;
    private const int Slices = 32;

    private const string ShaderSource = @"
cbuffer SphereCb : register(b0)
{
    row_major float4x4 WorldMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float AlphaThreshold;
    float DepthScale;
    float LightIntensity;
    float TexWidth;
    float TexHeight;
};

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; float3 Norm : NORMAL; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float3 Norm : TEXCOORD1; float Op : TEXCOORD2; };

Texture2D    gTex    : register(t0);
SamplerState gSampler: register(s0);

PSInput VS_Sphere(VSInput input)
{
    PSInput o;
    float sphereRadius = min(TexWidth, TexHeight);
    float3 scaledPos = input.Pos * float3(1.0, 1.0, sphereRadius * DepthScale);
    float4 pos = mul(float4(scaledPos, 1.0), WorldMatrix);

    o.Pos.x =  pos.x / HalfWidth;
    o.Pos.y = -pos.y / HalfHeight;
    o.Pos.z = -pos.z / 200000.0 + 0.5 * pos.w;
    o.Pos.w =  pos.w;

    o.UV = input.UV;
    o.Norm = mul(float4(input.Norm, 0.0), WorldMatrix).xyz;
    o.Op = Opacity;
    return o;
}

float4 PS_Sphere(PSInput input) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op;
    clip(c.a - AlphaThreshold);

    float3 lightDir = normalize(float3(0.3, -0.5, -1.0));
    float3 n = normalize(input.Norm);
    float ndl = saturate(dot(n, -lightDir));
    float lighting = lerp(1.0, 0.4 + 0.6 * ndl, LightIntensity);
    c.rgb *= lighting;

    return c;
}
";

    public void Initialize(ID3D11Device device, ID3D11DeviceContext ctx)
    {
        if (_initialized) return;

        try
        {
            var vsBlob = Vortice.D3DCompiler.Compiler.Compile(
                ShaderSource, "VS_Sphere", "sphere_shader",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);

            _vs = device.CreateVertexShader(vsBlob);

            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,   12, 0),
                new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float, 20, 0),
            };
            _inputLayout = device.CreateInputLayout(inputElements, vsBlob);
            vsBlob.Dispose();

            var psBlob = Vortice.D3DCompiler.Compiler.Compile(
                ShaderSource, "PS_Sphere", "sphere_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            _ps = device.CreatePixelShader(psBlob);
            psBlob.Dispose();

            _cbBuffer = device.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<SphereCb>(), BindFlags.ConstantBuffer));

            CreateSphereGeometry(device);

            _initialized = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] SphereD3DEffect Initialize エラー: {ex.Message}");
        }
    }

    private unsafe void CreateSphereGeometry(ID3D11Device device)
    {
        var vertices = new List<SphereVertex>();
        var indices = new List<ushort>();

        for (int stack = 0; stack <= Stacks; stack++)
        {
            float phi = MathF.PI * stack / Stacks;
            float y = MathF.Cos(phi) * 0.5f;
            float r = MathF.Sin(phi) * 0.5f;
            float v = (float)stack / Stacks;

            for (int slice = 0; slice <= Slices; slice++)
            {
                float theta = 2f * MathF.PI * slice / Slices;
                float x = r * MathF.Cos(theta);
                float z = r * MathF.Sin(theta);
                float u = (float)slice / Slices;

                var normal = Vector3.Normalize(new Vector3(x, y, z));

                vertices.Add(new SphereVertex
                {
                    Position = new Vector3(x, y, z),
                    TexCoord = new Vector2(u, v),
                    Normal = normal,
                });
            }
        }

        for (int stack = 0; stack < Stacks; stack++)
        {
            for (int slice = 0; slice < Slices; slice++)
            {
                int row0 = stack * (Slices + 1);
                int row1 = (stack + 1) * (Slices + 1);

                indices.Add((ushort)(row0 + slice));
                indices.Add((ushort)(row1 + slice));
                indices.Add((ushort)(row0 + slice + 1));

                indices.Add((ushort)(row0 + slice + 1));
                indices.Add((ushort)(row1 + slice));
                indices.Add((ushort)(row1 + slice + 1));
            }
        }

        _indexCount = indices.Count;

        var vertArray = vertices.ToArray();
        var idxArray = indices.ToArray();

        int vertStride = Marshal.SizeOf<SphereVertex>();
        fixed (SphereVertex* pv = vertArray)
        {
            _vertexBuffer = device.CreateBuffer(
                new BufferDescription(vertStride * vertArray.Length, BindFlags.VertexBuffer),
                new SubresourceData((IntPtr)pv, vertStride * vertArray.Length));
        }

        fixed (ushort* pi = idxArray)
        {
            _indexBuffer = device.CreateBuffer(
                new BufferDescription(sizeof(ushort) * idxArray.Length, BindFlags.IndexBuffer),
                new SubresourceData((IntPtr)pi, sizeof(ushort) * idxArray.Length));
        }
    }

    public void Render(ID3D11DeviceContext ctx, ID3D11Device device,
                       ID3D11ShaderResourceView inputSrv,
                       D3DRenderContext renderContext)
    {
        if (!_initialized || _vs == null || _ps == null) return;

        var cb = new SphereCb
        {
            WorldMatrix = renderContext.WorldMatrix,
            HalfWidth = renderContext.HalfScreenWidth,
            HalfHeight = renderContext.HalfScreenHeight,
            Opacity = renderContext.Opacity,
            AlphaThreshold = renderContext.AlphaThreshold,
            DepthScale = DepthScale,
            LightIntensity = LightIntensity,
            TexWidth = renderContext.TextureWidth,
            TexHeight = renderContext.TextureHeight,
        };

        ctx.UpdateSubresource(ref cb, _cbBuffer!);

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<SphereVertex>(), 0);
        ctx.IASetIndexBuffer(_indexBuffer!, Format.R16_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.VSSetConstantBuffer(0, _cbBuffer);
        ctx.PSSetConstantBuffer(0, _cbBuffer);
        ctx.PSSetShaderResource(0, inputSrv);

        ctx.DrawIndexed(_indexCount, 0, 0);
    }

    public void Dispose()
    {
        _vs?.Dispose();
        _ps?.Dispose();
        _inputLayout?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _cbBuffer?.Dispose();
        _initialized = false;
    }
}
