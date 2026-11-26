// Portions adapted from YMM4-ObjLoader (Copyright (c) 2025 routersys, MIT License)
// and YMM43D (Copyright (c) 2026 いるかぁぁ, MIT License).
// Static mesh + material loader for .pmx and .pmd (skips bones/morphs/physics).

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D.Parsers;

internal static class PmxPmdParser
{
    public static ParsedModel? ParsePmx(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        string fullPath = Path.GetFullPath(filePath);
        string baseDir = Path.GetDirectoryName(fullPath) ?? string.Empty;

        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var br = new BinaryReader(fs);

        if (fs.Length < 36) return null;
        byte[] magic = br.ReadBytes(4);
        if (Encoding.ASCII.GetString(magic) != "PMX ") return null;

        float ver = br.ReadSingle();
        if (ver < 2.0f)
        {
            ModelLoadHelper.Log($"PMX {ver:F1} は未対応です（2.0以降対応）: {filePath}");
            return null;
        }

        byte globalCount = br.ReadByte();
        byte[] rawGlobals = br.ReadBytes(globalCount);
        Span<byte> g = stackalloc byte[8];
        rawGlobals.AsSpan(0, Math.Min(8, rawGlobals.Length)).CopyTo(g);

        Encoding enc = g[0] == 0 ? Encoding.Unicode : Encoding.UTF8;
        int addUvCount = g[1];
        int vertexIdxSize = g[2];
        int textureIdxSize = g[3];
        int boneIdxSize = g[5];

        // 名前・コメント（日・英）4文字列をスキップ
        for (int i = 0; i < 4; i++)
        {
            int sl = br.ReadInt32();
            if (sl > 0) fs.Seek(sl, SeekOrigin.Current);
        }

        int vCount = br.ReadInt32();
        if (vCount <= 0) return null;

        var vertices = new ModelVertex[vCount];
        int addUvSkipBytes = addUvCount * 16;

        for (int i = 0; i < vCount; i++)
        {
            var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var norm = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var uv = new Vector2(br.ReadSingle(), br.ReadSingle());

            if (addUvSkipBytes > 0) fs.Seek(addUvSkipBytes, SeekOrigin.Current);

            byte weightType = br.ReadByte();
            int skipWeightBytes = weightType switch
            {
                0 => boneIdxSize,                  // BDEF1
                1 => boneIdxSize * 2 + 4,          // BDEF2
                2 or 4 => boneIdxSize * 4 + 16,    // BDEF4 / QDEF
                3 => boneIdxSize * 2 + 4 + 36,     // SDEF
                _ => boneIdxSize,
            };
            if (skipWeightBytes > 0) fs.Seek(skipWeightBytes, SeekOrigin.Current);

            br.ReadSingle(); // edgeScale

            vertices[i] = new ModelVertex
            {
                Position = pos,
                Normal = norm,
                TexCoord = uv,
                Color = Vector4.One,
            };
        }

        int iCount = br.ReadInt32();
        if (iCount < 3) return null;

        var indices = new uint[iCount];
        if (vertexIdxSize == 1)
        {
            for (int i = 0; i < iCount; i++) indices[i] = br.ReadByte();
        }
        else if (vertexIdxSize == 2)
        {
            for (int i = 0; i < iCount; i++) indices[i] = br.ReadUInt16();
        }
        else
        {
            for (int i = 0; i < iCount; i++) indices[i] = br.ReadUInt32();
        }

        int tCount = br.ReadInt32();
        var textures = new string?[Math.Max(0, tCount)];
        for (int i = 0; i < tCount; i++)
        {
            int tLen = br.ReadInt32();
            if (tLen > 0)
            {
                string rel = enc.GetString(br.ReadBytes(tLen)).Trim('\0');
                textures[i] = ModelLoadHelper.ResolveTexturePath(rel, baseDir);
            }
        }

        int mCount = br.ReadInt32();
        var parts = new List<ParsedModelPart>(Math.Max(0, mCount));
        int offset = 0;

        for (int i = 0; i < mCount; i++)
        {
            string matName = ReadPmxText(br, enc);
            string matNameEn = ReadPmxText(br, enc);
            if (string.IsNullOrEmpty(matName)) matName = matNameEn;

            var diffuse = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // specular RGB
            float specPow = br.ReadSingle();
            br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // ambient RGB
            br.ReadByte(); // drawing flags
            fs.Seek(16 + 4, SeekOrigin.Current); // edgeColor + edgeSize

            int texIdx = ReadSignedIndex(br, textureIdxSize);
            ReadSignedIndex(br, textureIdxSize); // sphere texture index
            br.ReadByte(); // sphere mode

            byte toonFlag = br.ReadByte();
            if (toonFlag == 0)
                ReadSignedIndex(br, textureIdxSize);
            else
                br.ReadByte();

            // memo
            int memoLen = br.ReadInt32();
            if (memoLen > 0) fs.Seek(memoLen, SeekOrigin.Current);

            int faceVertCount = br.ReadInt32();
            string? texPath = (uint)texIdx < (uint)textures.Length ? textures[texIdx] : null;
            float roughness = specPow > 0f
                ? Math.Clamp(MathF.Sqrt(2f / (specPow + 2f)), 0.15f, 0.92f)
                : -1f;

            if (faceVertCount > 0 && offset + faceVertCount <= indices.Length)
            {
                parts.Add(new ParsedModelPart
                {
                    Name = matName,
                    IndexOffset = offset,
                    IndexCount = faceVertCount,
                    BaseColor = diffuse,
                    Roughness = roughness,
                    TexturePath = texPath,
                    ForceOpaque = texPath == null && diffuse.W >= 0.999f,
                });
            }
            offset += Math.Max(0, faceVertCount);
        }

        ModelLoadHelper.CalculateBounds(vertices, out var bMin, out var bMax, out var center, out float scale);
        return new ParsedModel
        {
            Vertices = vertices,
            Indices = indices,
            Parts = parts,
            BoundsMin = bMin,
            BoundsMax = bMax,
            ModelCenter = center,
            ModelScale = scale,
        };
    }

