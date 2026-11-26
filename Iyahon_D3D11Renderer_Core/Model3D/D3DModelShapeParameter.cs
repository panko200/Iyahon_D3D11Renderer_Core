using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using Iyahon_D3D11Renderer_Core.Model3D.Parsers;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Settings;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

internal class D3DModelShapeParameter : ShapeParameterBase, ID3DModelTransformParameter
{
    [Display(GroupName = "モデル設定", Name = "ファイル", Description = "OBJ / GLB / GLTF / VRM / FBX / PMX / PMD / STL / PLY / 3MF を読み込みます。")]
    [FileSelector(FileGroupType.None, CustomFilterName = "3Dモデル", CustomFilterValue = ModelParserDispatcher.FileDialogFilter)]
    public string FilePath
    {
        get => filePath;
        set
        {
            string next = value ?? string.Empty;
            if (!string.Equals(filePath, next, StringComparison.Ordinal))
            {
                string prev = filePath;
                Set(ref filePath, next);
                if (!string.IsNullOrEmpty(embeddedMeshData))
                {
                    bool shouldClear;
                    if (Viewport3D.Modeling.EditableMesh.TryReadSourceFilePath(embeddedMeshData, out var embSourcePath) &&
                        embSourcePath != null)
                    {
                        shouldClear = !string.Equals(embSourcePath, next, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        shouldClear = !string.IsNullOrEmpty(prev);
                    }
                    if (shouldClear)
                    {
                        EmbeddedMeshData = string.Empty;
                    }
                }
            }
        }
    }
    private string filePath = string.Empty;

    [Display(GroupName = "モデル設定", Name = "サイズ正規化", Description = "ONにするとモデルの最大幅を基準サイズへ自動正規化します。OFFでモデル原寸×単位スケールを使用します。")]
    [ToggleSlider]
    public bool NormalizeSize
    {
        get => normalizeSize;
        set => Set(ref normalizeSize, value);
    }
    private bool normalizeSize = true;

    [Display(GroupName = "モデル設定", Name = "単位スケール", Description = "サイズ正規化OFF時に 1 ワールド単位を何 px とみなすかを指定します（1m=100 など）。")]
    [AnimationSlider("F1", "px", 0.1, 1000)]
    [ShowPropertyEditorWhen(nameof(NormalizeSize), false)]
    public Animation UnitScale { get; } = new Animation(100.0, 0.1, 10000);

    [Display(GroupName = "モデル設定", Name = "底面基準配置", Description = "ONにするとモデルの底面（接地面）を原点Y=0に合わせて床・机の上に置きやすくします。")]
    [ToggleSlider]
    public bool GroundToOrigin
    {
        get => groundToOrigin;
        set => Set(ref groundToOrigin, value);
    }
    private bool groundToOrigin = false;

    [Display(GroupName = "モデル設定", Name = "倍率")]
    [AnimationSlider("F1", "%", 0, 400)]
    public Animation ModelScale { get; } = new Animation(100.0, 0, 100000);

    [Display(GroupName = "ローカル調整", Name = "拡大率 X")]
    [AnimationSlider("F1", "%", 0, 400)]
    public Animation ScaleX { get; } = new Animation(100.0, 0, 100000);

    [Display(GroupName = "ローカル調整", Name = "拡大率 Y")]
    [AnimationSlider("F1", "%", 0, 400)]
    public Animation ScaleY { get; } = new Animation(100.0, 0, 100000);

    [Display(GroupName = "ローカル調整", Name = "拡大率 Z")]
    [AnimationSlider("F1", "%", 0, 400)]
    public Animation ScaleZ { get; } = new Animation(100.0, 0, 100000);

    [Display(GroupName = "ローカル調整", Name = "モデル回転 X")]
    [AnimationSlider("F1", "°", -360, 360)]
    public Animation LocalRotX { get; } = new Animation(0.0, -100000, 100000);

    [Display(GroupName = "ローカル調整", Name = "モデル回転 Y")]
    [AnimationSlider("F1", "°", -360, 360)]
    public Animation LocalRotY { get; } = new Animation(0.0, -100000, 100000);

    [Display(GroupName = "ローカル調整", Name = "モデル回転 Z")]
    [AnimationSlider("F1", "°", -360, 360)]
    public Animation LocalRotZ { get; } = new Animation(0.0, -100000, 100000);

    [Display(GroupName = "質感・マテリアル", Name = "色合い (Tint)")]
    [ColorPicker]
    public Color TintColor
    {
        get => tintColor;
        set => Set(ref tintColor, value);
    }
    private Color tintColor = Colors.White;

    [Display(GroupName = "質感・マテリアル", Name = "PBR上書き", Description = "ONにするとモデルのMetallic / Roughnessを一括で指定値に上書きします。")]
    [ToggleSlider]
    public bool OverrideMaterial
    {
        get => overrideMaterial;
        set => Set(ref overrideMaterial, value);
    }
    private bool overrideMaterial = false;

    [Display(GroupName = "質感・マテリアル", Name = "金属度 (Metallic)")]
    [AnimationSlider("F2", "", 0, 1)]
    [ShowPropertyEditorWhen(nameof(OverrideMaterial), true)]
    public Animation Metallic { get; } = new Animation(0.0, 0, 1);

    [Display(GroupName = "質感・マテリアル", Name = "粗さ (Roughness)")]
    [AnimationSlider("F2", "", 0.04, 1)]
    [ShowPropertyEditorWhen(nameof(OverrideMaterial), true)]
    public Animation Roughness { get; } = new Animation(0.5, 0.04, 1);

    [Display(GroupName = "質感・マテリアル", Name = "自己発光倍率", Description = "モデルが持つEmissive（発光マテリアル）の強さ倍率です。")]
    [AnimationSlider("F2", "", 0, 10)]
    public Animation EmissiveIntensity { get; } = new Animation(1.0, 0, 20);

    [Display(GroupName = "質感・マテリアル", Name = "背面カリング", Description = "ONにすると裏向きのポリゴンをスキップして高速化します（薄い葉や片面モデルで透ける場合はOFF）。")]
    [ToggleSlider]
    public bool CullBackface
    {
        get => cullBackface;
        set => Set(ref cullBackface, value);
    }
    private bool cullBackface = false;

    [System.ComponentModel.Browsable(false)]
    public string EmbeddedMeshData
    {
        get => embeddedMeshData;
        set => Set(ref embeddedMeshData, value);
    }
    private string embeddedMeshData = string.Empty;

    [System.ComponentModel.Browsable(false)]
    public string ModifierStackData
    {
        get => modifierStackData;
        set => Set(ref modifierStackData, value ?? string.Empty);
    }
    private string modifierStackData = string.Empty;

    public Viewport3D.Modeling.EditableMesh CreateDefaultEditableMesh()
        => Viewport3D.Modeling.EditableMesh.CreateCube(1f);

    public D3DModelShapeParameter(SharedDataStore? sharedData) : base(sharedData) { }
    public D3DModelShapeParameter() : this(null) { }

    public override IEnumerable<string> CreateMaskExoFilter(
        int keyFrameIndex, ExoOutputDescription desc, ShapeMaskExoOutputDescription shapeMaskDesc)
        => Array.Empty<string>();

    public override IEnumerable<string> CreateShapeItemExoFilter(
        int keyFrameIndex, ExoOutputDescription desc)
        => Array.Empty<string>();

    public override IShapeSource CreateShapeSource(IGraphicsDevicesAndContext devices)
        => new D3DModelShapeSource(devices, this);

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return UnitScale;
        yield return ModelScale;
        yield return ScaleX;
        yield return ScaleY;
        yield return ScaleZ;
        yield return LocalRotX;
        yield return LocalRotY;
        yield return LocalRotZ;
        yield return Metallic;
        yield return Roughness;
        yield return EmissiveIntensity;
    }

