using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iyahon_D3D11Renderer_Core.Model3D;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D.Modifiers;

public enum MeshModifierType
{
    Mirror = 10,
    Solidify = 20,
    Boolean = 30,
    Array = 40,
    Triangulate = 45,
    Bevel = 50,
    Shrinkwrap = 60,
    SubdivisionSimple = 70,
    SubdivisionCatmullClark = 80,
    Displace = 90
}

public enum TriangulateQuadMethod
{
    Beauty = 0,
    Fixed = 1,
    FixedAlternate = 2,
    ShortestDiagonal = 3,
    LongestDiagonal = 4
}

public enum TriangulateNgonMethod
{
    Beauty = 0,
    Clip = 1
}

public enum BooleanOperationKind
{
    Difference = 0,
    Union = 1,
    Intersect = 2
}

public enum ModifierTargetSourceKind
{
    SceneLayer = 0,
    BuiltInPrimitive = 1
}

public enum ShrinkwrapModeKind
{
    NearestSurfacePoint = 0,
    ProjectNormal = 1,
    NearestVertex = 2,
    ProjectAxis = 3
}

public enum ShrinkwrapSnapKind
{
    OnSurface = 0,
    OutsideSurface = 1,
    Inside = 2,
    Outside = 3
}

public enum DisplaceTextureKind
{
    Noise = 0,
    Voronoi = 1,
    Wave = 2,
    Checker = 3,
    Image = 4
}

public enum DisplaceDirectionKind
{
    Normal = 0,
    X = 1,
    Y = 2,
    Z = 3
}

public enum DisplaceCoordKind
{
    Object = 0,
    UV = 1
}

public enum ModifierAxisKind
{
    X = 0,
    Y = 1,
    Z = 2,
    NegX = 3,
    NegY = 4,
    NegZ = 5
}

/// <summary>
/// 非破壊モディファイアスタック内の1エントリを表すシリアライズ可能モデル。
/// </summary>
public sealed class MeshModifierItem
{
    public int Id { get; set; }
    public MeshModifierType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool Expanded { get; set; } = true;

    // ── ミラー (Mirror) ──
    public bool MirrorX { get; set; } = true;
    public bool MirrorY { get; set; }
    public bool MirrorZ { get; set; }
    public bool MirrorBisectX { get; set; }
    public bool MirrorBisectY { get; set; }
    public bool MirrorBisectZ { get; set; }
    public bool MirrorFlipX { get; set; }
    public bool MirrorFlipY { get; set; }
    public bool MirrorFlipZ { get; set; }
    public bool MirrorMerge { get; set; } = true;
    public float MirrorMergeThreshold { get; set; } = 0.002f;
    public bool MirrorClip { get; set; }

    // ── ソリッド化 (Solidify) ──
    public float SolidifyThickness { get; set; } = 0.08f;
    public float SolidifyOffset { get; set; } = -1.0f;
    public bool SolidifyFillRim { get; set; } = true;
    public bool SolidifyOnlyRim { get; set; }
    public bool SolidifyEvenThickness { get; set; } = true;
    public bool SolidifyFlipNormals { get; set; }
    public int SolidifyMaterialOffset { get; set; }
    public int SolidifyRimMaterialOffset { get; set; }

    // ── ブーリアン / シュリンクラップ共通ターゲット ──
    public BooleanOperationKind BooleanOperation { get; set; } = BooleanOperationKind.Difference;
    public ModifierTargetSourceKind TargetSource { get; set; } = ModifierTargetSourceKind.BuiltInPrimitive;
    public int TargetLayer { get; set; }
    public D3DPrimitiveShapeKind TargetPrimitive { get; set; } = D3DPrimitiveShapeKind.UvSphere;
    public float TargetPosX { get; set; } = 0.25f;
    public float TargetPosY { get; set; } = 0.25f;
    public float TargetPosZ { get; set; } = 0.25f;
    public float TargetRotX { get; set; }
    public float TargetRotY { get; set; }
    public float TargetRotZ { get; set; }
    public float TargetScaleX { get; set; } = 0.7f;
    public float TargetScaleY { get; set; } = 0.7f;
    public float TargetScaleZ { get; set; } = 0.7f;

