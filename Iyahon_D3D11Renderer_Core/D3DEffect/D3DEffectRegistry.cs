using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect;

/// <summary>
/// D3Dエフェクトのレジストリ。
/// 組み込みエフェクトおよび外部プラグインのエフェクトを登録・管理する。
/// 
/// 第三者プラグインは以下のように登録する:
/// <code>
/// D3DEffectRegistry.Register&lt;MyCustomEffect&gt;();
/// </code>
/// </summary>
public static class D3DEffectRegistry
{
    private static readonly Dictionary<string, D3DEffectInfo> _effects = new();
    private static readonly object _lock = new();

    /// <summary>
    /// エフェクトを登録する。
    /// ID はエフェクトの型のフルネームを使用。
    /// </summary>
    public static void Register<T>() where T : ID3DEffect, new()
    {
        var instance = new T();
        var info = new D3DEffectInfo
        {
            Id = typeof(T).FullName ?? typeof(T).Name,
            Name = instance.Name,
            Category = instance.Category,
            EffectType = typeof(T),
        };
        instance.Dispose();

        lock (_lock)
        {
            _effects[info.Id] = info;
        }

        System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] D3DEffectRegistry: 登録 '{info.Name}' (ID={info.Id})");
    }

    /// <summary>
    /// エフェクトを登録する（型を指定）。
    /// </summary>
    public static void Register(Type effectType)
    {
        if (!typeof(ID3DEffect).IsAssignableFrom(effectType))
            throw new ArgumentException($"{effectType.Name} は ID3DEffect を実装していません。");

        if (effectType.GetConstructor(Type.EmptyTypes) == null)
            throw new ArgumentException($"{effectType.Name} にパラメータなしコンストラクタがありません。");

        var instance = (ID3DEffect)Activator.CreateInstance(effectType)!;
        var info = new D3DEffectInfo
        {
            Id = effectType.FullName ?? effectType.Name,
            Name = instance.Name,
            Category = instance.Category,
            EffectType = effectType,
        };
        instance.Dispose();

        lock (_lock)
        {
            _effects[info.Id] = info;
        }

        System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] D3DEffectRegistry: 登録 '{info.Name}' (ID={info.Id})");
    }

    /// <summary>
    /// 登録済みエフェクトの一覧を取得する。
    /// </summary>
    public static IReadOnlyList<D3DEffectInfo> GetRegisteredEffects()
    {
        lock (_lock)
        {
            return _effects.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// 指定IDのエフェクトのインスタンスを生成する。
    /// </summary>
    public static ID3DEffect? CreateEffect(string effectId)
    {
        lock (_lock)
        {
            if (!_effects.TryGetValue(effectId, out var info))
                return null;

            try
            {
                return (ID3DEffect)Activator.CreateInstance(info.EffectType)!;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] D3DEffectRegistry: エフェクト生成失敗 ID={effectId}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 指定IDのエフェクト情報を取得する。
    /// </summary>
    public static D3DEffectInfo? GetEffectInfo(string effectId)
    {
        lock (_lock)
        {
            return _effects.TryGetValue(effectId, out var info) ? info : null;
        }
    }
}
