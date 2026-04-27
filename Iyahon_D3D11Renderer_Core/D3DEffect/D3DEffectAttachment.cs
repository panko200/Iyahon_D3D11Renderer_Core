using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect;

/// <summary>
/// アイテムに紐づくD3Dエフェクトの実行時データ。
/// DepthSortRendererPatch がレンダリング時にこの情報を参照する。
/// </summary>
internal sealed class D3DEffectAttachment
{
    /// <summary>選択されたエフェクトID</summary>
    public string? EffectId { get; set; }

    /// <summary>エフェクトの実行時パラメータ値</summary>
    public Dictionary<string, float> FloatParams { get; } = new();

    /// <summary>エフェクトの実行時boolパラメータ値</summary>
    public Dictionary<string, bool> BoolParams { get; } = new();

    /// <summary>エフェクトが有効かどうか</summary>
    public bool IsActive => !string.IsNullOrEmpty(EffectId);
}

/// <summary>
/// IVideoItem に D3DEffectAttachment を紐づけるためのストア。
/// ConditionalWeakTable で GC に優しい設計。
/// </summary>
internal static class D3DEffectAttachmentStore
{
    private static readonly ConditionalWeakTable<IVideoItem, D3DEffectAttachment> _store = new();

    /// <summary>
    /// アイテムの D3DEffectAttachment を取得する。なければ作成。
    /// </summary>
    public static D3DEffectAttachment GetOrCreate(IVideoItem item)
    {
        return _store.GetOrCreateValue(item);
    }

    /// <summary>
    /// アイテムの D3DEffectAttachment を取得する。なければ null。
    /// </summary>
    public static D3DEffectAttachment? Get(IVideoItem item)
    {
        if (_store.TryGetValue(item, out var attachment))
            return attachment;
        return null;
    }

    /// <summary>
    /// アイテムの D3DEffectAttachment を設定する。
    /// </summary>
    public static void Set(IVideoItem item, D3DEffectAttachment attachment)
    {
        _store.AddOrUpdate(item, attachment);
    }
}
