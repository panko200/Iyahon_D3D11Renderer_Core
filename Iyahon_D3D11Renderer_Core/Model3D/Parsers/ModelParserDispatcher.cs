using System;
using System.IO;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D.Parsers;

internal static class ModelParserDispatcher
{
    public const string FileDialogFilter = "*.obj;*.glb;*.gltf;*.vrm;*.fbx;*.pmx;*.pmd;*.stl;*.ply;*.3mf";

    public static ParsedModel? Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".obj" => ObjParser.Parse(filePath),
                ".glb" or ".gltf" or ".vrm" => GltfParser.Parse(filePath),
                ".fbx" => FbxParser.Parse(filePath),
                ".pmx" => PmxPmdParser.ParsePmx(filePath),
                ".pmd" => PmxPmdParser.ParsePmd(filePath),
                ".stl" => SimpleMeshParsers.ParseStl(filePath),
                ".ply" => SimpleMeshParsers.ParsePly(filePath),
                ".3mf" => SimpleMeshParsers.Parse3mf(filePath),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            ModelLoadHelper.Log($"3Dモデルパースエラー ({filePath}): {ex.Message}");
            return null;
        }
    }
}
