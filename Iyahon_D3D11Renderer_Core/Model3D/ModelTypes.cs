// Portions of the 3D model loading logic are adapted from:
// - YMM4-ObjLoader (Copyright (c) 2025 routersys, MIT License)
// - YMM43D (Copyright (c) 2026 いるかぁぁ, MIT License)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

[StructLayout(LayoutKind.Sequential)]
internal struct ModelVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
    public Vector4 Color;
}

internal sealed class ParsedModelPart
{
    public string Name = string.Empty;
    public int IndexOffset;
    public int IndexCount;
    public Vector4 BaseColor = Vector4.One;
    public Vector4 EmissiveColor = Vector4.Zero; // xyz = sRGB emissive, w = intensity
    public float Metallic = 0f;
    public float Roughness = -1f; // < 0 => DefaultRoughness
    public string? TexturePath;
    public byte[]? EmbeddedTextureBytes;
    public string? EmbeddedTextureKey;
    public string? EmbeddedTextureExt;
    public string? NormalTexturePath;
    public byte[]? EmbeddedNormalBytes;
    public string? EmbeddedNormalKey;
    public string? EmbeddedNormalExt;
    public float NormalScale = 1.0f;
    public string? RoughnessMetallicTexturePath;
    public byte[]? EmbeddedRoughMetalBytes;
    public string? EmbeddedRoughMetalKey;
    public string? EmbeddedRoughMetalExt;
    public int RoughMetalPacking = 0; // 0=none, 1=glTF ORM(G=Rough,B=Metal), 2=Roughness, 3=Metallic, 4=Glossiness
    public string? EmissiveTexturePath;
    public byte[]? EmbeddedEmissiveBytes;
    public string? EmbeddedEmissiveKey;
    public string? EmbeddedEmissiveExt;
    public float Transmission = 0f;
    public float Ior = 1.5f;
    public string? TransmissionTexturePath;
    public byte[]? EmbeddedTransmissionBytes;
    public string? EmbeddedTransmissionKey;
    public string? EmbeddedTransmissionExt;
    public bool ForceOpaque;
    public bool CullBackface;
    public bool CastShadow = true;
    public int SourcePartIndex = -1;
    public int MaterialSlotIndex = -1;
    public Viewport3D.Shading.MaterialNodeGraph? NodeGraph;
}

internal sealed class ParsedModel
{
    public ModelVertex[] Vertices = Array.Empty<ModelVertex>();
    public uint[] Indices = Array.Empty<uint>();
    public List<ParsedModelPart> Parts = new();
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public Vector3 ModelCenter;
    public float ModelScale = 1f;
}

internal static class ModelLoadHelper
{
    private static readonly Lazy<Encoding> s_shiftJis = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }
        catch
        {
            return Encoding.UTF8;
        }
    });

    internal static void Log(string msg)
        => Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] Model3D: {msg}");

    public static void CalculateNormals(ModelVertex[] vertices, uint[] indices)
    {
        if (vertices.Length == 0 || indices.Length < 3) return;

        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Normal = Vector3.Zero;
        }

        int triCount = indices.Length / 3;
        for (int t = 0; t < triCount; t++)
        {
            uint i0 = indices[t * 3];
            uint i1 = indices[t * 3 + 1];
            uint i2 = indices[t * 3 + 2];
            if (i0 >= (uint)vertices.Length || i1 >= (uint)vertices.Length || i2 >= (uint)vertices.Length)
                continue;

            Vector3 p0 = vertices[i0].Position;
            Vector3 p1 = vertices[i1].Position;
            Vector3 p2 = vertices[i2].Position;
            Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);

            vertices[i0].Normal += cross;
            vertices[i1].Normal += cross;
            vertices[i2].Normal += cross;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            float lenSq = vertices[i].Normal.LengthSquared();
            vertices[i].Normal = lenSq > 1e-12f
                ? vertices[i].Normal / MathF.Sqrt(lenSq)
                : Vector3.UnitZ;
        }
    }

    public static void CalculateBounds(
        ModelVertex[] vertices,
        out Vector3 min,
        out Vector3 max,
        out Vector3 center,
        out float scale)
    {
        if (vertices.Length == 0)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
            center = Vector3.Zero;
            scale = 1.0f;
            return;
        }

        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 p = vertices[i].Position;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        center = (min + max) * 0.5f;
        Vector3 size = max - min;
        float maxSize = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        scale = maxSize > 1e-6f ? 1.5f / maxSize : 1.0f;
    }

    public static string? ResolveTexturePath(string? rawPath, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;

        string cleaned = rawPath.Trim().Trim('"', '\'');
        if (cleaned.Length == 0) return null;

        cleaned = cleaned.Replace('/', Path.DirectorySeparatorChar)
                         .Replace('\\', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(cleaned))
        {
            if (File.Exists(cleaned)) return cleaned;
            string fileOnlyRooted = Path.GetFileName(cleaned);
            return FindInDirs(fileOnlyRooted, baseDir);
        }

        if (!string.IsNullOrEmpty(baseDir))
        {
            string direct = Path.GetFullPath(Path.Combine(baseDir, cleaned));
            if (File.Exists(direct)) return direct;

            string parent = Path.GetFullPath(Path.Combine(baseDir, "..", cleaned));
            if (File.Exists(parent)) return parent;

            string fileName = Path.GetFileName(cleaned);
            if (!string.IsNullOrEmpty(fileName))
            {
                return FindInDirs(fileName, baseDir);
            }
        }

        return File.Exists(cleaned) ? cleaned : null;
    }

    private static string? FindInDirs(string fileName, string baseDir)
    {
        if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(fileName)) return null;

        string[] candidates =
        {
            Path.Combine(baseDir, fileName),
            Path.Combine(baseDir, "textures", fileName),
            Path.Combine(baseDir, "Textures", fileName),
            Path.Combine(baseDir, "tex", fileName),
            Path.Combine(baseDir, "..", "textures", fileName),
            Path.Combine(baseDir, "..", "Textures", fileName),
        };

        foreach (string c in candidates)
        {
            try
            {
                string full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    public static Encoding DetectTextEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        return Utf8.IsValid(bytes) ? Encoding.UTF8 : s_shiftJis.Value;
    }

    public static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes.Slice(3));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes.Slice(2));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes.Slice(2));

        Encoding enc = Utf8.IsValid(bytes) ? Encoding.UTF8 : s_shiftJis.Value;
        string text = enc.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
    }
}
