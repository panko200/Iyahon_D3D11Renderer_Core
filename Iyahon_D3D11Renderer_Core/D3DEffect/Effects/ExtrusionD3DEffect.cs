using Iyahon_D3D11Renderer_Core.D3DEffect;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Iyahon_D3D11Renderer_Core.D3DEffect;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect.Effects;

/// <summary>
/// Extrusion effect.
/// Raymarches through a box volume to create 3D extrusion from alpha shape.
/// Based on YMM43D Extrusion3D, with configurable alpha threshold.
/// </summary>
public sealed class ExtrusionD3DEffect : ID3DEffect
{
    public string Name => "押し出し";
    public string Category => "3Dオブジェクト";

    // Effect-specific parameters
    public float Thickness { get; set; } = 100f;
    public float LightIntensity { get; set; } = 0.5f;
    public float AlphaThreshold { get; set; } = 0.5f;
    public int ExtrusionType { get; set; } = 1; // 1=Image, 2=Solid
    public Vector4 SideColor { get; set; } = new Vector4(1f, 1f, 1f, 1f);
    public float Attenuation { get; set; } = 0f;

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtrusionVertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtrusionCb
    {
        public Matrix4x4 WorldMatrix;       // 64
        public Matrix4x4 ScaleMatrix;       // 64 -> 128
        public Matrix4x4 ViewProjMatrix;    // 64 -> 192
        public float HalfWidth;             // 4
        public float HalfHeight;            // 4
        public float Opacity;               // 4
        public float AlphaThreshold;        // 4  -> 208
        public float Thickness;             // 4
        public float LightIntensity;        // 4
        public int ExtrusionType;           // 4
        public float Attenuation;           // 4  -> 224
        public Vector4 SideColor;           // 16 -> 240
        public Vector3 CameraLocalPos;      // 12
        public float IsOrthographic;        // 4  -> 256
    }

    private bool _initialized;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private ID3D11Buffer? _cbBuffer;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterCullFront;
    private int _indexCount;

    //2byte chars cause shader load errors - keep comments in ASCII only
    private const string ShaderSource = @"
cbuffer ExtrusionCb : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ScaleMatrix;
    row_major float4x4 ViewProjMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float AlphaThreshold;
    float Thickness;
    float LightIntensity;
    int ExtrusionType;
    float Attenuation;
    float4 SideColor;
    float3 CameraLocalPos;
    float IsOrthographic;
};

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float3 LocalPos : TEXCOORD1; };

struct PSOutput {
    float4 Color : SV_Target;
    float  Depth : SV_Depth;
};

Texture2D    gTex    : register(t0);
SamplerState gSampler: register(s0);

PSInput VS_Extrusion(VSInput input)
{
    PSInput o;
    // Scale -> World (WorldMatrix includes Camera + Perspective)
    float4 scaled = mul(float4(input.Pos, 1.0), ScaleMatrix);
    float4 pos = mul(scaled, WorldMatrix);

    if (HalfWidth > 0.0)
    {
        // Screen-space transform (DepthSortRenderer)
        o.Pos.x =  pos.x / HalfWidth;
        o.Pos.y = -pos.y / HalfHeight;
        o.Pos.z = -pos.z / 200000.0 + 0.5 * pos.w;
        o.Pos.w =  pos.w;
    }
    else
    {
        // ViewProjection transform (3D Preview)
        o.Pos = mul(pos, ViewProjMatrix);
    }

    o.UV = input.UV;
    o.LocalPos = input.Pos;
    return o;
}

