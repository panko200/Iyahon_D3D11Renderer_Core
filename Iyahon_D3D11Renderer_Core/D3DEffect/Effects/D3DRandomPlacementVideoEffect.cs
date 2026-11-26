using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect.Effects;

public enum D3DRandomDistribution
{
    [Display(Name = "直方体 (Box)")]
    Box = 0,

    [Display(Name = "球状 (Sphere)")]
    Sphere = 1,

    [Display(Name = "円柱 (Cylinder)")]
    Cylinder = 2,

    [Display(Name = "リング (Ring)")]
    Ring = 3,
}

/// <summary>
/// 3DモデルやD3Dオブジェクトを3D空間内に決定論的ランダム配置して複製描画する映像エフェクト。
/// </summary>
[VideoEffect(
    "D3D ランダム配置",
    ["D3D"],
    ["ランダム", "インスタンス", "random", "D3D"],
    IsAviUtlSupported = false)]
internal sealed class D3DRandomPlacementVideoEffect : VideoEffectBase
{
    public override string Label => "D3D ランダム配置";

    [Display(GroupName = "配置", Name = "個数")]
    [AnimationSlider("F0", "個", 1, 100)]
    public Animation Count { get; } = new Animation(10, 1, 200);

    [Display(GroupName = "配置", Name = "分布形状")]
    [EnumComboBox]
    public D3DRandomDistribution Distribution
    {
        get => distribution;
        set => Set(ref distribution, value);
    }
    private D3DRandomDistribution distribution = D3DRandomDistribution.Box;

    [Display(GroupName = "配置", Name = "範囲 X")]
    [AnimationSlider("F0", "px", 0, 2000)]
    public Animation RangeX { get; } = new Animation(400, 0, 20000);

    [Display(GroupName = "配置", Name = "範囲 Y")]
    [AnimationSlider("F0", "px", 0, 2000)]
    public Animation RangeY { get; } = new Animation(300, 0, 20000);

    [Display(GroupName = "配置", Name = "範囲 Z")]
    [AnimationSlider("F0", "px", 0, 2000)]
    public Animation RangeZ { get; } = new Animation(400, 0, 20000);

    [Display(GroupName = "スケール", Name = "最小拡大率")]
    [AnimationSlider("F1", "%", 0, 500)]
    public Animation MinScale { get; } = new Animation(100, 0, 2000);

    [Display(GroupName = "スケール", Name = "最大拡大率")]
    [AnimationSlider("F1", "%", 0, 500)]
    public Animation MaxScale { get; } = new Animation(100, 0, 2000);

    [Display(GroupName = "回転", Name = "ランダム回転 X")]
    [AnimationSlider("F1", "°", 0, 360)]
    public Animation RandomRotationX { get; } = new Animation(0, 0, 360);

    [Display(GroupName = "回転", Name = "ランダム回転 Y")]
    [AnimationSlider("F1", "°", 0, 360)]
    public Animation RandomRotationY { get; } = new Animation(0, 0, 360);

    [Display(GroupName = "回転", Name = "ランダム回転 Z")]
    [AnimationSlider("F1", "°", 0, 360)]
    public Animation RandomRotationZ { get; } = new Animation(0, 0, 360);

    [Display(GroupName = "アニメーション", Name = "ランダム移動速度")]
    [AnimationSlider("F1", "px/s", 0, 300)]
    public Animation RandomMoveSpeed { get; } = new Animation(0, 0, 2000);

    [Display(GroupName = "アニメーション", Name = "ランダム回転速度")]
    [AnimationSlider("F1", "°/s", 0, 360)]
    public Animation RandomRotSpeed { get; } = new Animation(0, 0, 1440);

    [Display(GroupName = "その他", Name = "シード値")]
    [AnimationSlider("F0", "", 0, 1000)]
    public Animation Seed { get; } = new Animation(1, 0, 100000);

    [Display(GroupName = "その他", Name = "元のアイテムを表示")]
    [ToggleSlider]
    public bool IncludeOriginal
    {
        get => includeOriginal;
        set => Set(ref includeOriginal, value);
    }
    private bool includeOriginal = true;

