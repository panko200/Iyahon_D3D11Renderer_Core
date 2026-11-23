using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Windows;
using YukkuriMovieMaker.Plugin;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

public enum TransparencyMode
{
    [Display(Name = "OIT（順序非依存・FXAA付き）")]
    OIT,

    [Display(Name = "標準（depth判定・AA有効）")]
    Standard,
}

public enum StandardDepthLayerCount
{
    [Display(Name = "2層（軽量）")]
    Two = 2,

    [Display(Name = "4層（推奨）")]
    Four = 4,

    [Display(Name = "8層（高品質）")]
    Eight = 8,
}

public enum ShadowResolution
{
    [Display(Name = "低（512px：超軽量）")]
    Low = 512,

    [Display(Name = "中（1024px：標準）")]
    Medium = 1024,

    [Display(Name = "高（2048px：綺麗・推奨）")]
    High = 2048,

    [Display(Name = "超高（4096px：高精細・重い）")]
    Ultra = 4096,
}

public enum ShadowQuality
{
    [Display(Name = "軽量（4タップPCF）")]
    Low = 0,

    [Display(Name = "標準（8タップPCF・推奨）")]
    Medium = 1,

    [Display(Name = "高品質（16タップPCF）")]
    High = 2,

    [Display(Name = "最高品質（24タップPCF・重い）")]
    Ultra = 3,
}

public enum ShadowCascadeCount
{
    [Display(Name = "1段（単一・最軽量）")]
    One = 1,

    [Display(Name = "2段（軽量CSM）")]
    Two = 2,

    [Display(Name = "3段（標準CSM・推奨）")]
    Three = 3,

    [Display(Name = "4段（最高精度CSM）")]
    Four = 4,
}

public enum ShadowMemoryBudget
{
    [Display(Name = "256 MB（省メモリ）")]
    MB256 = 256,

    [Display(Name = "512 MB（標準・推奨）")]
    MB512 = 512,

    [Display(Name = "1024 MB（多灯・4K向け）")]
    MB1024 = 1024,

    [Display(Name = "2048 MB（大規模シーン向け）")]
    MB2048 = 2048,
}

public enum ToneMappingMode
{
    [Display(Name = "GT（グランツーリスモ・推奨）")]
    GranTurismo = 0,

    [Display(Name = "ACES Filmic（映画調）")]
    AcesFilmic = 1,

    [Display(Name = "AgX（Blender 4準拠・自然）")]
    AgX = 2,

    [Display(Name = "なし（元色維持・2D重視）")]
    None = 3,
}

public enum ModelTextureLimit
{
    [Display(Name = "制限なし (原寸)")]
    Original = 0,

    [Display(Name = "4096 px")]
    Size4096 = 4096,

    [Display(Name = "2048 px (推奨・省メモリ)")]
    Size2048 = 2048,

    [Display(Name = "1024 px (超軽量)")]
    Size1024 = 1024,
}

public enum AntiAliasingMode
{
    [Display(Name = "なし (無効)")]
    Off = 0,

    [Display(Name = "FXAA（軽量・標準/OIT対応）")]
    Fxaa = 1,

    [Display(Name = "SSAA 1.5x（高精細・中負荷）")]
    Ssaa15x = 2,

    [Display(Name = "SSAA 2.0x（最高画質・高負荷）")]
    Ssaa2x = 3,
}

internal class D3D11RendererSettings : SettingsBase<D3D11RendererSettings>
{
    public override SettingsCategory Category => SettingsCategory.Other;
    public override string Name => "D3D11描画設定";
    public override bool HasSettingView => true;
    public override object? SettingView => new D3D11SettingsView();

    private TransparencyMode transparencyMode = TransparencyMode.OIT;
    private StandardDepthLayerCount standardDepthLayerCount = StandardDepthLayerCount.Four;
    private ShadowResolution shadowResolution = ShadowResolution.Medium;
    private ShadowQuality shadowQuality = ShadowQuality.Medium;
    private ShadowCascadeCount shadowCascadeCount = ShadowCascadeCount.Three;
    private ShadowMemoryBudget shadowMemoryBudget = ShadowMemoryBudget.MB512;
    private bool enableShadow = false;
    private bool enableSoftShadow = false;
    private double shadowSoftness = 1.0;
    private double ambientIntensity = 0.3;

