// Portions adapted from YMM43D (Copyright (c) 2026 いるかぁぁ, MIT License).
// Extracts static geometry, transform hierarchy, materials, and external/embedded textures cleanly.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D.Parsers;

internal static class FbxParser
{
    private const string BinaryMagic = "Kaydara FBX Binary  ";
    private const int MaxDepth = 64;

    private sealed class FbxNode
    {
        public string Name { get; }
        public object?[] Properties { get; }
        public List<FbxNode> Children { get; } = new();

        public FbxNode(string name, object?[] properties)
        {
            Name = name;
            Properties = properties;
        }

        public FbxNode? Child(string name)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i].Name == name) return Children[i];
            }
            return null;
        }

        public string Text(int index)
            => (uint)index < (uint)Properties.Length && Properties[index] is byte[] bytes
                ? Encoding.UTF8.GetString(bytes)
                : string.Empty;

        public long Number(int index) => (uint)index < (uint)Properties.Length && Properties[index] != null
            ? Properties[index] switch
            {
                long n => n,
                int n => n,
                short n => n,
                sbyte n => n,
                double n => (long)n,
                float n => (long)n,
                _ => 0L,
            }
            : 0L;

        public double[] DoublesOf(string name)
            => Child(name) is { } c && c.Properties.Length > 0 && c.Properties[0] is double[] d ? d : Array.Empty<double>();

        public int[] IntegersOf(string name)
        {
            var c = Child(name);
            if (c == null || c.Properties.Length == 0) return Array.Empty<int>();
            if (c.Properties[0] is int[] iArr) return iArr;
            if (c.Properties[0] is long[] lArr)
            {
                var res = new int[lArr.Length];
                for (int i = 0; i < lArr.Length; i++) res[i] = (int)lArr[i];
                return res;
            }
            return Array.Empty<int>();
        }

        public string TextOf(string name) => Child(name)?.Text(0) ?? string.Empty;
    }

    public static ParsedModel? Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        string fullPath = Path.GetFullPath(filePath);
        string baseDir = Path.GetDirectoryName(fullPath) ?? string.Empty;

        var root = LoadBinaryTree(fullPath);
        if (root == null) return null;

        var objects = new Dictionary<long, FbxNode>();
        var childrenOf = new Dictionary<long, List<long>>();
        var parentsOf = new Dictionary<long, List<long>>();
        var propertiesOf = new Dictionary<long, List<(long Child, string Property)>>();

        var objNode = root.Child("Objects");
        var connNode = root.Child("Connections");
        if (objNode == null || connNode == null) return null;

        foreach (var node in objNode.Children)
        {
            objects[node.Number(0)] = node;
        }

        foreach (var link in connNode.Children)
        {
            if (link.Name != "C") continue;
            string kind = link.Text(0);
            long child = link.Number(1);
            long parent = link.Number(2);

            if (kind == "OO")
            {
                if (!childrenOf.TryGetValue(parent, out var list))
                    childrenOf[parent] = list = new();
                list.Add(child);

                if (!parentsOf.TryGetValue(child, out var pList))
                    parentsOf[child] = pList = new();
                pList.Add(parent);
            }
            else if (kind == "OP")
            {
                if (!propertiesOf.TryGetValue(parent, out var list))
                    propertiesOf[parent] = list = new();
                list.Add((child, link.Text(3)));
            }
        }

        Matrix4x4 axisMatrix = ReadAxisMatrix(root);
        var worldCache = new Dictionary<long, Matrix4x4>();

        var vertices = new List<ModelVertex>(4096);
        var indices = new List<uint>(8192);
        var parts = new List<ParsedModelPart>();

        foreach (var (id, node) in objects)
        {
            if (node.Name != "Model") continue;
            if (!childrenOf.TryGetValue(id, out var modelChildren)) continue;

            foreach (long childId in modelChildren)
            {
                if (objects.TryGetValue(childId, out var geometry) && geometry.Name == "Geometry")
                {
                    Matrix4x4 world = GeometricMatrix(node) * GetWorldMatrix(id, objects, parentsOf, worldCache) * axisMatrix;
                    AppendGeometry(
                        geometry, id, world,
                        objects, childrenOf, propertiesOf, baseDir, fullPath,
                        vertices, indices, parts);
                }
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

    private static FbxNode? LoadBinaryTree(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 27 || Encoding.ASCII.GetString(bytes, 0, BinaryMagic.Length) != BinaryMagic)
        {
            ModelLoadHelper.Log($"テキスト形式のFBXは未対応です（バイナリFBXのみ対応）: {path}");
            return null;
        }

        uint version = BitConverter.ToUInt32(bytes, 23);
        bool wide = version >= 7500;
        var root = new FbxNode(string.Empty, Array.Empty<object?>());
        int at = 27;

        while (ReadNode(bytes, ref at, wide, 0) is { } node)
        {
            root.Children.Add(node);
        }
        return root;
    }

    private static FbxNode? ReadNode(byte[] bytes, ref int at, bool wide, int depth)
    {
        if (depth > MaxDepth) return null;
        int headerSize = wide ? 25 : 13;
        if (at + headerSize > bytes.Length) return null;

        long end, propertyCount, propertyLength;
        if (wide)
        {
            end = BitConverter.ToInt64(bytes, at);
            propertyCount = BitConverter.ToInt64(bytes, at + 8);
            propertyLength = BitConverter.ToInt64(bytes, at + 16);
            at += 24;
        }
        else
        {
            end = BitConverter.ToUInt32(bytes, at);
            propertyCount = BitConverter.ToUInt32(bytes, at + 4);
            propertyLength = BitConverter.ToUInt32(bytes, at + 8);
            at += 12;
        }

        byte nameLength = bytes[at++];
        if (end == 0 && propertyCount == 0 && propertyLength == 0 && nameLength == 0)
            return null;

        if (end <= at || end > bytes.Length || at + nameLength > end)
            return null;

        string name = Encoding.UTF8.GetString(bytes, at, nameLength);
        at += nameLength;

        var properties = new object?[(int)Math.Min(propertyCount, 1024L)];
        for (int i = 0; i < properties.Length; i++)
        {
            if (at >= end) break;
            properties[i] = ReadProperty(bytes, ref at, (int)end);
        }

        var node = new FbxNode(name, properties);
        while (at <= end - headerSize)
        {
            var child = ReadNode(bytes, ref at, wide, depth + 1);
            if (child == null) break;
            node.Children.Add(child);
        }

        at = (int)end;
        return node;
    }

    private static object? ReadProperty(byte[] bytes, ref int at, int limit)
    {
        if (at >= limit) return null;
        char type = (char)bytes[at++];

        switch (type)
        {
            case 'Y': if (at + 2 > limit) return null; short sv = BitConverter.ToInt16(bytes, at); at += 2; return sv;
            case 'C': return at < limit && bytes[at++] != 0;
            case 'I': if (at + 4 > limit) return null; int iv = BitConverter.ToInt32(bytes, at); at += 4; return iv;
            case 'F': if (at + 4 > limit) return null; float fv = BitConverter.ToSingle(bytes, at); at += 4; return fv;
            case 'D': if (at + 8 > limit) return null; double dv = BitConverter.ToDouble(bytes, at); at += 8; return dv;
            case 'L': if (at + 8 > limit) return null; long lv = BitConverter.ToInt64(bytes, at); at += 8; return lv;
            case 'S':
            case 'R':
            {
                if (at + 4 > limit) return null;
                int len = BitConverter.ToInt32(bytes, at);
                at += 4;
                if (len < 0 || at + len > limit) return null;
                byte[] sub = new byte[len];
                Buffer.BlockCopy(bytes, at, sub, 0, len);
                at += len;
                return sub;
            }
            case 'f': return ToDoubles(ReadArray(bytes, ref at, limit, 4), 4);
            case 'd': return ToDoubles(ReadArray(bytes, ref at, limit, 8), 8);
            case 'i': return ToIntegers(ReadArray(bytes, ref at, limit, 4), 4);
            case 'l': return ToLongs(ReadArray(bytes, ref at, limit, 8));
            case 'b': return ToIntegers(ReadArray(bytes, ref at, limit, 1), 1);
            default:
                at = limit;
                return null;
        }
    }

    private static byte[] ReadArray(byte[] bytes, ref int at, int limit, int elemSize)
    {
        if (at + 12 > limit) { at = limit; return Array.Empty<byte>(); }
        int count = BitConverter.ToInt32(bytes, at);
        int encoding = BitConverter.ToInt32(bytes, at + 4);
        int compLen = BitConverter.ToInt32(bytes, at + 8);
        at += 12;

        if (compLen < 0 || at + compLen > limit) { at = limit; return Array.Empty<byte>(); }
        if (encoding == 0)
        {
            byte[] raw = new byte[compLen];
            Buffer.BlockCopy(bytes, at, raw, 0, compLen);
            at += compLen;
            return raw;
        }

        using var source = new MemoryStream(bytes, at, compLen, writable: false);
        at += compLen;
        using var inflate = new ZLibStream(source, CompressionMode.Decompress);
        int expected = Math.Max(0, count * elemSize);
        using var target = new MemoryStream(expected);
        inflate.CopyTo(target);
        return target.ToArray();
    }

    private static double[] ToDoubles(byte[] data, int elemSize)
    {
        int count = data.Length / elemSize;
        var res = new double[count];
        for (int i = 0; i < count; i++)
            res[i] = elemSize == 4 ? BitConverter.ToSingle(data, i * 4) : BitConverter.ToDouble(data, i * 8);
        return res;
    }

    private static long[] ToLongs(byte[] data)
    {
        int count = data.Length / 8;
        var res = new long[count];
        for (int i = 0; i < count; i++)
            res[i] = BitConverter.ToInt64(data, i * 8);
        return res;
    }

    private static int[] ToIntegers(byte[] data, int elemSize)
    {
        int count = data.Length / elemSize;
        var res = new int[count];
        for (int i = 0; i < count; i++)
        {
            res[i] = elemSize switch
            {
                1 => data[i],
                4 => BitConverter.ToInt32(data, i * 4),
                _ => (int)BitConverter.ToInt64(data, i * 8),
            };
        }
        return res;
    }

    private static Matrix4x4 ReadAxisMatrix(FbxNode root)
    {
        var props = root.Child("GlobalSettings")?.Child("Properties70");
        if (props == null) return Matrix4x4.Identity;

        int up = (int)PropDouble(props, "UpAxis", 1);
        bool flipSign = PropDouble(props, "UpAxisSign", 1) < 0;

        return (up, flipSign) switch
        {
            (0, false) => new Matrix4x4(0, 1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 1),
            (0, true) => new Matrix4x4(0, -1, 0, 0, 0, 0, 1, 0, -1, 0, 0, 0, 0, 0, 0, 1),
            (2, false) => new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1),
            (2, true) => new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1),
            (_, true) => new Matrix4x4(1, 0, 0, 0, 0, -1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1),
            _ => Matrix4x4.Identity,
        };
    }

    private static Matrix4x4 GetWorldMatrix(
        long id,
        Dictionary<long, FbxNode> objects,
        Dictionary<long, List<long>> parentsOf,
        Dictionary<long, Matrix4x4> cache)
    {
        if (cache.TryGetValue(id, out var cached)) return cached;
        cache[id] = Matrix4x4.Identity;

        Matrix4x4 local = objects.TryGetValue(id, out var node) ? LocalMatrix(node) : Matrix4x4.Identity;
        long parentId = 0;
        if (parentsOf.TryGetValue(id, out var ps))
        {
            foreach (long p in ps)
            {
                if (objects.TryGetValue(p, out var pn) && pn.Name == "Model")
                {
                    parentId = p;
                    break;
                }
            }
        }

        Matrix4x4 world = parentId != 0 ? local * GetWorldMatrix(parentId, objects, parentsOf, cache) : local;
        return cache[id] = world;
    }

    private static Matrix4x4 LocalMatrix(FbxNode node)
    {
        var props = node.Child("Properties70");
        if (props == null) return Matrix4x4.Identity;

        int order = (int)PropDouble(props, "RotationOrder", 0);
        Vector3 sPivot = PropVector(props, "ScalingPivot", Vector3.Zero);
        Vector3 sOff = PropVector(props, "ScalingOffset", Vector3.Zero);
        Vector3 rPivot = PropVector(props, "RotationPivot", Vector3.Zero);
        Vector3 rOff = PropVector(props, "RotationOffset", Vector3.Zero);
        Matrix4x4 preRot = EulerTurn(PropVector(props, "PreRotation", Vector3.Zero), order);
        Matrix4x4 postRot = EulerTurn(PropVector(props, "PostRotation", Vector3.Zero), order);
        Matrix4x4.Invert(postRot, out var postInv);

        Vector3 t = PropVector(props, "Lcl Translation", Vector3.Zero);
        Vector3 r = PropVector(props, "Lcl Rotation", Vector3.Zero);
        Vector3 s = PropVector(props, "Lcl Scaling", Vector3.One);

        return Matrix4x4.CreateTranslation(-sPivot)
             * Matrix4x4.CreateScale(s)
             * Matrix4x4.CreateTranslation(sPivot + sOff - rPivot)
             * postInv
             * EulerTurn(r, order)
             * preRot
             * Matrix4x4.CreateTranslation(rPivot + rOff + t);
    }

    private static Matrix4x4 GeometricMatrix(FbxNode node)
    {
        var props = node.Child("Properties70");
        if (props == null) return Matrix4x4.Identity;

        int order = (int)PropDouble(props, "RotationOrder", 0);
        Vector3 t = PropVector(props, "GeometricTranslation", Vector3.Zero);
        Vector3 r = PropVector(props, "GeometricRotation", Vector3.Zero);
        Vector3 s = PropVector(props, "GeometricScaling", Vector3.One);

        return Matrix4x4.CreateScale(s) * EulerTurn(r, order) * Matrix4x4.CreateTranslation(t);
    }

    private static Matrix4x4 EulerTurn(in Vector3 deg, int order)
    {
        var x = Matrix4x4.CreateRotationX(deg.X * (MathF.PI / 180f));
        var y = Matrix4x4.CreateRotationY(deg.Y * (MathF.PI / 180f));
        var z = Matrix4x4.CreateRotationZ(deg.Z * (MathF.PI / 180f));

        return order switch
        {
            1 => x * z * y,
            2 => y * z * x,
            3 => y * x * z,
            4 => z * x * y,
            5 => z * y * x,
            _ => x * y * z,
        };
    }

    private static FbxNode? FindProp(FbxNode props70, string name)
    {
        for (int i = 0; i < props70.Children.Count; i++)
        {
            var c = props70.Children[i];
            if (c.Name == "P" && c.Text(0) == name) return c;
        }
        return null;
    }

    private static double PropAsDouble(FbxNode node, int idx)
        => (uint)idx < (uint)node.Properties.Length && node.Properties[idx] != null
            ? node.Properties[idx] switch
            {
                double v => v,
                float v => v,
                int v => v,
                long v => v,
                short v => v,
                _ => 0d,
            }
            : 0d;

    private static double PropDouble(FbxNode props70, string name, double fallback)
        => FindProp(props70, name) is { } p && p.Properties.Length > 4 ? PropAsDouble(p, 4) : fallback;

    private static Vector3 PropVector(FbxNode props70, string name, Vector3 fallback)
        => FindProp(props70, name) is { } p && p.Properties.Length >= 7
            ? new Vector3((float)PropAsDouble(p, 4), (float)PropAsDouble(p, 5), (float)PropAsDouble(p, 6))
            : fallback;

    private readonly struct LayerData
    {
        public readonly double[] Values;
        public readonly int[] Indices;
        public readonly bool PerPoint;
        public readonly int Width;
        public bool HasValues => Values.Length > 0;

        public LayerData(double[] values, int[] indices, bool perPoint, int width)
        {
            Values = values; Indices = indices; PerPoint = perPoint; Width = width;
        }

        public int IndexOf(int corner, int point)
        {
            if (!HasValues) return -1;
            int at = PerPoint ? point : corner;
            return Indices.Length > 0 ? ((uint)at < (uint)Indices.Length ? Indices[at] : -1) : at;
        }

        public Vector3 Vec3(int index)
        {
            int at = index * Width;
            return index < 0 || at + 2 >= Values.Length
                ? Vector3.Zero
                : new Vector3((float)Values[at], (float)Values[at + 1], (float)Values[at + 2]);
        }

        public Vector2 Vec2(int index)
        {
            int at = index * Width;
            return index < 0 || at + 1 >= Values.Length
                ? Vector2.Zero
                : new Vector2((float)Values[at], (float)Values[at + 1]);
        }
    }

    private static LayerData ReadLayer(FbxNode geo, string layerName, string valName, string idxName, int width)
    {
        var layer = geo.Child(layerName);
        if (layer == null) return new LayerData(Array.Empty<double>(), Array.Empty<int>(), false, width);

        string mapping = layer.TextOf("MappingInformationType");
        string reference = layer.TextOf("ReferenceInformationType");
        double[] values = layer.DoublesOf(valName);
        int[] indices = reference.StartsWith("IndexToDirect", StringComparison.Ordinal)
            ? layer.IntegersOf(idxName)
            : Array.Empty<int>();
        bool perPoint = mapping is "ByVertice" or "ByVertex" or "ByControlPoint";
        return new LayerData(values, indices, perPoint, width);
    }

    private static void AppendGeometry(
        FbxNode geometry,
        long modelId,
        in Matrix4x4 world,
        Dictionary<long, FbxNode> objects,
        Dictionary<long, List<long>> childrenOf,
        Dictionary<long, List<(long Child, string Property)>> propertiesOf,
        string baseDir,
        string modelFullPath,
        List<ModelVertex> vertices,
        List<uint> indices,
        List<ParsedModelPart> parts)
    {
        double[] positions = geometry.DoublesOf("Vertices");
        int[] polygons = geometry.IntegersOf("PolygonVertexIndex");
        if (positions.Length < 3 || polygons.Length < 3) return;

        int pointCount = positions.Length / 3;
        Matrix4x4 normalMat = Matrix4x4.Invert(world, out var inv) ? Matrix4x4.Transpose(inv) : Matrix4x4.Identity;

        var normals = ReadLayer(geometry, "LayerElementNormal", "Normals", "NormalsIndex", 3);
        var uvs = ReadLayer(geometry, "LayerElementUV", "UV", "UVIndex", 2);

        int[] matIndices = Array.Empty<int>();
        bool matAllSame = true;
        if (geometry.Child("LayerElementMaterial") is { } matLayer)
        {
            matIndices = matLayer.IntegersOf("Materials");
            matAllSame = matLayer.TextOf("MappingInformationType") == "AllSame";
        }

        var matSlots = new List<long>();
        if (childrenOf.TryGetValue(modelId, out var modelKids))
        {
            foreach (long cid in modelKids)
            {
                if (objects.TryGetValue(cid, out var cn) && cn.Name == "Material")
                    matSlots.Add(cid);
            }
        }

        // ポリゴンをマテリアルスロット別に平坦な3頂点組リストへ展開（配列アロケーションゼロ）
        var groups = new Dictionary<int, List<(int Point, int Corner)>>();
        var corners = new List<(int Point, int Corner)>(8);
        int polyIdx = 0;

        for (int i = 0; i < polygons.Length; i++)
        {
            int raw = polygons[i];
            int pt = raw < 0 ? ~raw : raw;
            if ((uint)pt >= (uint)pointCount) return;

            corners.Add((pt, i));
            if (raw >= 0) continue;

            int slot = 0;
            if (matIndices.Length > 0)
            {
                slot = matAllSame ? matIndices[0] : (polyIdx < matIndices.Length ? matIndices[polyIdx] : 0);
            }

            if (!groups.TryGetValue(slot, out var triList))
                groups[slot] = triList = new List<(int, int)>();

            for (int t = 1; t + 1 < corners.Count; t++)
            {
                triList.Add(corners[0]);
                triList.Add(corners[t]);
                triList.Add(corners[t + 1]);
            }

            corners.Clear();
            polyIdx++;
        }

        var dedup = new Dictionary<(int pt, int nIdx, int uvIdx), uint>();

        foreach (var (slot, triCorners) in groups)
        {
            if (triCorners.Count < 3) continue;

            int startIdxOffset = indices.Count;
            dedup.Clear();

            for (int c = 0; c + 2 < triCorners.Count; c += 3)
            {
                uint i0 = GetOrAddFbxVertex(triCorners[c], world, normalMat, positions, normals, uvs, dedup, vertices);
                uint i1 = GetOrAddFbxVertex(triCorners[c + 1], world, normalMat, positions, normals, uvs, dedup, vertices);
                uint i2 = GetOrAddFbxVertex(triCorners[c + 2], world, normalMat, positions, normals, uvs, dedup, vertices);

                indices.Add(i0); indices.Add(i1); indices.Add(i2);

                if (!normals.HasValues)
                {
                    Vector3 faceCross = Vector3.Cross(
                        vertices[(int)i1].Position - vertices[(int)i0].Position,
                        vertices[(int)i2].Position - vertices[(int)i0].Position);
                    Vector3 flat = faceCross.LengthSquared() > 1e-12f ? Vector3.Normalize(faceCross) : Vector3.UnitZ;
                    void SetN(uint vi) { var vx = vertices[(int)vi]; vx.Normal = flat; vertices[(int)vi] = vx; }
                    SetN(i0); SetN(i1); SetN(i2);
                }
            }

            int idxCount = indices.Count - startIdxOffset;
            BuildFbxPart(slot, matSlots, objects, childrenOf, propertiesOf, baseDir, modelFullPath, startIdxOffset, idxCount, parts);
        }
    }

    private static uint GetOrAddFbxVertex(
        (int Point, int Corner) tc,
        in Matrix4x4 world,
        in Matrix4x4 normalMat,
        double[] positions,
        in LayerData normals,
        in LayerData uvs,
        Dictionary<(int pt, int nIdx, int uvIdx), uint> dedup,
        List<ModelVertex> vertices)
    {
        int nIdx = normals.IndexOf(tc.Corner, tc.Point);
        int uvIdx = uvs.IndexOf(tc.Corner, tc.Point);
        var key = (tc.Point, nIdx, uvIdx);

        if (dedup.TryGetValue(key, out uint existing))
            return existing;

        int pBase = tc.Point * 3;
        Vector3 pos = Vector3.Transform(
            new Vector3((float)positions[pBase], (float)positions[pBase + 1], (float)positions[pBase + 2]),
            world);

        Vector3 norm = Vector3.Zero;
        if (normals.HasValues)
        {
            norm = Vector3.TransformNormal(normals.Vec3(nIdx), normalMat);
            float nSq = norm.LengthSquared();
            if (nSq > 1e-12f) norm /= MathF.Sqrt(nSq);
        }

        Vector2 uv = uvs.Vec2(uvIdx);
        uv.Y = 1f - uv.Y;

        uint newIdx = (uint)vertices.Count;
        dedup[key] = newIdx;
        vertices.Add(new ModelVertex
        {
            Position = pos,
            Normal = norm,
            TexCoord = uv,
            Color = Vector4.One,
        });
        return newIdx;
    }

    private static void BuildFbxPart(
        int slot,
        List<long> matSlots,
        Dictionary<long, FbxNode> objects,
        Dictionary<long, List<long>> childrenOf,
        Dictionary<long, List<(long Child, string Property)>> propertiesOf,
        string baseDir,
        string modelFullPath,
        int indexOffset,
        int indexCount,
        List<ParsedModelPart> parts)
    {
        string matName = string.Empty;
        Vector4 baseColor = Vector4.One;
        Vector4 emissiveColor = Vector4.Zero;
        float roughness = -1f;
        float metallic = 0f;
        string? texPath = null;
        byte[]? embBytes = null;
        string? embKey = null;

        if ((uint)slot < (uint)matSlots.Count && objects.TryGetValue(matSlots[slot], out var matNode))
        {
            long matId = matSlots[slot];
            matName = CleanFbxLabel(matNode.Text(1));

            FbxNode? texNode = FindDiffuseTexture(matId, objects, propertiesOf);
            if (texNode != null)
            {
                ResolveFbxTexture(texNode, objects, childrenOf, baseDir, modelFullPath, out texPath, out embBytes, out embKey);
            }

            if (matNode.Child("Properties70") is { } props)
            {
                if (texPath == null && embBytes == null)
                {
                    Vector3 diff = PropVector(props, "DiffuseColor", PropVector(props, "Diffuse", Vector3.One));
                    baseColor = new Vector4(diff, 1f);
                }

                Vector3 em = PropVector(props, "EmissiveColor", PropVector(props, "Emissive", Vector3.Zero));
                float ef = (float)PropDouble(props, "EmissiveFactor", 1.0);
                if ((em * ef).LengthSquared() > 1e-6f)
                    emissiveColor = new Vector4(em, MathF.Max(ef, 0f));

                double shininess = PropDouble(props, "Shininess", -1.0);
                if (shininess >= 0.0)
                    roughness = Math.Clamp(MathF.Pow(2f / ((float)shininess + 2f), 0.25f), 0.06f, 1.0f);
            }
        }

        parts.Add(new ParsedModelPart
        {
            Name = matName,
            IndexOffset = indexOffset,
            IndexCount = indexCount,
            BaseColor = baseColor,
            EmissiveColor = emissiveColor,
            Metallic = metallic,
            Roughness = roughness,
            TexturePath = texPath,
            EmbeddedTextureBytes = embBytes,
            EmbeddedTextureKey = embKey,
            ForceOpaque = texPath == null && embBytes == null && baseColor.W >= 0.999f,
        });
    }

    private static string CleanFbxLabel(string raw)
    {
        int end = raw.IndexOf('\0');
        return end >= 0 ? raw.Substring(0, end) : raw;
    }

    private static FbxNode? FindDiffuseTexture(
        long matId,
        Dictionary<long, FbxNode> objects,
        Dictionary<long, List<(long Child, string Property)>> propertiesOf)
    {
        if (!propertiesOf.TryGetValue(matId, out var props)) return null;

        FbxNode? fallback = null;
        foreach (var (childId, propName) in props)
        {
            if (!objects.TryGetValue(childId, out var texNode) || texNode.Name != "Texture")
                continue;

            if (propName.Contains("Diffuse", StringComparison.OrdinalIgnoreCase) ||
                propName.Contains("BaseColor", StringComparison.OrdinalIgnoreCase))
            {
                return texNode;
            }
            fallback ??= texNode;
        }
        return fallback;
    }

    private static void ResolveFbxTexture(
        FbxNode texNode,
        Dictionary<long, FbxNode> objects,
        Dictionary<long, List<long>> childrenOf,
        string baseDir,
        string modelFullPath,
        out string? texPath,
        out byte[]? embBytes,
        out string? embKey)
    {
        texPath = null;
        embBytes = null;
        embKey = null;

        long texId = texNode.Number(0);
        if (childrenOf.TryGetValue(texId, out var kids))
        {
            foreach (long cid in kids)
            {
                if (objects.TryGetValue(cid, out var video) &&
                    video.Name == "Video" &&
                    video.Child("Content") is { } content &&
                    content.Properties.Length > 0 &&
                    content.Properties[0] is byte[] { Length: > 0 } raw)
                {
                    embBytes = raw;
                    embKey = $"{modelFullPath}#fbxvid:{cid}";
                    return;
                }
            }
        }

        string rel = CleanFbxLabel(texNode.TextOf("RelativeFilename"));
        if (!string.IsNullOrEmpty(rel))
        {
            texPath = ModelLoadHelper.ResolveTexturePath(rel, baseDir);
            if (texPath != null) return;
        }

        string abs = CleanFbxLabel(texNode.TextOf("FileName"));
        if (!string.IsNullOrEmpty(abs))
        {
            texPath = ModelLoadHelper.ResolveTexturePath(abs, baseDir);
        }
    }
}
