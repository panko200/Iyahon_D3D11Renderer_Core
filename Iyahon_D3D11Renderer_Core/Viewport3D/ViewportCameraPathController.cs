using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Input;
using Iyahon_D3D11Renderer_Core.CameraPath;
using YukkuriMovieMaker.UndoRedo;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal enum CameraPathHandleKind
{
    None,
    Keyframe,
    TangentIn,
    TangentOut,
    LookAtTarget,
}

internal readonly record struct CameraPathHandleRef(CameraPathHandleKind Kind, int KeyframeIndex)
{
    public static CameraPathHandleRef None => new(CameraPathHandleKind.None, -1);
}

/// <summary>
/// D3D11 3Dビューポート上での「D3D11 カメラパス」3D軌道表示および設定点・制御点・注視点の直接ドラッグ編集。
/// </summary>
internal sealed class ViewportCameraPathController
{
    private const long EditFrame = 0L;
    private const long EditLength = 1L;
    private const int EditFps = 30;
    private const float PickRadiusPx = 14f;

    private static readonly Vector4 ColTrajectory = new(0.28f, 0.78f, 1.00f, 0.92f);
    private static readonly Vector4 ColKeyframe = new(0.24f, 0.85f, 0.95f, 0.95f);
    private static readonly Vector4 ColKeyframeSel = new(1.00f, 0.77f, 0.25f, 1.00f);
    private static readonly Vector4 ColHandleArm = new(0.65f, 0.70f, 0.80f, 0.75f);
    private static readonly Vector4 ColHandlePoint = new(0.35f, 0.95f, 0.55f, 0.95f);
    private static readonly Vector4 ColLookAtLine = new(0.95f, 0.40f, 0.80f, 0.65f);
    private static readonly Vector4 ColLookAtPoint = new(1.00f, 0.35f, 0.85f, 0.95f);
    private static readonly Vector4 ColCurrentCam = new(1.00f, 0.95f, 0.35f, 0.95f);
    private static readonly Vector4 ColCurrentUp = new(0.35f, 1.00f, 0.45f, 0.95f);
    private static readonly Vector4 ColHover = new(1.00f, 1.00f, 1.00f, 1.00f);

    private readonly List<D3DCameraState> _sampleBuffer = new(128);
    private readonly List<(CameraPathHandleRef Handle, Vector3 WorldPos)> _pickCandidates = new();

    private D3DCameraPathVideoEffect? _dragEffect;
    private CameraPathHandleRef _dragHandle = CameraPathHandleRef.None;
    private Vector3 _dragPlanePoint;
    private Vector3 _dragPlaneNormal;
    private Vector3 _dragStartHit;
    private readonly Dictionary<int, (Vector3 StartSavedPos, Vector3 StartSavedTarget, double StartLenIn, double StartLenOut, double StartAz, double StartEl)> _dragStartState = new();
    private bool _dragModified;

    public CameraPathHandleRef HoveredHandle { get; private set; } = CameraPathHandleRef.None;
    public bool IsDragging => _dragHandle.Kind != CameraPathHandleKind.None && _dragEffect != null;
    public bool UseBlenderAxis { get; set; }

    private Vector3 SavedToWorldEye(Vector3 saved)
        => UseBlenderAxis
            ? new(saved.X, -saved.Z, D3DCameraPathSolver.BasePerspectiveDistance - saved.Y)
            : new(saved.X, saved.Y, D3DCameraPathSolver.BasePerspectiveDistance - saved.Z);

    private Vector3 SavedDirToWorld(Vector3 dir)
        => D3DCameraPathSolver.ToPreviewSpace(dir, UseBlenderAxis);

    private Vector3 WorldDirToSaved(Vector3 dir)
        => D3DCameraPathSolver.ToPreviewSpace(dir, UseBlenderAxis);

    private Vector3 WorldDeltaToSavedDelta(Vector3 deltaWorld)
        => UseBlenderAxis
            ? BlenderAxisMath.WorldToBlender(deltaWorld)
            : new(deltaWorld.X, deltaWorld.Y, -deltaWorld.Z);

