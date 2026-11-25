using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

/// <summary>
/// 内蔵低メモリ3Dモデルローダーの図形プラグイン。
/// YMM4の図形一覧に「D3D11 3Dモデル」として表示され、OBJ / GLB / GLTF / VRM / FBX / PMX / PMD / STL / PLY / 3MF を直接描画する。
/// </summary>
public class D3DModelShapePlugin : IShapePlugin
{
    public string Name => "D3D11 3Dモデル";

    public bool IsExoShapeSupported => false;
    public bool IsExoMaskSupported => false;

    public IShapeParameter CreateShapeParameter(SharedDataStore? sharedData)
        => new D3DModelShapeParameter(sharedData);
}
