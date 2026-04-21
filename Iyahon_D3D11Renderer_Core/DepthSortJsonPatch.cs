using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Reflection;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// DefaultContractResolver.CreateProperties を Postfix パッチして
/// VisualItem (およびそのサブクラス) の JSON スキーマに
/// "_useD3D11DepthSort" フィールドを追加する。
///
/// YMM4 の保存・読み込みはすべてこの経路を通るため、
/// Timeline の保存処理を直接パッチする必要がない。
/// </summary>
[HarmonyPatch]
internal static class DepthSortJsonPatch
{
    private const string FieldName = "_useD3D11DepthSort";

    private static MethodBase TargetMethod()
        => AccessTools.Method(
            typeof(DefaultContractResolver),
            "CreateProperties",
            new[] { typeof(Type), typeof(MemberSerialization) });

    private static void Postfix(Type type, ref IList<JsonProperty> __result)
    {
        // VisualItem を継承する型のみ対象
        if (!typeof(VisualItem).IsAssignableFrom(type)) return;

        // 既に注入済みならスキップ（二重適用防止）
        foreach (var p in __result)
            if (p.PropertyName == FieldName) return;

        var injected = new JsonProperty
        {
            PropertyName  = FieldName,
            PropertyType  = typeof(bool),
            DeclaringType = type,
            ValueProvider = new DepthSortValueProvider(),
            Readable      = true,
            Writable      = true,
            Required      = Required.Default,
            NullValueHandling  = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Populate,
            DefaultValue  = false,
        };

        __result.Add(injected);
    }
}