    public void BuildOverlays(
        D3DCameraPathVideoEffect? effect,
        List<Viewport3DRenderer.OverlayVertex> depthLines,
        List<Viewport3DRenderer.OverlayVertex> topLines,
        List<Viewport3DRenderer.OverlayVertex> topTris,
        ViewportCamera camera,
        int videoW,
        int videoH)
    {
        if (effect == null || effect.Keyframes.Count == 0)
            return;

        var timing = D3DCameraPathViewportBridge.GetPlaybackTiming(effect);
        var settings = effect.CreateSettings(timing.Frame, timing.Length, timing.Fps);
        var kfs = effect.Keyframes;

        // 1. 軌道サンプリング線
        if (kfs.Count >= 2)
        {
            int samples = Math.Clamp(kfs.Count * 20, 48, 240);
            D3DCameraPathSolver.SamplePath(kfs, in settings, timing.Length, timing.Fps, samples, _sampleBuffer, UseBlenderAxis);
            Vector3? prevEye = null;
            for (int i = 0; i < _sampleBuffer.Count; i++)
            {
                D3DCameraPathSolver.GetView(_sampleBuffer[i], out var eye, out _, out _, out _, UseBlenderAxis);
                if (prevEye.HasValue)
                    ViewportOverlayBuilder.AddLine(depthLines, prevEye.Value, eye, ColTrajectory);
                prevEye = eye;
            }
        }

        var selectedSet = new HashSet<int>(D3DCameraPathViewportBridge.GetSelectedIndicesSnapshot());
        if (ReferenceEquals(D3DCameraPathViewportBridge.ActiveEffect, effect) && D3DCameraPathViewportBridge.PrimarySelectedIndex >= 0)
            selectedSet.Add(D3DCameraPathViewportBridge.PrimarySelectedIndex);
        if (selectedSet.Count == 0)
            selectedSet.Add(0);

        bool showTangents = effect.CurveType.UsesTangent();
        bool showLookAt = effect.LookAt;
        float baseDist = camera.IsOrthographic ? camera.Distance * camera.OrthoScale : camera.Distance;

        for (int i = 0; i < kfs.Count; i++)
        {
            var kf = kfs[i];
            var st = D3DCameraPathSolver.EvaluateKeyframe(kf, EditFrame, EditLength, EditFps);
            D3DCameraPathSolver.GetView(in st, out var eye, out var r, out var u, out var f, UseBlenderAxis);

            bool isSel = selectedSet.Contains(i);
            bool isHoverKf = HoveredHandle == new CameraPathHandleRef(CameraPathHandleKind.Keyframe, i);
            float distToCam = MathF.Max(80f, camera.IsOrthographic ? baseDist : Vector3.Distance(camera.CameraPosition, eye));
            float kfRadius = Math.Clamp(distToCam * 0.016f, 6f, 80f);
            Vector4 kfCol = isHoverKf ? ColHover : (isSel ? ColKeyframeSel : ColKeyframe);

            ViewportOverlayBuilder.AddCenterDiamond(topTris, eye, kfRadius, kfCol);

            // 設定点カメラの向きインジケータ
            float axisLen = kfRadius * 2.2f;
            ViewportOverlayBuilder.AddLine(depthLines, eye, eye + f * axisLen, kfCol);
            ViewportOverlayBuilder.AddLine(depthLines, eye, eye + u * (axisLen * 0.6f), ColCurrentUp);

            // 接線（二次・三次ベジェ）
            if (showTangents)
            {
                var tan = D3DCameraPathSolver.EvaluateTangent(kf, EditFrame, EditLength, EditFps);
                var savedDir = D3DCameraPathSolver.GetTangentDirection(tan.Azimuth, tan.Elevation, UseBlenderAxis);
                var worldDir = SavedDirToWorld(savedDir);

                if (i > 0)
                {
                    Vector3 inPos = eye - worldDir * (float)tan.LengthIn;
                    bool hoverIn = HoveredHandle == new CameraPathHandleRef(CameraPathHandleKind.TangentIn, i);
                    float hr = Math.Clamp(Vector3.Distance(camera.CameraPosition, inPos) * 0.012f, 4.5f, 60f);
                    ViewportOverlayBuilder.AddLine(depthLines, eye, inPos, ColHandleArm);
                    ViewportOverlayBuilder.AddCenterDiamond(topTris, inPos, hr, hoverIn ? ColHover : ColHandlePoint);
                }
                if (i < kfs.Count - 1)
                {
                    Vector3 outPos = eye + worldDir * (float)tan.LengthOut;
                    bool hoverOut = HoveredHandle == new CameraPathHandleRef(CameraPathHandleKind.TangentOut, i);
                    float hr = Math.Clamp(Vector3.Distance(camera.CameraPosition, outPos) * 0.012f, 4.5f, 60f);
                    ViewportOverlayBuilder.AddLine(depthLines, eye, outPos, ColHandleArm);
                    ViewportOverlayBuilder.AddCenterDiamond(topTris, outPos, hr, hoverOut ? ColHover : ColHandlePoint);
                }
            }

            // 注視点（LookAt ON 時）
            if (showLookAt)
            {
                Vector3 savedTgt = D3DCameraPathSolver.EvaluateTarget(kf, EditFrame, EditLength, EditFps);
                Vector3 worldTgt = D3DCameraPathSolver.ToPreviewSpace(savedTgt, UseBlenderAxis);
                bool hoverTgt = HoveredHandle == new CameraPathHandleRef(CameraPathHandleKind.LookAtTarget, i);
                float tr = Math.Clamp(Vector3.Distance(camera.CameraPosition, worldTgt) * 0.014f, 5f, 70f);
                ViewportOverlayBuilder.AddLine(depthLines, eye, worldTgt, ColLookAtLine);
                ViewportOverlayBuilder.AddCenterDiamond(topTris, worldTgt, tr, hoverTgt ? ColHover : ColLookAtPoint);
            }
        }

        // 現在フレームの評価カメラをフラスタム描画
        var curState = effect.EvaluateCameraState(timing.Frame, timing.Length, timing.Fps, UseBlenderAxis);
        D3DCameraPathSolver.GetView(in curState, out var curEye, out var cr, out var cu, out var cf, UseBlenderAxis);

        float fovRad = Math.Clamp(curState.Fov, 1f, 175f) * (MathF.PI / 180f);
        float aspect = Math.Max(0.1f, (float)Math.Max(64, videoW) / Math.Max(64, videoH));
        float frustumDepth = Math.Clamp(baseDist * 0.22f, 60f, 360f);
        float halfH = frustumDepth * MathF.Tan(fovRad * 0.5f);
        float halfW = halfH * aspect;

        Vector3 fc = curEye + cf * frustumDepth;
        Vector3 c0 = fc - cr * halfW - cu * halfH;
        Vector3 c1 = fc + cr * halfW - cu * halfH;
        Vector3 c2 = fc + cr * halfW + cu * halfH;
        Vector3 c3 = fc - cr * halfW + cu * halfH;

        ViewportOverlayBuilder.AddQuadLoop(topLines, c0, c1, c2, c3, ColCurrentCam);
        ViewportOverlayBuilder.AddLine(topLines, curEye, c0, ColCurrentCam);
        ViewportOverlayBuilder.AddLine(topLines, curEye, c1, ColCurrentCam);
        ViewportOverlayBuilder.AddLine(topLines, curEye, c2, ColCurrentCam);
        ViewportOverlayBuilder.AddLine(topLines, curEye, c3, ColCurrentCam);
        Vector3 upTip = fc + cu * (halfH * 1.32f);
        ViewportOverlayBuilder.AddLine(topLines, curEye + cu * (halfH * 0.1f), upTip, ColCurrentUp);
        ViewportOverlayBuilder.AddCenterDiamond(topTris, curEye, Math.Clamp(frustumDepth * 0.055f, 5f, 30f), ColCurrentCam);
    }

