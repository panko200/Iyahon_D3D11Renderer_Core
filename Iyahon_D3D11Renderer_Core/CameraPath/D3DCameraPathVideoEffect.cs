using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.UndoRedo;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.CameraPath;

/// <summary>
/// D3D11 カメラパス映像エフェクト。
/// 設定点ごとの位置・角度・視野角・接線・注視点を補間し、
/// 3D軌道と制御点の表示・ドラッグ編集を「D3D11 3Dビューポート」上で直接行えます。
/// </summary>
[VideoEffect("D3D カメラパス", ["D3D"], ["カメラ", "パス", "camera"], IsAviUtlSupported = false)]
public class D3DCameraPathVideoEffect : VideoEffectBase
{
    public override string Label => "D3D カメラパス";

    [Display(GroupName = "軌道", Name = "軌道の種類", Description = "設定点どうしを結ぶ線の種類です。\n二次・三次ベジェ曲線を選ぶと、D3D11 3Dビューポート上で制御点（接線）をつかんで動かせます。", Order = 100)]
    [EnumComboBox]
    public D3DCameraCurveType CurveType
    {
        get => curveType;
        set
        {
            if (curveType == value)
                return;

            Set(ref curveType, value);

            if (value.UsesTangent())
                Keyframes = D3DCameraTangentTools.WithAutoTangents(Keyframes);

            D3DCameraPathViewportBridge.NotifyPropertyEdited();
        }
    }
    private D3DCameraCurveType curveType = D3DCameraCurveType.Straight;

    [Display(GroupName = "軌道", Name = "カーブの丸み", Description = "ベジェ曲線のときの丸みです。0で直線的、5で標準、10で大きく膨らみます。", Order = 110)]
    [ShowPropertyEditorWhen(nameof(CurveType), D3DCameraCurveType.Bezier)]
    [AnimationSlider("F1", "", 0, 10)]
    public Animation CurveRoundness { get; } = new Animation(5, 0, 10);

    [Display(GroupName = "軌道", Name = "角度の飛び出し許容", Description = "補間時に角度や視野角が設定点の値を超えてよい角度（度）です。", Order = 120)]
    [AnimationSlider("F1", "°", 0, 180)]
    public Animation AngleOvershoot { get; } = new Animation(180, 0, 100000);

    [Display(GroupName = "時間の制御", Name = "時間の制御", Description = "パス上を進む時間の決め方です。", Order = 200)]
    [EnumComboBox]
    public D3DCameraTimeControl TimeControl
    {
        get => timeControl;
        set
        {
            if (timeControl == value) return;
            Set(ref timeControl, value);
            D3DCameraPathViewportBridge.NotifyPropertyEdited();
        }
    }
    private D3DCameraTimeControl timeControl = D3DCameraTimeControl.KeyframeTime;

    [Display(GroupName = "時間の制御", Name = "動きの滑らかさ", Description = "0=カクカク、10=滑らか。キーフレーム時刻のときに使います。", Order = 210)]
    [ShowPropertyEditorWhen(nameof(TimeControl), D3DCameraTimeControl.KeyframeTime)]
    [AnimationSlider("F1", "", 0, 10)]
    public Animation TimeSmoothness { get; } = new Animation(5, 0, 10);

    [Display(GroupName = "時間の制御", Name = "イージングの種類", Description = "アイテム全体の時間に対するイージングの種類です。", Order = 220)]
    [ShowPropertyEditorWhen(nameof(TimeControl), D3DCameraTimeControl.Easing)]
    [EnumComboBox]
    public EasingType EasingType
    {
        get => easingType;
        set
        {
            if (easingType == value) return;
            Set(ref easingType, value);
            D3DCameraPathViewportBridge.NotifyPropertyEdited();
        }
    }
    private EasingType easingType = EasingType.Cubic;

    [Display(GroupName = "時間の制御", Name = "イージングの方向", Description = "イージングをどちら側に効かせるかです。", Order = 230)]
    [ShowPropertyEditorWhen(nameof(TimeControl), D3DCameraTimeControl.Easing)]
    [EnumComboBox]
    public EasingMode EasingMode
    {
        get => easingMode;
        set
        {
            if (easingMode == value) return;
            Set(ref easingMode, value);
            D3DCameraPathViewportBridge.NotifyPropertyEdited();
        }
    }
    private EasingMode easingMode = EasingMode.InOut;

