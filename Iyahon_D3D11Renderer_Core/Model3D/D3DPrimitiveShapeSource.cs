using System;
using System.Globalization;
using System.Numerics;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

internal sealed class D3DPrimitiveShapeSource : IShapeSource, ID3DBuiltInModelSource
{
    private readonly IGraphicsDevicesAndContext _devices;
    private readonly D3DPrimitiveShapeParameter _parameter;
    private ID2D1CommandList? _commandList;
    private ID2D1SolidColorBrush? _transparentBrush;
    private Vortice.RawRectF _currentLocalBounds;

    internal D3DPrimitiveShapeParameter Parameter => _parameter;
    internal ObjModelData? LastModelData { get; private set; }

    string ID3DBuiltInModelSource.FilePath => string.Empty;
    string ID3DBuiltInModelSource.EmbeddedMeshData => _parameter.EmbeddedMeshData;
    string ID3DBuiltInModelSource.ModifierStackData => _parameter.ModifierStackData;
    ObjModelData? ID3DBuiltInModelSource.LastModelData => LastModelData;

    public ID2D1Image Output => _commandList ?? throw new InvalidOperationException("Update() が呼び出されていません。");

    public D3DPrimitiveShapeSource(IGraphicsDevicesAndContext devices, D3DPrimitiveShapeParameter parameter)
    {
        _devices = devices;
        _parameter = parameter;
        UpdateBoundsOutput(new Vortice.RawRectF(-100f, -100f, 100f, 100f));
    }

    public void Update(TimelineItemSourceDescription desc)
    {
        var frame = desc.ItemPosition.Frame;
        var length = desc.ItemDuration.Frame;
        var fps = desc.FPS;

        string embedded = _parameter.EmbeddedMeshData;
        string modData = _parameter.ModifierStackData;
        int maxTex = (int)D3D11RendererSettings.Default.ModelMaxTextureSize;
        var evalTime = new Viewport3D.Shading.ShaderEvalTime(frame, length, fps);

        CachedGpuModel? cached;
        string? procKey = null;
        Func<EditableMesh>? procFactory = null;

        if (string.IsNullOrWhiteSpace(embedded))
        {
            bool smooth = _parameter.SmoothShading;
            var mat = _parameter.BuildMaterialAt(frame, length, fps);
            string matSig =
                $"{mat.BaseColor.X:F3},{mat.BaseColor.Y:F3},{mat.BaseColor.Z:F3},{mat.BaseColor.W:F3}:" +
                $"{mat.Metallic:F3}:{mat.Roughness:F3}:{mat.Transmission:F3}:{mat.Ior:F3}:" +
                $"{mat.EmissiveColor.X:F3},{mat.EmissiveColor.Y:F3},{mat.EmissiveColor.Z:F3},{mat.EmissiveColor.W:F3}";

            if (_parameter.ShapeKind == D3DPrimitiveShapeKind.BezierCurve)
            {
                int sides = Math.Clamp((int)Math.Round(_parameter.BezierSides.GetValue(frame, length, fps)), 3, 64);
                int res = Math.Clamp((int)Math.Round(_parameter.BezierResolution.GetValue(frame, length, fps)), 2, 64);
                var interp = _parameter.BezierInterpolation;
                var style = _parameter.BezierLineStyle;
                bool closed = _parameter.BezierClosed;
                var sCap = _parameter.BezierStartCap;
                var eCap = _parameter.BezierEndCap;
                string cJson = _parameter.BezierCurveData ?? string.Empty;

                procKey = string.Create(
                    CultureInfo.InvariantCulture,
                    $"bz:{(int)interp}:{(int)style}:{sides}:{res}:{(closed ? 1 : 0)}:{(int)sCap}:{(int)eCap}:{(smooth ? 1 : 0)}:{cJson.GetHashCode(StringComparison.Ordinal)}:{cJson.Length}:{matSig}");
                procFactory = () =>
                {
                    BezierCurve3DData.TryDeserialize(cJson, out var cData);
                    return BezierCurve3DMeshBuilder.BuildMesh(cData, interp, style, sides, res, closed, sCap, eCap, smooth, mat);
                };
            }
            else
            {
                int segs = Math.Clamp((int)Math.Round(_parameter.Segments.GetValue(frame, length, fps)), 3, 64);
                int rings = Math.Clamp((int)Math.Round(_parameter.Rings.GetValue(frame, length, fps)), 3, 48);
                int sub = Math.Clamp((int)Math.Round(_parameter.Subdivisions.GetValue(frame, length, fps)), 0, 4);
                float tube = Math.Clamp((float)_parameter.TubeRadius.GetValue(frame, length, fps), 0.05f, 0.90f);
                var primType = D3DPrimitiveShapeParameter.ToEditablePrimitive(_parameter.ShapeKind);

                procKey = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(int)_parameter.ShapeKind}:{segs}:{rings}:{sub}:{tube:F3}:{(smooth ? 1 : 0)}:{matSig}");
                procFactory = () => EditableMesh.CreatePrimitive(primType, 1f, segs, rings, sub, tube, smooth, mat);
            }
        }