    // ── 配列 (Array) ──
    public int ArrayCount { get; set; } = 3;
    public bool ArrayUseRelativeOffset { get; set; } = true;
    public float ArrayRelX { get; set; } = 1.1f;
    public float ArrayRelY { get; set; }
    public float ArrayRelZ { get; set; }
    public bool ArrayUseConstantOffset { get; set; }
    public float ArrayConstX { get; set; }
    public float ArrayConstY { get; set; }
    public float ArrayConstZ { get; set; }
    public float ArrayRotX { get; set; }
    public float ArrayRotY { get; set; }
    public float ArrayRotZ { get; set; }
    public float ArrayScale { get; set; } = 1.0f;
    public bool ArrayMerge { get; set; }
    public float ArrayMergeThreshold { get; set; } = 0.002f;

    // ── ベベル (Bevel) ──
    public float BevelAmount { get; set; } = 0.08f;
    public int BevelSegments { get; set; } = 2;
    public float BevelAngleLimitDeg { get; set; } = 30f;
    public float BevelProfile { get; set; } = 0.5f;
    public bool BevelClampOverlap { get; set; } = true;
    public bool BevelSmooth { get; set; } = true;

    // ── シュリンクラップ (Shrinkwrap) ──
    public ShrinkwrapModeKind ShrinkwrapMode { get; set; } = ShrinkwrapModeKind.NearestSurfacePoint;
    public ShrinkwrapSnapKind ShrinkwrapSnap { get; set; } = ShrinkwrapSnapKind.OnSurface;
    public ModifierAxisKind ShrinkwrapAxis { get; set; } = ModifierAxisKind.NegY;
    public float ShrinkwrapOffset { get; set; } = 0.01f;
    public float ShrinkwrapMix { get; set; } = 1.0f;

    // ── 細分化 (Subdivision) ──
    public int SubdivLevels { get; set; } = 1;
    public bool SubdivSmoothShading { get; set; } = true;
    public bool SubdivKeepCorners { get; set; }

    // ── ディスプレイス (Displace) ──
    public DisplaceTextureKind DisplaceTexture { get; set; } = DisplaceTextureKind.Noise;
    public DisplaceDirectionKind DisplaceDirection { get; set; } = DisplaceDirectionKind.Normal;
    public DisplaceCoordKind DisplaceCoord { get; set; } = DisplaceCoordKind.Object;
    public float DisplaceStrength { get; set; } = 0.15f;
    public float DisplaceMidlevel { get; set; } = 0.5f;
    public float DisplaceScale { get; set; } = 4.0f;
    public float DisplaceDetail { get; set; } = 2.0f;
    public float DisplaceOffsetX { get; set; }
    public float DisplaceOffsetY { get; set; }
    public float DisplaceOffsetZ { get; set; }
    public string DisplaceImagePath { get; set; } = string.Empty;

    // ── 三角形化 (Triangulate) ──
    public TriangulateQuadMethod TriangulateQuadMethod { get; set; } = TriangulateQuadMethod.Beauty;
    public TriangulateNgonMethod TriangulateNgonMethod { get; set; } = TriangulateNgonMethod.Beauty;
    public int TriangulateMinVertices { get; set; } = 4;
    public bool TriangulateKeepNormals { get; set; }

    [JsonIgnore]
    public bool IsSceneDependent =>
        (Type == MeshModifierType.Boolean || Type == MeshModifierType.Shrinkwrap) &&
        TargetSource == ModifierTargetSourceKind.SceneLayer;

    public static string GetTypeDisplayName(MeshModifierType type) => type switch
    {
        MeshModifierType.Mirror => "ミラー",
        MeshModifierType.Solidify => "ソリッド化",
        MeshModifierType.Boolean => "ブーリアン",
        MeshModifierType.Array => "配列",
        MeshModifierType.Triangulate => "三角形化",
        MeshModifierType.Bevel => "ベベル",
        MeshModifierType.Shrinkwrap => "シュリンクラップ",
        MeshModifierType.SubdivisionSimple => "細分化",
        MeshModifierType.SubdivisionCatmullClark => "細分化 (曲面)",
        MeshModifierType.Displace => "ディスプレイス",
        _ => "モディファイア"
    };

    public static MeshModifierItem CreateDefault(MeshModifierType type)
    {
        var item = new MeshModifierItem
        {
            Type = type,
            Name = GetTypeDisplayName(type),
            Enabled = true,
            Expanded = true,
        };
        if (type == MeshModifierType.Shrinkwrap)
        {
            item.TargetPosX = 0f;
            item.TargetPosY = 0f;
            item.TargetPosZ = 0f;
            item.TargetScaleX = 0.9f;
            item.TargetScaleY = 0.9f;
            item.TargetScaleZ = 0.9f;
        }
        return item;
    }