PSOutput PS_Extrusion(PSInput input)
{
    PSOutput output;
    output.Color = float4(0, 0, 0, 0);
    output.Depth = input.Pos.z;

    float3 ro, rd;
    if (IsOrthographic > 0.5)
    {
        rd = normalize(CameraLocalPos); // CameraLocalPos acts as ray direction
        ro = input.LocalPos - rd * 2.0; // Start outside the volume
    }
    else
    {
        ro = CameraLocalPos;
        rd = normalize(input.LocalPos - CameraLocalPos);
    }

    // ray-box intersection (+-0.5 box)
    float3 invDir = 1.0 / rd;
    float3 t0 = (-0.5 - ro) * invDir;
    float3 t1 = ( 0.5 - ro) * invDir;

    float3 tmin = min(t0, t1);
    float3 tmax = max(t0, t1);

    float tNear = max(max(tmin.x, tmin.y), tmin.z);
    float tFar  = min(min(tmax.x, tmax.y), tmax.z);

    if (tNear > tFar || tFar < 0.0) discard;

    // dithering noise
    float noise = frac(sin(dot(input.Pos.xy, float2(12.9898, 78.233))) * 43758.5453);

    int numSteps = 128;
    float stepSize = (tFar - tNear) / numSteps;
    float t = max(tNear, 0.0) + stepSize * noise;

    float hitT = -1.0;
    float2 hitUV = float2(0, 0);
    float3 hitPos = float3(0, 0, 0);

    for (int i = 0; i < numSteps; i++)
    {
        float3 pos = ro + rd * t;
        float2 uv = float2(pos.x + 0.5, pos.y + 0.5);

        float a = gTex.SampleLevel(gSampler, uv, 0).a;
        if (a > AlphaThreshold)
        {
            // binary search refinement
            float tLow = max(tNear, t - stepSize);
            float tHigh = t;
            for (int j = 0; j < 10; j++)
            {
                float tMid = (tLow + tHigh) * 0.5;
                float3 mPos = ro + rd * tMid;
                float2 mUV = float2(mPos.x + 0.5, mPos.y + 0.5);
                float mA = gTex.SampleLevel(gSampler, mUV, 0).a;
                if (mA > AlphaThreshold) tHigh = tMid;
                else tLow = tMid;
            }
            hitT = tHigh;
            hitPos = ro + rd * hitT;
            hitUV = float2(hitPos.x + 0.5, hitPos.y + 0.5);
            break;
        }
        t += stepSize;
    }

    if (hitT < 0.0) discard;

    // SV_Depth: hitPos (local +-0.5) -> Scale -> World -> clip Z
    float4 scaledHit = mul(float4(hitPos, 1.0), ScaleMatrix);
    float4 worldHit = mul(scaledHit, WorldMatrix);

    if (HalfWidth > 0.0)
    {
        // Screen-space depth (DepthSortRenderer)
        float clipZ = -worldHit.z / 200000.0 + 0.5 * worldHit.w;
        float clipW = worldHit.w;
        output.Depth = saturate(clipZ / clipW);
    }
    else
    {
        // ViewProjection depth (3D Preview)
        float4 clipHit = mul(worldHit, ViewProjMatrix);
        output.Depth = saturate(clipHit.z / clipHit.w);
    }

    // front/back face detection (Z near +-0.5)
    bool isFace = (abs(hitPos.z) > 0.499);

    // normal calculation
    float3 normal = float3(0, 0, 0);
    if (abs(hitPos.z + 0.5) < 0.005)
    {
        normal = float3(0, 0, -1);
    }
    else if (abs(hitPos.z - 0.5) < 0.005)
    {
        normal = float3(0, 0, 1);
    }
    else
    {
        float eps = 0.01;
        float aR = gTex.SampleLevel(gSampler, hitUV + float2(eps, 0), 0).a;
        float aL = gTex.SampleLevel(gSampler, hitUV - float2(eps, 0), 0).a;
        float aU = gTex.SampleLevel(gSampler, hitUV + float2(0, eps), 0).a;
        float aD = gTex.SampleLevel(gSampler, hitUV - float2(0, eps), 0).a;
        normal = normalize(float3(aL - aR, aD - aU, 0.005));
    }

    if (isFace)
    {
        float4 texColor = gTex.SampleLevel(gSampler, hitUV, 0);
        output.Color = float4(texColor.rgb, texColor.a * Opacity);
    }
    else
    {
        float3 col;
        float shade = 1.0;

        if (ExtrusionType == 1) // Image
        {
            col = gTex.SampleLevel(gSampler, hitUV, 0).rgb;
            float3 lightDir = normalize(float3(0.3, -0.5, -1.0));
            float ndl = saturate(dot(normal, -lightDir));
            float light = lerp(1.0, 0.5 + 0.5 * ndl, LightIntensity);
            shade = lerp(1.0, light, Attenuation);
        }
        else // Solid
        {
            col = SideColor.rgb;
            float3 lightDir = normalize(float3(0.3, -0.5, -1.0));
            float ndl = saturate(dot(normal, -lightDir));
            shade = lerp(1.0, 0.5 + 0.5 * ndl, LightIntensity);
        }

        col *= shade;
        output.Color = float4(col, 1.0 * Opacity);
    }

    return output;
}
";

    public void Initialize(ID3D11Device device, ID3D11DeviceContext ctx)
    {
        if (_initialized) return;

        try
        {
            var vsBlob = Vortice.D3DCompiler.Compiler.Compile(
                ShaderSource, "VS_Extrusion", "extrusion_shader",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);

            _vs = device.CreateVertexShader(vsBlob);

            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,   12, 0),
            };
            _inputLayout = device.CreateInputLayout(inputElements, vsBlob);
            vsBlob.Dispose();

            var psBlob = Vortice.D3DCompiler.Compiler.Compile(
                ShaderSource, "PS_Extrusion", "extrusion_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            _ps = device.CreatePixelShader(psBlob);
            psBlob.Dispose();

            _cbBuffer = device.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<ExtrusionCb>(), BindFlags.ConstantBuffer));

            _sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
            });

            _rasterCullFront = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Front,
                DepthClipEnable = false,
            });

            CreateCubeGeometry(device);

            _initialized = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] ExtrusionD3DEffect Initialize error: {ex.Message}");
        }
    }

    private unsafe void CreateCubeGeometry(ID3D11Device device)
    {
        var vertices = new List<ExtrusionVertex>();
        var indices = new List<ushort>();

        var faces = new[]
        {
            (new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f)),
            (new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f,  0.5f),
             new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f)),
            (new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
             new Vector3( 0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f,  0.5f)),
            (new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f, -0.5f)),
            (new Vector3(-0.5f, -0.5f,  0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
             new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f)),
            (new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f,  0.5f), new Vector3( 0.5f,  0.5f,  0.5f)),
        };

        foreach (var (v0, v1, v2, v3) in faces)
        {
            ushort baseIdx = (ushort)vertices.Count;
            vertices.Add(new ExtrusionVertex { Position = v0, TexCoord = new Vector2(0, 0) });
            vertices.Add(new ExtrusionVertex { Position = v1, TexCoord = new Vector2(1, 0) });
            vertices.Add(new ExtrusionVertex { Position = v2, TexCoord = new Vector2(0, 1) });
            vertices.Add(new ExtrusionVertex { Position = v3, TexCoord = new Vector2(1, 1) });

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

        int vertStride = Marshal.SizeOf<ExtrusionVertex>();
        fixed (ExtrusionVertex* pv = vertArray)
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

        var scaleMatrix = Matrix4x4.CreateScale(1f, 1f, Thickness);

        // Compute CameraLocalPos based on rendering mode
        Vector3 cameraLocalPos;
        float isOrthographic = 0f;

        if (renderContext.HalfScreenWidth > 0)
        {
            // Normal mode (DepthSortRenderer) - compute from CameraMatrix + d2dProj
            var cam = renderContext.CameraMatrix;
            var d2dProj = new Matrix4x4(
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, -1f / 1000f,
                0f, 0f, 0f, 1f
            );

            var combined = cam * d2dProj;
            // Check if W is independent of X, Y, and Z (true orthographic)
            if (Math.Abs(combined.M14) < 0.00001f &&
                Math.Abs(combined.M24) < 0.00001f &&
                Math.Abs(combined.M34) < 0.00001f)
            {
                isOrthographic = 1f;
            }

            Matrix4x4.Invert(d2dProj, out var invD2dProj);
            Matrix4x4.Invert(cam, out var invCam);

            var modelMatrix = renderContext.WorldMatrix * invD2dProj * invCam;
            var effectiveModel = scaleMatrix * modelMatrix;
            Matrix4x4.Invert(effectiveModel, out var invEffectiveModel);

            if (isOrthographic > 0.5f)
            {
                var cameraForwardWorld = Vector3.TransformNormal(new Vector3(0, 0, -1), invCam);
                cameraLocalPos = Vector3.Normalize(Vector3.TransformNormal(cameraForwardWorld, invEffectiveModel));
            }
            else
            {
                var cameraWorldPos = Vector3.Transform(new Vector3(0, 0, 1000), invCam);
                cameraLocalPos = Vector3.Transform(cameraWorldPos, invEffectiveModel);
            }
        }
        else
        {
            // ViewProj mode (3D Preview) - use CameraWorldPosition from context
            var viewProj = renderContext.ViewProjectionMatrix;
            if (Math.Abs(viewProj.M14) < 0.00001f &&
                Math.Abs(viewProj.M24) < 0.00001f &&
                Math.Abs(viewProj.M34) < 0.00001f)
            {
                isOrthographic = 1f;
            }

            var effectiveModel = scaleMatrix * renderContext.WorldMatrix;
            Matrix4x4.Invert(effectiveModel, out var invEffectiveModel);

            if (isOrthographic > 0.5f)
            {
                Matrix4x4.Invert(viewProj, out var invViewProj);
                var rayDirWorld = Vector3.Normalize(
                    Vector3.Transform(new Vector3(0, 0, 1), invViewProj) -
                    Vector3.Transform(new Vector3(0, 0, 0), invViewProj));
                cameraLocalPos = Vector3.Normalize(Vector3.TransformNormal(rayDirWorld, invEffectiveModel));
            }
            else
            {
                var cameraWorldPos = renderContext.CameraWorldPosition;
                cameraLocalPos = Vector3.Transform(cameraWorldPos, invEffectiveModel);
            }
        }

        var cb = new ExtrusionCb
        {
            WorldMatrix = renderContext.WorldMatrix,
            ScaleMatrix = scaleMatrix,
            ViewProjMatrix = renderContext.ViewProjectionMatrix,
            HalfWidth = renderContext.HalfScreenWidth,
            HalfHeight = renderContext.HalfScreenHeight,
            Opacity = renderContext.Opacity,
            AlphaThreshold = AlphaThreshold,
            Thickness = Thickness,
            LightIntensity = LightIntensity,
            ExtrusionType = ExtrusionType,
            Attenuation = Attenuation,
            SideColor = SideColor,
            CameraLocalPos = cameraLocalPos,
            IsOrthographic = isOrthographic,
        };

        ctx.UpdateSubresource(ref cb, _cbBuffer!);

        ctx.RSSetState(_rasterCullFront);

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<ExtrusionVertex>(), 0);
        ctx.IASetIndexBuffer(_indexBuffer!, Format.R16_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.VSSetConstantBuffer(0, _cbBuffer);
        ctx.PSSetConstantBuffer(0, _cbBuffer);
        ctx.PSSetShaderResource(0, inputSrv);
        ctx.PSSetSampler(0, _sampler);

        ctx.DrawIndexed(_indexCount, 0, 0);

        ctx.RSSetState(null!);
        ctx.PSSetShaderResource(0, null!);
    }

    public void Dispose()
    {
        _vs?.Dispose();
        _ps?.Dispose();
        _inputLayout?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _cbBuffer?.Dispose();
        _sampler?.Dispose();
        _rasterCullFront?.Dispose();
        _initialized = false;
    }
}
