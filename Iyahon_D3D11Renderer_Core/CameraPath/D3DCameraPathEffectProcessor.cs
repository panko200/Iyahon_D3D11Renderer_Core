using System;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.CameraPath;

/// <summary>
/// D3D11 カメラパス映像エフェクトのプロセッサ。
/// DrawDescription のカメラ行列と視野角をパス評価結果で更新する。
/// </summary>
internal sealed class D3DCameraPathEffectProcessor : IVideoEffectProcessor
{
    private readonly IGraphicsDevicesAndContext _devices;
    private readonly D3DCameraPathVideoEffect _item;
    private ID2D1Image? _input;

    public D3DCameraPathEffectProcessor(IGraphicsDevicesAndContext devices, D3DCameraPathVideoEffect item)
    {
        _devices = devices;
        _item = item;
    }

    public ID2D1Image Output => _input!;

    public DrawDescription Update(EffectDescription effectDescription)
    {
        var drawDescription = effectDescription.DrawDescription;

        long frame = effectDescription.ItemPosition.Frame;
        long length = effectDescription.ItemDuration.Frame;
        int fps = Math.Max(1, effectDescription.FPS);
        int screenH = Math.Max(64, effectDescription.ScreenSize.Height);

        D3DCameraPathViewportBridge.UpdatePlaybackTiming(_item, frame, length, fps, screenH);

        bool useBlender = BlenderAxisContext.IsBlenderAxisActive;
        var state = _item.EvaluateCameraState(frame, length, fps, useBlender);
        var camera = drawDescription.Camera * D3DCameraPathSolver.BuildCameraMatrix(state, useBlender);
        var perspectiveDistance = D3DCameraPathSolver.CalculatePerspectiveDistance(state.Fov, screenH);

        return drawDescription with
        {
            Camera = camera,
            PerspectiveDistance = perspectiveDistance,
        };
    }

    public void SetInput(ID2D1Image? input) => _input = input;

    public void ClearInput() => _input = null;

    public void Dispose()
    {
    }
}
