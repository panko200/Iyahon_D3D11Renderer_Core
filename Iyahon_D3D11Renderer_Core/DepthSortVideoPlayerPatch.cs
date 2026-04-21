using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

internal static class DepthSortVideoPlayerPatch
{
    private static readonly Type? _playerType =
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == "YukkuriMovieMaker.Player.TimelineVideoPlayer");

    private static readonly FieldInfo? _isTimelineChangedField =
        _playerType?.GetField("isTimelineChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly ConcurrentBag<WeakReference> _instances = new();

    internal static void Apply(Harmony harmony)
    {
        if (_playerType == null) { Log("TimelineVideoPlayer 型が見つかりません。"); return; }
        if (_isTimelineChangedField == null) { Log("isTimelineChanged が見つかりません。"); return; }

        var ctors = _playerType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (ctors.Length == 0) { Log("コンストラクタが見つかりません。"); return; }

        harmony.Patch(ctors[0], postfix: new HarmonyMethod(
            typeof(DepthSortVideoPlayerPatch), nameof(CtorPostfix)));

        Log("パッチ適用完了。");
    }

    private static void CtorPostfix(object __instance)
        => _instances.Add(new WeakReference(__instance));

    internal static void SetTimelineChanged()
    {
        if (_isTimelineChangedField == null) return;
        foreach (var wr in _instances)
        {
            var instance = wr.Target;
            if (instance == null) continue;
            try { _isTimelineChangedField.SetValue(instance, true); }
            catch { }
        }
    }

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] DepthSortVideoPlayerPatch: {msg}");
}
