using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// D3D11環境の図形プラグイン。
/// YMM4の図形一覧に「D3D11環境」として表示される。
/// タイムラインに配置すると、シーンごとのHDRI環境マップ・スカイボックス・環境光／IBL・フォグ・ゴッドレイをアニメーション制御できる。
/// </summary>
public class D3DEnvironmentShapePlugin : IShapePlugin
{
    public string Name => "D3D11環境";

    public bool IsExoShapeSupported => false;
    public bool IsExoMaskSupported => false;

    public IShapeParameter CreateShapeParameter(SharedDataStore? sharedData)
    {
        return new D3DEnvironmentShapeParameter(sharedData);
    }
}
