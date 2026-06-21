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

    /// <summary>ObjLoaderプラグインが検出できたかどうか</summary>
    public static bool IsAvailable => _initialized && _available;

    /// <summary>
    /// 初期化。ObjLoaderの型をリフレクションで検索しキャッシュする。
    /// ObjLoaderがインストールされていない場合は _available = false になる。
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // ObjLoaderPlugin 型を検索
            _objLoaderPluginType = FindType("ObjLoader.Plugin.ObjLoaderPlugin");
            if (_objLoaderPluginType == null)
            {
                Log("ObjLoaderPlugin 未検出。ObjLoader連携は無効。");
                return;
            }
            Log($"ObjLoaderPlugin 検出: {_objLoaderPluginType.Assembly.GetName().Name}");

            // ShapeItem 型
            _shapeItemType = FindType("YukkuriMovieMaker.Project.Items.ShapeItem");
            if (_shapeItemType != null)
            {
                _shapeType2Prop = _shapeItemType.GetProperty("ShapeType2",
                    BindingFlags.Instance | BindingFlags.Public);
            }

            // ObjLoaderSource 型
            _objLoaderSourceType = FindType("ObjLoader.Rendering.Core.ObjLoaderSource");
            if (_objLoaderSourceType != null)
            {
                _visibilityResolverField = _objLoaderSourceType.GetField("_visibilityResolver",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            // VisibilityAndSkinningResolver → LayersToRender
            var resolverType = FindType("ObjLoader.Rendering.Core.Resolvers.VisibilityAndSkinningResolver");
            if (resolverType != null)
            {
                _layersToRenderProp = resolverType.GetProperty("LayersToRender",
                    BindingFlags.Instance | BindingFlags.Public);
            }

            // GpuResourceCacheItem プロパティ
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

            // ModelPart フィールド
            var modelPartType = FindType("ObjLoader.Core.Models.ModelPart");
            if (modelPartType != null)
            {
                _mpIndexOffsetField = modelPartType.GetField("IndexOffset");
                _mpIndexCountField = modelPartType.GetField("IndexCount");
                _mpBaseColorField = modelPartType.GetField("BaseColor");
            }

            // EffectedItemSource の Source プロパティ
            var eisType = FindType("YukkuriMovieMaker.Player.Video.EffectedItemSource");
            if (eisType != null)
            {
                _eisSourceProp = eisType.GetProperty("Source",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            // ShapeSource 型と内部の source フィールド
            _shapeSourceType = FindType("YukkuriMovieMaker.Player.Video.Items.ShapeSource");
            if (_shapeSourceType != null)
            {
                _shapeSourceInternalSourceField = _shapeSourceType.GetField("source",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            // 全て揃っているか確認
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
            if (!_available)
            {
                Log($"  ShapeItem={_shapeItemType != null} ShapeType2={_shapeType2Prop != null}");
                Log($"  ObjLoaderSource={_objLoaderSourceType != null} resolver={_visibilityResolverField != null}");
                Log($"  LayersToRender={_layersToRenderProp != null}");
                Log($"  VB={_rcVertexBufferProp != null} IB={_rcIndexBufferProp != null}");
                Log($"  Parts={_rcPartsProp != null} Textures={_rcPartTexturesProp != null}");
                Log($"  Center={_rcModelCenterProp != null} Scale={_rcModelScaleProp != null}");
                Log($"  EIS.Source={_eisSourceProp != null}");
                Log($"  ShapeSource={_shapeSourceType != null} ShapeSource.source={_shapeSourceInternalSourceField != null}");
            }
        }
        catch (Exception ex)
        {
            Log($"初期化エラー: {ex.Message}");
            _available = false;
        }
    }

    /// <summary>
    /// 指定アイテムがObjLoaderの図形かどうかを判定する。
    /// </summary>
    public static bool IsObjLoaderItem(IVideoItem item)
    {
        if (!_available || _shapeItemType == null || _shapeType2Prop == null || _objLoaderPluginType == null)
            return false;

        try
        {
            var itemType = item.GetType();
            bool isShapeItem = _shapeItemType.IsInstanceOfType(item);
            Log($"[診断] item型={itemType.FullName}, IsShapeItem={isShapeItem}");

            if (!isShapeItem)
            {
                // ShapeItem でない場合、全インターフェースと基底型を列挙
                Log($"[診断]   基底型: {itemType.BaseType?.FullName}");
                return false;
            }

            var shapeType = _shapeType2Prop.GetValue(item) as Type;
            Log($"[診断] ShapeType2={shapeType?.FullName}, ObjLoaderPlugin={_objLoaderPluginType.FullName}");
            Log($"[診断] 型一致={shapeType == _objLoaderPluginType}, AssemblyMatch={shapeType?.Assembly == _objLoaderPluginType.Assembly}");

            return shapeType == _objLoaderPluginType;
        }
        catch (Exception ex)
        {
            Log($"[診断] IsObjLoaderItem 例外: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// EffectedItemSourceからObjLoaderのモデルデータを取得する。
    /// ObjLoaderSource → _visibilityResolver → LayersToRender → GpuResourceCacheItem の順でアクセス。
    /// </summary>
    /// <returns>取得できた場合はモデルデータのリスト、失敗時はnull</returns>
    public static List<ObjModelData>? TryGetModelData(object eisValue)
    {
        if (!_available) return null;

        try
        {
            Log($"[診断] TryGetModelData: eisValue型={eisValue.GetType().FullName}");

            // EffectedItemSource → Source (ShapeSource or ObjLoaderSource)
            var source = _eisSourceProp!.GetValue(eisValue);
            Log($"[診断] TryGetModelData: Source={source?.GetType().FullName ?? "null"}");

            if (source == null)
            {
                Log("[診断] TryGetModelData: Source is null");
                return null;
            }

            // ShapeSource の場合は内部の source フィールドを取り出す
            if (_shapeSourceType != null && _shapeSourceType.IsInstanceOfType(source))
            {
                Log("[診断] TryGetModelData: ShapeSource検出。内部の source フィールドの取得を試みます。");
                if (_shapeSourceInternalSourceField != null)
                {
                    var internalSource = _shapeSourceInternalSourceField.GetValue(source);
                    Log($"[診断] TryGetModelData: ShapeSource.source={internalSource?.GetType().FullName ?? "null"}");
                    if (internalSource != null)
                    {
                        source = internalSource;
                    }
                }
                else
                {
                    Log("[診断] TryGetModelData: ShapeSource.source フィールドの情報がキャッシュされていません。");
                }
            }

            bool isObjLoaderSource = _objLoaderSourceType!.IsInstanceOfType(source);
            Log($"[診断] TryGetModelData: IsObjLoaderSource={isObjLoaderSource}");

            if (!isObjLoaderSource)
            {
                // Source の型情報を詳しくログ
                Log($"[診断]   Source実際の型={source.GetType().FullName}");
                Log($"[診断]   期待する型={_objLoaderSourceType.FullName}");
                Log($"[診断]   Source基底型={source.GetType().BaseType?.FullName}");

                // IShapeSource2 を実装しているか?
                var interfaces = source.GetType().GetInterfaces();
                foreach (var iface in interfaces)
                    Log($"[診断]   implements: {iface.FullName}");

                // もしかしたらSourceはラッパーで、内側にObjLoaderSourceがある？
                // EffectedItemSource の全プロパティ・フィールドを列挙
                Log("[診断]   EIS のプロパティ一覧:");
                foreach (var prop in eisValue.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        var val = prop.GetValue(eisValue);
                        Log($"[診断]     {prop.Name}: {val?.GetType().Name ?? "null"}");
                    }
                    catch { Log($"[診断]     {prop.Name}: (例外)"); }
                }

                return null;
            }

            // ObjLoaderSource → _visibilityResolver
            var resolver = _visibilityResolverField!.GetValue(source);
            Log($"[診断] TryGetModelData: resolver={resolver?.GetType().FullName ?? "null"}");
            if (resolver == null) return null;

            // VisibilityAndSkinningResolver → LayersToRender
            var layersToRender = _layersToRenderProp!.GetValue(resolver);
            Log($"[診断] TryGetModelData: layersToRender={layersToRender?.GetType().FullName ?? "null"}");

            if (layersToRender is not IList layerList)
            {
                Log("[診断] TryGetModelData: layersToRender is not IList");
                return null;
            }

            Log($"[診断] TryGetModelData: layerList.Count={layerList.Count}");
            if (layerList.Count == 0) return null;

            var results = new List<ObjModelData>();

            for (int i = 0; i < layerList.Count; i++)
            {
                var layer = layerList[i];
                if (layer == null) { Log($"[診断] layer[{i}]=null"); continue; }
                Log($"[診断] layer[{i}] 型={layer.GetType().FullName}");

                var modelData = ExtractModelData(layer);
                Log($"[診断] layer[{i}] ExtractModelData={modelData != null}");
                if (modelData != null)
                    results.Add(modelData);
            }

            Log($"[診断] TryGetModelData: 結果={results.Count}件");
            return results.Count > 0 ? results : null;
        }
        catch (Exception ex)
        {
            Log($"TryGetModelData エラー: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    /// <summary>
    /// LayersToRender のタプル要素からGpuResourceCacheItemを抽出し、ObjModelDataに変換する。
    /// ValueTupleなので .Item2 でGpuResourceCacheItemにアクセスする。
    /// </summary>
    private static ObjModelData? ExtractModelData(object tupleElement)
    {
        try
        {
            // ValueTuple<LayerData, GpuResourceCacheItem, LayerState, ID3D11Buffer?>
            // Item2 = GpuResourceCacheItem
            var tupleType = tupleElement.GetType();
            var item2Field = tupleType.GetField("Item2");
            if (item2Field == null) return null;

            var resourceObj = item2Field.GetValue(tupleElement);
            if (resourceObj == null) return null;

            // GpuResourceCacheItem のプロパティを取得
            var vb = _rcVertexBufferProp!.GetValue(resourceObj) as ID3D11Buffer;
            var ib = _rcIndexBufferProp!.GetValue(resourceObj) as ID3D11Buffer;
            if (vb == null || ib == null) return null;

            var indexCount = (int)(_rcIndexCountProp?.GetValue(resourceObj) ?? 0);
            var modelCenter = (Vector3)(_rcModelCenterProp!.GetValue(resourceObj) ?? Vector3.Zero);
            var modelScale = (float)(_rcModelScaleProp!.GetValue(resourceObj) ?? 1f);

            // Parts 配列を読み取る
            var partsArray = _rcPartsProp!.GetValue(resourceObj) as Array;
            var texturesArray = _rcPartTexturesProp!.GetValue(resourceObj) as Array;

            if (partsArray == null || partsArray.Length == 0)
                return null;

            var parts = new ObjPartRenderInfo[partsArray.Length];
            for (int i = 0; i < partsArray.Length; i++)
            {
                var partObj = partsArray.GetValue(i);
                if (partObj == null) continue;

                parts[i] = new ObjPartRenderInfo
                {
                    IndexOffset = (int)(_mpIndexOffsetField?.GetValue(partObj) ?? 0),
                    IndexCount = (int)(_mpIndexCountField?.GetValue(partObj) ?? 0),
                    BaseColor = (Vector4)(_mpBaseColorField?.GetValue(partObj) ?? Vector4.One),
                    Texture = texturesArray != null && i < texturesArray.Length
                        ? texturesArray.GetValue(i) as ID3D11ShaderResourceView
                        : null,
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
