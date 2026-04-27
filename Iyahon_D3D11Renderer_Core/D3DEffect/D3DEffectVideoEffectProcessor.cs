using System;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect;

/// <summary>
/// D3DエフェクトのVideoEffectProcessor。
/// D2Dパイプラインとしてはパススルー（何もしない）。
/// 実際のD3Dエフェクト適用は DepthSortRendererPatch.UpdatePostfix で行う。
/// </summary>
internal sealed class D3DEffectVideoEffectProcessor : IVideoEffectProcessor, IDrawable, IDisposable
{
    private readonly IGraphicsDevicesAndContext _devices;
    private readonly D3DEffectVideoEffect _effect;
    private ID2D1Image? _input;
    private bool _disposed;

    /// <summary>最後の Update で計算されたアニメーション値</summary>
    public float CurrentDepthScale { get; private set; } = 1f;
    public float CurrentLightIntensity { get; private set; } = 0.5f;
    public string CurrentEffectId => _effect.SelectedEffectId;

    public D3DEffectVideoEffectProcessor(IGraphicsDevicesAndContext devices, D3DEffectVideoEffect effect)
    {
        _devices = devices;
        _effect = effect;
    }

    public ID2D1Image Output => _input ?? throw new InvalidOperationException("input is null");

    public void SetInput(ID2D1Image? input)
    {
        _input = input;
    }

    public void ClearInput()
    {
        _input = null;
    }

    /// <summary>
    /// エフェクトの更新。
    /// アニメーション値を計算してキャッシュ。D2Dとしてはパススルー。
    /// </summary>
    public DrawDescription Update(EffectDescription effectDescription)
    {
        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;

        // エフェクト種別に応じて DepthScale を選択
        CurrentDepthScale = _effect.EffectSelection switch
        {
            D3DEffectSelection.Cube => (float)_effect.CubeDepthScale.GetValue(frame, length, fps),
            D3DEffectSelection.Sphere => (float)_effect.SphereDepthScale.GetValue(frame, length, fps),
            _ => 1f,
        };
        CurrentLightIntensity = (float)_effect.LightIntensity.GetValue(frame, length, fps);

        return effectDescription.DrawDescription;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