    private void CollectPickCandidates(D3DCameraPathVideoEffect effect)
    {
        _pickCandidates.Clear();
        var kfs = effect.Keyframes;
        bool showTangents = effect.CurveType.UsesTangent();
        bool showLookAt = effect.LookAt;

        // 優先度: 制御点 (In/Out) → 注視点 → 設定点本体
        if (showTangents)
        {
            for (int i = 0; i < kfs.Count; i++)
            {
                var st = D3DCameraPathSolver.EvaluateKeyframe(kfs[i], EditFrame, EditLength, EditFps);
                var eye = SavedToWorldEye(st.Position);
                var tan = D3DCameraPathSolver.EvaluateTangent(kfs[i], EditFrame, EditLength, EditFps);
                var dirW = SavedDirToWorld(D3DCameraPathSolver.GetTangentDirection(tan.Azimuth, tan.Elevation, UseBlenderAxis));
                if (i < kfs.Count - 1)
                    _pickCandidates.Add((new CameraPathHandleRef(CameraPathHandleKind.TangentOut, i), eye + dirW * (float)tan.LengthOut));
                if (i > 0)
                    _pickCandidates.Add((new CameraPathHandleRef(CameraPathHandleKind.TangentIn, i), eye - dirW * (float)tan.LengthIn));
            }
        }

        if (showLookAt)
        {
            for (int i = 0; i < kfs.Count; i++)
            {
                var tgtW = D3DCameraPathSolver.ToPreviewSpace(D3DCameraPathSolver.EvaluateTarget(kfs[i], EditFrame, EditLength, EditFps), UseBlenderAxis);
                _pickCandidates.Add((new CameraPathHandleRef(CameraPathHandleKind.LookAtTarget, i), tgtW));
            }
        }

        for (int i = 0; i < kfs.Count; i++)
        {
            var st = D3DCameraPathSolver.EvaluateKeyframe(kfs[i], EditFrame, EditLength, EditFps);
            _pickCandidates.Add((new CameraPathHandleRef(CameraPathHandleKind.Keyframe, i), SavedToWorldEye(st.Position)));
        }
    }