    public MeshModifierItem Clone() => new()
    {
        Id = Id,
        Type = Type,
        Name = Name,
        Enabled = Enabled,
        Expanded = Expanded,
        MirrorX = MirrorX,
        MirrorY = MirrorY,
        MirrorZ = MirrorZ,
        MirrorBisectX = MirrorBisectX,
        MirrorBisectY = MirrorBisectY,
        MirrorBisectZ = MirrorBisectZ,
        MirrorFlipX = MirrorFlipX,
        MirrorFlipY = MirrorFlipY,
        MirrorFlipZ = MirrorFlipZ,
        MirrorMerge = MirrorMerge,
        MirrorMergeThreshold = MirrorMergeThreshold,
        MirrorClip = MirrorClip,
        SolidifyThickness = SolidifyThickness,
        SolidifyOffset = SolidifyOffset,
        SolidifyFillRim = SolidifyFillRim,
        SolidifyOnlyRim = SolidifyOnlyRim,
        SolidifyEvenThickness = SolidifyEvenThickness,
        SolidifyFlipNormals = SolidifyFlipNormals,
        SolidifyMaterialOffset = SolidifyMaterialOffset,
        SolidifyRimMaterialOffset = SolidifyRimMaterialOffset,
        TriangulateQuadMethod = TriangulateQuadMethod,
        TriangulateNgonMethod = TriangulateNgonMethod,
        TriangulateMinVertices = TriangulateMinVertices,
        TriangulateKeepNormals = TriangulateKeepNormals,
        BooleanOperation = BooleanOperation,
        TargetSource = TargetSource,
        TargetLayer = TargetLayer,
        TargetPrimitive = TargetPrimitive,
        TargetPosX = TargetPosX,
        TargetPosY = TargetPosY,
        TargetPosZ = TargetPosZ,
        TargetRotX = TargetRotX,
        TargetRotY = TargetRotY,
        TargetRotZ = TargetRotZ,
        TargetScaleX = TargetScaleX,
        TargetScaleY = TargetScaleY,
        TargetScaleZ = TargetScaleZ,
        ArrayCount = ArrayCount,
        ArrayUseRelativeOffset = ArrayUseRelativeOffset,
        ArrayRelX = ArrayRelX,
        ArrayRelY = ArrayRelY,
        ArrayRelZ = ArrayRelZ,
        ArrayUseConstantOffset = ArrayUseConstantOffset,
        ArrayConstX = ArrayConstX,
        ArrayConstY = ArrayConstY,
        ArrayConstZ = ArrayConstZ,
        ArrayRotX = ArrayRotX,
        ArrayRotY = ArrayRotY,
        ArrayRotZ = ArrayRotZ,
        ArrayScale = ArrayScale,
        ArrayMerge = ArrayMerge,
        ArrayMergeThreshold = ArrayMergeThreshold,
        BevelAmount = BevelAmount,
        BevelSegments = BevelSegments,
        BevelAngleLimitDeg = BevelAngleLimitDeg,
        BevelProfile = BevelProfile,
        BevelClampOverlap = BevelClampOverlap,
        BevelSmooth = BevelSmooth,
        ShrinkwrapMode = ShrinkwrapMode,
        ShrinkwrapSnap = ShrinkwrapSnap,
        ShrinkwrapAxis = ShrinkwrapAxis,
        ShrinkwrapOffset = ShrinkwrapOffset,
        ShrinkwrapMix = ShrinkwrapMix,
        SubdivLevels = SubdivLevels,
        SubdivSmoothShading = SubdivSmoothShading,
        SubdivKeepCorners = SubdivKeepCorners,
        DisplaceTexture = DisplaceTexture,
        DisplaceDirection = DisplaceDirection,
        DisplaceCoord = DisplaceCoord,
        DisplaceStrength = DisplaceStrength,
        DisplaceMidlevel = DisplaceMidlevel,
        DisplaceScale = DisplaceScale,
        DisplaceDetail = DisplaceDetail,
        DisplaceOffsetX = DisplaceOffsetX,
        DisplaceOffsetY = DisplaceOffsetY,
        DisplaceOffsetZ = DisplaceOffsetZ,
        DisplaceImagePath = DisplaceImagePath ?? string.Empty,
    };

