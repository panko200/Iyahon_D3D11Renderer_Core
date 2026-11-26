// Portions adapted from YMM4-ObjLoader (Copyright (c) 2025 routersys, MIT License)
// and YMM43D (Copyright (c) 2026 いるかぁぁ, MIT License).
// Rewritten for streaming zero-LOH-pin parsing and material-based batching.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D.Parsers;

internal static class ObjParser
{
    private sealed class MtlEntry
    {
        public Vector4 DiffuseColor = Vector4.One;
        public Vector4 EmissiveColor = Vector4.Zero;
        public Vector3 SpecularColor = new(0.5f, 0.5f, 0.5f);
        public bool HasKs;
        public float Metallic = 0f;
        public float Roughness = -1f;
        public string? TexturePath;
        public string? NormalTexturePath;
        public float NormalScale = 1.0f;
        public string? RoughMetalTexturePath;
        public int RoughMetalPacking = 0; // 2=Roughness, 3=Metallic, 4=Glossiness
        public bool HasPr;
    }

    public static ParsedModel? Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        string fullPath = Path.GetFullPath(filePath);
        string baseDir = Path.GetDirectoryName(fullPath) ?? string.Empty;

        var rawPos = new List<Vector3>(4096);
        List<Vector4>? rawCol = null;
        var rawUv = new List<Vector2>(4096);
        var rawNorm = new List<Vector3>(4096);

        var vertices = new List<ModelVertex>(4096);
        var vertexDedup = new Dictionary<(int v, int vt, int vn), uint>(4096);
        var matBuckets = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        var matOrder = new List<string>();
        var materials = new Dictionary<string, MtlEntry>(StringComparer.Ordinal);
        var loadedMtls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool hasLibrary = false;
        bool triedSibling = false;
        string currentMat = string.Empty;
        List<uint> currentBucket = GetOrCreateBucket(matBuckets, matOrder, currentMat);

        Span<int> faceV = stackalloc int[64];
        Span<int> faceVt = stackalloc int[64];
        Span<int> faceVn = stackalloc int[64];