        if (Viewport3D.Modifiers.MeshModifierStack.HasAnyEnabledModifiers(modData) &&
            !Viewport3D.Modifiers.MeshModifierStack.HasSceneDependentModifiers(modData))
        {
            cached = ModelGpuCache.GetOrBuildWithModifiersAtTime(
                _devices.D3D.Device,
                _devices.D3D.DeviceContext,
                embedded,
                string.Empty,
                procKey,
                procFactory,
                modData,
                null,
                -1,
                Matrix4x4.Identity,
                maxTex,
                evalTime);
        }
        else if (!string.IsNullOrWhiteSpace(embedded))
        {
            cached = ModelGpuCache.GetOrBuildFromEditableMeshAtTime(
                _devices.D3D.Device, _devices.D3D.DeviceContext, embedded, string.Empty, maxTex, evalTime);
        }
        else
        {
            cached = ModelGpuCache.GetOrBuildProceduralMesh(
                _devices.D3D.Device,
                procKey!,
                procFactory!);
        }

        if (cached == null)
        {
            LastModelData = null;
            UpdateBoundsOutput(new Vortice.RawRectF(-100f, -100f, 100f, 100f));
            return;
        }

        float master = (float)_parameter.ModelScale.GetValue(frame, length, fps) / 100f;
        float sx = (float)_parameter.ScaleX.GetValue(frame, length, fps) / 100f * master;
        float sy = (float)_parameter.ScaleY.GetValue(frame, length, fps) / 100f * master;
        float sz = (float)_parameter.ScaleZ.GetValue(frame, length, fps) / 100f * master;

        var localRot = new Vector3(
            (float)_parameter.LocalRotX.GetValue(frame, length, fps),
            (float)_parameter.LocalRotY.GetValue(frame, length, fps),
            (float)_parameter.LocalRotZ.GetValue(frame, length, fps));

        LastModelData = cached.CreateRenderData(
            normalizeSize: _parameter.NormalizeSize,
            groundToOrigin: _parameter.GroundToOrigin,
            unitScale: _parameter.EffectiveUnitScale,
            localScale: new Vector3(sx, sy, sz),
            localRotationDeg: localRot,
            tintColor: Vector4.One,
            overrideMaterial: false,
            overrideMetallic: 0f,
            overrideRoughness: 0.5f,
            emissiveMultiplier: 1f,
            cullBackface: _parameter.CullBackface);

        if (_parameter.ShapeKind == D3DPrimitiveShapeKind.Noise3D)
        {
            LastModelData.Noise3D = _parameter.BuildNoise3DAt(frame, length, fps, cached.BoundsMin, cached.BoundsMax);
        }

        LastModelData.ModifierStackData = modData;
        LastModelData.EmbeddedMeshData = embedded;
        if (!string.IsNullOrWhiteSpace(procKey) && procFactory != null)
        {
            LastModelData.ProceduralCacheKey = procKey;
            LastModelData.ProceduralMeshFactory = procFactory;
        }

        UpdateBoundsOutput(DepthSortRenderer.ComputeLocalModelBoundsRect(LastModelData));
    }

    private void UpdateBoundsOutput(Vortice.RawRectF boundsRect)
    {
        if (_commandList != null &&
            MathF.Abs(boundsRect.Left - _currentLocalBounds.Left) < 0.5f &&
            MathF.Abs(boundsRect.Top - _currentLocalBounds.Top) < 0.5f &&
            MathF.Abs(boundsRect.Right - _currentLocalBounds.Right) < 0.5f &&
            MathF.Abs(boundsRect.Bottom - _currentLocalBounds.Bottom) < 0.5f)
        {
            return;
        }

        _commandList?.Dispose();
        _currentLocalBounds = boundsRect;

        var dc = _devices.DeviceContext;
        _transparentBrush ??= dc.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0f));
        _commandList = dc.CreateCommandList();
        dc.Target = _commandList;
        dc.BeginDraw();
        dc.Clear(new Color4(0f, 0f, 0f, 0f));
        dc.FillRectangle(boundsRect, _transparentBrush);
        dc.EndDraw();
        dc.Target = null;
        _commandList.Close();
    }

    public void Dispose()
    {
        _commandList?.Dispose();
        _commandList = null;
        _transparentBrush?.Dispose();
        _transparentBrush = null;
        LastModelData = null;
    }
}
