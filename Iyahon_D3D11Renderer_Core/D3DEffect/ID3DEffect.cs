using System;
using System.Numerics;
using Vortice.Direct3D11;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect;

/// <summary>
/// D3D描画時に各エフェクトに渡される共通コンテキスト。
/// エフェクト固有パラメータは各 ID3DEffect 実装のプロパティで設定する。
/// </summary>
public class D3DRenderContext
{
    /// <summary>ワールド変換行列</summary>
    public Matrix4x4 WorldMatrix { get; set; }

    /// <summary>テクスチャ幅（ピクセル）</summary>
    public int TextureWidth { get; set; }

    /// <summary>テクスチャ高さ（ピクセル）</summary>
    public int TextureHeight { get; set; }

    /// <summary>画面幅の半分</summary>
    public float HalfScreenWidth { get; set; }

    /// <summary>画面高さの半分</summary>
    public float HalfScreenHeight { get; set; }

    /// <summary>不透明度 (0.0〜1.0)</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>αクリップ閾値</summary>
    public float AlphaThreshold { get; set; } = 0.004f;

    /// <summary>YMM4のCamera行列（ビュー行列相当）。カメラ位置計算に使用。</summary>
    public Matrix4x4 CameraMatrix { get; set; } = Matrix4x4.Identity;
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
/// エフェクト固有パラメータは実装クラスのプロパティとして公開し、
/// 対応する ID3DVideoEffect.ConfigureEffect() で設定する。
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
    /// エフェクト固有パラメータは事前にプロパティ経由で設定済みであること。
    /// </summary>
    void Render(ID3D11DeviceContext ctx, ID3D11Device device,
                ID3D11ShaderResourceView inputSrv,
                D3DRenderContext renderContext);
}