    internal void AppendContentKey(StringBuilder sb)
    {
        sb.Append((int)Type).Append(':');
        switch (Type)
        {
            case MeshModifierType.Mirror:
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0}{1}{2}:{3}{4}{5}:{6}{7}{8}:{9}:{10:G5}:{11}",
                    MirrorX ? 1 : 0, MirrorY ? 1 : 0, MirrorZ ? 1 : 0,
                    MirrorBisectX ? 1 : 0, MirrorBisectY ? 1 : 0, MirrorBisectZ ? 1 : 0,
                    MirrorFlipX ? 1 : 0, MirrorFlipY ? 1 : 0, MirrorFlipZ ? 1 : 0,
                    MirrorMerge ? 1 : 0, MirrorMergeThreshold, MirrorClip ? 1 : 0);
                break;

            case MeshModifierType.Solidify:
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0:G5}:{1:G5}:{2}{3}{4}{5}:{6},{7}",
                    SolidifyThickness, SolidifyOffset,
                    SolidifyFillRim ? 1 : 0, SolidifyOnlyRim ? 1 : 0,
                    SolidifyEvenThickness ? 1 : 0, SolidifyFlipNormals ? 1 : 0,
                    SolidifyMaterialOffset, SolidifyRimMaterialOffset);
                break;

            case MeshModifierType.Triangulate:
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0}:{1}:{2}:{3}",
                    (int)TriangulateQuadMethod, (int)TriangulateNgonMethod,
                    TriangulateMinVertices, TriangulateKeepNormals ? 1 : 0);
                break;

            case MeshModifierType.Boolean:
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0}:{1}:{2}:{3}:{4:G4},{5:G4},{6:G4}:{7:G4},{8:G4},{9:G4}:{10:G4},{11:G4},{12:G4}",
                    (int)BooleanOperation, (int)TargetSource, TargetLayer, (int)TargetPrimitive,
                    TargetPosX, TargetPosY, TargetPosZ,
                    TargetRotX, TargetRotY, TargetRotZ,
                    TargetScaleX, TargetScaleY, TargetScaleZ);
                break;

            case MeshModifierType.Array:
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0}:{1}:{2:G5},{3:G5},{4:G5}:{5}:{6:G5},{7:G5},{8:G5}:{9:G4},{10:G4},{11:G4}:{12:G5}:{13}:{14:G5}",
                    ArrayCount, ArrayUseRelativeOffset ? 1 : 0, ArrayRelX, ArrayRelY, ArrayRelZ,
                    ArrayUseConstantOffset ? 1 : 0, ArrayConstX, ArrayConstY, ArrayConstZ,
                    ArrayRotX, ArrayRotY, ArrayRotZ, ArrayScale,
                    ArrayMerge ? 1 : 0, ArrayMergeThreshold);
                break;

            case MeshModifierType.Bevel:
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0:G5}:{1}:{2:G4}:{3:G4}:{4}{5}",
                    BevelAmount, BevelSegments, BevelAngleLimitDeg, BevelProfile,
                    BevelClampOverlap ? 1 : 0, BevelSmooth ? 1 : 0);
                break;

            case MeshModifierType.Shrinkwrap:
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0}:{1}:{2}:{3:G5}:{4:G4}:{5}:{6}:{7}:{8:G4},{9:G4},{10:G4}:{11:G4},{12:G4},{13:G4}:{14:G4},{15:G4},{16:G4}",
                    (int)ShrinkwrapMode, (int)ShrinkwrapSnap, (int)ShrinkwrapAxis, ShrinkwrapOffset, ShrinkwrapMix,
                    (int)TargetSource, TargetLayer, (int)TargetPrimitive,
                    TargetPosX, TargetPosY, TargetPosZ,
                    TargetRotX, TargetRotY, TargetRotZ,
                    TargetScaleX, TargetScaleY, TargetScaleZ);
                break;

            case MeshModifierType.SubdivisionSimple:
            case MeshModifierType.SubdivisionCatmullClark:
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0}:{1}{2}", SubdivLevels, SubdivSmoothShading ? 1 : 0, SubdivKeepCorners ? 1 : 0);
                break;

            case MeshModifierType.Displace:
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0}:{1}:{2}:{3:G5}:{4:G5}:{5:G5}:{6:G4}:{7:G4},{8:G4},{9:G4}:{10}",
                    (int)DisplaceTexture, (int)DisplaceDirection, (int)DisplaceCoord,
                    DisplaceStrength, DisplaceMidlevel, DisplaceScale, DisplaceDetail,
                    DisplaceOffsetX, DisplaceOffsetY, DisplaceOffsetZ, DisplaceImagePath ?? string.Empty);
                break;
        }
    }
}

/// <summary>
/// 3Dオブジェクト単位の非破壊モディファイアスタックと JSON シリアライズ。
/// </summary>
public sealed class MeshModifierStack
{
    private const string MagicPrefix = "IMOD1:";