    protected override void LoadSharedData(SharedDataStore store)
    {
        var data = store.Load<SharedData>();
        if (data is null) return;
        data.CopyTo(this);
    }

    protected override void SaveSharedData(SharedDataStore store)
        => store.Save(new SharedData(this));

    private class SharedData
    {
        public string FilePath { get; set; } = string.Empty;
        public bool NormalizeSize { get; set; } = true;
        public Animation UnitScale { get; } = new Animation(100.0, 0.1, 10000);
        public bool GroundToOrigin { get; set; } = false;
        public Animation ModelScale { get; } = new Animation(100.0, 0, 100000);
        public Animation ScaleX { get; } = new Animation(100.0, 0, 100000);
        public Animation ScaleY { get; } = new Animation(100.0, 0, 100000);
        public Animation ScaleZ { get; } = new Animation(100.0, 0, 100000);
        public Animation LocalRotX { get; } = new Animation(0.0, -100000, 100000);
        public Animation LocalRotY { get; } = new Animation(0.0, -100000, 100000);
        public Animation LocalRotZ { get; } = new Animation(0.0, -100000, 100000);
        public Color TintColor { get; set; } = Colors.White;
        public bool OverrideMaterial { get; set; } = false;
        public Animation Metallic { get; } = new Animation(0.0, 0, 1);
        public Animation Roughness { get; } = new Animation(0.5, 0.04, 1);
        public Animation EmissiveIntensity { get; } = new Animation(1.0, 0, 20);
        public bool CullBackface { get; set; } = false;
        public string EmbeddedMeshData { get; set; } = string.Empty;
        public string ModifierStackData { get; set; } = string.Empty;