    // ── 現代3Dレンダリング（PBR / GTAO / SSGI / Bloom / HDR） ──
    private bool enablePbr = true;
    private double iblIntensity = 0.65;
    private double defaultRoughness = 0.50;
    private bool enableSsao = true;
    private double ssaoIntensity = 1.0;
    private double ssaoRadius = 70.0;
    private bool enableSsgi = true;
    private double ssgiIntensity = 0.70;
    private bool enableBloom = true;
    private double bloomThreshold = 1.05;
    private double bloomIntensity = 0.30;
    private ToneMappingMode toneMapping = ToneMappingMode.GranTurismo;
    private double exposure = 0.0;
    private bool enableHdrInjectorIntegration = true;
    private double hdrPeakLuminance = 4.0;
    private ModelTextureLimit modelMaxTextureSize = ModelTextureLimit.Size2048;

    // ── 高画質化・アンチエイリアシング・SSR・コンタクトシャドウ・環境マップ・大気 ──
    private AntiAliasingMode antiAliasing = AntiAliasingMode.Fxaa;
    private bool enableSsr = true;
    private double ssrIntensity = 0.75;
    private double ssrMaxRoughness = 0.65;
    private bool enableContactShadow = true;
    private double contactShadowIntensity = 0.60;
    private double contactShadowLength = 45.0;
    private string environmentMapPath = string.Empty;
    private double environmentRotation = 0.0;
    private double environmentExposure = 1.0;
    private double environmentReflectionIntensity = 1.0;
    private bool showSkybox = false;
    private double skyboxBlur = 0.0;
    private bool enableFog = false;
    private double fogDensity = 0.25;
    private double fogStart = 800.0;
    private double fogEnd = 4000.0;
    private bool enableGodRays = false;
    private double godRayIntensity = 0.60;
    private bool _suppressNotify;

    public TransparencyMode TransparencyMode
    {
        get => transparencyMode;
        set => SetAndNotify(ref transparencyMode, value);
    }

    public StandardDepthLayerCount StandardDepthLayerCount
    {
        get => standardDepthLayerCount;
        set => SetAndNotify(ref standardDepthLayerCount, value);
    }

    public ShadowResolution ShadowResolution
    {
        get => shadowResolution;
        set => SetAndNotify(ref shadowResolution, value);
    }

    public ShadowQuality ShadowQuality
    {
        get => shadowQuality;
        set => SetAndNotify(ref shadowQuality, value);
    }

    public ShadowCascadeCount ShadowCascadeCount
    {
        get => shadowCascadeCount;
        set => SetAndNotify(ref shadowCascadeCount, value);
    }

    public ShadowMemoryBudget ShadowMemoryBudget
    {
        get => shadowMemoryBudget;
        set => SetAndNotify(ref shadowMemoryBudget, value);
    }

    public bool EnableShadow
    {
        get => enableShadow;
        set => SetAndNotify(ref enableShadow, value);
    }

    /// <summary>影のぼかし（Soft Shadow）を有効にするか</summary>
    public bool EnableSoftShadow
    {
        get => enableSoftShadow;
        set => SetAndNotify(ref enableSoftShadow, value);
    }

    /// <summary>影のぼかし半径倍率（0.1〜4.0）</summary>
    public double ShadowSoftness
    {
        get => shadowSoftness;
        set => SetAndNotify(ref shadowSoftness, Math.Clamp(value, 0.1, 4.0));
    }

    public double AmbientIntensity
    {
        get => ambientIntensity;
        set => SetAndNotify(ref ambientIntensity, Math.Clamp(value, 0.0, 2.0));
    }

    public bool EnablePbr
    {
        get => enablePbr;
        set => SetAndNotify(ref enablePbr, value);
    }

    public double IblIntensity
    {
        get => iblIntensity;
        set => SetAndNotify(ref iblIntensity, Math.Clamp(value, 0.0, 2.0));
    }

    public double DefaultRoughness
    {
        get => defaultRoughness;
        set => SetAndNotify(ref defaultRoughness, Math.Clamp(value, 0.05, 1.0));
    }

    public bool EnableSsao
    {
        get => enableSsao;
        set => SetAndNotify(ref enableSsao, value);
    }