    public CameraPathHandleRef HitTest(D3DCameraPathVideoEffect? effect, Vector2 mousePx, ViewportCamera camera, float vw, float vh, out Vector3 worldPos)
    {
        worldPos = Vector3.Zero;
        if (effect == null || effect.Keyframes.Count == 0)
            return CameraPathHandleRef.None;

        CollectPickCandidates(effect);
        float bestDist = PickRadiusPx;
        var best = CameraPathHandleRef.None;

        for (int i = 0; i < _pickCandidates.Count; i++)
        {
            var (handle, wp) = _pickCandidates[i];
            var sp = camera.WorldToScreen(wp, vw, vh);
            if (sp is not { } s) continue;
            float d = Vector2.Distance(s, mousePx);
            if (d <= bestDist)
            {
                bestDist = d;
                best = handle;
                worldPos = wp;
            }
        }
        return best;
    }

    public bool UpdateHover(D3DCameraPathVideoEffect? effect, Vector2 mousePx, ViewportCamera camera, float vw, float vh)
    {
        if (IsDragging) return false;
        var next = HitTest(effect, mousePx, camera, vw, vh, out _);
        if (next != HoveredHandle)
        {
            HoveredHandle = next;
            return true;
        }
        return false;
    }

    public bool TryBeginDrag(
        D3DCameraPathVideoEffect? effect,
        Vector2 mousePx,
        ViewportCamera camera,
        float vw,
        float vh,
        ModifierKeys mods)
    {
        if (effect == null || effect.Keyframes.Count == 0)
            return false;

        var hit = HitTest(effect, mousePx, camera, vw, vh, out var hitWorldPos);
        if (hit.Kind == CameraPathHandleKind.None)
            return false;

        camera.GetCameraAxes(out _, out _, out var camFwd);
        var ray = camera.ScreenToRay(mousePx.X, mousePx.Y, vw, vh);
        var startHit = ray.IntersectPlaneUnbounded(hitWorldPos, camFwd) ?? hitWorldPos;

        _dragEffect = effect;
        _dragHandle = hit;
        _dragPlanePoint = hitWorldPos;
        _dragPlaneNormal = camFwd;
        _dragStartHit = startHit;
        _dragModified = false;
        _dragStartState.Clear();

        // 編集点の選択同期（複数選択されている場合は維持）
        var sel = D3DCameraPathViewportBridge.GetSelectedIndicesSnapshot().ToHashSet();
        if (hit.Kind == CameraPathHandleKind.Keyframe)
        {
            if ((mods & ModifierKeys.Control) != 0)
            {
                if (!sel.Add(hit.KeyframeIndex) && sel.Count > 1)
                    sel.Remove(hit.KeyframeIndex);
            }
            else if (!sel.Contains(hit.KeyframeIndex))
            {
                sel.Clear();
                sel.Add(hit.KeyframeIndex);
            }
        }
        else
        {
            sel.Clear();
            sel.Add(hit.KeyframeIndex);
        }
        D3DCameraPathViewportBridge.NotifyKeyframePickedFromViewport(effect, hit.KeyframeIndex, sel);

        var indicesToSnapshot = (hit.Kind == CameraPathHandleKind.Keyframe && sel.Contains(hit.KeyframeIndex))
            ? sel
            : [hit.KeyframeIndex];

        foreach (int idx in indicesToSnapshot)
        {
            if (idx < 0 || idx >= effect.Keyframes.Count) continue;
            var kf = effect.Keyframes[idx];
            var st = D3DCameraPathSolver.EvaluateKeyframe(kf, EditFrame, EditLength, EditFps);
            var tgt = D3DCameraPathSolver.EvaluateTarget(kf, EditFrame, EditLength, EditFps);
            var tan = D3DCameraPathSolver.EvaluateTangent(kf, EditFrame, EditLength, EditFps);
            _dragStartState[idx] = (st.Position, tgt, tan.LengthIn, tan.LengthOut, tan.Azimuth, tan.Elevation);
        }

        HoveredHandle = hit;
        return true;
    }

