using System.Numerics;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;
using YukkuriMovieMaker.Commons;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

/// <summary>
/// Viewport3D / MeshEditSession / Manipulator から共通に扱う 3D形状パラメータ契約。
/// D3DModelShapeParameter と D3DPrimitiveShapeParameter の双方が実装する。
/// </summary>
internal interface ID3DModelTransformParameter
{
    Animation ModelScale { get; }
    Animation ScaleX { get; }
    Animation ScaleY { get; }
    Animation ScaleZ { get; }
    Animation LocalRotX { get; }
    Animation LocalRotY { get; }
    Animation LocalRotZ { get; }

    bool NormalizeSize { get; }
    bool GroundToOrigin { get; }
    Animation UnitScale { get; }

    string FilePath { get; }
    string EmbeddedMeshData { get; set; }
    string ModifierStackData { get; set; }

    EditableMesh CreateDefaultEditableMesh();
}

/// <summary>
/// DepthSortRendererPatch からモデルの現在データ・パスを取得するための共通ソース契約。
/// </summary>
internal interface ID3DBuiltInModelSource
{
    string FilePath { get; }
    string EmbeddedMeshData { get; }
    string ModifierStackData { get; }
    ObjModelData? LastModelData { get; }
}
