using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

/// <summary>
/// 基本的な 3D プリミティブ図形（立方体・UV球・ICO球・円・円柱・円錐・トーラス）を
/// 直接描画・モデリング編集できる図形プラグイン。
/// YMM4 の図形一覧に「D3D11 図形」として表示される。
/// </summary>
public class D3DPrimitiveShapePlugin : IShapePlugin
{
    public string Name => "D3D11 図形";

    public bool IsExoShapeSupported => false;
    public bool IsExoMaskSupported => false;

    public IShapeParameter CreateShapeParameter(SharedDataStore? sharedData)
        => new D3DPrimitiveShapeParameter(sharedData);
}