    public bool UpdateDrag(Vector2 mousePx, ViewportCamera camera, float vw, float vh, ModifierKeys mods)
    {
        if (!IsDragging || _dragEffect == null)
            return false;

        var ray = camera.ScreenToRay(mousePx.X, mousePx.Y, vw, vh);
        var curHit = ray.IntersectPlaneUnbounded(_dragPlanePoint, _dragPlaneNormal);
        if (!curHit.HasValue)
            return false;

        Vector3 deltaWorld = curHit.Value - _dragStartHit;
        if ((mods & ModifierKeys.Shift) != 0)
        {
            // Shift で最も変化量の大きい1軸へスナップ
            float ax = MathF.Abs(deltaWorld.X), ay = MathF.Abs(deltaWorld.Y), az = MathF.Abs(deltaWorld.Z);
            if (ax >= ay && ax >= az) deltaWorld = new Vector3(deltaWorld.X, 0f, 0f);
            else if (ay >= ax && ay >= az) deltaWorld = new Vector3(0f, deltaWorld.Y, 0f);
            else deltaWorld = new Vector3(0f, 0f, deltaWorld.Z);
        }

        Vector3 deltaSaved = WorldDeltaToSavedDelta(deltaWorld);
        var kfs = _dragEffect.Keyframes;

        if (_dragHandle.Kind == CameraPathHandleKind.Keyframe)
        {
            foreach (var kv in _dragStartState)
            {
                if (kv.Key < 0 || kv.Key >= kfs.Count) continue;
                var kf = kfs[kv.Key];
                Vector3 nextPos = kv.Value.StartSavedPos + deltaSaved;
                D3DCameraTangentTools.SetValue(kf.X, EditFrame, nextPos.X);
                D3DCameraTangentTools.SetValue(kf.Y, EditFrame, nextPos.Y);
                D3DCameraTangentTools.SetValue(kf.Z, EditFrame, nextPos.Z);
            }
        }
        else if (_dragHandle.Kind == CameraPathHandleKind.LookAtTarget)
        {
            if (_dragStartState.TryGetValue(_dragHandle.KeyframeIndex, out var init) &&
                _dragHandle.KeyframeIndex >= 0 && _dragHandle.KeyframeIndex < kfs.Count)
            {
                var kf = kfs[_dragHandle.KeyframeIndex];
                Vector3 nextTgt = init.StartSavedTarget + deltaSaved;
                D3DCameraTangentTools.SetValue(kf.TargetX, EditFrame, nextTgt.X);
                D3DCameraTangentTools.SetValue(kf.TargetY, EditFrame, nextTgt.Y);
                D3DCameraTangentTools.SetValue(kf.TargetZ, EditFrame, nextTgt.Z);
            }
        }
        else if (_dragHandle.Kind is CameraPathHandleKind.TangentIn or CameraPathHandleKind.TangentOut)
        {
            if (_dragStartState.TryGetValue(_dragHandle.KeyframeIndex, out var init) &&
                _dragHandle.KeyframeIndex >= 0 && _dragHandle.KeyframeIndex < kfs.Count)
            {
                var kf = kfs[_dragHandle.KeyframeIndex];
                Vector3 eye = SavedToWorldEye(init.StartSavedPos);
                Vector3 startWorldDir = SavedDirToWorld(D3DCameraPathSolver.GetTangentDirection(init.StartAz, init.StartEl, UseBlenderAxis));
                float startLen = _dragHandle.Kind == CameraPathHandleKind.TangentOut
                    ? (float)init.StartLenOut
                    : (float)init.StartLenIn;
                Vector3 startCtrl = _dragHandle.Kind == CameraPathHandleKind.TangentOut
                    ? eye + startWorldDir * startLen
                    : eye - startWorldDir * startLen;

                Vector3 newCtrl = startCtrl + deltaWorld;
                Vector3 vecFromEye = newCtrl - eye;
                float newLen = vecFromEye.Length();
                Vector3 newWorldDir = _dragHandle.Kind == CameraPathHandleKind.TangentOut ? vecFromEye : -vecFromEye;
                Vector3 newSavedDir = WorldDirToSaved(newWorldDir);

                D3DCameraPathSolver.GetTangentAngles(newSavedDir, init.StartAz, out double az, out double el, UseBlenderAxis);
                D3DCameraTangentTools.SetValue(kf.TangentAngleH, EditFrame, az);
                D3DCameraTangentTools.SetValue(kf.TangentAngleV, EditFrame, el);
                if (_dragHandle.Kind == CameraPathHandleKind.TangentOut)
                    D3DCameraTangentTools.SetValue(kf.TangentLengthOut, EditFrame, newLen);
                else
                    D3DCameraTangentTools.SetValue(kf.TangentLengthIn, EditFrame, newLen);
            }
        }

        _dragModified = true;
        D3DCameraPathViewportBridge.NotifyPathEditedFromViewport(_dragEffect);
        return true;
    }

