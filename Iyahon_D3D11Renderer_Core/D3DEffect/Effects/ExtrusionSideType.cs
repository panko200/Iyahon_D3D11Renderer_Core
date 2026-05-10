using System.ComponentModel.DataAnnotations;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect.Effects;

/// <summary>
/// 押し出しの側面タイプ。
/// </summary>
public enum ExtrusionSideType
{
    [Display(Name = "画像")] Image = 1,
    [Display(Name = "単色")] Solid = 2,
}