    public static ParsedModel? ParsePmd(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        string fullPath = Path.GetFullPath(filePath);
        string baseDir = Path.GetDirectoryName(fullPath) ?? string.Empty;

        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        Encoding sjis;
        try { sjis = Encoding.GetEncoding(932); }
        catch { sjis = Encoding.UTF8; }

        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var br = new BinaryReader(fs);

        if (fs.Length < 283) return null;
        if (Encoding.ASCII.GetString(br.ReadBytes(3)) != "Pmd") return null;

        br.ReadSingle(); // version
        fs.Seek(20 + 256, SeekOrigin.Current); // name + comment

        int vCount = br.ReadInt32();
        if (vCount <= 0) return null;

        var vertices = new ModelVertex[vCount];
        for (int i = 0; i < vCount; i++)
        {
            var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var norm = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var uv = new Vector2(br.ReadSingle(), br.ReadSingle());
            fs.Seek(6, SeekOrigin.Current); // bone0, bone1, weight, edge

            vertices[i] = new ModelVertex
            {
                Position = pos,
                Normal = norm,
                TexCoord = uv,
                Color = Vector4.One,
            };
        }

        int iCount = br.ReadInt32();
        if (iCount < 3) return null;

        var indices = new uint[iCount];
        for (int i = 0; i < iCount; i++)
        {
            indices[i] = br.ReadUInt16();
        }

        int mCount = br.ReadInt32();
        var parts = new List<ParsedModelPart>(Math.Max(0, mCount));
        int offset = 0;

        for (int i = 0; i < mCount; i++)
        {
            var diffuse = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            float specPow = br.ReadSingle();
            br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // spec
            br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // amb
            br.ReadByte(); // toon
            br.ReadByte(); // edge
            int faceCount = br.ReadInt32();

            byte[] texBuf = br.ReadBytes(20);
            int zeroIdx = Array.IndexOf(texBuf, (byte)0);
            string texRaw = sjis.GetString(texBuf, 0, zeroIdx >= 0 ? zeroIdx : texBuf.Length).Trim();
            int starIdx = texRaw.IndexOf('*');
            if (starIdx >= 0) texRaw = texRaw.Substring(0, starIdx);

            string? texPath = ModelLoadHelper.ResolveTexturePath(texRaw, baseDir);
            float roughness = specPow > 0f
                ? Math.Clamp(MathF.Sqrt(2f / (specPow + 2f)), 0.15f, 0.92f)
                : -1f;

            if (faceCount > 0 && offset + faceCount <= indices.Length)
            {
                parts.Add(new ParsedModelPart
                {
                    Name = $"Mat_{i}",
                    IndexOffset = offset,
                    IndexCount = faceCount,
                    BaseColor = diffuse,
                    Roughness = roughness,
                    TexturePath = texPath,
                    ForceOpaque = texPath == null && diffuse.W >= 0.999f,
                });
            }
            offset += Math.Max(0, faceCount);
        }

        ModelLoadHelper.CalculateBounds(vertices, out var bMin, out var bMax, out var center, out float scale);
        return new ParsedModel
        {
            Vertices = vertices,
            Indices = indices,
            Parts = parts,
            BoundsMin = bMin,
            BoundsMax = bMax,
            ModelCenter = center,
            ModelScale = scale,
        };
    }

    private static string ReadPmxText(BinaryReader br, Encoding enc)
    {
        int len = br.ReadInt32();
        if (len <= 0) return string.Empty;
        return enc.GetString(br.ReadBytes(len)).Trim('\0');
    }

    private static int ReadSignedIndex(BinaryReader br, int size) => size switch
    {
        1 => br.ReadSByte(),
        2 => br.ReadInt16(),
        4 => br.ReadInt32(),
        _ => -1,
    };
}
