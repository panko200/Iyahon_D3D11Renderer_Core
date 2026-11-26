using System;
using System.Linq;
using System.Reflection;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// リフレクション経由で HDRInjector プラグインの有無と EnableHdr 設定を検出するブリッジ。
/// HDRInjector 未導入環境でもアセンブリ参照なしで安全に動作する。
/// </summary>
internal static class HdrInjectorBridge
{
    private static bool _initialized;
    private static bool _available;
    private static PropertyInfo? _defaultProp;
    private static PropertyInfo? _enableHdrProp;

    public static bool IsAvailable => _initialized && _available;

    public static bool IsHdrEnabledInInjector
    {
        get
        {
            if (!_initialized) Initialize();
            if (!_available || _defaultProp == null || _enableHdrProp == null)
                return false;

            try
            {
                var settingsObj = _defaultProp.GetValue(null);
                if (settingsObj == null) return false;
                return _enableHdrProp.GetValue(settingsObj) is bool b && b;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsHdrActive =>
        IsAvailable
        && IsHdrEnabledInInjector
        && D3D11RendererSettings.Default.EnableHdrInjectorIntegration;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            var settingsType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "HDRInjector.Settings.HdrInjectorSettings");

            if (settingsType == null)
            {
                Log("HDRInjectorSettings 未検出。SDR出力モードで動作します。");
                return;
            }

            _enableHdrProp = settingsType.GetProperty("EnableHdr", BindingFlags.Instance | BindingFlags.Public);

            Type? t = settingsType;
            while (t != null && _defaultProp == null)
            {
                _defaultProp = t.GetProperty("Default", BindingFlags.Static | BindingFlags.Public);
                t = t.BaseType;
            }

            _available = _defaultProp != null && _enableHdrProp != null;
            Log($"初期化完了: available={_available}, EnableHdr={IsHdrEnabledInInjector}");
        }
        catch (Exception ex)
        {
            Log($"初期化エラー: {ex.Message}");
            _available = false;
        }
    }

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] HdrInjectorBridge: {msg}");
}
