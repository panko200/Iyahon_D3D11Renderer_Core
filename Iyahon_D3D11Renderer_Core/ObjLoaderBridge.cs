using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Vortice.Direct3D11;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// OBJモデルのGPUリソース情報（ブリッジ経由で取得）。
/// ObjLoaderのGpuResourceCacheItemから抽出したデータ。
/// </summary>
internal sealed class ObjModelData
{
    public required ID3D11Buffer VertexBuffer { get; init; }
    public required ID3D11Buffer IndexBuffer { get; init; }
    public required ObjPartRenderInfo[] Parts { get; init; }
    public Vector3 ModelCenter { get; init; }
    public float ModelScale { get; init; } = 1f;
    public int IndexCount { get; init; }

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public bool NormalizeSize { get; init; } = true;
    public bool GroundToOrigin { get; init; } = false;
    public float UnitScale { get; init; } = 100f;
    public Vector3 LocalScale { get; init; } = Vector3.One;
    public Vector3 LocalRotationDeg { get; init; } = Vector3.Zero;
    public Vector4 TintColor { get; init; } = Vector4.One;
    public bool OverrideMaterial { get; init; } = false;
    public float OverrideMetallic { get; init; } = 0f;
    public float OverrideRoughness { get; init; } = 0.5f;
    public float EmissiveMultiplier { get; init; } = 1f;
    public bool CullBackface { get; init; } = false;

    public string ProceduralCacheKey { get; set; } = string.Empty;
    public Func<Viewport3D.Modeling.EditableMesh>? ProceduralMeshFactory { get; set; }
    public string ModifierStackData { get; set; } = string.Empty;
    public Model3D.Noise3DParameters Noise3D { get; set; }
    public ID3D11Buffer? SmoothNormalBuffer { get; set; }

    public ObjModelData WithCachedModel(Model3D.CachedGpuModel cached)
    {
        var copy = cached.CreateRenderData(
            NormalizeSize,
            GroundToOrigin,
            UnitScale,
            LocalScale,
            LocalRotationDeg,
            TintColor,
            OverrideMaterial,
            OverrideMetallic,
            OverrideRoughness,
            EmissiveMultiplier,
            CullBackface);
        copy.ProceduralCacheKey = ProceduralCacheKey;
        copy.ProceduralMeshFactory = ProceduralMeshFactory;
        copy.ModifierStackData = ModifierStackData;
        var np = Noise3D;
        if (np.Enabled)
        {
            np.BoundsMin = cached.BoundsMin;
            np.BoundsMax = cached.BoundsMax;
        }
        copy.Noise3D = np;
        return copy;
    }
}

/// <summary>
/// OBJモデルのパーツ描画情報。
/// </summary>
internal struct ObjPartRenderInfo
{
    public int IndexOffset;
    public int IndexCount;
    public Vector4 BaseColor;
    public ID3D11ShaderResourceView? Texture;
    public ID3D11ShaderResourceView? NormalTexture;
    public float NormalScale;
    public ID3D11ShaderResourceView? RoughnessMetallicTexture;
    public int RoughMetalPacking; // 0=none, 1=glTF ORM, 2=Roughness, 3=Metallic, 4=Glossiness
    public ID3D11ShaderResourceView? EmissiveTexture;
    public float Transmission;
    public float Ior;
    public ID3D11ShaderResourceView? TransmissionTexture;
    public float Metallic;
    public float Roughness;
    public Vector4 EmissiveColor;
    public bool ForceOpaque;
    public bool CullBackface;
    public bool DisableShadow;

    // ── 拡張 Principled BSDF PBR ──
    public float CoatWeight;
    public float CoatRoughness;
    public float CoatIor;
    public float DiffuseRoughness;
    public float ThinFilmThickness;
    public float ThinFilmIor;
    public float Anisotropic;
    public float AnisotropicRotation;
    public float SubsurfaceWeight;
    public float SubsurfaceScale;
    public Vector3 SubsurfaceColor;
    public float SheenWeight;
    public float SheenRoughness;
    public Vector3 SheenTint;
    public bool IsDiffuseOnly;

    // ── リアルタイム GPU シェーダーノードチャンネル (ShaderToRgb / LayerWeight / Fresnel / AO / Wireframe / LayerTexture) ──
    public ID3D11ShaderResourceView? ShaderLutTexture;
    public int ShaderLutMode; // 0=Off, 1=ShaderToRgb(Toon), 2=LayerWeight/Fresnel, 3=AO
    public float ShaderLutParamA;
    public float ShaderLutParamB;
    public float ShaderLutEmissiveMax;

