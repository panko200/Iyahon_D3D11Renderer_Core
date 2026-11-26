using System;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// D3D11環境の IShapeSource 実装。
/// </summary>
internal sealed class D3DEnvironmentShapeSource : IShapeSource
{
    private readonly IGraphicsDevicesAndContext _devices;
    private readonly D3DEnvironmentShapeParameter _parameter;
    private ID2D1CommandList? _commandList;

    internal EnvironmentParameters LastEnvironment { get; private set; }
    internal AtmosphereParameters LastAtmosphere { get; private set; }

    public ID2D1Image Output => _commandList ?? throw new InvalidOperationException("Update() が呼び出されていません。");

    public D3DEnvironmentShapeSource(IGraphicsDevicesAndContext devices, D3DEnvironmentShapeParameter parameter)
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

        LastEnvironment = _parameter.EvaluateEnvironment(frame, length, fps);
        LastAtmosphere = _parameter.EvaluateAtmosphere(frame, length, fps);
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