    public int NextId { get; set; } = 1;
    public List<MeshModifierItem> Items { get; set; } = new();

    [JsonIgnore]
    public bool HasEnabledModifiers
    {
        get
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Enabled) return true;
            return false;
        }
    }

    [JsonIgnore]
    public bool HasSceneDependentEnabledModifiers
    {
        get
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Enabled && Items[i].IsSceneDependent) return true;
            return false;
        }
    }

    public MeshModifierItem AddModifier(MeshModifierType type)
    {
        var item = MeshModifierItem.CreateDefault(type);
        item.Id = Math.Max(1, NextId++);
        Items.Add(item);
        return item;
    }

    public string Serialize()
    {
        if (Items.Count == 0) return string.Empty;
        return MagicPrefix + JsonSerializer.Serialize(this);
    }

    public static bool TryDeserialize(string? data, out MeshModifierStack stack)
    {
        stack = new MeshModifierStack();
        if (string.IsNullOrWhiteSpace(data))
            return false;

        string json = data.StartsWith(MagicPrefix, StringComparison.Ordinal)
            ? data.Substring(MagicPrefix.Length)
            : data;

        try
        {
            var parsed = JsonSerializer.Deserialize<MeshModifierStack>(json);
            if (parsed == null)
                return false;
            parsed.Items ??= new List<MeshModifierItem>();
            int maxId = 0;
            for (int i = 0; i < parsed.Items.Count; i++)
            {
                var it = parsed.Items[i];
                if (it == null)
                {
                    parsed.Items.RemoveAt(i--);
                    continue;
                }
                if (it.Id <= 0) it.Id = ++maxId;
                else if (it.Id > maxId) maxId = it.Id;
                if (string.IsNullOrWhiteSpace(it.Name))
                    it.Name = MeshModifierItem.GetTypeDisplayName(it.Type);
            }
            if (parsed.NextId <= maxId)
                parsed.NextId = maxId + 1;
            stack = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public MeshModifierStack Clone()
    {
        var copy = new MeshModifierStack { NextId = NextId };
        copy.Items.Capacity = Items.Count;
        for (int i = 0; i < Items.Count; i++)
            copy.Items.Add(Items[i].Clone());
        return copy;
    }

    public string ComputeEnabledContentKey()
    {
        if (!HasEnabledModifiers) return string.Empty;
        var sb = new StringBuilder(128);
        for (int i = 0; i < Items.Count; i++)
        {
            if (!Items[i].Enabled) continue;
            if (sb.Length > 0) sb.Append('|');
            Items[i].AppendContentKey(sb);
        }
        return sb.ToString();
    }

    public static bool HasAnyEnabledModifiers(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return false;
        return TryDeserialize(data, out var stack) && stack.HasEnabledModifiers;
    }

    public static bool HasSceneDependentModifiers(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return false;
        return TryDeserialize(data, out var stack) && stack.HasSceneDependentEnabledModifiers;
    }
}

/// <summary>
/// 他レイヤーの 3D オブジェクトを Boolean / Shrinkwrap ターゲットとして参照するための情報。
/// </summary>
internal sealed class SceneModifierTarget
{
    public int Layer { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string BaseKey { get; init; } = string.Empty;
    public Matrix4x4 ModelToWorld { get; init; } = Matrix4x4.Identity;
    public Func<EditableMesh?>? ResolveBaseMesh { get; init; }
    public string ModifierStackData { get; init; } = string.Empty;
}

/// <summary>
/// フレーム / ビューポート単位のシーン内他レイヤー参照解決コンテキスト。
/// </summary>
internal sealed class SceneModifierContext
{
    private readonly Dictionary<int, SceneModifierTarget> _targetsByLayer = new();

    public void RegisterTarget(SceneModifierTarget target)
    {
        _targetsByLayer[target.Layer] = target;
    }

    public bool TryGetTarget(int layer, out SceneModifierTarget? target)
        => _targetsByLayer.TryGetValue(layer, out target);

    public IEnumerable<SceneModifierTarget> AllTargets => _targetsByLayer.Values;

    public string ComputeSceneTargetsCacheKey(
        MeshModifierStack stack,
        int selfLayer,
        in Matrix4x4 selfModelToWorld)
    {
        if (!stack.HasSceneDependentEnabledModifiers || !Matrix4x4.Invert(selfModelToWorld, out var invSelf))
            return string.Empty;

        var sb = new StringBuilder(128);
        for (int i = 0; i < stack.Items.Count; i++)
        {
            var mod = stack.Items[i];
            if (!mod.Enabled || !mod.IsSceneDependent) continue;
            if (mod.TargetLayer == selfLayer || !_targetsByLayer.TryGetValue(mod.TargetLayer, out var tgt))
            {
                sb.Append($"missing:{mod.TargetLayer};");
                continue;
            }
            var rel = tgt.ModelToWorld * invSelf;
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "L{0}[{1}|{2}|{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18}];",
                tgt.Layer, tgt.BaseKey, tgt.ModifierStackData,
                Q(rel.M11), Q(rel.M12), Q(rel.M13), Q(rel.M14),
                Q(rel.M21), Q(rel.M22), Q(rel.M23), Q(rel.M24),
                Q(rel.M31), Q(rel.M32), Q(rel.M33), Q(rel.M34),
                Q(rel.M41), Q(rel.M42), Q(rel.M43), Q(rel.M44));
        }
        return sb.ToString();
    }

    public bool TryResolveTargetMeshInLocalSpace(
        int targetLayer,
        int requesterLayer,
        in Matrix4x4 requesterModelToWorld,
        out EditableMesh? targetMeshInRequesterLocal,
        out string cacheToken,
        HashSet<int>? visitingLayers = null)
    {
        targetMeshInRequesterLocal = null;
        cacheToken = string.Empty;

        if (targetLayer == requesterLayer) return false;
        if (!_targetsByLayer.TryGetValue(targetLayer, out var tgt)) return false;

        visitingLayers ??= new HashSet<int>();
        if (!visitingLayers.Add(targetLayer))
            return false;

        try
        {
            if (!Matrix4x4.Invert(requesterModelToWorld, out var invReq))
                return false;

            var baseMesh = tgt.ResolveBaseMesh?.Invoke();
            if (baseMesh == null || baseMesh.Vertices.Count == 0)
                return false;

            EditableMesh evaluatedTarget = baseMesh;
            if (MeshModifierStack.TryDeserialize(tgt.ModifierStackData, out var targetStack) &&
                targetStack.HasEnabledModifiers)
            {
                evaluatedTarget = MeshModifierEvaluator.Evaluate(
                    baseMesh, targetStack, this, tgt.Layer, tgt.ModelToWorld, visitingLayers);
            }
            else
            {
                evaluatedTarget = baseMesh.Clone();
            }

            var relMat = tgt.ModelToWorld * invReq;
            TransformMeshInPlace(evaluatedTarget, relMat);

            cacheToken = string.Format(
                CultureInfo.InvariantCulture,
                "L{0}:{1}:{2}:{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14}",
                tgt.Layer, tgt.BaseKey, tgt.ModifierStackData,
                Q(relMat.M11), Q(relMat.M12), Q(relMat.M13),
                Q(relMat.M21), Q(relMat.M22), Q(relMat.M23),
                Q(relMat.M31), Q(relMat.M32), Q(relMat.M33),
                Q(relMat.M41), Q(relMat.M42), Q(relMat.M43));

            targetMeshInRequesterLocal = evaluatedTarget;
            return true;
        }
        finally
        {
            visitingLayers.Remove(targetLayer);
        }
    }

    internal static void TransformMeshInPlace(EditableMesh mesh, in Matrix4x4 mat)
    {
        bool flipWinding = mat.GetDeterminant() < 0f;
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var v = mesh.Vertices[i];
            v.Position = Vector3.Transform(v.Position, mat);
            Vector3 n = Vector3.TransformNormal(v.Normal, mat);
            float lsq = n.LengthSquared();
            v.Normal = lsq > 1e-10f ? n / MathF.Sqrt(lsq) : Vector3.UnitY;
        }
        if (flipWinding)
        {
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var f = mesh.Faces[fi];
                f.VertexIndices.Reverse();
                f.FaceUVs.Reverse();
                f.FaceCornerNormals?.Reverse();
            }
        }
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var cn = mesh.Faces[fi].FaceCornerNormals;
            if (cn != null)
            {
                for (int k = 0; k < cn.Count; k++)
                {
                    Vector3 t = Vector3.TransformNormal(cn[k], mat);
                    float lsq = t.LengthSquared();
                    cn[k] = lsq > 1e-10f ? t / MathF.Sqrt(lsq) : Vector3.UnitY;
                }
            }
        }
        mesh.RecalculateNormals();
    }

    private static long Q(float v) => (long)Math.Round(v * 10000.0);
}