    public int WireframeMode; // 0=Off, 1=On
    public float WireframeSize;
    public bool WireframePixelSize;
    public Vector4 WireframeBaseColor;
    public Vector4 WireframeEmissiveColor;

    // default(ObjPartRenderInfo) が 0 初期化されても安全なように (Layer + 1) を格納する (0 = 無効, 1 = Layer 0, ...)
    public int LayerTextureAlbedoLayerPlusOne;
    public int LayerTextureEmissiveLayerPlusOne;
    public Vector4 LayerUvTransform; // (ScaleX, ScaleY, OffsetX, OffsetY)
    public float LayerUvRotationRad;
    public bool LayerFlipV;

    public readonly bool IsOpaque => Transmission <= 0.001f && TransmissionTexture == null &&
        (ForceOpaque || (BaseColor.W >= 0.999f && Texture == null && LayerTextureAlbedoLayerPlusOne == 0));
}

/// <summary>
/// リフレクションを使ってObjLoaderプラグインのデータにアクセスするブリッジ。
/// ObjLoaderがインストールされていない場合は何も起きない（安全）。
/// </summary>
internal static class ObjLoaderBridge
{
    private static bool _initialized;
    private static bool _available;

    // ── ObjLoader型キャッシュ ──
    private static Type? _objLoaderPluginType;
    private static Type? _shapeItemType;
    private static PropertyInfo? _shapeType2Prop;

    // ── EffectedItemSource → Source → ObjLoaderSource ──
    private static PropertyInfo? _eisSourceProp;
    private static Type? _objLoaderSourceType;

    // ── ShapeSource フィールド ──
    private static Type? _shapeSourceType;
    private static FieldInfo? _shapeSourceInternalSourceField;

    // ── ObjLoaderSource → _visibilityResolver ──
    private static FieldInfo? _visibilityResolverField;

    // ── VisibilityAndSkinningResolver → LayersToRender ──
    private static PropertyInfo? _layersToRenderProp;

    // ── LayersToRender タプルキャッシュ ──
    private static Type? _cachedTupleType;
    private static FieldInfo? _tupleItem1Field;
    private static FieldInfo? _tupleItem2Field;
    private static FieldInfo? _tupleItem4Field;

    // ── LayerData / PartMaterialData キャッシュ ──
    private static PropertyInfo? _ldPartMaterialsProp;
    private static PropertyInfo? _pmMetallicProp;
    private static PropertyInfo? _pmRoughnessProp;

    // ── GpuResourceCacheItem プロパティ ──
    private static PropertyInfo? _rcVertexBufferProp;
    private static PropertyInfo? _rcIndexBufferProp;
    private static PropertyInfo? _rcIndexCountProp;
    private static PropertyInfo? _rcPartsProp;
    private static PropertyInfo? _rcPartTexturesProp;
    private static PropertyInfo? _rcModelCenterProp;
    private static PropertyInfo? _rcModelScaleProp;

    // ── ModelPart フィールド ──
    private static FieldInfo? _mpIndexOffsetField;
    private static FieldInfo? _mpIndexCountField;
    private static FieldInfo? _mpBaseColorField;
    private static FieldInfo? _mpMetallicField;
    private static FieldInfo? _mpRoughnessField;

    /// <summary>ObjLoaderプラグインが検出できたかどうか</summary>
    public static bool IsAvailable => _initialized && _available;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            _objLoaderPluginType = FindType("ObjLoader.Plugin.ObjLoaderPlugin");
            if (_objLoaderPluginType == null)
            {
                Log("ObjLoaderPlugin 未検出。ObjLoader連携は無効。");
                return;
            }
            Log($"ObjLoaderPlugin 検出: {_objLoaderPluginType.Assembly.GetName().Name}");

            _shapeItemType = FindType("YukkuriMovieMaker.Project.Items.ShapeItem");
            if (_shapeItemType != null)
            {
                _shapeType2Prop = _shapeItemType.GetProperty("ShapeType2",
                    BindingFlags.Instance | BindingFlags.Public);
            }

