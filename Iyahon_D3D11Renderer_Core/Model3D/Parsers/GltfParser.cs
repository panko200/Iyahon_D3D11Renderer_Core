// Portions adapted from YMM4-ObjLoader (Copyright (c) 2025 routersys, MIT License)
// and YMM43D (Copyright (c) 2026 いるかぁぁ, MIT License).
// Keeps embedded textures in memory (zero %TEMP% disk writes) and supports .glb / .gltf / .vrm.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D.Parsers;

internal static class GltfParser
{
    private readonly struct GltfImageSource
    {
        public readonly string Key;
        public readonly string Ext;
        public readonly string? FilePath;
        public readonly byte[]? Bytes;

        public GltfImageSource(string key, string ext, string? filePath, byte[]? bytes)
        {
            Key = key;
            Ext = ext;
            FilePath = filePath;
            Bytes = bytes;
        }
    }

    private readonly struct GltfMaterialInfo
    {
        public readonly string Name;
        public readonly Vector4 BaseColor;
        public readonly Vector4 EmissiveColor;
        public readonly float Metallic;
        public readonly float Roughness;
        public readonly int ImageIndex;
        public readonly int NormalImageIndex;
        public readonly float NormalScale;
        public readonly int RoughMetalImageIndex;
        public readonly int EmissiveImageIndex;
        public readonly float Transmission;
        public readonly float Ior;
        public readonly int TransmissionImageIndex;
        public readonly bool ForceOpaque;

        public GltfMaterialInfo(
            string name, Vector4 baseColor, Vector4 emissiveColor,
            float metallic, float roughness, int imageIndex,
            int normalImageIndex, float normalScale, int roughMetalImageIndex, int emissiveImageIndex,
            float transmission, float ior, int transmissionImageIndex, bool forceOpaque)
        {
            Name = name;
            BaseColor = baseColor;
            EmissiveColor = emissiveColor;
            Metallic = metallic;
            Roughness = roughness;
            ImageIndex = imageIndex;
            NormalImageIndex = normalImageIndex;
            NormalScale = normalScale;
            RoughMetalImageIndex = roughMetalImageIndex;
            EmissiveImageIndex = emissiveImageIndex;
            Transmission = transmission;
            Ior = ior;
            TransmissionImageIndex = transmissionImageIndex;
            ForceOpaque = forceOpaque;
        }
    }

    public static ParsedModel? Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        string fullPath = Path.GetFullPath(filePath);
        string baseDir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        string ext = Path.GetExtension(fullPath).ToLowerInvariant();

        byte[]? glbBin = null;
        string? jsonText = null;