    public double SsaoIntensity
    {
        get => ssaoIntensity;
        set => SetAndNotify(ref ssaoIntensity, Math.Clamp(value, 0.0, 2.5));
    }

    public double SsaoRadius
    {
        get => ssaoRadius;
        set => SetAndNotify(ref ssaoRadius, Math.Clamp(value, 10.0, 300.0));
    }

    public bool EnableSsgi
    {
        get => enableSsgi;
        set => SetAndNotify(ref enableSsgi, value);
    }

    public double SsgiIntensity
    {
        get => ssgiIntensity;
        set => SetAndNotify(ref ssgiIntensity, Math.Clamp(value, 0.0, 2.5));
    }

    public bool EnableBloom
    {
        get => enableBloom;
        set => SetAndNotify(ref enableBloom, value);
    }

    public double BloomThreshold
    {
        get => bloomThreshold;
        set => SetAndNotify(ref bloomThreshold, Math.Clamp(value, 0.2, 5.0));
    }

    public double BloomIntensity
    {
        get => bloomIntensity;
        set => SetAndNotify(ref bloomIntensity, Math.Clamp(value, 0.0, 2.0));
    }

    public ToneMappingMode ToneMapping
    {
        get => toneMapping;
        set => SetAndNotify(ref toneMapping, value);
    }

    public double Exposure
    {
        get => exposure;
        set => SetAndNotify(ref exposure, Math.Clamp(value, -3.0, 3.0));
    }

    public bool EnableHdrInjectorIntegration
    {
        get => enableHdrInjectorIntegration;
        set => SetAndNotify(ref enableHdrInjectorIntegration, value);
    }

    public double HdrPeakLuminance
    {
        get => hdrPeakLuminance;
        set => SetAndNotify(ref hdrPeakLuminance, Math.Clamp(value, 1.0, 12.5));
    }

    public ModelTextureLimit ModelMaxTextureSize
    {
        get => modelMaxTextureSize;
        set => SetAndNotify(ref modelMaxTextureSize, value);
    }

    public AntiAliasingMode AntiAliasing
    {
        get => antiAliasing;
        set => SetAndNotify(ref antiAliasing, value);
    }

    public bool EnableSsr
    {
        get => enableSsr;
        set => SetAndNotify(ref enableSsr, value);
    }

    public double SsrIntensity
    {
        get => ssrIntensity;
        set => SetAndNotify(ref ssrIntensity, Math.Clamp(value, 0.0, 2.0));
    }

    public double SsrMaxRoughness
    {
        get => ssrMaxRoughness;
        set => SetAndNotify(ref ssrMaxRoughness, Math.Clamp(value, 0.1, 0.95));
    }

    public bool EnableContactShadow
    {
        get => enableContactShadow;
        set => SetAndNotify(ref enableContactShadow, value);
    }

    public double ContactShadowIntensity
    {
        get => contactShadowIntensity;
        set => SetAndNotify(ref contactShadowIntensity, Math.Clamp(value, 0.0, 1.5));
    }

    public double ContactShadowLength
    {
        get => contactShadowLength;
        set => SetAndNotify(ref contactShadowLength, Math.Clamp(value, 5.0, 200.0));
    }

    public string EnvironmentMapPath
    {
        get => environmentMapPath;
        set => SetAndNotify(ref environmentMapPath, value ?? string.Empty);
    }

    public double EnvironmentRotation
    {
        get => environmentRotation;
        set => SetAndNotify(ref environmentRotation, Math.Clamp(value, 0.0, 360.0));
    }

    public double EnvironmentExposure
    {
        get => environmentExposure;
        set => SetAndNotify(ref environmentExposure, Math.Clamp(value, 0.1, 5.0));
    }

    public double EnvironmentReflectionIntensity
    {
        get => environmentReflectionIntensity;
        set => SetAndNotify(ref environmentReflectionIntensity, Math.Clamp(value, 0.0, 5.0));
    }

    public bool ShowSkybox
    {
        get => showSkybox;
        set => SetAndNotify(ref showSkybox, value);
    }

    public double SkyboxBlur
    {
        get => skyboxBlur;
        set => SetAndNotify(ref skyboxBlur, Math.Clamp(value, 0.0, 1.0));
    }