            _objLoaderSourceType = FindType("ObjLoader.Rendering.Core.ObjLoaderSource");
            if (_objLoaderSourceType != null)
            {
                _visibilityResolverField = _objLoaderSourceType.GetField("_visibilityResolver",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            var resolverType = FindType("ObjLoader.Rendering.Core.Resolvers.VisibilityAndSkinningResolver");
            if (resolverType != null)
            {
                _layersToRenderProp = resolverType.GetProperty("LayersToRender",
                    BindingFlags.Instance | BindingFlags.Public);
            }

            var cacheItemType = FindType("ObjLoader.Cache.Gpu.GpuResourceCacheItem");
            if (cacheItemType != null)
            {
                _rcVertexBufferProp = cacheItemType.GetProperty("VertexBuffer");
                _rcIndexBufferProp = cacheItemType.GetProperty("IndexBuffer");
                _rcIndexCountProp = cacheItemType.GetProperty("IndexCount");
                _rcPartsProp = cacheItemType.GetProperty("Parts");
                _rcPartTexturesProp = cacheItemType.GetProperty("PartTextures");
                _rcModelCenterProp = cacheItemType.GetProperty("ModelCenter");
                _rcModelScaleProp = cacheItemType.GetProperty("ModelScale");
            }

            var modelPartType = FindType("ObjLoader.Core.Models.ModelPart");
            if (modelPartType != null)
            {
                _mpIndexOffsetField = modelPartType.GetField("IndexOffset");
                _mpIndexCountField = modelPartType.GetField("IndexCount");
                _mpBaseColorField = modelPartType.GetField("BaseColor");
                _mpMetallicField = modelPartType.GetField("Metallic");
                _mpRoughnessField = modelPartType.GetField("Roughness");
            }

            var layerDataType = FindType("ObjLoader.Core.Timeline.LayerData");
            if (layerDataType != null)
            {
                _ldPartMaterialsProp = layerDataType.GetProperty("PartMaterials");
            }

            var partMatType = FindType("ObjLoader.Core.Models.PartMaterialData");
            if (partMatType != null)
            {
                _pmMetallicProp = partMatType.GetProperty("Metallic");
                _pmRoughnessProp = partMatType.GetProperty("Roughness");
            }

            var eisType = FindType("YukkuriMovieMaker.Player.Video.EffectedItemSource");
            if (eisType != null)
            {
                _eisSourceProp = eisType.GetProperty("Source",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            _shapeSourceType = FindType("YukkuriMovieMaker.Player.Video.Items.ShapeSource");
            if (_shapeSourceType != null)
            {
                _shapeSourceInternalSourceField = _shapeSourceType.GetField("source",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            _available = _objLoaderPluginType != null
                      && _shapeItemType != null
                      && _shapeType2Prop != null
                      && _objLoaderSourceType != null
                      && _visibilityResolverField != null
                      && _layersToRenderProp != null
                      && _rcVertexBufferProp != null
                      && _rcIndexBufferProp != null
                      && _rcPartsProp != null
                      && _rcPartTexturesProp != null
                      && _rcModelCenterProp != null
                      && _rcModelScaleProp != null
                      && _eisSourceProp != null
                      && _shapeSourceType != null
                      && _shapeSourceInternalSourceField != null;

            Log($"初期化完了: available={_available}");
        }
        catch (Exception ex)
        {
            Log($"初期化エラー: {ex.Message}");
            _available = false;
        }
    }

    public static bool IsObjLoaderItem(IVideoItem item)
    {
        if (!_available || _shapeItemType == null || _shapeType2Prop == null || _objLoaderPluginType == null)
            return false;

        try
        {
            if (!_shapeItemType.IsInstanceOfType(item))
                return false;

            var shapeType = _shapeType2Prop.GetValue(item) as Type;
            return shapeType == _objLoaderPluginType;
        }
        catch
        {
            return false;
        }
    }

    public static List<ObjModelData>? TryGetModelData(object eisValue)
    {
        if (!_available) return null;

        try
        {
            var source = _eisSourceProp!.GetValue(eisValue);
            if (source == null) return null;

            if (_shapeSourceType != null && _shapeSourceType.IsInstanceOfType(source) && _shapeSourceInternalSourceField != null)
            {
                var internalSource = _shapeSourceInternalSourceField.GetValue(source);
                if (internalSource != null)
                {
                    source = internalSource;
                }
            }

            if (!_objLoaderSourceType!.IsInstanceOfType(source)) return null;

            var resolver = _visibilityResolverField!.GetValue(source);
            if (resolver == null) return null;

            if (_layersToRenderProp!.GetValue(resolver) is not IList layerList || layerList.Count == 0)
                return null;

            var results = new List<ObjModelData>(layerList.Count);
            for (int i = 0; i < layerList.Count; i++)
            {
                var layer = layerList[i];
                if (layer == null) continue;

                var modelData = ExtractModelData(layer);
                if (modelData != null)
                    results.Add(modelData);
            }

            return results.Count > 0 ? results : null;
        }
        catch (Exception ex)
        {
            Log($"TryGetModelData エラー: {ex.Message}");
            return null;
        }
    }

    private static ObjModelData? ExtractModelData(object tupleElement)
    {
        try
        {
            var tupleType = tupleElement.GetType();
            if (_cachedTupleType != tupleType)
            {
                _tupleItem1Field = tupleType.GetField("Item1");
                _tupleItem2Field = tupleType.GetField("Item2");
                _tupleItem4Field = tupleType.GetField("Item4");
                _cachedTupleType = tupleType;
            }

            if (_tupleItem2Field == null) return null;
            var resourceObj = _tupleItem2Field.GetValue(tupleElement);
            if (resourceObj == null) return null;

            IDictionary? partMaterials = null;
            if (_tupleItem1Field != null && _ldPartMaterialsProp != null)
            {
                var layerDataObj = _tupleItem1Field.GetValue(tupleElement);
                if (layerDataObj != null)
                    partMaterials = _ldPartMaterialsProp.GetValue(layerDataObj) as IDictionary;
            }

            // ボーンアニメーションのスキニング済み頂点バッファ (OverrideVB = Item4) を優先使用
            var overrideVb = _tupleItem4Field?.GetValue(tupleElement) as ID3D11Buffer;
            var vb = overrideVb ?? (_rcVertexBufferProp!.GetValue(resourceObj) as ID3D11Buffer);
            var ib = _rcIndexBufferProp!.GetValue(resourceObj) as ID3D11Buffer;
            if (vb == null || ib == null) return null;

            var indexCount = (int)(_rcIndexCountProp?.GetValue(resourceObj) ?? 0);
            var modelCenter = (Vector3)(_rcModelCenterProp!.GetValue(resourceObj) ?? Vector3.Zero);
            var modelScale = (float)(_rcModelScaleProp!.GetValue(resourceObj) ?? 1f);

            var partsArray = _rcPartsProp!.GetValue(resourceObj) as Array;
            var texturesArray = _rcPartTexturesProp!.GetValue(resourceObj) as Array;
            if (partsArray == null || partsArray.Length == 0) return null;

            var parts = new ObjPartRenderInfo[partsArray.Length];
            for (int i = 0; i < partsArray.Length; i++)
            {
                var partObj = partsArray.GetValue(i);
                if (partObj == null) continue;

                float mpMetallic = (float)(_mpMetallicField?.GetValue(partObj) ?? 0f);
                float mpRoughness = (float)(_mpRoughnessField?.GetValue(partObj) ?? 0f);
                bool hasExplicitPbr = mpMetallic > 0.001f || mpRoughness > 0.001f;

                if (partMaterials != null && partMaterials.Contains(i))
                {
                    var matObj = partMaterials[i];
                    if (matObj != null)
                    {
                        if (_pmMetallicProp?.GetValue(matObj) is double mVal)
                            mpMetallic = (float)mVal;
                        if (_pmRoughnessProp?.GetValue(matObj) is double rVal)
                            mpRoughness = (float)rVal;
                        hasExplicitPbr = true;
                    }
                }

                parts[i] = new ObjPartRenderInfo
                {
                    IndexOffset = (int)(_mpIndexOffsetField?.GetValue(partObj) ?? 0),
                    IndexCount = (int)(_mpIndexCountField?.GetValue(partObj) ?? 0),
                    BaseColor = (Vector4)(_mpBaseColorField?.GetValue(partObj) ?? Vector4.One),
                    Texture = texturesArray != null && i < texturesArray.Length
                        ? texturesArray.GetValue(i) as ID3D11ShaderResourceView
                        : null,
                    Metallic = mpMetallic,
                    Roughness = hasExplicitPbr ? Math.Clamp(mpRoughness, 0.04f, 1.0f) : -1f,
                };
            }

            return new ObjModelData
            {
                VertexBuffer = vb,
                IndexBuffer = ib,
                Parts = parts,
                ModelCenter = modelCenter,
                ModelScale = modelScale,
                IndexCount = indexCount,
            };
        }
        catch (Exception ex)
        {
            Log($"ExtractModelData エラー: {ex.Message}");
            return null;
        }
    }

    private static Type? FindType(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] ObjLoaderBridge: {msg}");
}
