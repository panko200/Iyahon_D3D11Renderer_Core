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
/// 立方体エフェクト。
/// 入力テクスチャを立方体の各面にマッピングして描画する。
/// </summary>
public sealed class CubeD3DEffect : ID3DEffect
{
    public string Name => "立方体";
    public string Category => "3Dオブジェクト";

    // ── エフェクト固有パラメータ（VideoEffect から設定される） ──
    public float DepthScale { get; set; } = 100f;
    public float LightIntensity { get; set; } = 0.5f;

    [StructLayout(LayoutKind.Sequential)]
    private struct CubeVertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;
        public Vector3 Normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CubeCb
    {
        public Matrix4x4 WorldMatrix;   // 64
        public float HalfWidth;         // 4
        public float HalfHeight;        // 4
        public float Opacity;           // 4
        public float AlphaThreshold;    // 4
        public float DepthScale;        // 4
        public float LightIntensity;    // 4
        public float _pad0;             // 4
        public float _pad1;             // 4  → 96 bytes (16倍数 OK)
    }

    private bool _initialized;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private ID3D11Buffer? _cbBuffer;
    private int _indexCount;

    private const string ShaderSource = @"
cbuffer CubeCb : register(b0)
{
    row_major float4x4 WorldMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float AlphaThreshold;
    float DepthScale;
    float LightIntensity;
    float _pad0;
    float _pad1;
};

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; float3 Norm : NORMAL; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float3 Norm : TEXCOORD1; float Op : TEXCOORD2; };

Texture2D    gTex    : register(t0);
SamplerState gSampler: register(s0);

PSInput VS_Cube(VSInput input)
{
    PSInput o;
    float3 scaledPos = input.Pos * float3(1.0, 1.0, DepthScale);
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

float4 PS_Cube(PSInput input) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op;
    clip(c.a - AlphaThreshold);

    float3 lightDir = normalize(float3(0.3, -0.5, -1.0));
    float3 n = normalize(input.Norm);
    float ndl = saturate(dot(n, -lightDir));
    float lighting = lerp(1.0, 0.5 + 0.5 * ndl, LightIntensity);
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
                ShaderSource, "VS_Cube", "cube_shader",
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
                ShaderSource, "PS_Cube", "cube_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            _ps = device.CreatePixelShader(psBlob);
            psBlob.Dispose();

            _cbBuffer = device.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<CubeCb>(), BindFlags.ConstantBuffer));

            CreateCubeGeometry(device);

            _initialized = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] CubeD3DEffect Initialize エラー: {ex.Message}");
        }
    }

    private unsafe void CreateCubeGeometry(ID3D11Device device)
    {
        // 6面 × 4頂点
        var vertices = new List<CubeVertex>();
        var indices = new List<ushort>();

        var faces = new[]
        {
            // 前面 (Z-)
            (new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
             new Vector3(0, 0, -1)),
            // 背面 (Z+)
            (new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f,  0.5f),
             new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f),
             new Vector3(0, 0, 1)),
            // 右面 (X+)
            (new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
             new Vector3( 0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f,  0.5f),
             new Vector3(1, 0, 0)),
            // 左面 (X-)
            (new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f, -0.5f),
             new Vector3(-1, 0, 0)),
            // 上面 (Y-)
            (new Vector3(-0.5f, -0.5f,  0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
             new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
             new Vector3(0, -1, 0)),
            // 下面 (Y+)
            (new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f,  0.5f), new Vector3( 0.5f,  0.5f,  0.5f),
             new Vector3(0, 1, 0)),
        };

        foreach (var (v0, v1, v2, v3, normal) in faces)
        {
            ushort baseIdx = (ushort)vertices.Count;
            vertices.Add(new CubeVertex { Position = v0, TexCoord = new Vector2(0, 0), Normal = normal });
            vertices.Add(new CubeVertex { Position = v1, TexCoord = new Vector2(1, 0), Normal = normal });
            vertices.Add(new CubeVertex { Position = v2, TexCoord = new Vector2(0, 1), Normal = normal });
            vertices.Add(new CubeVertex { Position = v3, TexCoord = new Vector2(1, 1), Normal = normal });

            indices.Add(baseIdx);
            indices.Add((ushort)(baseIdx + 1));
            indices.Add((ushort)(baseIdx + 2));
            indices.Add((ushort)(baseIdx + 1));
            indices.Add((ushort)(baseIdx + 3));
            indices.Add((ushort)(baseIdx + 2));
        }

        _indexCount = indices.Count;

        var vertArray = vertices.ToArray();
        var idxArray = indices.ToArray();

        int vertStride = Marshal.SizeOf<CubeVertex>();
        fixed (CubeVertex* pv = vertArray)
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

        var cb = new CubeCb
        {
            WorldMatrix = renderContext.WorldMatrix,
            HalfWidth = renderContext.HalfScreenWidth,
            HalfHeight = renderContext.HalfScreenHeight,
            Opacity = renderContext.Opacity,
            AlphaThreshold = renderContext.AlphaThreshold,
            DepthScale = DepthScale,
            LightIntensity = LightIntensity,
        };

        ctx.UpdateSubresource(ref cb, _cbBuffer!);

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<CubeVertex>(), 0);
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
