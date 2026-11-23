using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

internal sealed class D3DModelShapeSource : IShapeSource, ID3DBuiltInModelSource
{
    private readonly IGraphicsDevicesAndContext _devices;
    private readonly D3DModelShapeParameter _parameter;
    private ID2D1CommandList? _commandList;
    private ID2D1SolidColorBrush? _transparentBrush;
    private Vortice.RawRectF _currentLocalBounds;

    internal D3DModelShapeParameter Parameter => _parameter;
    internal ObjModelData? LastModelData { get; private set; }

    string ID3DBuiltInModelSource.FilePath => _parameter.FilePath;
    string ID3DBuiltInModelSource.EmbeddedMeshData => _parameter.EmbeddedMeshData;
    string ID3DBuiltInModelSource.ModifierStackData => _parameter.ModifierStackData;
    ObjModelData? ID3DBuiltInModelSource.LastModelData => LastModelData;

    public ID2D1Image Output => _commandList ?? throw new InvalidOperationException("Update() が呼び出されていません。");

    public D3DModelShapeSource(IGraphicsDevicesAndContext devices, D3DModelShapeParameter parameter)
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
        var evalTime = new Viewport3D.Shading.ShaderEvalTime(frame, length, fps);

        int maxTexSize = (int)D3D11RendererSettings.Default.ModelMaxTextureSize;
        string embeddedMesh = _parameter.EmbeddedMeshData;
        string path = _parameter.FilePath;
        string modData = _parameter.ModifierStackData;

        CachedGpuModel? cached = null;
        if (Viewport3D.Modifiers.MeshModifierStack.HasAnyEnabledModifiers(modData) &&
            !Viewport3D.Modifiers.MeshModifierStack.HasSceneDependentModifiers(modData))
        {
            cached = ModelGpuCache.GetOrBuildWithModifiersAtTime(
                _devices.D3D.Device,
                _devices.D3D.DeviceContext,
                embeddedMesh,
                path,
                null,
                null,
                modData,
                null,
                -1,
                Matrix4x4.Identity,
                maxTexSize,
                evalTime);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(embeddedMesh))
            {
                cached = ModelGpuCache.GetOrBuildFromEditableMeshAtTime(
                    _devices.D3D.Device, _devices.D3D.DeviceContext, embeddedMesh, path, maxTexSize, evalTime);
            }

            if (cached == null)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    LastModelData = null;
                    UpdateBoundsOutput(new Vortice.RawRectF(-100f, -100f, 100f, 100f));
                    return;
                }
                cached = ModelGpuCache.GetOrLoad(_devices.D3D.Device, _devices.D3D.DeviceContext, path, maxTexSize);
            }
        }
        if (cached == null)
        {
            LastModelData = null;
            UpdateBoundsOutput(new Vortice.RawRectF(-100f, -100f, 100f, 100f));
            return;
        }

        float unitScale = (float)_parameter.UnitScale.GetValue(frame, length, fps);
        float master = (float)_parameter.ModelScale.GetValue(frame, length, fps) / 100f;
        float sx = (float)_parameter.ScaleX.GetValue(frame, length, fps) / 100f * master;
        float sy = (float)_parameter.ScaleY.GetValue(frame, length, fps) / 100f * master;
        float sz = (float)_parameter.ScaleZ.GetValue(frame, length, fps) / 100f * master;

        var localRot = new Vector3(
            (float)_parameter.LocalRotX.GetValue(frame, length, fps),
            (float)_parameter.LocalRotY.GetValue(frame, length, fps),
            (float)_parameter.LocalRotZ.GetValue(frame, length, fps));

        var tc = _parameter.TintColor;
        var tint = new Vector4(tc.R / 255f, tc.G / 255f, tc.B / 255f, tc.A / 255f);

        float metallic = (float)_parameter.Metallic.GetValue(frame, length, fps);
        float roughness = (float)_parameter.Roughness.GetValue(frame, length, fps);
        float emissiveMult = (float)_parameter.EmissiveIntensity.GetValue(frame, length, fps);

        LastModelData = cached.CreateRenderData(
            _parameter.NormalizeSize,
            _parameter.GroundToOrigin,
            unitScale,
            new Vector3(sx, sy, sz),
            localRot,
            tint,
            _parameter.OverrideMaterial,
            metallic,
            roughness,
            emissiveMult,
            _parameter.CullBackface);
        LastModelData.ModifierStackData = modData;

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
        LastModelData = null;
        _commandList?.Dispose();
        _commandList = null;
        _transparentBrush?.Dispose();
        _transparentBrush = null;
    }
}