    public List<DrawDescription> EvaluateInstances(DrawDescription baseDesc, long frame, long length, int fps, bool useBlenderAxis = false)
    {
        int count = Math.Clamp((int)Math.Round(Count.GetValue(frame, length, fps)), 1, 200);
        float rx = MathF.Max(0f, (float)RangeX.GetValue(frame, length, fps));
        float ry = MathF.Max(0f, (float)RangeY.GetValue(frame, length, fps));
        float rz = MathF.Max(0f, (float)RangeZ.GetValue(frame, length, fps));
        float minS = MathF.Max(0.01f, (float)MinScale.GetValue(frame, length, fps) / 100f);
        float maxS = MathF.Max(minS, (float)MaxScale.GetValue(frame, length, fps) / 100f);
        float rotX = (float)RandomRotationX.GetValue(frame, length, fps);
        float rotY = (float)RandomRotationY.GetValue(frame, length, fps);
        float rotZ = (float)RandomRotationZ.GetValue(frame, length, fps);
        float moveSpeed = (float)RandomMoveSpeed.GetValue(frame, length, fps);
        float rotSpeed = (float)RandomRotSpeed.GetValue(frame, length, fps);
        uint seedVal = unchecked((uint)Math.Max(0, (int)Math.Round(Seed.GetValue(frame, length, fps))));
        float timeSec = fps > 0 ? frame / (float)fps : 0f;

        var results = new List<DrawDescription>(count + (IncludeOriginal ? 1 : 0));
        if (IncludeOriginal)
            results.Add(baseDesc);

        for (int i = 0; i < count; i++)
        {
            uint state = HashSeed(seedVal, (uint)(i + 1));

            Vector3 offset;
            switch (Distribution)
            {
                case D3DRandomDistribution.Sphere:
                {
                    float u = NextSigned(ref state);
                    float theta = NextFloat01(ref state) * MathF.Tau;
                    float r = MathF.Pow(NextFloat01(ref state), 1f / 3f);
                    float sinPhi = MathF.Sqrt(MathF.Max(0f, 1f - u * u));
                    offset = useBlenderAxis
                        ? new Vector3(
                            sinPhi * MathF.Cos(theta) * rx * r,
                            sinPhi * MathF.Sin(theta) * ry * r,
                            u * rz * r)
                        : new Vector3(
                            sinPhi * MathF.Cos(theta) * rx * r,
                            u * ry * r,
                            sinPhi * MathF.Sin(theta) * rz * r);
                    break;
                }
                case D3DRandomDistribution.Cylinder:
                {
                    float theta = NextFloat01(ref state) * MathF.Tau;
                    float r = MathF.Sqrt(NextFloat01(ref state));
                    float y = NextSigned(ref state);
                    offset = useBlenderAxis
                        ? new Vector3(
                            MathF.Cos(theta) * rx * r,
                            MathF.Sin(theta) * ry * r,
                            y * rz)
                        : new Vector3(
                            MathF.Cos(theta) * rx * r,
                            y * ry,
                            MathF.Sin(theta) * rz * r);
                    break;
                }
                case D3DRandomDistribution.Ring:
                {
                    float theta = NextFloat01(ref state) * MathF.Tau;
                    float radialScale = 0.85f + 0.30f * NextFloat01(ref state);
                    float y = NextSigned(ref state) * 0.25f;
                    offset = useBlenderAxis
                        ? new Vector3(
                            MathF.Cos(theta) * rx * radialScale,
                            MathF.Sin(theta) * ry * radialScale,
                            y * rz)
                        : new Vector3(
                            MathF.Cos(theta) * rx * radialScale,
                            y * ry,
                            MathF.Sin(theta) * rz * radialScale);
                    break;
                }
                default: // Box
                {
                    offset = new Vector3(
                        NextSigned(ref state) * rx,
                        NextSigned(ref state) * ry,
                        NextSigned(ref state) * rz);
                    break;
                }
            }

            uint motionState = HashSeed(seedVal ^ 0x51A7BEEFu, (uint)(i + 1));

            if (MathF.Abs(moveSpeed) > 1e-4f)
            {
                float fx = 0.6f + 0.8f * NextFloat01(ref motionState);
                float fy = 0.6f + 0.8f * NextFloat01(ref motionState);
                float fz = 0.6f + 0.8f * NextFloat01(ref motionState);
                float phX = NextFloat01(ref motionState) * MathF.Tau;
                float phY = NextFloat01(ref motionState) * MathF.Tau;
                float phZ = NextFloat01(ref motionState) * MathF.Tau;
                float amp = moveSpeed * 0.35f;
                offset += new Vector3(
                    (MathF.Sin(timeSec * fx * 2.0f + phX) - MathF.Sin(phX)) * amp,
                    (MathF.Sin(timeSec * fy * 2.0f + phY) - MathF.Sin(phY)) * amp,
                    (MathF.Sin(timeSec * fz * 2.0f + phZ) - MathF.Sin(phZ)) * amp);
            }

            if (useBlenderAxis)
                offset = BlenderAxisMath.BlenderToWorld(offset);

            float scaleMul = minS + (maxS - minS) * NextFloat01(ref state);
            Vector3 dRot = new(
                NextSigned(ref state) * (rotX * 0.5f),
                NextSigned(ref state) * (rotY * 0.5f),
                NextSigned(ref state) * (rotZ * 0.5f));

            if (MathF.Abs(rotSpeed) > 1e-4f)
            {
                Vector3 spinAxis = new(
                    NextSigned(ref motionState),
                    NextSigned(ref motionState),
                    NextSigned(ref motionState));
                dRot += spinAxis * (rotSpeed * timeSec);
            }

            results.Add(baseDesc with
            {
                Draw = baseDesc.Draw + offset,
                Zoom = baseDesc.Zoom * scaleMul,
                Rotation = baseDesc.Rotation + dRot,
            });
        }

        return results;
    }

    private static uint HashSeed(uint seed, uint index)
    {
        uint x = seed * 0x9E3779B9u + index * 0x85EBCA6Bu + 0xC2B2AE35u;
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x == 0 ? 1u : x;
    }

    private static float NextFloat01(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state & 0x00FFFFFFu) / 16777216.0f;
    }

    private static float NextSigned(ref uint state)
        => NextFloat01(ref state) * 2.0f - 1.0f;

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new D3DPassthroughProcessor();

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return Count;
        yield return RangeX;
        yield return RangeY;
        yield return RangeZ;
        yield return MinScale;
        yield return MaxScale;
        yield return RandomRotationX;
        yield return RandomRotationY;
        yield return RandomRotationZ;
        yield return RandomMoveSpeed;
        yield return RandomRotSpeed;
        yield return Seed;
    }
}