    public void EndDrag(UndoRedoManager? undoRedo)
    {
        if (!IsDragging) return;
        if (_dragModified)
            undoRedo?.Record();
        _dragEffect = null;
        _dragHandle = CameraPathHandleRef.None;
        _dragStartState.Clear();
        _dragModified = false;
    }

    public void CancelDrag()
    {
        if (!IsDragging || _dragEffect == null)
        {
            _dragEffect = null;
            _dragHandle = CameraPathHandleRef.None;
            _dragStartState.Clear();
            return;
        }

        var kfs = _dragEffect.Keyframes;
        foreach (var kv in _dragStartState)
        {
            if (kv.Key < 0 || kv.Key >= kfs.Count) continue;
            var kf = kfs[kv.Key];
            D3DCameraTangentTools.SetValue(kf.X, EditFrame, kv.Value.StartSavedPos.X);
            D3DCameraTangentTools.SetValue(kf.Y, EditFrame, kv.Value.StartSavedPos.Y);
            D3DCameraTangentTools.SetValue(kf.Z, EditFrame, kv.Value.StartSavedPos.Z);
            D3DCameraTangentTools.SetValue(kf.TargetX, EditFrame, kv.Value.StartSavedTarget.X);
            D3DCameraTangentTools.SetValue(kf.TargetY, EditFrame, kv.Value.StartSavedTarget.Y);
            D3DCameraTangentTools.SetValue(kf.TargetZ, EditFrame, kv.Value.StartSavedTarget.Z);
            D3DCameraTangentTools.SetValue(kf.TangentAngleH, EditFrame, kv.Value.StartAz);
            D3DCameraTangentTools.SetValue(kf.TangentAngleV, EditFrame, kv.Value.StartEl);
            D3DCameraTangentTools.SetValue(kf.TangentLengthIn, EditFrame, kv.Value.StartLenIn);
            D3DCameraTangentTools.SetValue(kf.TangentLengthOut, EditFrame, kv.Value.StartLenOut);
        }

        D3DCameraPathViewportBridge.NotifyPathEditedFromViewport(_dragEffect);
        _dragEffect = null;
        _dragHandle = CameraPathHandleRef.None;
        _dragStartState.Clear();
        _dragModified = false;
    }

