using System;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect;

/// <summary>
/// D3Dエフェクト対応の映像エフェクトが実装するマーカーインターフェース。
/// VideoEffectBase のサブクラスがこのインターフェースを実装することで、
/// DepthSortRendererPatch が自動検出し、D3D11 レンダリングパイプラインに組み込む。
/// 
/// 第三者プラグインでの使用例:
/// <code>
/// [VideoEffect("My3DEffect", new[] { "D3D" }, new[] { "3D" })]
/// public class My3DVideoEffect : VideoEffectBase, ID3DVideoEffect
/// {
///     public string D3DEffectId => typeof(My3DEffect).FullName;
///     public void ConfigureEffect(ID3DEffect effect, long frame, long length, int fps)
///     {
///         if (effect is My3DEffect my) { my.Param = (float)MyParam.GetValue(frame, length, fps); }
///     }
///     // ... Animation properties, CreateVideoEffect, etc.
/// }
/// </code>
/// </summary>
public interface ID3DVideoEffect
{
    /// <summary>
    /// エフェクトID。D3DEffectRegistry で登録した ID と一致させること。
    /// 通常は typeof(YourEffect).FullName を使用する。
    /// </summary>
    string D3DEffectId { get; }

    /// <summary>
    /// 描画前に呼ばれ、エフェクト固有パラメータを設定する。
    /// Animation プロパティの現在値を effect のプロパティに書き込む。
    /// </summary>
    /// <param name="effect">パラメータ設定対象の ID3DEffect インスタンス</param>
    /// <param name="frame">アイテム内の現在フレーム位置</param>
    /// <param name="length">アイテムの長さ（フレーム数）</param>
    /// <param name="fps">FPS</param>
    void ConfigureEffect(ID3DEffect effect, long frame, long length, int fps);
}
