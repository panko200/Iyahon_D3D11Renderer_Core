using System;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

/// <summary>
/// プロパティパネル (BezierCurvePointEditor) と D3D11 3Dビューポート (ViewportBezierCurveController)
/// の選択ポイントおよび制御点変更を同期するブリッジ。
/// </summary>
internal static class ViewportBezierCurveBridge
{
    private static readonly object _lock = new();
    private static D3DPrimitiveShapeParameter? _activeParam;
    private static int _selectedPointIndex;

    public static event Action<D3DPrimitiveShapeParameter, int>? PointSelectedFromViewport;
    public static event Action<D3DPrimitiveShapeParameter>? CurveEditedFromViewport;
    public static event Action<D3DPrimitiveShapeParameter, int>? SelectionChangedFromEditor;

    public static D3DPrimitiveShapeParameter? ActiveParameter
    {
        get { lock (_lock) return _activeParam; }
    }

    public static int SelectedPointIndex
    {
        get { lock (_lock) return _selectedPointIndex; }
    }

    public static void SetEditorSelection(D3DPrimitiveShapeParameter? param, int pointIndex)
    {
        lock (_lock)
        {
            _activeParam = param;
            _selectedPointIndex = pointIndex;
        }
        if (param != null)
            SelectionChangedFromEditor?.Invoke(param, pointIndex);
    }

    public static void ClearEditor(D3DPrimitiveShapeParameter? param)
    {
        lock (_lock)
        {
            if (param != null && ReferenceEquals(_activeParam, param))
                _activeParam = null;
        }
    }

    public static void NotifyPointPickedFromViewport(D3DPrimitiveShapeParameter param, int index)
    {
        lock (_lock)
        {
            _activeParam = param;
            _selectedPointIndex = index;
        }
        PointSelectedFromViewport?.Invoke(param, index);
    }

    public static void NotifyCurveEditedFromViewport(D3DPrimitiveShapeParameter param)
    {
        DepthSortVideoPlayerPatch.SetTimelineChanged();
        CurveEditedFromViewport?.Invoke(param);
    }
}
