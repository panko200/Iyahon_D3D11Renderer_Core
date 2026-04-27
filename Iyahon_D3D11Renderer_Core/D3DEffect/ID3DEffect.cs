using System;
using System.Collections.Generic;
using Vortice.Direct3D11;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect;

/// <summary>
/// D3Dエフェクトのパラメータ。
/// エフェクトに渡す values（アニメーション値等）を格納する。
/// </summary>
public class D3DEffectParameters
{
    /// <summary>エフェクト固有のfloatパラメータ</summary>
    public Dictionary<string, float> FloatParams { get; } = new();

    /// <summary>エフェクト固有のboolパラメータ</summary>
    public Dictionary<string, bool> BoolParams { get; } = new();

    /// <summary>テクスチャ幅（ピクセル）</summary>
    public int TextureWidth { get; set; }

    /// <summary>テクスチャ高さ（ピクセル）</summary>
    public int TextureHeight { get; set; }

    /// <summary>画面幅の半分</summary>
    public float HalfScreenWidth { get; set; }

    /// <summary>画面高さの半分</summary>
    public float HalfScreenHeight { get; set; }

    public float GetFloat(string key, float defaultValue = 0f)
        => FloatParams.TryGetValue(key, out var v) ? v : defaultValue;

    public bool GetBool(string key, bool defaultValue = false)
        => BoolParams.TryGetValue(key, out var v) ? v : defaultValue;
}

/// <summary>
/// D3Dエフェクトの情報（レジストリ用）。
/// </summary>
public sealed class D3DEffectInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required Type EffectType { get; init; }
}

/// <summary>
/// 外部プラグインが実装するD3Dエフェクトのインターフェース。
/// 各エフェクトは入力テクスチャを受け取り、D3D11でジオメトリを描画する。
/// </summary>
public interface ID3DEffect : IDisposable
{
    /// <summary>エフェクト名（表示用）</summary>
    string Name { get; }

    /// <summary>カテゴリ（UI分類用）</summary>
    string Category { get; }

    /// <summary>
    /// D3D11リソースを初期化する。
    /// デバイスが変更された場合に再呼び出しされる。
    /// </summary>
    void Initialize(ID3D11Device device, ID3D11DeviceContext ctx);

    /// <summary>
    /// エフェクトのジオメトリを描画する。
    /// 呼び出し側で RenderTarget, DepthStencil, Viewport, BlendState 等は設定済み。
    /// エフェクトは VS/PS を設定し、入力テクスチャを使って描画する。
    /// </summary>
    /// <param name="ctx">デバイスコンテキスト</param>
    /// <param name="device">D3D11デバイス</param>
    /// <param name="inputSrv">入力テクスチャの SRV</param>
    /// <param name="cbPerObject">PerObject 定数バッファ（WorldMatrix等を書き込み済み）</param>
    /// <param name="parameters">エフェクトパラメータ</param>
    void Render(ID3D11DeviceContext ctx, ID3D11Device device,
                ID3D11ShaderResourceView inputSrv,
                ID3D11Buffer cbPerObject,
                D3DEffectParameters parameters);

    /// <summary>
    /// このエフェクトが公開するパラメータ定義の一覧。
    /// UI生成に使用。
    /// </summary>
    IReadOnlyList<D3DEffectParameterDefinition> GetParameterDefinitions();
}

/// <summary>
/// エフェクトパラメータの定義（UI生成用）。
/// </summary>
public sealed class D3DEffectParameterDefinition
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required D3DEffectParameterType Type { get; init; }
    public float DefaultValue { get; init; }
    public float MinValue { get; init; }
    public float MaxValue { get; init; } = 1000f;
    public string? GroupName { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// パラメータの型。
/// </summary>
public enum D3DEffectParameterType
{
    Float,
    Bool,
}