    public bool EnableFog
    {
        get => enableFog;
        set => SetAndNotify(ref enableFog, value);
    }

    public double FogDensity
    {
        get => fogDensity;
        set => SetAndNotify(ref fogDensity, Math.Clamp(value, 0.0, 1.0));
    }

    public double FogStart
    {
        get => fogStart;
        set => SetAndNotify(ref fogStart, Math.Clamp(value, 0.0, 20000.0));
    }

    public double FogEnd
    {
        get => fogEnd;
        set => SetAndNotify(ref fogEnd, Math.Clamp(value, 100.0, 50000.0));
    }

    public bool EnableGodRays
    {
        get => enableGodRays;
        set => SetAndNotify(ref enableGodRays, value);
    }

    public double GodRayIntensity
    {
        get => godRayIntensity;
        set => SetAndNotify(ref godRayIntensity, Math.Clamp(value, 0.0, 3.0));
    }

    public void ApplyQualityPreset(string presetName)
    {
        _suppressNotify = true;
        try
        {
            switch (presetName.ToLowerInvariant())
            {
                case "low":
                    ShadowResolution = ShadowResolution.Low;
                    ShadowQuality = ShadowQuality.Low;
                    EnableSoftShadow = false;
                    EnableSsao = false;
                    EnableSsgi = false;
                    EnableSsr = false;
                    EnableContactShadow = false;
                    EnableBloom = false;
                    AntiAliasing = AntiAliasingMode.Off;
                    ModelMaxTextureSize = ModelTextureLimit.Size1024;
                    break;

                case "standard":
                    ShadowResolution = ShadowResolution.Medium;
                    ShadowQuality = ShadowQuality.Medium;
                    EnableSoftShadow = false;
                    EnableSsao = true;
                    SsaoIntensity = 1.0;
                    EnableSsgi = false;
                    EnableSsr = false;
                    EnableContactShadow = true;
                    ContactShadowIntensity = 0.50;
                    EnableBloom = true;
                    BloomIntensity = 0.25;
                    AntiAliasing = AntiAliasingMode.Fxaa;
                    ModelMaxTextureSize = ModelTextureLimit.Size2048;
                    break;

                case "high":
                    ShadowResolution = ShadowResolution.High;
                    ShadowQuality = ShadowQuality.High;
                    EnableSoftShadow = true;
                    EnableSsao = true;
                    SsaoIntensity = 1.0;
                    EnableSsgi = true;
                    SsgiIntensity = 0.70;
                    EnableSsr = true;
                    SsrIntensity = 0.75;
                    SsrMaxRoughness = 0.65;
                    EnableContactShadow = true;
                    ContactShadowIntensity = 0.60;
                    EnableBloom = true;
                    BloomIntensity = 0.30;
                    AntiAliasing = AntiAliasingMode.Fxaa;
                    ModelMaxTextureSize = ModelTextureLimit.Size2048;
                    break;

                case "cinematic":
                    ShadowResolution = ShadowResolution.High;
                    ShadowQuality = ShadowQuality.Ultra;
                    EnableSoftShadow = true;
                    EnableSsao = true;
                    SsaoIntensity = 1.15;
                    EnableSsgi = true;
                    SsgiIntensity = 0.90;
                    EnableSsr = true;
                    SsrIntensity = 0.90;
                    SsrMaxRoughness = 0.75;
                    EnableContactShadow = true;
                    ContactShadowIntensity = 0.70;
                    EnableBloom = true;
                    BloomIntensity = 0.35;
                    AntiAliasing = AntiAliasingMode.Ssaa15x;
                    ModelMaxTextureSize = ModelTextureLimit.Size4096;
                    break;
            }
        }
        finally
        {
            _suppressNotify = false;
        }
        NotifyTimelineChanged();
    }

    private void SetAndNotify<T>(ref T storage, T value, [CallerMemberName] string name = "")
    {
        if (Set(ref storage, value, name) && !_suppressNotify)
        {
            NotifyTimelineChanged();
        }
    }

    private static void NotifyTimelineChanged()
    {
        try
        {
            Application.Current?.Dispatcher?.BeginInvoke(DepthSortVideoPlayerPatch.SetTimelineChanged);
        }
        catch { }
    }

    public override void Initialize() { }
}
