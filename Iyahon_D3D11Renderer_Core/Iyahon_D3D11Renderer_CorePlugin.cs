using HarmonyLib;
using System;
using System.Reflection;
using System.Windows;
using YukkuriMovieMaker.Plugin;
using Iyahon_D3D11Renderer_Core.D3DEffect;
using Iyahon_D3D11Renderer_Core.D3DEffect.Effects;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

public class Iyahon_D3D11Renderer_CorePlugin : IPlugin
{
    private static bool _initialized;

    static Iyahon_D3D11Renderer_CorePlugin()
    {
        if (_initialized) return;
        Initialize();
        _initialized = true;
    }

    public string Name => "イヤホンD3D11レンダラー";

    private static void Initialize()
    {
        try
        {
            var harmony = new Harmony("com.iyahon.d3d11renderercore");

            // 属性ベースのパッチ (DepthSortJsonPatch, DepthSortClonePatch)
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            // 手動パッチ
            DepthSortRendererPatch.Apply(harmony);
            DepthSortVideoPlayerPatch.Apply(harmony);
            DepthSortPropertiesEditorPatch.Apply(harmony);

            Log("初期化完了");

            // D3Dエフェクトの組み込みエフェクトを登録
            D3DEffectRegistry.Register<CubeD3DEffect>();
            D3DEffectRegistry.Register<SphereD3DEffect>();
            D3DEffectRegistry.Register<ExtrusionD3DEffect>();
            Log("D3Dエフェクト登録完了（立方体, 球, 押し出し）");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Iyahon_D3D11Renderer_Core 初期化エラー:\n{ex.Message}\n\n{ex.StackTrace}",
                "Iyahon_D3D11Renderer_Core Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    internal static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] {msg}");
}