        const int ReadBufSize = 256 * 1024;
        byte[] buf = ArrayPool<byte>.Shared.Rent(ReadBufSize);
        int leftover = 0;

        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufSize, FileOptions.SequentialScan);

            while (true)
            {
                if (leftover == buf.Length)
                {
                    int newLen = checked(buf.Length * 2);
                    byte[] grown = ArrayPool<byte>.Shared.Rent(newLen);
                    Buffer.BlockCopy(buf, 0, grown, 0, leftover);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = grown;
                }

                int bytesRead = fs.Read(buf, leftover, buf.Length - leftover);
                int total = leftover + bytesRead;
                bool isEof = bytesRead == 0;

                int pos = 0;
                while (pos < total)
                {
                    int nl = Array.IndexOf(buf, (byte)'\n', pos, total - pos);
                    if (nl < 0)
                    {
                        if (!isEof) break;
                        nl = total;
                    }

                    var line = TrimAscii(buf.AsSpan(pos, nl - pos));
                    pos = nl + 1;

                    if (line.Length == 0 || line[0] == (byte)'#') continue;

                    // BOM スキップ（先頭行のみ）
                    if (line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF)
                    {
                        line = TrimAscii(line.Slice(3));
                        if (line.Length == 0 || line[0] == (byte)'#') continue;
                    }

                    byte c0 = line[0];
                    if (c0 == (byte)'v')
                    {
                        if (line.Length > 1 && IsSpace(line[1]))
                        {
                            ParseVertexLine(line.Slice(2), rawPos, ref rawCol);
                        }
                        else if (line.Length > 2 && line[1] == (byte)'t' && IsSpace(line[2]))
                        {
                            var s = line.Slice(3);
                            if (TryReadFloat(ref s, out float u) && TryReadFloat(ref s, out float v))
                                rawUv.Add(new Vector2(u, 1.0f - v));
                            else
                                rawUv.Add(Vector2.Zero);
                        }
                        else if (line.Length > 2 && line[1] == (byte)'n' && IsSpace(line[2]))
                        {
                            var s = line.Slice(3);
                            TryReadFloat(ref s, out float nx);
                            TryReadFloat(ref s, out float ny);
                            TryReadFloat(ref s, out float nz);
                            rawNorm.Add(new Vector3(nx, ny, nz));
                        }
                    }
                    else if (c0 == (byte)'f' && line.Length > 1 && IsSpace(line[1]))
                    {
                        var s = line.Slice(2);
                        int cornerCount = 0;
                        int vCnt = rawPos.Count;
                        int vtCnt = rawUv.Count;
                        int vnCnt = rawNorm.Count;

                        while (true)
                        {
                            SkipSpaces(ref s);
                            if (s.Length == 0) break;

                            if (TryParseFaceCorner(ref s, vCnt, vtCnt, vnCnt, out int iv, out int ivt, out int ivn))
                            {
                                if (cornerCount < faceV.Length)
                                {
                                    faceV[cornerCount] = iv;
                                    faceVt[cornerCount] = ivt;
                                    faceVn[cornerCount] = ivn;
                                    cornerCount++;
                                }
                                else
                                {
                                    // 64頂点超N角形の稀なフォールバック
                                    break;
                                }
                            }
                        }

                        if (cornerCount >= 3)
                        {
                            uint firstIdx = AddOrGetVertex(
                                faceV[0], faceVt[0], faceVn[0],
                                rawPos, rawCol, rawUv, rawNorm,
                                vertexDedup, vertices);

                            uint prevIdx = AddOrGetVertex(
                                faceV[1], faceVt[1], faceVn[1],
                                rawPos, rawCol, rawUv, rawNorm,
                                vertexDedup, vertices);

                            for (int k = 2; k < cornerCount; k++)
                            {
                                uint curIdx = AddOrGetVertex(
                                    faceV[k], faceVt[k], faceVn[k],
                                    rawPos, rawCol, rawUv, rawNorm,
                                    vertexDedup, vertices);

                                currentBucket.Add(firstIdx);
                                currentBucket.Add(prevIdx);
                                currentBucket.Add(curIdx);
                                prevIdx = curIdx;
                            }
                        }
                    }
                    else if (c0 == (byte)'u' && StartsWithKeyword(line, "usemtl"u8))
                    {
                        var rest = TrimAscii(line.Slice(6));
                        string matName = ModelLoadHelper.DetectTextEncoding(rest).GetString(rest);

                        if (!hasLibrary && !triedSibling)
                        {
                            triedSibling = true;
                            string sibling = Path.ChangeExtension(fullPath, ".mtl");
                            if (File.Exists(sibling) && loadedMtls.Add(sibling))
                            {
                                ParseMtlFile(sibling, materials);
                            }
                        }

                        if (!string.Equals(matName, currentMat, StringComparison.Ordinal))
                        {
                            currentMat = matName;
                            currentBucket = GetOrCreateBucket(matBuckets, matOrder, currentMat);
                        }
                    }
                    else if (c0 == (byte)'m' && StartsWithKeyword(line, "mtllib"u8))
                    {
                        var rest = TrimAscii(line.Slice(6));
                        string libRaw = ModelLoadHelper.DetectTextEncoding(rest).GetString(rest);
                        if (!string.IsNullOrEmpty(libRaw))
                        {
                            hasLibrary = true;
                            LoadMtlLibraries(libRaw, baseDir, materials, loadedMtls);
                        }
                    }
                }

                if (isEof) break;

                leftover = total - pos;
                if (leftover > 0)
                {
                    Buffer.BlockCopy(buf, pos, buf, 0, leftover);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        if (vertices.Count == 0) return null;

        int totalIndices = 0;
        for (int i = 0; i < matOrder.Count; i++)
        {
            totalIndices += matBuckets[matOrder[i]].Count;
        }
        if (totalIndices < 3) return null;

        var vertArr = vertices.ToArray();
        var idxArr = new uint[totalIndices];
        var parts = new List<ParsedModelPart>(matOrder.Count);

        int offset = 0;
        for (int i = 0; i < matOrder.Count; i++)
        {
            string matName = matOrder[i];
            var bucket = matBuckets[matName];
            if (bucket.Count == 0) continue;

            bucket.CopyTo(idxArr, offset);

            MtlEntry? mat = null;
            if (!string.IsNullOrEmpty(matName))
                materials.TryGetValue(matName, out mat);

            Vector4 baseCol = mat?.DiffuseColor ?? Vector4.One;
            if (baseCol.W <= 0f) baseCol.W = 1f;

            float partRoughness = mat?.Roughness ?? -1f;
            if (mat != null && !mat.HasPr && mat.HasKs &&
                MathF.Max(mat.SpecularColor.X, MathF.Max(mat.SpecularColor.Y, mat.SpecularColor.Z)) < 0.02f)
            {
                partRoughness = MathF.Max(partRoughness < 0f ? 0.90f : partRoughness, 0.90f);
            }

            parts.Add(new ParsedModelPart
            {
                Name = matName,
                IndexOffset = offset,
                IndexCount = bucket.Count,
                BaseColor = baseCol,
                EmissiveColor = mat?.EmissiveColor ?? Vector4.Zero,
                Metallic = mat?.Metallic ?? 0f,
                Roughness = partRoughness,
                TexturePath = mat?.TexturePath,
                NormalTexturePath = mat?.NormalTexturePath,
                NormalScale = mat?.NormalScale ?? 1.0f,
                RoughnessMetallicTexturePath = mat?.RoughMetalTexturePath,
                RoughMetalPacking = mat?.RoughMetalPacking ?? 0,
                ForceOpaque = mat != null && mat.DiffuseColor.W >= 0.999f && string.IsNullOrEmpty(mat.TexturePath),
            });

            offset += bucket.Count;
        }

        if (rawNorm.Count == 0)
        {
            ModelLoadHelper.CalculateNormals(vertArr, idxArr);
        }

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

    private static List<uint> GetOrCreateBucket(
        Dictionary<string, List<uint>> buckets,
        List<string> order,
        string name)
    {
        if (!buckets.TryGetValue(name, out var list))
        {
            list = new List<uint>(256);
            buckets[name] = list;
            order.Add(name);
        }
        return list;
    }

    private static uint AddOrGetVertex(
        int vIdx, int vtIdx, int vnIdx,
        List<Vector3> rawPos,
        List<Vector4>? rawCol,
        List<Vector2> rawUv,
        List<Vector3> rawNorm,
        Dictionary<(int v, int vt, int vn), uint> dedup,
        List<ModelVertex> vertices)
    {
        var key = (vIdx, vtIdx, vnIdx);
        if (dedup.TryGetValue(key, out uint existing))
            return existing;

        uint newIdx = (uint)vertices.Count;
        dedup[key] = newIdx;

        Vector3 p = (uint)vIdx < (uint)rawPos.Count ? rawPos[vIdx] : Vector3.Zero;
        Vector2 uv = (uint)vtIdx < (uint)rawUv.Count ? rawUv[vtIdx] : Vector2.Zero;
        Vector3 n = (uint)vnIdx < (uint)rawNorm.Count ? rawNorm[vnIdx] : Vector3.Zero;
        Vector4 col = rawCol != null && (uint)vIdx < (uint)rawCol.Count ? rawCol[vIdx] : Vector4.One;

        vertices.Add(new ModelVertex
        {
            Position = p,
            Normal = n,
            TexCoord = uv,
            Color = col,
        });
        return newIdx;
    }

    private static void ParseVertexLine(
        ReadOnlySpan<byte> s,
        List<Vector3> rawPos,
        ref List<Vector4>? rawCol)
    {
        TryReadFloat(ref s, out float x);
        TryReadFloat(ref s, out float y);
        TryReadFloat(ref s, out float z);
        rawPos.Add(new Vector3(x, y, z));

        if (TryReadFloat(ref s, out float r) &&
            TryReadFloat(ref s, out float g) &&
            TryReadFloat(ref s, out float b))
        {
            if (rawCol == null)
            {
                rawCol = new List<Vector4>(rawPos.Count);
                for (int i = 0; i < rawPos.Count - 1; i++) rawCol.Add(Vector4.One);
            }
            rawCol.Add(new Vector4(r, g, b, 1f));
        }
        else if (rawCol != null)
        {
            rawCol.Add(Vector4.One);
        }
    }

    private static bool TryParseFaceCorner(
        ref ReadOnlySpan<byte> s,
        int vCount, int vtCount, int vnCount,
        out int vIdx, out int vtIdx, out int vnIdx)
    {
        vIdx = -1;
        vtIdx = -1;
        vnIdx = -1;

        if (!TryReadInt(ref s, out int rawV))
        {
            while (s.Length > 0 && !IsSpace(s[0])) s = s.Slice(1);
            return false;
        }

        vIdx = rawV < 0 ? vCount + rawV : rawV - 1;

        if (s.Length > 0 && s[0] == (byte)'/')
        {
            s = s.Slice(1);
            if (s.Length > 0 && s[0] != (byte)'/' && !IsSpace(s[0]))
            {
                if (TryReadInt(ref s, out int rawVt))
                    vtIdx = rawVt < 0 ? vtCount + rawVt : rawVt - 1;
            }
            if (s.Length > 0 && s[0] == (byte)'/')
            {
                s = s.Slice(1);
                if (s.Length > 0 && !IsSpace(s[0]))
                {
                    if (TryReadInt(ref s, out int rawVn))
                        vnIdx = rawVn < 0 ? vnCount + rawVn : rawVn - 1;
                }
            }
        }

        while (s.Length > 0 && !IsSpace(s[0])) s = s.Slice(1);
        return true;
    }

    private static void LoadMtlLibraries(
        string libRaw,
        string baseDir,
        Dictionary<string, MtlEntry> materials,
        HashSet<string> loadedMtls)
    {
        string? wholePath = ModelLoadHelper.ResolveTexturePath(libRaw, baseDir);
        if (wholePath != null)
        {
            if (loadedMtls.Add(wholePath))
                ParseMtlFile(wholePath, materials);
            return;
        }

        var tokens = libRaw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string? resolved = ModelLoadHelper.ResolveTexturePath(tokens[i], baseDir);
            if (resolved != null && loadedMtls.Add(resolved))
            {
                ParseMtlFile(resolved, materials);
            }
        }
    }

    private static void ParseMtlFile(string mtlPath, Dictionary<string, MtlEntry> materials)
    {
        try
        {
            string mtlDir = Path.GetDirectoryName(mtlPath) ?? string.Empty;
            byte[] allBytes = File.ReadAllBytes(mtlPath);
            string text = ModelLoadHelper.DecodeText(allBytes);

            using var sr = new StringReader(text);
            string? line;
            MtlEntry? current = null;

            while ((line = sr.ReadLine()) != null)
            {
                string trim = line.Trim();
                if (trim.Length == 0 || trim[0] == '#') continue;

                int sp = IndexOfWhitespace(trim);
                string keyword = sp > 0 ? trim.Substring(0, sp).ToLowerInvariant() : trim.ToLowerInvariant();
                string rest = sp > 0 ? trim.Substring(sp + 1).Trim() : string.Empty;

                if (keyword == "newmtl")
                {
                    if (rest.Length > 0)
                    {
                        if (!materials.TryGetValue(rest, out current))
                        {
                            current = new MtlEntry();
                            materials[rest] = current;
                        }
                    }
                    continue;
                }

                if (current == null || rest.Length == 0) continue;

                switch (keyword)
                {
                    case "kd":
                    {
                        var p = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3 &&
                            TryParseFloat(p[0], out float r) &&
                            TryParseFloat(p[1], out float g) &&
                            TryParseFloat(p[2], out float b))
                        {
                            current.DiffuseColor.X = r;
                            current.DiffuseColor.Y = g;
                            current.DiffuseColor.Z = b;
                        }
                        break;
                    }
                    case "d":
                    {
                        var p = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 1 && TryParseFloat(p[0], out float d))
                            current.DiffuseColor.W = Math.Clamp(d, 0f, 1f);
                        break;
                    }
                    case "tr":
                    {
                        var p = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 1 && TryParseFloat(p[0], out float tr))
                            current.DiffuseColor.W = Math.Clamp(1f - tr, 0f, 1f);
                        break;
                    }
                    case "ke":
                    {
                        var p = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3 &&
                            TryParseFloat(p[0], out float r) &&
                            TryParseFloat(p[1], out float g) &&
                            TryParseFloat(p[2], out float b) &&
                            (r > 0f || g > 0f || b > 0f))
                        {
                            current.EmissiveColor = new Vector4(r, g, b, 1f);
                        }
                        break;
                    }
                    case "ks":
                    {
                        var p = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3 &&
                            TryParseFloat(p[0], out float r) &&
                            TryParseFloat(p[1], out float g) &&
                            TryParseFloat(p[2], out float b))
                        {
                            current.SpecularColor = new Vector3(r, g, b);
                            current.HasKs = true;
                        }
                        break;
                    }
                    case "pm":
                    {
                        var p = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 1 && TryParseFloat(p[0], out float pm))
                            current.Metallic = Math.Clamp(pm, 0f, 1f);
                        break;
                    }
                    case "pr":
                    {
                        var p = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 1 && TryParseFloat(p[0], out float pr))
                        {
                            current.Roughness = Math.Clamp(pr, 0.04f, 1.0f);
                            current.HasPr = true;
                        }
                        break;
                    }
                    case "ns":
                    {
                        if (!current.HasPr)
                        {
                            var p = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                            if (p.Length >= 1 && TryParseFloat(p[0], out float ns))
                            {
                                current.Roughness = Math.Clamp(MathF.Pow(2f / (MathF.Max(ns, 0f) + 2f), 0.25f), 0.06f, 1.0f);
                            }
                        }
                        break;
                    }
                    case "map_kd":
                    {
                        string cleanedTex = StripMtlTextureOptions(rest, out _);
                        current.TexturePath = ModelLoadHelper.ResolveTexturePath(cleanedTex, mtlDir);
                        break;
                    }
                    case "map_bump":
                    case "bump":
                    case "norm":
                    {
                        string cleanedTex = StripMtlTextureOptions(rest, out float? bmScale);
                        var resolved = ModelLoadHelper.ResolveTexturePath(cleanedTex, mtlDir);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            current.NormalTexturePath = resolved;
                            if (bmScale.HasValue)
                                current.NormalScale = Math.Clamp(bmScale.Value, 0f, 5f);
                        }
                        break;
                    }
                    case "map_pr":
                    {
                        string cleanedTex = StripMtlTextureOptions(rest, out _);
                        var resolved = ModelLoadHelper.ResolveTexturePath(cleanedTex, mtlDir);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            current.RoughMetalTexturePath = resolved;
                            current.RoughMetalPacking = 2; // grayscale roughness
                        }
                        break;
                    }
                    case "map_pm":
                    {
                        if (current.RoughMetalPacking == 0)
                        {
                            string cleanedTex = StripMtlTextureOptions(rest, out _);
                            var resolved = ModelLoadHelper.ResolveTexturePath(cleanedTex, mtlDir);
                            if (!string.IsNullOrEmpty(resolved))
                            {
                                current.RoughMetalTexturePath = resolved;
                                current.RoughMetalPacking = 3; // grayscale metallic
                            }
                        }
                        break;
                    }
                    case "map_ns":
                    {
                        if (current.RoughMetalPacking == 0)
                        {
                            string cleanedTex = StripMtlTextureOptions(rest, out _);
                            var resolved = ModelLoadHelper.ResolveTexturePath(cleanedTex, mtlDir);
                            if (!string.IsNullOrEmpty(resolved))
                            {
                                current.RoughMetalTexturePath = resolved;
                                current.RoughMetalPacking = 4; // grayscale glossiness/shininess
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModelLoadHelper.Log($"MTL読み込みエラー ({mtlPath}): {ex.Message}");
        }
    }

    private static string StripMtlTextureOptions(string rest, out float? bumpMultiplier)
    {
        bumpMultiplier = null;
        var tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        while (i < tokens.Length && tokens[i].StartsWith('-'))
        {
            string opt = tokens[i].ToLowerInvariant();
            int skipArgs = opt switch
            {
                "-s" or "-o" or "-t" => 3,
                "-mm" => 2,
                "-bm" or "-clamp" or "-blendu" or "-blendv" or "-boost" or "-imfchan" or "-texres" => 1,
                _ => 0,
            };
            i++;
            while (skipArgs > 0 && i < tokens.Length && TryParseFloat(tokens[i], out float fVal))
            {
                if (opt == "-bm") bumpMultiplier = fVal;
                i++;
                skipArgs--;
            }
            if (skipArgs > 0 && i < tokens.Length && (tokens[i].Equals("on", StringComparison.OrdinalIgnoreCase) || tokens[i].Equals("off", StringComparison.OrdinalIgnoreCase)))
            {
                i++;
            }
        }

        if (i >= tokens.Length) return rest;
        return string.Join(' ', tokens, i, tokens.Length - i);
    }

    private static bool TryParseFloat(string s, out float value)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i])) return i;
        }
        return -1;
    }

    private static bool IsSpace(byte b) => b <= (byte)' ';

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> span)
    {
        int s = 0;
        int e = span.Length - 1;
        while (s <= e && span[s] <= (byte)' ') s++;
        while (e >= s && span[e] <= (byte)' ') e--;
        return s <= e ? span.Slice(s, e - s + 1) : ReadOnlySpan<byte>.Empty;
    }

    private static void SkipSpaces(ref ReadOnlySpan<byte> span)
    {
        int i = 0;
        while (i < span.Length && span[i] <= (byte)' ') i++;
        span = span.Slice(i);
    }

    private static bool StartsWithKeyword(ReadOnlySpan<byte> line, ReadOnlySpan<byte> kw)
        => line.Length > kw.Length && IsSpace(line[kw.Length]) && line.Slice(0, kw.Length).SequenceEqual(kw);

    private static bool TryReadInt(ref ReadOnlySpan<byte> span, out int value)
    {
        SkipSpaces(ref span);
        if (span.Length == 0) { value = 0; return false; }

        int idx = 0;
        bool neg = false;
        if (span[0] == (byte)'-') { neg = true; idx++; }
        else if (span[0] == (byte)'+') { idx++; }

        if (idx >= span.Length || span[idx] < (byte)'0' || span[idx] > (byte)'9')
        {
            value = 0;
            return false;
        }

        int num = 0;
        while (idx < span.Length)
        {
            byte c = span[idx];
            if (c < (byte)'0' || c > (byte)'9') break;
            num = num * 10 + (c - (byte)'0');
            idx++;
        }

        span = span.Slice(idx);
        value = neg ? -num : num;
        return true;
    }

    private static bool TryReadFloat(ref ReadOnlySpan<byte> span, out float value)
    {
        SkipSpaces(ref span);
        if (span.Length == 0) { value = 0f; return false; }

        int len = 0;
        while (len < span.Length && !IsSpace(span[len])) len++;
        if (len == 0) { value = 0f; return false; }

        bool ok = System.Buffers.Text.Utf8Parser.TryParse(span.Slice(0, len), out value, out int consumed) && consumed > 0;
        span = span.Slice(len);
        return ok;
    }
}