        public SharedData() { }

        public SharedData(D3DModelShapeParameter p)
        {
            FilePath = p.FilePath;
            NormalizeSize = p.NormalizeSize;
            UnitScale.CopyFrom(p.UnitScale);
            GroundToOrigin = p.GroundToOrigin;
            ModelScale.CopyFrom(p.ModelScale);
            ScaleX.CopyFrom(p.ScaleX);
            ScaleY.CopyFrom(p.ScaleY);
            ScaleZ.CopyFrom(p.ScaleZ);
            LocalRotX.CopyFrom(p.LocalRotX);
            LocalRotY.CopyFrom(p.LocalRotY);
            LocalRotZ.CopyFrom(p.LocalRotZ);
            TintColor = p.TintColor;
            OverrideMaterial = p.OverrideMaterial;
            Metallic.CopyFrom(p.Metallic);
            Roughness.CopyFrom(p.Roughness);
            EmissiveIntensity.CopyFrom(p.EmissiveIntensity);
            CullBackface = p.CullBackface;
            EmbeddedMeshData = p.EmbeddedMeshData;
            ModifierStackData = p.ModifierStackData;
        }

        public void CopyTo(D3DModelShapeParameter p)
        {
            p.FilePath = FilePath;
            p.NormalizeSize = NormalizeSize;
            p.UnitScale.CopyFrom(UnitScale);
            p.GroundToOrigin = GroundToOrigin;
            p.ModelScale.CopyFrom(ModelScale);
            p.ScaleX.CopyFrom(ScaleX);
            p.ScaleY.CopyFrom(ScaleY);
            p.ScaleZ.CopyFrom(ScaleZ);
            p.LocalRotX.CopyFrom(LocalRotX);
            p.LocalRotY.CopyFrom(LocalRotY);
            p.LocalRotZ.CopyFrom(LocalRotZ);
            p.TintColor = TintColor;
            p.OverrideMaterial = OverrideMaterial;
            p.Metallic.CopyFrom(Metallic);
            p.Roughness.CopyFrom(Roughness);
            p.EmissiveIntensity.CopyFrom(EmissiveIntensity);
            p.CullBackface = CullBackface;
            p.EmbeddedMeshData = EmbeddedMeshData;
            p.ModifierStackData = ModifierStackData;
        }
    }
}
