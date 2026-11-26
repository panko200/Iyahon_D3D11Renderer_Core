using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.CameraPath;

internal readonly record struct D3DCameraPlaybackTiming(long Frame, long Length, int Fps, int ScreenHeight);

/// <summary>
/// 「D3D11 カメラパス」設定パネル (D3DCameraKeyframeEditor) と
/// D3D11 3Dビューポート (ViewportCameraPathController) の双方向同期ブリッジ。
/// </summary>
internal static class D3DCameraPathViewportBridge
{
    private static readonly object _lock = new();
    private static readonly ConditionalWeakTable<D3DCameraPathVideoEffect, BoxedTiming> _timings = new();

    private static D3DCameraPathVideoEffect? _activeEffect;
    private static int[] _selectedIndices = [0];
    private static int _primarySelectedIndex = 0;

    private sealed class BoxedTiming
    {
        public D3DCameraPlaybackTiming Value;
    }

    public static event Action? StateChanged;
    public static event Action<D3DCameraPathVideoEffect, int>? KeyframePickedFromViewport;
    public static event Action<D3DCameraPathVideoEffect>? PathEditedFromViewport;
    public static event Action<D3DCameraPathVideoEffect>? FocusPathRequested;
    public static event Action<D3DCameraPathVideoEffect, int>? FocusKeyframeRequested;
    public static event Action<D3DCameraPathVideoEffect>? AddCurrentViewportCameraRequested;

    public static D3DCameraPathVideoEffect? ActiveEffect
    {
        get { lock (_lock) return _activeEffect; }
    }

    public static int PrimarySelectedIndex
    {
        get { lock (_lock) return _primarySelectedIndex; }
    }

    public static int[] GetSelectedIndicesSnapshot()
    {
        lock (_lock) return (int[])_selectedIndices.Clone();
    }

    public static D3DCameraPlaybackTiming GetPlaybackTiming(D3DCameraPathVideoEffect effect)
    {
        lock (_lock)
        {
            if (_timings.TryGetValue(effect, out var box))
                return box.Value;
        }
        return new D3DCameraPlaybackTiming(0, 120, 60, 1080);
    }

    public static void UpdatePlaybackTiming(D3DCameraPathVideoEffect effect, long frame, long length, int fps, int screenHeight)
    {
        long normLen = Math.Max(1, length);
        int normFps = Math.Max(1, fps);
        int normH = Math.Max(64, screenHeight);
        var next = new D3DCameraPlaybackTiming(frame, normLen, normFps, normH);
        bool changed = false;

        lock (_lock)
        {
            var box = _timings.GetOrCreateValue(effect);
            if (box.Value != next)
            {
                box.Value = next;
                changed = ReferenceEquals(_activeEffect, effect);
            }
        }

        if (changed)
            StateChanged?.Invoke();
    }

    public static void SetActiveSelection(D3DCameraPathVideoEffect? effect, IReadOnlyCollection<int>? indices, int primaryIndex)
    {
        lock (_lock)
        {
            _activeEffect = effect;
            _selectedIndices = indices is { Count: > 0 } ? indices.Distinct().OrderBy(x => x).ToArray() : [];
            _primarySelectedIndex = primaryIndex;
        }
        StateChanged?.Invoke();
    }

    public static void ClearActiveEditor(D3DCameraPathVideoEffect? effect)
    {
        bool cleared = false;
        lock (_lock)
        {
            if (effect != null && ReferenceEquals(_activeEffect, effect))
            {
                _activeEffect = null;
                cleared = true;
            }
        }
        if (cleared)
            StateChanged?.Invoke();
    }

    public static void NotifyKeyframePickedFromViewport(D3DCameraPathVideoEffect effect, int index, IReadOnlyCollection<int> selectionSnapshot)
    {
        lock (_lock)
        {
            _activeEffect = effect;
            _selectedIndices = selectionSnapshot.Count > 0 ? selectionSnapshot.ToArray() : [index];
            _primarySelectedIndex = index;
        }
        KeyframePickedFromViewport?.Invoke(effect, index);
        StateChanged?.Invoke();
    }

    public static void NotifyPathEditedFromViewport(D3DCameraPathVideoEffect effect)
    {
        DepthSortVideoPlayerPatch.SetTimelineChanged();
        PathEditedFromViewport?.Invoke(effect);
        StateChanged?.Invoke();
    }

    public static void NotifyPropertyEdited()
    {
        DepthSortVideoPlayerPatch.SetTimelineChanged();
        StateChanged?.Invoke();
    }

    public static void RequestFocusPath(D3DCameraPathVideoEffect effect)
        => FocusPathRequested?.Invoke(effect);

    public static void RequestFocusKeyframe(D3DCameraPathVideoEffect effect, int index)
        => FocusKeyframeRequested?.Invoke(effect, index);

    public static void RequestAddCurrentViewportCamera(D3DCameraPathVideoEffect effect)
        => AddCurrentViewportCameraRequested?.Invoke(effect);
}
