// Portions adapted from YMM4-ObjLoader (Copyright (c) 2025 routersys, MIT License).
// Clean parsers for .stl (binary/ASCII), .ply (binary LE/BE + ASCII), and .3mf (ZIP+XML).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Xml;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D.Parsers;

internal static class SimpleMeshParsers
{
    // ═══════════════════════════════════════════════════
    //  STL (Binary / ASCII)
    // ═══════════════════════════════════════════════════

    public static ParsedModel? ParseStl(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        byte[] bytes = File.ReadAllBytes(filePath);
        if (bytes.Length < 15) return null;

        bool looksAscii = true;
        int checkLen = Math.Min(bytes.Length, 256);
        for (int i = 0; i < checkLen; i++)
        {
            if (bytes[i] == 0) { looksAscii = false; break; }
        }
        if (looksAscii)
        {
            string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 80)).TrimStart();
            if (!head.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
                looksAscii = false;
            else if (bytes.Length >= 84)
            {
                uint triCount = BitConverter.ToUInt32(bytes, 80);
                if ((long)triCount > 0 && bytes.Length == 84L + (long)triCount * 50L)
                    looksAscii = false;
            }
        }

        return looksAscii ? ParseStlAscii(bytes) : ParseStlBinary(bytes);
    }

    private static ParsedModel? ParseStlBinary(byte[] bytes)
    {
        if (bytes.Length < 84) return null;
        int triCount = BitConverter.ToInt32(bytes, 80);
        if (triCount <= 0 || bytes.Length < 84L + (long)triCount * 50L) return null;

        var vertices = new List<ModelVertex>(triCount);
        var indices = new uint[triCount * 3];
        var dedup = new Dictionary<(Vector3 Pos, Vector3 Norm), uint>(triCount);

        int offset = 84;
        for (int t = 0; t < triCount; t++)
        {
            Vector3 n = ReadVec3LE(bytes, offset);
            Vector3 v0 = ReadVec3LE(bytes, offset + 12);
            Vector3 v1 = ReadVec3LE(bytes, offset + 24);
            Vector3 v2 = ReadVec3LE(bytes, offset + 36);
            offset += 50;

            if (n.LengthSquared() < 1e-8f)
            {
                Vector3 cross = Vector3.Cross(v1 - v0, v2 - v0);
                n = cross.LengthSquared() > 1e-12f ? Vector3.Normalize(cross) : Vector3.UnitZ;
            }
            else
            {
                n = Vector3.Normalize(n);
            }

            indices[t * 3] = AddStlVert(v0, n, dedup, vertices);
            indices[t * 3 + 1] = AddStlVert(v1, n, dedup, vertices);
            indices[t * 3 + 2] = AddStlVert(v2, n, dedup, vertices);
        }

        var vArr = vertices.ToArray();
        ModelLoadHelper.CalculateBounds(vArr, out var bMin, out var bMax, out var center, out float scale);
        return new ParsedModel
        {
            Vertices = vArr,
            Indices = indices,
            Parts = new List<ParsedModelPart>
            {
                new() { Name = "STL", IndexOffset = 0, IndexCount = indices.Length, BaseColor = Vector4.One, ForceOpaque = true },
            },
            BoundsMin = bMin,
            BoundsMax = bMax,
            ModelCenter = center,
            ModelScale = scale,
        };
    }

    private static ParsedModel? ParseStlAscii(byte[] bytes)
    {
        var vertices = new List<ModelVertex>(1024);
        var indices = new List<uint>(2048);
        var dedup = new Dictionary<(Vector3 Pos, Vector3 Norm), uint>(1024);

        using var ms = new MemoryStream(bytes, writable: false);
        using var sr = new StreamReader(ms, Encoding.ASCII);

        Vector3 curNorm = Vector3.UnitZ;
        Span<uint> triIdx = stackalloc uint[3];
        int triPos = 0;

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            if (parts.Length >= 5 && parts[0] == "facet" && parts[1] == "normal")
            {
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float nx);
                float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float ny);
                float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float nz);
                var n = new Vector3(nx, ny, nz);
                curNorm = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitZ;
                triPos = 0;
            }
            else if (parts.Length >= 4 && parts[0] == "vertex")
            {
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
                float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z);
                uint idx = AddStlVert(new Vector3(x, y, z), curNorm, dedup, vertices);
                if (triPos < 3) triIdx[triPos++] = idx;
                if (triPos == 3)
                {
                    indices.Add(triIdx[0]); indices.Add(triIdx[1]); indices.Add(triIdx[2]);
                    triPos = 0;
                }
            }
        }

        if (vertices.Count == 0 || indices.Count < 3) return null;
        var vArr = vertices.ToArray();
        var iArr = indices.ToArray();
        ModelLoadHelper.CalculateBounds(vArr, out var bMin, out var bMax, out var center, out float scale);

        return new ParsedModel
        {
            Vertices = vArr,
            Indices = iArr,
            Parts = new List<ParsedModelPart>
            {
                new() { Name = "STL", IndexOffset = 0, IndexCount = iArr.Length, BaseColor = Vector4.One, ForceOpaque = true },
            },
            BoundsMin = bMin,
            BoundsMax = bMax,
            ModelCenter = center,
            ModelScale = scale,
        };
    }

    private static uint AddStlVert(
        in Vector3 pos, in Vector3 norm,
        Dictionary<(Vector3 Pos, Vector3 Norm), uint> dedup,
        List<ModelVertex> vertices)
    {
        var key = (pos, norm);
        if (dedup.TryGetValue(key, out uint existing)) return existing;
        uint id = (uint)vertices.Count;
        dedup[key] = id;
        vertices.Add(new ModelVertex { Position = pos, Normal = norm, TexCoord = Vector2.Zero, Color = Vector4.One });
        return id;
    }

    private static Vector3 ReadVec3LE(byte[] b, int off)
        => new(BitConverter.ToSingle(b, off), BitConverter.ToSingle(b, off + 4), BitConverter.ToSingle(b, off + 8));

    // ═══════════════════════════════════════════════════
    //  PLY (ASCII / Binary LE / Binary BE)
    // ═══════════════════════════════════════════════════

    private enum PlyFormat { Ascii, BinaryLE, BinaryBE }

    private sealed class PlyProp
    {
        public string Name = string.Empty;
        public string Type = string.Empty;
        public bool IsList;
        public string CountType = string.Empty;
    }

    public static ParsedModel? ParsePly(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        string fullPath = Path.GetFullPath(filePath);
        string baseDir = Path.GetDirectoryName(fullPath) ?? string.Empty;

        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);

        var headerLines = new List<string>();
        var lineBuf = new List<byte>(128);
        bool foundEndHeader = false;

        int b;
        while ((b = fs.ReadByte()) != -1)
        {
            if (b == '\n')
            {
                string ln = Encoding.ASCII.GetString(lineBuf.ToArray()).Trim('\r', ' ', '\t');
                lineBuf.Clear();
                headerLines.Add(ln);
                if (ln == "end_header") { foundEndHeader = true; break; }
            }
            else
            {
                lineBuf.Add((byte)b);
            }
        }

        if (!foundEndHeader || headerLines.Count == 0 || headerLines[0] != "ply")
            return null;

        PlyFormat fmt = PlyFormat.Ascii;
        int vertCount = 0;
        int faceCount = 0;
        string currentElement = string.Empty;
        string? texFile = null;
        var vertProps = new List<PlyProp>();
        var faceProps = new List<PlyProp>();

        for (int i = 1; i < headerLines.Count; i++)
        {
            var tokens = headerLines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            if (tokens[0] == "format" && tokens.Length >= 2)
            {
                fmt = tokens[1] switch
                {
                    "binary_little_endian" => PlyFormat.BinaryLE,
                    "binary_big_endian" => PlyFormat.BinaryBE,
                    _ => PlyFormat.Ascii,
                };
            }
            else if (tokens[0] == "comment" && tokens.Length >= 3 && tokens[1].Equals("TextureFile", StringComparison.OrdinalIgnoreCase))
            {
                texFile = ModelLoadHelper.ResolveTexturePath(string.Join(' ', tokens, 2, tokens.Length - 2), baseDir);
            }
            else if (tokens[0] == "element" && tokens.Length >= 3)
            {
                currentElement = tokens[1];
                int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cnt);
                if (currentElement == "vertex") vertCount = cnt;
                else if (currentElement == "face") faceCount = cnt;
            }
            else if (tokens[0] == "property")
            {
                PlyProp prop;
                if (tokens.Length >= 5 && tokens[1] == "list")
                {
                    prop = new PlyProp { IsList = true, CountType = tokens[2], Type = tokens[3], Name = tokens[4] };
                }
                else if (tokens.Length >= 3)
                {
                    prop = new PlyProp { Type = tokens[1], Name = tokens[2] };
                }
                else continue;

                if (currentElement == "vertex") vertProps.Add(prop);
                else if (currentElement == "face") faceProps.Add(prop);
            }
        }

        if (vertCount <= 0) return null;

        var vertices = new ModelVertex[vertCount];
        var indices = new List<uint>(Math.Max(faceCount * 3, 256));
        bool hasNormals = vertProps.Exists(p => p.Name is "nx" or "ny" or "nz");

        if (fmt == PlyFormat.Ascii)
        {
            using var sr = new StreamReader(fs, Encoding.ASCII);
            for (int v = 0; v < vertCount; v++)
            {
                string? ln = sr.ReadLine();
                if (ln == null) break;
                var cols = ln.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                Vector3 pos = Vector3.Zero, norm = Vector3.Zero;
                Vector2 uv = Vector2.Zero;
                Vector4 col = Vector4.One;

                for (int p = 0; p < vertProps.Count && p < cols.Length; p++)
                {
                    float.TryParse(cols[p], NumberStyles.Float, CultureInfo.InvariantCulture, out float val);
                    bool isIntCol = vertProps[p].Type is "uchar" or "uint8" or "char" or "int8";
                    AssignPlyProp(vertProps[p].Name, val, isIntCol, ref pos, ref norm, ref uv, ref col);
                }
                vertices[v] = new ModelVertex { Position = pos, Normal = norm, TexCoord = uv, Color = col };
            }

            for (int f = 0; f < faceCount; f++)
            {
                string? ln = sr.ReadLine();
                if (ln == null) break;
                var cols = ln.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 4) continue;
                int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cnt);
                if (cnt < 3 || cols.Length < 1 + cnt) continue;

                uint.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint i0);
                uint.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint i1);
                for (int k = 3; k <= cnt; k++)
                {
                    uint.TryParse(cols[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint i2);
                    if (i0 < (uint)vertCount && i1 < (uint)vertCount && i2 < (uint)vertCount)
                    {
                        indices.Add(i0); indices.Add(i1); indices.Add(i2);
                    }
                    i1 = i2;
                }
            }
        }
        else
        {
            bool be = fmt == PlyFormat.BinaryBE;
            using var br = new BinaryReader(fs);
            for (int v = 0; v < vertCount; v++)
            {
                Vector3 pos = Vector3.Zero, norm = Vector3.Zero;
                Vector2 uv = Vector2.Zero;
                Vector4 col = Vector4.One;

                for (int p = 0; p < vertProps.Count; p++)
                {
                    double val = ReadPlyScalar(br, vertProps[p].Type, be);
                    bool isIntCol = vertProps[p].Type is "uchar" or "uint8" or "char" or "int8";
                    AssignPlyProp(vertProps[p].Name, (float)val, isIntCol, ref pos, ref norm, ref uv, ref col);
                }
                vertices[v] = new ModelVertex { Position = pos, Normal = norm, TexCoord = uv, Color = col };
            }

            for (int f = 0; f < faceCount; f++)
            {
                for (int p = 0; p < faceProps.Count; p++)
                {
                    var fp = faceProps[p];
                    if (fp.IsList)
                    {
                        int cnt = (int)ReadPlyScalar(br, fp.CountType, be);
                        if (fp.Name is "vertex_indices" or "vertex_index" && cnt >= 3)
                        {
                            uint i0 = (uint)ReadPlyScalar(br, fp.Type, be);
                            uint i1 = (uint)ReadPlyScalar(br, fp.Type, be);
                            for (int k = 2; k < cnt; k++)
                            {
                                uint i2 = (uint)ReadPlyScalar(br, fp.Type, be);
                                if (i0 < (uint)vertCount && i1 < (uint)vertCount && i2 < (uint)vertCount)
                                {
                                    indices.Add(i0); indices.Add(i1); indices.Add(i2);
                                }
                                i1 = i2;
                            }
                        }
                        else
                        {
                            for (int k = 0; k < cnt; k++) ReadPlyScalar(br, fp.Type, be);
                        }
                    }
                    else
                    {
                        ReadPlyScalar(br, fp.Type, be);
                    }
                }
            }
        }

        if (indices.Count < 3) return null;
        var idxArr = indices.ToArray();
        if (!hasNormals) ModelLoadHelper.CalculateNormals(vertices, idxArr);
        ModelLoadHelper.CalculateBounds(vertices, out var bMin, out var bMax, out var center, out float scale);

        return new ParsedModel
        {
            Vertices = vertices,
            Indices = idxArr,
            Parts = new List<ParsedModelPart>
            {
                new()
                {
                    Name = "PLY",
                    IndexOffset = 0,
                    IndexCount = idxArr.Length,
                    BaseColor = Vector4.One,
                    TexturePath = texFile,
                    ForceOpaque = texFile == null,
                },
            },
            BoundsMin = bMin,
            BoundsMax = bMax,
            ModelCenter = center,
            ModelScale = scale,
        };
    }

    private static void AssignPlyProp(
        string name, float val, bool isByteCol,
        ref Vector3 pos, ref Vector3 norm, ref Vector2 uv, ref Vector4 col)
    {
        switch (name)
        {
            case "x": pos.X = val; break;
            case "y": pos.Y = val; break;
            case "z": pos.Z = val; break;
            case "nx": norm.X = val; break;
            case "ny": norm.Y = val; break;
            case "nz": norm.Z = val; break;
            case "u" or "s" or "texture_u": uv.X = val; break;
            case "v" or "t" or "texture_v": uv.Y = 1.0f - val; break;
            case "red" or "r": col.X = isByteCol ? val / 255f : val; break;
            case "green" or "g": col.Y = isByteCol ? val / 255f : val; break;
            case "blue" or "b": col.Z = isByteCol ? val / 255f : val; break;
            case "alpha" or "a": col.W = isByteCol ? val / 255f : val; break;
        }
    }

    private static double ReadPlyScalar(BinaryReader br, string type, bool be)
    {
        Span<byte> buf = stackalloc byte[8];
        switch (type)
        {
            case "char" or "int8": return br.ReadSByte();
            case "uchar" or "uint8": return br.ReadByte();
            case "short" or "int16":
                br.BaseStream.ReadExactly(buf.Slice(0, 2));
                if (be) buf.Slice(0, 2).Reverse();
                return BitConverter.ToInt16(buf);
            case "ushort" or "uint16":
                br.BaseStream.ReadExactly(buf.Slice(0, 2));
                if (be) buf.Slice(0, 2).Reverse();
                return BitConverter.ToUInt16(buf);
            case "int" or "int32":
                br.BaseStream.ReadExactly(buf.Slice(0, 4));
                if (be) buf.Slice(0, 4).Reverse();
                return BitConverter.ToInt32(buf);
            case "uint" or "uint32":
                br.BaseStream.ReadExactly(buf.Slice(0, 4));
                if (be) buf.Slice(0, 4).Reverse();
                return BitConverter.ToUInt32(buf);
            case "float" or "float32":
                br.BaseStream.ReadExactly(buf.Slice(0, 4));
                if (be) buf.Slice(0, 4).Reverse();
                return BitConverter.ToSingle(buf);
            case "double" or "float64":
                br.BaseStream.ReadExactly(buf.Slice(0, 8));
                if (be) buf.Reverse();
                return BitConverter.ToDouble(buf);
            default:
                return 0.0;
        }
    }

    // ═══════════════════════════════════════════════════
    //  3MF (ZIP + XML)
    // ═══════════════════════════════════════════════════

    public static ParsedModel? Parse3mf(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        using var archive = ZipFile.OpenRead(filePath);
        var modelEntry = archive.GetEntry("3D/3dmodel.model");
        if (modelEntry == null)
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
                {
                    modelEntry = entry;
                    break;
                }
            }
        }
        if (modelEntry == null) return null;

        using var stream = modelEntry.Open();
        using var reader = XmlReader.Create(stream);

        var verts = new List<ModelVertex>(2048);
        var colorMap = new Dictionary<string, Vector4>(StringComparer.Ordinal);
        var groupedIndices = new Dictionary<Vector4, List<uint>>();

        string currentPid = string.Empty;
        int resIdx = 0;
        string objPid = string.Empty;
        string objP1 = string.Empty;
        int vertexOffset = 0;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "basematerials" or "colorgroup":
                        currentPid = reader.GetAttribute("id") ?? string.Empty;
                        resIdx = 0;
                        break;

                    case "base":
                    {
                        string val = reader.GetAttribute("displaycolor") ?? "#FFFFFFFF";
                        if (TryParseHexColor(val, out var col)) colorMap[$"{currentPid}:{resIdx}"] = col;
                        resIdx++;
                        break;
                    }
                    case "color":
                    {
                        string val = reader.GetAttribute("color") ?? "#FFFFFFFF";
                        if (TryParseHexColor(val, out var col)) colorMap[$"{currentPid}:{resIdx}"] = col;
                        resIdx++;
                        break;
                    }
                    case "object":
                        objPid = reader.GetAttribute("pid") ?? string.Empty;
                        objP1 = reader.GetAttribute("p1") ?? string.Empty;
                        break;

                    case "mesh":
                        vertexOffset = verts.Count;
                        break;

                    case "vertex":
                    {
                        float.TryParse(reader.GetAttribute("x"), NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
                        float.TryParse(reader.GetAttribute("y"), NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
                        float.TryParse(reader.GetAttribute("z"), NumberStyles.Float, CultureInfo.InvariantCulture, out float z);
                        verts.Add(new ModelVertex { Position = new Vector3(x, z, -y), Color = Vector4.One });
                        break;
                    }
                    case "triangle":
                    {
                        int.TryParse(reader.GetAttribute("v1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r1);
                        int.TryParse(reader.GetAttribute("v2"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r2);
                        int.TryParse(reader.GetAttribute("v3"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r3);
                        uint v1 = (uint)(r1 + vertexOffset);
                        uint v2 = (uint)(r2 + vertexOffset);
                        uint v3 = (uint)(r3 + vertexOffset);

                        string? pid = reader.GetAttribute("pid") ?? objPid;
                        string? p1 = reader.GetAttribute("p1") ?? (string.IsNullOrEmpty(reader.GetAttribute("pid")) ? objP1 : string.Empty);

                        Vector4 triCol = Vector4.One;
                        if (!string.IsNullOrEmpty(pid) && !string.IsNullOrEmpty(p1) && colorMap.TryGetValue($"{pid}:{p1}", out var foundCol))
                            triCol = foundCol;

                        if (!groupedIndices.TryGetValue(triCol, out var bucket))
                            groupedIndices[triCol] = bucket = new List<uint>(256);

                        if (v1 < (uint)verts.Count && v2 < (uint)verts.Count && v3 < (uint)verts.Count)
                        {
                            bucket.Add(v1); bucket.Add(v2); bucket.Add(v3);
                        }
                        break;
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "object")
            {
                objPid = string.Empty;
                objP1 = string.Empty;
            }
        }

        if (verts.Count == 0 || groupedIndices.Count == 0) return null;

        var vArr = verts.ToArray();
        var allIndices = new List<uint>(verts.Count * 3);
        var parts = new List<ParsedModelPart>(groupedIndices.Count);

        foreach (var (col, list) in groupedIndices)
        {
            if (list.Count == 0) continue;
            parts.Add(new ParsedModelPart
            {
                Name = $"3MF_{parts.Count}",
                IndexOffset = allIndices.Count,
                IndexCount = list.Count,
                BaseColor = col,
                ForceOpaque = col.W >= 0.999f,
            });
            allIndices.AddRange(list);
        }

        var iArr = allIndices.ToArray();
        if (iArr.Length < 3) return null;

        ModelLoadHelper.CalculateNormals(vArr, iArr);
        ModelLoadHelper.CalculateBounds(vArr, out var bMin, out var bMax, out var center, out float scale);

        return new ParsedModel
        {
            Vertices = vArr,
            Indices = iArr,
            Parts = parts,
            BoundsMin = bMin,
            BoundsMax = bMax,
            ModelCenter = center,
            ModelScale = scale,
        };
    }

    private static bool TryParseHexColor(string hex, out Vector4 color)
    {
        color = Vector4.One;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string clean = hex.StartsWith('#') ? hex.Substring(1) : hex;
        if (clean.Length is not (6 or 8)) return false;

        try
        {
            byte r = Convert.ToByte(clean.Substring(0, 2), 16);
            byte g = Convert.ToByte(clean.Substring(2, 2), 16);
            byte b = Convert.ToByte(clean.Substring(4, 2), 16);
            byte a = clean.Length == 8 ? Convert.ToByte(clean.Substring(6, 2), 16) : (byte)255;
            color = new Vector4(r / 255f, g / 255f, b / 255f, a / 255f);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