    [Display(GroupName = "時間の制御", Name = "ベジェ時間カーブ", Description = "時間に対する進行カーブをベジェ曲線で編集します。", Order = 240)]
    [ShowPropertyEditorWhen(nameof(TimeControl), D3DCameraTimeControl.TimeBezier)]
    [D3DCameraBezierAnimationEditor]
    public BezierAnimation TimeBezier { get; } = new BezierAnimation();

    [Display(GroupName = "時間の制御", Name = "進行の反転", Description = "ONにするとパスの終点から始点に向かって移動します。", Order = 250)]
    [ShowPropertyEditorWhen(nameof(TimeControl), D3DCameraTimeControl.Easing | D3DCameraTimeControl.TimeBezier)]
    [ToggleSlider]
    public bool Reverse
    {
        get => reverse;
        set
        {
            if (reverse == value) return;
            Set(ref reverse, value);
            D3DCameraPathViewportBridge.NotifyPropertyEdited();
        }
    }
    private bool reverse;

    [Display(GroupName = "注視点", Name = "注視点を使う", Description = "ONにすると、カメラの向きをXYZ角度ではなく「注視点」から決めます。", Order = 290)]
    [ToggleSlider]
    public bool LookAt
    {
        get => lookAt;
        set
        {
            if (lookAt == value) return;
            Set(ref lookAt, value);
            D3DCameraPathViewportBridge.NotifyPropertyEdited();
        }
    }
    private bool lookAt;

    [Display(GroupName = "カメラ設定点", Description = "設定点の一覧・詳細編集と D3D11 3Dビューポート操作です。", Order = 300)]
    [D3DCameraKeyframeEditor]
    public ImmutableList<D3DCameraKeyframe> Keyframes
    {
        get => keyframes;
        set
        {
            Set(ref keyframes, value);
            D3DCameraPathViewportBridge.NotifyPropertyEdited();
        }
    }
    private ImmutableList<D3DCameraKeyframe> keyframes = ImmutableList.Create(
        new D3DCameraKeyframe(0, 0, 0, 0, 0, 0, 0, D3DCameraPathSolver.DefaultFovDegrees),
        new D3DCameraKeyframe(90, 200, -100, 200, 0, 20, 0, D3DCameraPathSolver.DefaultFovDegrees));

    public D3DCameraPathVideoEffect()
    {
        SubscribeChildUndoRedoable(TimeBezier);
    }

    private void SubscribeBezierPoints() => SetBezierPointSubscription(true);
    private void UnsubscribeBezierPoints() => SetBezierPointSubscription(false);

    private void SetBezierPointSubscription(bool subscribe)
    {
        foreach (var point in TimeBezier.Points)
            SetPoint(point);

        foreach (var keyframe in Keyframes)
        foreach (var point in keyframe.SegmentTimeBezier.Points)
            SetPoint(point);

        void SetPoint(BezierAnimationPoint point)
        {
            if (subscribe)
                point.UndoRedoCommandCreated += BezierPoint_UndoRedoCommandCreated;
            else
                point.UndoRedoCommandCreated -= BezierPoint_UndoRedoCommandCreated;
        }
    }

    private void BezierPoint_UndoRedoCommandCreated(object? sender, UndoRedoEventArgs e)
    {
        RaiseUndoRedoPointCreatedEvent(sender, e);
    }

    public override void BeginEdit()
    {
        base.BeginEdit();
        UnsubscribeBezierPoints();
        SubscribeBezierPoints();
    }

    public override ValueTask EndEditAsync()
    {
        UnsubscribeBezierPoints();
        return base.EndEditAsync();
    }

    internal D3DCameraState EvaluateCameraState(long frame, long length, int fps)
        => D3DCameraPathSolver.Evaluate(Keyframes, CreateSettings(frame, length, fps), frame, length, fps);

    internal D3DCameraPathSettings CreateSettings(long frame, long length, int fps) => new(
        CurveType,
        (float)CurveRoundness.GetValue(frame, length, fps),
        (float)AngleOvershoot.GetValue(frame, length, fps),
        TimeControl,
        (float)TimeSmoothness.GetValue(frame, length, fps),
        EasingType,
        EasingMode,
        TimeBezier,
        Reverse,
        LookAt);

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new D3DCameraPathEffectProcessor(devices, this);

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        var list = new List<IAnimatable>
        {
            CurveRoundness,
            AngleOvershoot,
            TimeSmoothness,
        };

        foreach (var keyframe in Keyframes)
            list.Add(keyframe);

        return list;
    }
}