        if (ext == ".glb" || ext == ".vrm")
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (fs.Length >= 12)
            {
                uint magic = br.ReadUInt32();
                if (magic == 0x46546C67) // "glTF"
                {
                    br.ReadUInt32(); // version
                    uint totalLen = br.ReadUInt32();
                    long limit = Math.Min(fs.Length, totalLen);

                    while (fs.Position + 8 <= limit)
                    {
                        uint chunkLen = br.ReadUInt32();
                        uint chunkType = br.ReadUInt32();
                        if (fs.Position + chunkLen > limit) break;

                        if (chunkType == 0x4E4F534A && jsonText == null) // "JSON"
                        {
                            byte[] jBytes = br.ReadBytes((int)chunkLen);
                            jsonText = Encoding.UTF8.GetString(jBytes);
                        }
                        else if (chunkType == 0x004E4942 && glbBin == null) // "BIN\0"
                        {
                            glbBin = br.ReadBytes((int)chunkLen);
                        }
                        else
                        {
                            fs.Seek(chunkLen, SeekOrigin.Current);
                        }
                    }
                }
            }
        }

        if (jsonText == null)
        {
            jsonText = File.ReadAllText(fullPath, Encoding.UTF8);
        }
        if (jsonText.Length > 0 && jsonText[0] == '\uFEFF')
            jsonText = jsonText.Substring(1);
        if (string.IsNullOrWhiteSpace(jsonText)) return null;

        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        if (root.TryGetProperty("extensionsRequired", out var reqExts) && reqExts.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in reqExts.EnumerateArray())
            {
                if (string.Equals(e.GetString(), "KHR_draco_mesh_compression", StringComparison.Ordinal))
                {
                    ModelLoadHelper.Log($"Draco 圧縮された glTF は未対応です: {filePath}");
                    return null;
                }
            }
        }

        var buffers = LoadBuffers(root, glbBin, baseDir);
        var images = LoadImages(root, buffers, baseDir, fullPath);
        var texToImage = LoadTextures(root);
        var materials = LoadMaterials(root, texToImage);

        var vertices = new List<ModelVertex>(4096);
        var indices = new List<uint>(8192);
        var parts = new List<ParsedModelPart>();

        root.TryGetProperty("nodes", out var nodesEl);
        if (!root.TryGetProperty("meshes", out var meshesEl) || meshesEl.ValueKind != JsonValueKind.Array)
            return null;

        var sceneNodes = new List<int>();
        if (root.TryGetProperty("scenes", out var scenesEl) && scenesEl.ValueKind == JsonValueKind.Array && scenesEl.GetArrayLength() > 0)
        {
            int sceneIdx = root.TryGetProperty("scene", out var scIdxEl) ? scIdxEl.GetInt32() : 0;
            if ((uint)sceneIdx < (uint)scenesEl.GetArrayLength())
            {
                var sc = scenesEl[sceneIdx];
                if (sc.TryGetProperty("nodes", out var sn) && sn.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in sn.EnumerateArray())
                        sceneNodes.Add(n.GetInt32());
                }
            }
        }

        if (sceneNodes.Count > 0 && nodesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (int nodeIdx in sceneNodes)
            {
                TraverseNode(root, buffers, nodesEl, meshesEl, materials, images, nodeIdx, Matrix4x4.Identity, vertices, indices, parts);
            }
        }
        else
        {
            int mc = meshesEl.GetArrayLength();
            for (int m = 0; m < mc; m++)
            {
                AppendMesh(root, buffers, meshesEl[m], materials, images, Matrix4x4.Identity, string.Empty, vertices, indices, parts);
            }
        }

        if (vertices.Count == 0 || indices.Count < 3) return null;

        var vertArr = vertices.ToArray();
        var idxArr = indices.ToArray();
        ModelLoadHelper.CalculateBounds(vertArr, out var bMin, out var bMax, out var center, out float scale);

        return new ParsedModel
        {
            Vertices = vertArr,
            Indices = idxArr,
            Parts = parts,
            BoundsMin = bMin,
            BoundsMax = bMax,
            ModelCenter = center,
            ModelScale = scale,
        };
    }

    private static List<byte[]?> LoadBuffers(JsonElement root, byte[]? glbBin, string baseDir)
    {
        var list = new List<byte[]?>();
        if (!root.TryGetProperty("buffers", out var bufs) || bufs.ValueKind != JsonValueKind.Array)
            return list;

        int i = 0;
        foreach (var b in bufs.EnumerateArray())
        {
            if (b.TryGetProperty("uri", out var uriProp))
            {
                string? uri = uriProp.GetString();
                if (!string.IsNullOrEmpty(uri))
                {
                    if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        int comma = uri.IndexOf(',');
                        if (comma >= 0)
                        {
                            try { list.Add(Convert.FromBase64String(uri.Substring(comma + 1))); i++; continue; }
                            catch { }
                        }
                    }
                    else
                    {
                        string relPath = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
                        string full = Path.GetFullPath(Path.Combine(baseDir, relPath));
                        if (File.Exists(full))
                        {
                            list.Add(File.ReadAllBytes(full));
                            i++;
                            continue;
                        }
                    }
                }
            }

            list.Add(i == 0 ? glbBin : null);
            i++;
        }
        return list;
    }

    private static List<GltfImageSource> LoadImages(JsonElement root, List<byte[]?> buffers, string baseDir, string modelFullPath)
    {
        var list = new List<GltfImageSource>();
        if (!root.TryGetProperty("images", out var imgs) || imgs.ValueKind != JsonValueKind.Array)
            return list;

        int idx = 0;
        foreach (var img in imgs.EnumerateArray())
        {
            string ext = ".png";
            if (img.TryGetProperty("mimeType", out var mimeProp))
            {
                ext = mimeProp.GetString() switch
                {
                    "image/jpeg" or "image/jpg" => ".jpg",
                    "image/webp" => ".webp",
                    "image/vnd-ms.dds" or "image/dds" => ".dds",
                    _ => ".png",
                };
            }

            byte[]? bytes = null;
            string? filePath = null;

            if (img.TryGetProperty("bufferView", out var bvProp) &&
                TryGetBufferView(root, buffers, bvProp.GetInt32(), out var buf, out int off, out int len, out _))
            {
                if (off >= 0 && len > 0 && off + len <= buf.Length)
                {
                    bytes = new byte[len];
                    Buffer.BlockCopy(buf, off, bytes, 0, len);
                }
            }
            else if (img.TryGetProperty("uri", out var uriProp))
            {
                string? uri = uriProp.GetString();
                if (!string.IsNullOrEmpty(uri))
                {
                    if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        int comma = uri.IndexOf(',');
                        if (comma >= 0)
                        {
                            if (uri.Contains("jpeg", StringComparison.OrdinalIgnoreCase)) ext = ".jpg";
                            else if (uri.Contains("webp", StringComparison.OrdinalIgnoreCase)) ext = ".webp";
                            try { bytes = Convert.FromBase64String(uri.Substring(comma + 1)); }
                            catch { bytes = null; }
                        }
                    }
                    else
                    {
                        string unescaped = Uri.UnescapeDataString(uri);
                        filePath = ModelLoadHelper.ResolveTexturePath(unescaped, baseDir);
                        ext = Path.GetExtension(unescaped);
                    }
                }
            }

            if (bytes != null && bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == 0x20534444)
                ext = ".dds";

            list.Add(new GltfImageSource($"{modelFullPath}#img:{idx}", ext, filePath, bytes));
            idx++;
        }

        return list;
    }

    private static List<int> LoadTextures(JsonElement root)
    {
        var list = new List<int>();
        if (!root.TryGetProperty("textures", out var texs) || texs.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var t in texs.EnumerateArray())
        {
            if (t.TryGetProperty("source", out var src))
                list.Add(src.GetInt32());
            else
                list.Add(-1);
        }
        return list;
    }

    private static List<GltfMaterialInfo> LoadMaterials(JsonElement root, List<int> texToImage)
    {
        var list = new List<GltfMaterialInfo>();
        if (!root.TryGetProperty("materials", out var mats) || mats.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var m in mats.EnumerateArray())
        {
            string name = m.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? string.Empty) : string.Empty;
            Vector4 baseColor = Vector4.One;
            float metallic = 0f;
            float roughness = -1f;
            int imageIdx = -1;
            int normalImgIdx = -1;
            float normalScale = 1f;
            int roughMetalImgIdx = -1;
            int emissiveImgIdx = -1;
            float transmission = 0f;
            float ior = 1.5f;
            int transmissionImgIdx = -1;

            if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                // glTF 2.0 spec §5.22: omitted metallicFactor & roughnessFactor both default to 1.0
                metallic = 1f;
                roughness = 1f;

                if (pbr.TryGetProperty("baseColorFactor", out var bcf) && bcf.GetArrayLength() == 4)
                {
                    baseColor = new Vector4(
                        bcf[0].GetSingle(),
                        bcf[1].GetSingle(),
                        bcf[2].GetSingle(),
                        bcf[3].GetSingle());
                }
                if (pbr.TryGetProperty("metallicFactor", out var mf))
                    metallic = Math.Clamp(mf.GetSingle(), 0f, 1f);
                if (pbr.TryGetProperty("roughnessFactor", out var rf))
                    roughness = Math.Clamp(rf.GetSingle(), 0.04f, 1f);

                if (pbr.TryGetProperty("baseColorTexture", out var bct) && bct.TryGetProperty("index", out var bctIdx))
                {
                    int ti = bctIdx.GetInt32();
                    if ((uint)ti < (uint)texToImage.Count) imageIdx = texToImage[ti];
                }
                if (pbr.TryGetProperty("metallicRoughnessTexture", out var mrt) && mrt.TryGetProperty("index", out var mrtIdx))
                {
                    int ti = mrtIdx.GetInt32();
                    if ((uint)ti < (uint)texToImage.Count)
                        roughMetalImgIdx = texToImage[ti];
                }
            }
            else if (m.TryGetProperty("extensions", out var matExts) &&
                     matExts.TryGetProperty("KHR_materials_pbrSpecularGlossiness", out var sg))
            {
                if (sg.TryGetProperty("diffuseFactor", out var df) && df.GetArrayLength() == 4)
                {
                    baseColor = new Vector4(df[0].GetSingle(), df[1].GetSingle(), df[2].GetSingle(), df[3].GetSingle());
                }
                if (sg.TryGetProperty("glossinessFactor", out var gf))
                    roughness = Math.Clamp(1f - gf.GetSingle(), 0.04f, 1f);
                if (sg.TryGetProperty("diffuseTexture", out var dt) && dt.TryGetProperty("index", out var dtIdx))
                {
                    int ti = dtIdx.GetInt32();
                    if ((uint)ti < (uint)texToImage.Count) imageIdx = texToImage[ti];
                }
            }

            if (m.TryGetProperty("extensions", out var rootExts))
            {
                if (rootExts.TryGetProperty("KHR_materials_transmission", out var ktr))
                {
                    bool hasTransFactor = ktr.TryGetProperty("transmissionFactor", out var trf);
                    if (hasTransFactor)
                        transmission = Math.Clamp(trf.GetSingle(), 0f, 1f);
                    if (ktr.TryGetProperty("transmissionTexture", out var trt) && trt.TryGetProperty("index", out var trtIdx))
                    {
                        int ti = trtIdx.GetInt32();
                        if ((uint)ti < (uint)texToImage.Count)
                        {
                            transmissionImgIdx = texToImage[ti];
                            if (!hasTransFactor) transmission = 1f;
                        }
                    }
                }
                if (rootExts.TryGetProperty("KHR_materials_ior", out var kior) &&
                    kior.TryGetProperty("ior", out var iorProp))
                {
                    ior = Math.Clamp(iorProp.GetSingle(), 1.0f, 3.0f);
                }
            }

            if (m.TryGetProperty("normalTexture", out var nt))
            {
                if (nt.TryGetProperty("index", out var ntIdx))
                {
                    int ti = ntIdx.GetInt32();
                    if ((uint)ti < (uint)texToImage.Count) normalImgIdx = texToImage[ti];
                }
                if (nt.TryGetProperty("scale", out var nsc))
                    normalScale = Math.Clamp(nsc.GetSingle(), 0f, 5f);
            }

            if (m.TryGetProperty("emissiveTexture", out var et) && et.TryGetProperty("index", out var etIdx))
            {
                int ti = etIdx.GetInt32();
                if ((uint)ti < (uint)texToImage.Count) emissiveImgIdx = texToImage[ti];
            }

            Vector4 emissive = Vector4.Zero;
            if (m.TryGetProperty("emissiveFactor", out var ef) && ef.GetArrayLength() >= 3)
            {
                float er = ef[0].GetSingle();
                float eg = ef[1].GetSingle();
                float eb = ef[2].GetSingle();
                if (er > 0f || eg > 0f || eb > 0f)
                {
                    float strength = 1f;
                    if (m.TryGetProperty("extensions", out var exts) &&
                        exts.TryGetProperty("KHR_materials_emissive_strength", out var es) &&
                        es.TryGetProperty("emissiveStrength", out var esVal))
                    {
                        strength = MathF.Max(esVal.GetSingle(), 0f);
                    }
                    emissive = new Vector4(er, eg, eb, strength);
                }
            }

            bool forceOpaque = true;
            if (m.TryGetProperty("alphaMode", out var am))
            {
                string? modeStr = am.GetString();
                if (string.Equals(modeStr, "BLEND", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(modeStr, "MASK", StringComparison.OrdinalIgnoreCase))
                {
                    forceOpaque = false;
                }
            }
            if (forceOpaque)
            {
                baseColor.W = 1f;
            }

            list.Add(new GltfMaterialInfo(
                name, baseColor, emissive, metallic, roughness,
                imageIdx, normalImgIdx, normalScale, roughMetalImgIdx, emissiveImgIdx,
                transmission, ior, transmissionImgIdx, forceOpaque));
        }
        return list;
    }

    private static void TraverseNode(
        JsonElement root,
        List<byte[]?> buffers,
        JsonElement nodes,
        JsonElement meshes,
        List<GltfMaterialInfo> materials,
        List<GltfImageSource> images,
        int nodeIdx,
        Matrix4x4 parentWorld,
        List<ModelVertex> vertices,
        List<uint> indices,
        List<ParsedModelPart> parts)
    {
        if ((uint)nodeIdx >= (uint)nodes.GetArrayLength()) return;
        var node = nodes[nodeIdx];
        string nodeName = node.TryGetProperty("name", out var nn) ? (nn.GetString() ?? string.Empty) : string.Empty;

        Matrix4x4 local = Matrix4x4.Identity;
        if (node.TryGetProperty("matrix", out var mProp) && mProp.GetArrayLength() == 16)
        {
            local = new Matrix4x4(
                mProp[0].GetSingle(), mProp[1].GetSingle(), mProp[2].GetSingle(), mProp[3].GetSingle(),
                mProp[4].GetSingle(), mProp[5].GetSingle(), mProp[6].GetSingle(), mProp[7].GetSingle(),
                mProp[8].GetSingle(), mProp[9].GetSingle(), mProp[10].GetSingle(), mProp[11].GetSingle(),
                mProp[12].GetSingle(), mProp[13].GetSingle(), mProp[14].GetSingle(), mProp[15].GetSingle());
        }
        else
        {
            Vector3 t = Vector3.Zero;
            Quaternion r = Quaternion.Identity;
            Vector3 s = Vector3.One;
            if (node.TryGetProperty("translation", out var tp) && tp.GetArrayLength() == 3)
                t = new Vector3(tp[0].GetSingle(), tp[1].GetSingle(), tp[2].GetSingle());
            if (node.TryGetProperty("rotation", out var rp) && rp.GetArrayLength() == 4)
                r = new Quaternion(rp[0].GetSingle(), rp[1].GetSingle(), rp[2].GetSingle(), rp[3].GetSingle());
            if (node.TryGetProperty("scale", out var sp) && sp.GetArrayLength() == 3)
                s = new Vector3(sp[0].GetSingle(), sp[1].GetSingle(), sp[2].GetSingle());

            local = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }

        Matrix4x4 world = local * parentWorld;

        if (node.TryGetProperty("mesh", out var meshProp))
        {
            int meshIdx = meshProp.GetInt32();
            if ((uint)meshIdx < (uint)meshes.GetArrayLength())
            {
                AppendMesh(root, buffers, meshes[meshIdx], materials, images, world, nodeName, vertices, indices, parts);
            }
        }

        if (node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in ch.EnumerateArray())
            {
                TraverseNode(root, buffers, nodes, meshes, materials, images, c.GetInt32(), world, vertices, indices, parts);
            }
        }
    }

    private static void AppendMesh(
        JsonElement root,
        List<byte[]?> buffers,
        JsonElement mesh,
        List<GltfMaterialInfo> materials,
        List<GltfImageSource> images,
        in Matrix4x4 world,
        string fallbackName,
        List<ModelVertex> vertices,
        List<uint> indices,
        List<ParsedModelPart> parts)
    {
        if (!mesh.TryGetProperty("primitives", out var prims) || prims.ValueKind != JsonValueKind.Array)
            return;

        string meshName = mesh.TryGetProperty("name", out var mn) ? (mn.GetString() ?? fallbackName) : fallbackName;
        Matrix4x4 normalMat = Matrix4x4.Invert(world, out var invWorld)
            ? Matrix4x4.Transpose(invWorld)
            : Matrix4x4.Identity;

        foreach (var prim in prims.EnumerateArray())
        {
            int mode = prim.TryGetProperty("mode", out var md) ? md.GetInt32() : 4;
            if (mode != 4 && mode != 5 && mode != 6) continue;

            if (!prim.TryGetProperty("attributes", out var attrs)) continue;
            if (!attrs.TryGetProperty("POSITION", out var posAccEl)) continue;

            Vector3[] positions = ReadVec3Accessor(root, buffers, posAccEl.GetInt32(), out int vCount);
            if (vCount <= 0) continue;

            bool hasNorm = attrs.TryGetProperty("NORMAL", out var normAccEl);
            Vector3[]? normals = hasNorm ? ReadVec3Accessor(root, buffers, normAccEl.GetInt32(), out _) : null;

            Vector2[]? uvs = null;
            if (attrs.TryGetProperty("TEXCOORD_0", out var uvAccEl))
                uvs = ReadVec2Accessor(root, buffers, uvAccEl.GetInt32());

            Vector4[]? colors = null;
            if (attrs.TryGetProperty("COLOR_0", out var colAccEl))
                colors = ReadColorAccessor(root, buffers, colAccEl.GetInt32(), vCount);

            int baseVertex = vertices.Count;
            for (int i = 0; i < vCount; i++)
            {
                Vector3 p = Vector3.Transform(positions[i], world);
                Vector3 n = normals != null && i < normals.Length
                    ? Vector3.TransformNormal(normals[i], normalMat)
                    : Vector3.Zero;
                float nLenSq = n.LengthSquared();
                if (nLenSq > 1e-12f) n /= MathF.Sqrt(nLenSq);

                vertices.Add(new ModelVertex
                {
                    Position = p,
                    Normal = n,
                    TexCoord = uvs != null && i < uvs.Length ? uvs[i] : Vector2.Zero,
                    Color = colors != null && i < colors.Length ? colors[i] : Vector4.One,
                });
            }

            uint[] rawIndices = prim.TryGetProperty("indices", out var indAccEl)
                ? ReadScalarIndices(root, buffers, indAccEl.GetInt32())
                : BuildSequentialIndices(vCount);

            int startIndexOffset = indices.Count;
            AppendPrimitiveIndices(mode, rawIndices, (uint)baseVertex, (uint)vCount, indices);
            int addedIndices = indices.Count - startIndexOffset;
            if (addedIndices < 3) continue;

            if (!hasNorm)
            {
                ComputeFlatNormalsForRange(vertices, indices, startIndexOffset, addedIndices);
            }

            GltfMaterialInfo mat = new(meshName, Vector4.One, Vector4.Zero, 0f, -1f, -1, -1, 1f, -1, -1, 0f, 1.5f, -1, true);
            if (prim.TryGetProperty("material", out var matProp))
            {
                int mIdx = matProp.GetInt32();
                if ((uint)mIdx < (uint)materials.Count)
                    mat = materials[mIdx];
            }

            static void ResolveImg(
                int imgIndex,
                List<GltfImageSource> imgs,
                out string? path,
                out byte[]? embBytes,
                out string? embKey,
                out string? embExt)
            {
                path = null;
                embBytes = null;
                embKey = null;
                embExt = null;
                if ((uint)imgIndex < (uint)imgs.Count)
                {
                    var img = imgs[imgIndex];
                    path = img.FilePath;
                    if (string.IsNullOrEmpty(path) && img.Bytes != null)
                    {
                        embBytes = img.Bytes;
                        embKey = img.Key;
                        embExt = img.Ext;
                    }
                }
            }

            ResolveImg(mat.ImageIndex, images, out string? texPath, out byte[]? embBytes, out string? embKey, out string? embExt);
            ResolveImg(mat.NormalImageIndex, images, out string? normPath, out byte[]? embNormBytes, out string? embNormKey, out string? embNormExt);
            ResolveImg(mat.RoughMetalImageIndex, images, out string? mrPath, out byte[]? embMrBytes, out string? embMrKey, out string? embMrExt);
            ResolveImg(mat.EmissiveImageIndex, images, out string? emPath, out byte[]? embEmBytes, out string? embEmKey, out string? embEmExt);
            ResolveImg(mat.TransmissionImageIndex, images, out string? trPath, out byte[]? embTrBytes, out string? embTrKey, out string? embTrExt);

            parts.Add(new ParsedModelPart
            {
                Name = string.IsNullOrEmpty(mat.Name) ? meshName : mat.Name,
                IndexOffset = startIndexOffset,
                IndexCount = addedIndices,
                BaseColor = mat.BaseColor,
                EmissiveColor = mat.EmissiveColor,
                Metallic = mat.Metallic,
                Roughness = mat.Roughness,
                TexturePath = texPath,
                EmbeddedTextureBytes = embBytes,
                EmbeddedTextureKey = embKey,
                EmbeddedTextureExt = embExt,
                NormalTexturePath = normPath,
                EmbeddedNormalBytes = embNormBytes,
                EmbeddedNormalKey = embNormKey,
                EmbeddedNormalExt = embNormExt,
                NormalScale = mat.NormalScale,
                RoughnessMetallicTexturePath = mrPath,
                EmbeddedRoughMetalBytes = embMrBytes,
                EmbeddedRoughMetalKey = embMrKey,
                EmbeddedRoughMetalExt = embMrExt,
                RoughMetalPacking = (mrPath != null || embMrBytes != null) ? 1 : 0,
                EmissiveTexturePath = emPath,
                EmbeddedEmissiveBytes = embEmBytes,
                EmbeddedEmissiveKey = embEmKey,
                EmbeddedEmissiveExt = embEmExt,
                Transmission = mat.Transmission,
                Ior = mat.Ior,
                TransmissionTexturePath = trPath,
                EmbeddedTransmissionBytes = embTrBytes,
                EmbeddedTransmissionKey = embTrKey,
                EmbeddedTransmissionExt = embTrExt,
                ForceOpaque = mat.ForceOpaque && texPath == null && embBytes == null,
            });
        }
    }

    private static void ComputeFlatNormalsForRange(
        List<ModelVertex> vertices, List<uint> indices, int startOffset, int count)
    {
        int tris = count / 3;
        for (int t = 0; t < tris; t++)
        {
            int i0 = (int)indices[startOffset + t * 3];
            int i1 = (int)indices[startOffset + t * 3 + 1];
            int i2 = (int)indices[startOffset + t * 3 + 2];
            if ((uint)i0 >= (uint)vertices.Count || (uint)i1 >= (uint)vertices.Count || (uint)i2 >= (uint)vertices.Count)
                continue;

            Vector3 cross = Vector3.Cross(
                vertices[i1].Position - vertices[i0].Position,
                vertices[i2].Position - vertices[i0].Position);
            float lenSq = cross.LengthSquared();
            Vector3 n = lenSq > 1e-12f ? cross / MathF.Sqrt(lenSq) : Vector3.UnitZ;

            void AddN(int idx)
            {
                var v = vertices[idx];
                v.Normal += cross;
                vertices[idx] = v;
            }

            AddN(i0); AddN(i1); AddN(i2);
            if (lenSq <= 1e-12f)
            {
                var v0 = vertices[i0]; v0.Normal = n; vertices[i0] = v0;
            }
        }

        for (int k = startOffset; k < startOffset + count; k++)
        {
            int idx = (int)indices[k];
            if ((uint)idx >= (uint)vertices.Count) continue;
            var v = vertices[idx];
            float lenSq = v.Normal.LengthSquared();
            v.Normal = lenSq > 1e-12f ? v.Normal / MathF.Sqrt(lenSq) : Vector3.UnitZ;
            vertices[idx] = v;
        }
    }

    private static uint[] BuildSequentialIndices(int vCount)
    {
        var arr = new uint[vCount];
        for (int i = 0; i < vCount; i++) arr[i] = (uint)i;
        return arr;
    }

    private static void AppendPrimitiveIndices(
        int mode, uint[] raw, uint baseVertex, uint vCount, List<uint> dst)
    {
        if (mode == 4)
        {
            int limit = (raw.Length / 3) * 3;
            for (int i = 0; i < limit; i++)
            {
                uint idx = raw[i] < vCount ? raw[i] : 0;
                dst.Add(baseVertex + idx);
            }
        }
        else if (mode == 5 && raw.Length >= 3) // TRIANGLE_STRIP
        {
            for (int i = 0; i <= raw.Length - 3; i++)
            {
                uint a = raw[i] < vCount ? raw[i] : 0;
                uint b = raw[i + 1] < vCount ? raw[i + 1] : 0;
                uint c = raw[i + 2] < vCount ? raw[i + 2] : 0;
                if ((i & 1) == 0)
                {
                    dst.Add(baseVertex + a); dst.Add(baseVertex + b); dst.Add(baseVertex + c);
                }
                else
                {
                    dst.Add(baseVertex + b); dst.Add(baseVertex + a); dst.Add(baseVertex + c);
                }
            }
        }
        else if (mode == 6 && raw.Length >= 3) // TRIANGLE_FAN
        {
            uint a = raw[0] < vCount ? raw[0] : 0;
            for (int i = 1; i <= raw.Length - 2; i++)
            {
                uint b = raw[i] < vCount ? raw[i] : 0;
                uint c = raw[i + 1] < vCount ? raw[i + 1] : 0;
                dst.Add(baseVertex + a); dst.Add(baseVertex + b); dst.Add(baseVertex + c);
            }
        }
    }

    private static bool TryGetAccessorSpan(
        JsonElement root,
        List<byte[]?> buffers,
        int accIdx,
        out byte[] buf,
        out int baseOffset,
        out int count,
        out int stride,
        out int compType,
        out bool normalized,
        out string typeStr)
    {
        buf = Array.Empty<byte>();
        baseOffset = 0; count = 0; stride = 0; compType = 0; normalized = false; typeStr = "SCALAR";

        if (!root.TryGetProperty("accessors", out var accs) || (uint)accIdx >= (uint)accs.GetArrayLength())
            return false;

        var acc = accs[accIdx];
        if (!acc.TryGetProperty("bufferView", out var bvProp))
            return false;

        count = acc.GetProperty("count").GetInt32();
        compType = acc.GetProperty("componentType").GetInt32();
        normalized = acc.TryGetProperty("normalized", out var nEl) && nEl.GetBoolean();
        typeStr = acc.GetProperty("type").GetString() ?? "SCALAR";
        int accOff = acc.TryGetProperty("byteOffset", out var aoEl) ? aoEl.GetInt32() : 0;

        if (!TryGetBufferView(root, buffers, bvProp.GetInt32(), out buf, out int bvOff, out _, out int bvStride))
            return false;

        baseOffset = bvOff + accOff;
        int elemSize = ComponentByteSize(compType) * TypeComponentCount(typeStr);
        stride = bvStride > 0 ? bvStride : elemSize;
        return elemSize > 0 && baseOffset >= 0;
    }

    private static bool TryGetBufferView(
        JsonElement root,
        List<byte[]?> buffers,
        int bvIdx,
        out byte[] buf,
        out int byteOffset,
        out int byteLength,
        out int byteStride)
    {
        buf = Array.Empty<byte>();
        byteOffset = 0; byteLength = 0; byteStride = 0;

        if (!root.TryGetProperty("bufferViews", out var bvs) || (uint)bvIdx >= (uint)bvs.GetArrayLength())
            return false;

        var bv = bvs[bvIdx];
        int bufIdx = bv.TryGetProperty("buffer", out var bProp) ? bProp.GetInt32() : 0;
        if ((uint)bufIdx >= (uint)buffers.Count || buffers[bufIdx] == null)
            return false;

        buf = buffers[bufIdx]!;
        byteOffset = bv.TryGetProperty("byteOffset", out var oProp) ? oProp.GetInt32() : 0;
        byteLength = bv.TryGetProperty("byteLength", out var lProp) ? lProp.GetInt32() : 0;
        byteStride = bv.TryGetProperty("byteStride", out var sProp) ? sProp.GetInt32() : 0;
        return true;
    }

    private static int ComponentByteSize(int compType) => compType switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => 0,
    };

    private static int TypeComponentCount(string typeStr) => typeStr switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        _ => 0,
    };

    private static float ReadFloatComponent(byte[] buf, int offset, int compType, bool normalized)
    {
        int size = ComponentByteSize(compType);
        if (offset < 0 || offset + size > buf.Length) return 0f;

        return compType switch
        {
            5126 => BitConverter.ToSingle(buf, offset),
            5120 => normalized ? MathF.Max((sbyte)buf[offset] / 127f, -1f) : (sbyte)buf[offset],
            5121 => normalized ? buf[offset] / 255f : buf[offset],
            5122 => normalized ? MathF.Max(BitConverter.ToInt16(buf, offset) / 32767f, -1f) : BitConverter.ToInt16(buf, offset),
            5123 => normalized ? BitConverter.ToUInt16(buf, offset) / 65535f : BitConverter.ToUInt16(buf, offset),
            5125 => BitConverter.ToUInt32(buf, offset),
            _ => 0f,
        };
    }

    private static Vector3[] ReadVec3Accessor(JsonElement root, List<byte[]?> buffers, int accIdx, out int count)
    {
        if (!TryGetAccessorSpan(root, buffers, accIdx, out var buf, out int off, out count, out int stride, out int compType, out bool norm, out _))
        {
            count = 0;
            return Array.Empty<Vector3>();
        }

        int cs = ComponentByteSize(compType);
        var arr = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int p = off + i * stride;
            arr[i] = new Vector3(
                ReadFloatComponent(buf, p, compType, norm),
                ReadFloatComponent(buf, p + cs, compType, norm),
                ReadFloatComponent(buf, p + cs * 2, compType, norm));
        }
        return arr;
    }

    private static Vector2[] ReadVec2Accessor(JsonElement root, List<byte[]?> buffers, int accIdx)
    {
        if (!TryGetAccessorSpan(root, buffers, accIdx, out var buf, out int off, out int count, out int stride, out int compType, out bool norm, out _))
            return Array.Empty<Vector2>();

        int cs = ComponentByteSize(compType);
        var arr = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int p = off + i * stride;
            arr[i] = new Vector2(
                ReadFloatComponent(buf, p, compType, norm),
                ReadFloatComponent(buf, p + cs, compType, norm));
        }
        return arr;
    }

    private static Vector4[] ReadColorAccessor(JsonElement root, List<byte[]?> buffers, int accIdx, int expectedCount)
    {
        if (!TryGetAccessorSpan(root, buffers, accIdx, out var buf, out int off, out int count, out int stride, out int compType, out bool norm, out var typeStr))
            return Array.Empty<Vector4>();

        int cs = ComponentByteSize(compType);
        bool hasAlpha = typeStr == "VEC4";
        int n = Math.Min(count, expectedCount);
        var arr = new Vector4[n];
        for (int i = 0; i < n; i++)
        {
            int p = off + i * stride;
            arr[i] = new Vector4(
                ReadFloatComponent(buf, p, compType, norm),
                ReadFloatComponent(buf, p + cs, compType, norm),
                ReadFloatComponent(buf, p + cs * 2, compType, norm),
                hasAlpha ? ReadFloatComponent(buf, p + cs * 3, compType, norm) : 1f);
        }
        return arr;
    }

    private static uint[] ReadScalarIndices(JsonElement root, List<byte[]?> buffers, int accIdx)
    {
        if (!TryGetAccessorSpan(root, buffers, accIdx, out var buf, out int off, out int count, out int stride, out int compType, out _, out _))
            return Array.Empty<uint>();

        var arr = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int p = off + i * stride;
            if (p < 0 || p + ComponentByteSize(compType) > buf.Length) break;
            arr[i] = compType switch
            {
                5121 => buf[p],
                5123 => BitConverter.ToUInt16(buf, p),
                5125 => BitConverter.ToUInt32(buf, p),
                5122 => (uint)Math.Max((int)BitConverter.ToInt16(buf, p), 0),
                _ => 0u,
            };
        }
        return arr;
    }
}
