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
            // シェーダコンパイル
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

            // 定数バッファ
            _cbBuffer = device.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<CubeCb>(), BindFlags.ConstantBuffer));

            // 立方体ジオメトリ作成
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

        // 面定義: (corners[4], normal)
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
                       ID3D11Buffer cbPerObject,
                       D3DEffectParameters parameters)
    {
        if (!_initialized || _vs == null || _ps == null) return;

        float depthScale = parameters.GetFloat("DepthScale", 1f);
        float lightIntensity = parameters.GetFloat("LightIntensity", 0.5f);
        float opacity = parameters.GetFloat("Opacity", 1f);
        float alphaThreshold = parameters.GetFloat("AlphaThreshold", 0.004f);

        var cb = new CubeCb
        {
            // WorldMatrix は呼び出し側のパイプラインから渡される
            // ここでは独自 CB に同じ値をセット（パラメータ経由）
            HalfWidth = parameters.HalfScreenWidth,
            HalfHeight = parameters.HalfScreenHeight,
            Opacity = opacity,
            AlphaThreshold = alphaThreshold,
            DepthScale = depthScale,
            LightIntensity = lightIntensity,
        };

        // WorldMatrixはparam経由で渡す
        if (parameters.FloatParams.ContainsKey("_WorldM11"))
        {
            cb.WorldMatrix = new Matrix4x4(
                parameters.GetFloat("_WorldM11"), parameters.GetFloat("_WorldM12"),
                parameters.GetFloat("_WorldM13"), parameters.GetFloat("_WorldM14"),
                parameters.GetFloat("_WorldM21"), parameters.GetFloat("_WorldM22"),
                parameters.GetFloat("_WorldM23"), parameters.GetFloat("_WorldM24"),
                parameters.GetFloat("_WorldM31"), parameters.GetFloat("_WorldM32"),
                parameters.GetFloat("_WorldM33"), parameters.GetFloat("_WorldM34"),
                parameters.GetFloat("_WorldM41"), parameters.GetFloat("_WorldM42"),
                parameters.GetFloat("_WorldM43"), parameters.GetFloat("_WorldM44")
            );
        }

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

    public IReadOnlyList<D3DEffectParameterDefinition> GetParameterDefinitions()
    {
        return new[]
        {
            new D3DEffectParameterDefinition
            {
                Key = "DepthScale",
                DisplayName = "奥行き",
                Type = D3DEffectParameterType.Float,
                DefaultValue = 100f,
                MinValue = 0f,
                MaxValue = 500f,
                GroupName = "立方体",
                Description = "立方体の奥行きスケール（1.0 = テクスチャ幅と同じ）",
            },
            new D3DEffectParameterDefinition
            {
                Key = "LightIntensity",
                DisplayName = "ライティング強度",
                Type = D3DEffectParameterType.Float,
                DefaultValue = 0.5f,
                MinValue = 0f,
                MaxValue = 1f,
                GroupName = "立方体",
                Description = "簡易ライティングの強度（0 = 無効, 1 = 最大）",
            },
        };
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