    public void AddCurrentViewportCamera(D3DCameraPathVideoEffect effect, ViewportCamera camera, UndoRedoManager? undoRedo)
    {
        Vector3 eye = camera.CameraPosition;
        // Saved Z+ = depth => saved.Z = 1000 - eye.Z (YMM4) / Blender: saved = (eye.X, 1000 - eye.Z, -eye.Y)
        Vector3 savedPos = UseBlenderAxis
            ? new(eye.X, D3DCameraPathSolver.BasePerspectiveDistance - eye.Z, -eye.Y)
            : new(eye.X, eye.Y, D3DCameraPathSolver.BasePerspectiveDistance - eye.Z);
        Vector3 savedTarget = UseBlenderAxis
            ? BlenderAxisMath.WorldToBlender(camera.Target)
            : new(camera.Target.X, camera.Target.Y, -camera.Target.Z);

        camera.GetCameraAxes(out _, out _, out var fwd);
        const float r2d = 180f / MathF.PI;
        float pitchDeg = MathF.Asin(Math.Clamp(-fwd.Y, -1f, 1f)) * r2d;
        float yawDeg = MathF.Atan2(fwd.X, -fwd.Z) * r2d;
        float rollDeg = camera.Roll * r2d;
        Vector3 savedRot = UseBlenderAxis
            ? BlenderAxisMath.CameraRotYmmSavedToBlender(new(pitchDeg, yawDeg, rollDeg))
            : new(pitchDeg, yawDeg, rollDeg);

        int nextFrame = 0;
        var kfs = effect.Keyframes;
        if (kfs.Count > 0)
            nextFrame = (int)Math.Round(kfs[^1].Frame.GetValue(0L, 1L, 30)) + 30;

        var added = new D3DCameraKeyframe(
            nextFrame,
            savedPos.X, savedPos.Y, savedPos.Z,
            savedRot.X, savedRot.Y, savedRot.Z,
            camera.FovDegrees);
        added.TargetX.ActiveValues.First().Value = savedTarget.X;
        added.TargetY.ActiveValues.First().Value = savedTarget.Y;
        added.TargetZ.ActiveValues.First().Value = savedTarget.Z;
        added.UseLookAt = effect.LookAt;

        if (effect.CurveType.UsesTangent() && kfs.Count > 0)
            D3DCameraTangentTools.SetAutoTangent(added, D3DCameraTangentTools.Position(kfs[^1]), null, UseBlenderAxis);

        effect.Keyframes = kfs.Add(added);
        undoRedo?.Record();
        D3DCameraPathViewportBridge.NotifyKeyframePickedFromViewport(effect, effect.Keyframes.Count - 1, [effect.Keyframes.Count - 1]);
    }

    public void FocusPath(D3DCameraPathVideoEffect effect, ViewportCamera camera)
    {
        if (effect.Keyframes.Count == 0) return;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < effect.Keyframes.Count; i++)
        {
            var st = D3DCameraPathSolver.EvaluateKeyframe(effect.Keyframes[i], EditFrame, EditLength, EditFps);
            Vector3 w = SavedToWorldEye(st.Position);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }
        Vector3 center = (min + max) * 0.5f;
        float radius = MathF.Max(250f, Vector3.Distance(min, max) * 0.65f);
        camera.FocusOn(center, radius);
    }

    public void FocusKeyframe(D3DCameraPathVideoEffect effect, int index, ViewportCamera camera)
    {
        if (index < 0 || index >= effect.Keyframes.Count) return;
        var st = D3DCameraPathSolver.EvaluateKeyframe(effect.Keyframes[index], EditFrame, EditLength, EditFps);
        camera.FocusOn(SavedToWorldEye(st.Position), 260f);
    }
}
