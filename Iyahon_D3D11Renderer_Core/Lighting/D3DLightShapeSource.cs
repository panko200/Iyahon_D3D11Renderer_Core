using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// D3D11光源の IShapeSource 実装。
/// </summary>
internal sealed class D3DLightShapeSource : IShapeSource
{
    private readonly IGraphicsDevicesAndContext _devices;
    private readonly D3DLightShapeParameter _parameter;
    private ID2D1CommandList? _commandList;

    internal LightData? LastRegisteredLight { get; private set; }

    public ID2D1Image Output => _commandList ?? throw new InvalidOperationException("Update() が呼び出されていません。");

    public D3DLightShapeSource(IGraphicsDevicesAndContext devices, D3DLightShapeParameter parameter)
    {
        _devices = devices;
        _parameter = parameter;

        CreateEmptyOutput();
    }

    public void Update(TimelineItemSourceDescription desc)
    {
        var frame = desc.ItemPosition.Frame;
        var length = desc.ItemDuration.Frame;
        var fps = desc.FPS;

        var dir = new Vector3(
            (float)_parameter.DirectionX.GetValue(frame, length, fps),
            (float)_parameter.DirectionY.GetValue(frame, length, fps),
            (float)_parameter.DirectionZ.GetValue(frame, length, fps));

        if (dir.LengthSquared() < 1e-6f)
            dir = new Vector3(0, -1, 0);
        else
            dir = Vector3.Normalize(dir);

        var lightData = new LightData
        {
            Type = _parameter.LightType,
            Color = new Vector3(
                _parameter.LightColor.R / 255f,
                _parameter.LightColor.G / 255f,
                _parameter.LightColor.B / 255f),
            Intensity = (float)_parameter.Intensity.GetValue(frame, length, fps),
            Direction = dir,
            Range = (float)_parameter.Range.GetValue(frame, length, fps),
            SpotInnerAngle = (float)_parameter.SpotInnerAngle.GetValue(frame, length, fps),
            SpotOuterAngle = (float)_parameter.SpotOuterAngle.GetValue(frame, length, fps),
            AreaWidth = (float)_parameter.AreaWidth.GetValue(frame, length, fps),
            AreaHeight = (float)_parameter.AreaHeight.GetValue(frame, length, fps),
            CastShadow = _parameter.CastShadow,
            ShadowIntensity = (float)_parameter.ShadowIntensity.GetValue(frame, length, fps),
            ShadowBias = (float)_parameter.ShadowBias.GetValue(frame, length, fps),
            ShowGizmo = _parameter.ShowGizmo, // ★追加：トグルの状態を転送
            Position = Vector3.Zero,
        };

        LastRegisteredLight = lightData;
    }

    private void CreateEmptyOutput()
    {
        _commandList?.Dispose();

        var dc = _devices.DeviceContext;
        _commandList = dc.CreateCommandList();
        dc.Target = _commandList;
        dc.BeginDraw();
        dc.Clear(new Color4(0f, 0f, 0f, 0f));
        dc.EndDraw();
        dc.Target = null;
        _commandList.Close();
    }

    public void Dispose()
    {
        _commandList?.Dispose();
        _commandList = null;
    }
}