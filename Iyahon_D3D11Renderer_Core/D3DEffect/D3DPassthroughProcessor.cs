using System;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect;

/// <summary>
/// D3Dエフェクト用のパススルー VideoEffectProcessor。
/// D2D パイプラインとしては何もせず入力をそのまま出力する。
/// 実際の 3D 描画は DepthSortRendererPatch → DepthSortRenderer で行われる。
/// すべての D3D VideoEffect で共有できる。
/// </summary>
internal sealed class D3DPassthroughProcessor : IVideoEffectProcessor, IDrawable, IDisposable
{
    private ID2D1Image? _input;
    private bool _disposed;

    public ID2D1Image Output => _input ?? throw new InvalidOperationException("input is null");

    public void SetInput(ID2D1Image? input) => _input = input;

    public void ClearInput() => _input = null;

    public DrawDescription Update(EffectDescription effectDescription)
        => effectDescription.DrawDescription;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
