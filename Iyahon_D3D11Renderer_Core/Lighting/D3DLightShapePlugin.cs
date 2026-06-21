using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// D3D11光源の図形プラグイン。
/// YMM4の図形一覧に「D3D11光源」として表示される。
/// タイムラインに配置すると、D3D11描画パイプラインの光源として機能する。
/// </summary>
public class D3DLightShapePlugin : IShapePlugin
{
    public string Name => "D3D11光源";

    public bool IsExoShapeSupported => false;
    public bool IsExoMaskSupported => false;

    public IShapeParameter CreateShapeParameter(SharedDataStore? sharedData)
    {
        return new D3DLightShapeParameter(sharedData);
    }
}
