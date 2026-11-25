using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D.Shading;

internal readonly struct EvaluatedMaterialScalars
{
    public readonly Vector4 BaseColor;
    public readonly Vector4 EmissiveColor;
    public readonly float Metallic;
    public readonly float Roughness;
    public readonly float Transmission;
    public readonly float Ior;
    public readonly float NormalScale;
    public readonly bool ForceOpaque;
    public readonly bool HasBaseTex;
    public readonly bool HasOrmTex;
    public readonly bool HasNormalTex;
    public readonly bool HasEmissiveTex;
    public readonly bool HasTransTex;

    public readonly float CoatWeight;
    public readonly float CoatRoughness;
    public readonly float CoatIor;
    public readonly float DiffuseRoughness;
    public readonly float ThinFilmThickness;
    public readonly float ThinFilmIor;
    public readonly float Anisotropic;
    public readonly float AnisotropicRotation;
    public readonly float SubsurfaceWeight;
    public readonly float SubsurfaceScale;
    public readonly Vector3 SubsurfaceColor;
    public readonly float SheenWeight;
    public readonly float SheenRoughness;
    public readonly Vector3 SheenTint;
    public readonly bool IsDiffuseOnly;

    public readonly int ShaderLutMode;
    public readonly float ShaderLutParamA;
    public readonly float ShaderLutParamB;
    public readonly float ShaderLutEmissiveMax;
    public readonly byte[]? ShaderLutBgra;
    public readonly string? ShaderLutHash;

    public readonly int WireframeMode;
    public readonly float WireframeSize;
    public readonly bool WireframePixelSize;
    public readonly Vector4 WireframeBaseColor;
    public readonly Vector4 WireframeEmissiveColor;

    public readonly int LayerTextureAlbedoLayerPlusOne;
    public readonly int LayerTextureEmissiveLayerPlusOne;
    public readonly Vector4 LayerUvTransform;
    public readonly float LayerUvRotationRad;
    public readonly bool LayerFlipV;

    public readonly bool HasTimeDependency;

    public EvaluatedMaterialScalars(
        Vector4 baseColor,
        Vector4 emissiveColor,
        float metallic,
        float roughness,
        float transmission,
        float ior,
        float normalScale,
        bool forceOpaque,
        bool hasBaseTex,
        bool hasOrmTex,
        bool hasNormalTex,
        bool hasEmissiveTex,
        bool hasTransTex,
        float coatWeight = 0f,
        float coatRoughness = 0.05f,
        float coatIor = 1.5f,
        float diffuseRoughness = 0f,
        float thinFilmThickness = 0f,
        float thinFilmIor = 1.33f,
        float anisotropic = 0f,
        float anisotropicRotation = 0f,
        float subsurfaceWeight = 0f,
        float subsurfaceScale = 0.25f,
        Vector3 subsurfaceColor = default,
        float sheenWeight = 0f,
        float sheenRoughness = 0.5f,
        Vector3 sheenTint = default,
        bool isDiffuseOnly = false,
        int shaderLutMode = 0,
        float shaderLutParamA = 0f,
        float shaderLutParamB = 0f,
        float shaderLutEmissiveMax = 0f,
        byte[]? shaderLutBgra = null,
        string? shaderLutHash = null,
        int wireframeMode = 0,
        float wireframeSize = 1.5f,
        bool wireframePixelSize = true,
        Vector4 wireframeBaseColor = default,
        Vector4 wireframeEmissiveColor = default,
        int layerTextureAlbedoLayerPlusOne = 0,
        int layerTextureEmissiveLayerPlusOne = 0,
        Vector4 layerUvTransform = default,
        float layerUvRotationRad = 0f,
        bool layerFlipV = false,
        bool hasTimeDependency = false)
    {
        BaseColor = baseColor;
        EmissiveColor = emissiveColor;
        Metallic = metallic;
        Roughness = roughness;
        Transmission = transmission;
        Ior = ior;
        NormalScale = normalScale;
        ForceOpaque = forceOpaque;
        HasBaseTex = hasBaseTex;
        HasOrmTex = hasOrmTex;
        HasNormalTex = hasNormalTex;
        HasEmissiveTex = hasEmissiveTex;
        HasTransTex = hasTransTex;
        CoatWeight = coatWeight;
        CoatRoughness = coatRoughness;
        CoatIor = coatIor;
        DiffuseRoughness = diffuseRoughness;
        ThinFilmThickness = thinFilmThickness;
        ThinFilmIor = thinFilmIor;
        Anisotropic = anisotropic;
        AnisotropicRotation = anisotropicRotation;
        SubsurfaceWeight = subsurfaceWeight;
        SubsurfaceScale = subsurfaceScale;
        SubsurfaceColor = subsurfaceColor;
        SheenWeight = sheenWeight;
        SheenRoughness = sheenRoughness;
        SheenTint = sheenTint;
        IsDiffuseOnly = isDiffuseOnly;
        ShaderLutMode = shaderLutMode;
        ShaderLutParamA = shaderLutParamA;
        ShaderLutParamB = shaderLutParamB;
        ShaderLutEmissiveMax = shaderLutEmissiveMax;
        ShaderLutBgra = shaderLutBgra;
        ShaderLutHash = shaderLutHash;
        WireframeMode = wireframeMode;
        WireframeSize = wireframeSize;
        WireframePixelSize = wireframePixelSize;
        WireframeBaseColor = wireframeBaseColor;
        WireframeEmissiveColor = wireframeEmissiveColor;
        LayerTextureAlbedoLayerPlusOne = layerTextureAlbedoLayerPlusOne;
        LayerTextureEmissiveLayerPlusOne = layerTextureEmissiveLayerPlusOne;
        LayerUvTransform = layerUvTransform;
        LayerUvRotationRad = layerUvRotationRad;
        LayerFlipV = layerFlipV;
        HasTimeDependency = hasTimeDependency;
    }
}

internal sealed class BakedNodeMaterial
{
    public int Width { get; init; }
    public int Height { get; init; }
    public Vector4 BaseColor { get; init; } = Vector4.One;
    public Vector4 EmissiveColor { get; init; } = Vector4.Zero;
    public float Metallic { get; init; }
    public float Roughness { get; init; } = 0.5f;
    public float Transmission { get; init; }
    public float Ior { get; init; } = 1.5f;
    public float NormalScale { get; init; } = 1f;
    public bool ForceOpaque { get; init; }

    public float CoatWeight { get; init; }
    public float CoatRoughness { get; init; } = 0.05f;
    public float CoatIor { get; init; } = 1.5f;
    public float DiffuseRoughness { get; init; }
    public float ThinFilmThickness { get; init; }
    public float ThinFilmIor { get; init; } = 1.33f;
    public float Anisotropic { get; init; }
    public float AnisotropicRotation { get; init; }
    public float SubsurfaceWeight { get; init; }
    public float SubsurfaceScale { get; init; } = 0.25f;
    public Vector3 SubsurfaceColor { get; init; } = new(1f, 0.35f, 0.25f);
    public float SheenWeight { get; init; }
    public float SheenRoughness { get; init; } = 0.5f;
    public Vector3 SheenTint { get; init; } = Vector3.One;
    public bool IsDiffuseOnly { get; init; }

    public int ShaderLutMode { get; init; }
    public float ShaderLutParamA { get; init; }
    public float ShaderLutParamB { get; init; }
    public float ShaderLutEmissiveMax { get; init; }
    public byte[]? ShaderLutBgra { get; init; }
    public string? ShaderLutHash { get; init; }

    public int WireframeMode { get; init; }
    public float WireframeSize { get; init; } = 1.5f;
    public bool WireframePixelSize { get; init; } = true;
    public Vector4 WireframeBaseColor { get; init; }
    public Vector4 WireframeEmissiveColor { get; init; }

    public int LayerTextureAlbedoLayerPlusOne { get; init; }
    public int LayerTextureEmissiveLayerPlusOne { get; init; }
    public Vector4 LayerUvTransform { get; init; } = new(1f, 1f, 0f, 0f);
    public float LayerUvRotationRad { get; init; }
    public bool LayerFlipV { get; init; }
    public bool HasTimeDependency { get; init; }

    public byte[]? BaseColorBgra { get; init; }
    public byte[]? NormalBgra { get; init; }
    public byte[]? OrmBgra { get; init; }
    public byte[]? EmissiveBgra { get; init; }
    public byte[]? TransmissionBgra { get; init; }
}

internal static class ShaderNodeEvaluator
{
    private const int MaxCachedImages = 16;
    private const int MaxBumpHeightEntries = 8;

    private sealed class CachedImagePixels
    {
        public string Path = string.Empty;
        public long WriteTicks;
        public long Length;
        public int Width;
        public int Height;
        public byte[] Pixels = Array.Empty<byte>();
    }

    private static readonly object s_imgLock = new();
    private static readonly List<CachedImagePixels> s_imageCache = new();
    private static readonly object s_bumpLock = new();
    private static readonly List<(string Key, float[] Heights)> s_bumpHeightLru = new();
    private static readonly byte[] s_perm = BuildPermutationTable();

    private static byte[] BuildPermutationTable()
    {
        int[] p =
        {
            151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,8,99,37,240,21,10,23,
            190,6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,57,177,33,88,237,149,56,87,174,20,
            125,136,171,168,68,175,74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,60,211,133,230,220,105,
            92,41,55,46,245,40,244,102,143,54,65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,200,196,135,
            130,116,188,159,86,164,100,109,198,173,186,3,64,52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,
            212,207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,119,248,152,2,44,154,163,70,221,153,101,
            155,167,43,172,9,129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,218,246,97,228,251,34,242,
            193,238,210,144,12,191,179,162,241,81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,184,84,204,
            176,115,121,50,45,127,4,150,254,138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180
        };
        var table = new byte[512];
        for (int i = 0; i < 512; i++)
            table[i] = (byte)p[i & 255];
        return table;
    }

    private readonly struct SurfaceSample
    {
        public readonly Vector4 BaseColor;
        public readonly float Metallic;
        public readonly float Roughness;
        public readonly float Ior;
        public readonly float Alpha;
        public readonly Vector3 Normal;
        public readonly bool HasNormal;
        public readonly float Transmission;
        public readonly Vector3 Emission; // sRGB [0..1] * strength

        public readonly float CoatWeight;
        public readonly float CoatRoughness;
        public readonly float CoatIor;
        public readonly float DiffuseRoughness;
        public readonly float ThinFilmThickness;
        public readonly float ThinFilmIor;
        public readonly float Anisotropic;
        public readonly float AnisotropicRotation;
        public readonly float SubsurfaceWeight;
        public readonly float SubsurfaceScale;
        public readonly Vector3 SubsurfaceColor;
        public readonly float SheenWeight;
        public readonly float SheenRoughness;
        public readonly Vector3 SheenTint;
        public readonly bool IsDiffuseOnly;

        public SurfaceSample(
            Vector4 baseColor, float metallic, float roughness, float ior, float alpha,
            Vector3 normal, bool hasNormal, float transmission, Vector3 emission,
            float coatWeight = 0f, float coatRoughness = 0.05f, float coatIor = 1.5f, float diffuseRoughness = 0f,
            float thinFilmThickness = 0f, float thinFilmIor = 1.33f, float anisotropic = 0f, float anisotropicRotation = 0f,
            float subsurfaceWeight = 0f, float subsurfaceScale = 0.25f, Vector3? subsurfaceColor = null,
            float sheenWeight = 0f, float sheenRoughness = 0.5f, Vector3? sheenTint = null, bool isDiffuseOnly = false)
        {
            BaseColor = baseColor;
            Metallic = metallic;
            Roughness = roughness;
            Ior = ior;
            Alpha = alpha;
            Normal = normal;
            HasNormal = hasNormal;
            Transmission = transmission;
            Emission = emission;
            CoatWeight = coatWeight;
            CoatRoughness = coatRoughness;
            CoatIor = coatIor;
            DiffuseRoughness = diffuseRoughness;
            ThinFilmThickness = thinFilmThickness;
            ThinFilmIor = thinFilmIor;
            Anisotropic = anisotropic;
            AnisotropicRotation = anisotropicRotation;
            SubsurfaceWeight = subsurfaceWeight;
            SubsurfaceScale = subsurfaceScale;
            SubsurfaceColor = subsurfaceColor ?? new Vector3(1f, 0.35f, 0.25f);
            SheenWeight = sheenWeight;
            SheenRoughness = sheenRoughness;
            SheenTint = sheenTint ?? Vector3.One;
            IsDiffuseOnly = isDiffuseOnly;
        }
    }

    private sealed class CompiledGraph
    {
        public readonly MaterialNodeGraph Graph;
        public readonly Dictionary<int, ShaderNode> NodeMap = new();
        public readonly Dictionary<(int ToId, string ToSocket), ShaderNodeLink> InputLinks = new();
        public readonly Dictionary<(int NodeId, string SocketName), ShaderSocketKind> OutputKindMap = new();
        public readonly Dictionary<int, ColorRampStop[]> SortedRampStops = new();
        public readonly Dictionary<int, bool> SpatialMemo = new();

        public CompiledGraph(MaterialNodeGraph graph)
        {
            Graph = graph;
            foreach (var n in graph.Nodes)
            {
                NodeMap[n.Id] = n;
                var spec = ShaderNodeRegistry.GetSpec(n.Type);
                for (int i = 0; i < spec.Outputs.Count; i++)
                {
                    var outDef = spec.Outputs[i];
                    OutputKindMap[(n.Id, outDef.Name)] = outDef.Kind;
                }
                if (n.Type == ShaderNodeType.ColorRamp && n.RampStops.Count > 0)
                {
                    SortedRampStops[n.Id] = n.RampStops.OrderBy(s => s.Position).ToArray();
                }
            }
            foreach (var l in graph.Links)
                InputLinks[(l.ToNodeId, l.ToSocket)] = l;
        }

        public bool IsNodeSpatial(int nodeId)
        {
            if (SpatialMemo.TryGetValue(nodeId, out bool cached))
                return cached;

            SpatialMemo[nodeId] = false;
            if (!NodeMap.TryGetValue(nodeId, out var node))
                return false;

            bool selfSpatial = node.Type switch
            {
                ShaderNodeType.TexCoord => true,
                ShaderNodeType.NoiseTexture => true,
                ShaderNodeType.VoronoiTexture => true,
                ShaderNodeType.CheckerTexture => true,
                ShaderNodeType.WaveTexture => true,
                ShaderNodeType.GradientTexture => true,
                ShaderNodeType.BrickTexture => true,
                ShaderNodeType.LayerTexture => true,
                ShaderNodeType.ImageTexture => !string.IsNullOrWhiteSpace(node.GetString("FilePath")),
                _ => false
            };

            if (!selfSpatial)
            {
                var spec = ShaderNodeRegistry.GetSpec(node.Type);
                foreach (var inp in spec.Inputs)
                {
                    if (InputLinks.TryGetValue((nodeId, inp.Name), out var link) && IsNodeSpatial(link.FromNodeId))
                    {
                        selfSpatial = true;
                        break;
                    }
                }
            }

            SpatialMemo[nodeId] = selfSpatial;
            return selfSpatial;
        }

        public bool IsSocketSpatial(int nodeId, string socketName)
            => InputLinks.TryGetValue((nodeId, socketName), out var link) && IsNodeSpatial(link.FromNodeId);
    }

    private sealed class EvalContext
    {
        public readonly CompiledGraph Compiled;
        public readonly Dictionary<int, ShaderNode> NodeMap;
        public readonly Dictionary<(int ToId, string ToSocket), ShaderNodeLink> InputLinks;
        public readonly Dictionary<(int NodeId, string SocketName), ShaderSocketKind> OutputKindMap;
        public readonly Dictionary<int, ColorRampStop[]> SortedRampStops;
        public readonly Dictionary<int, float[]>? PrebakedBumpHeights;
        public readonly int BakeResolution;
        public readonly ShaderEvalTime EvalTime;
        public readonly ID3D11Device? Device;
        public readonly ID3D11DeviceContext? DeviceContext;
        public readonly Dictionary<(int Id, string Sock, float U, float V), Vector4> PointCache = new();
        private uint _evalToken = 1;
        private readonly Dictionary<int, uint> _activeStack = new();

        public float? ShaderToRgbOverride;
        public float CurNdv = 0.7f;
        public float CurAo = 1f;
        public float CurWire;

        public EvalContext(
            CompiledGraph compiled,
            int bakeRes,
            Dictionary<int, float[]>? prebakedBumpHeights = null,
            ShaderEvalTime? evalTime = null,
            ID3D11Device? device = null,
            ID3D11DeviceContext? deviceContext = null)
        {
            Compiled = compiled;
            NodeMap = compiled.NodeMap;
            InputLinks = compiled.InputLinks;
            OutputKindMap = compiled.OutputKindMap;
            SortedRampStops = compiled.SortedRampStops;
            PrebakedBumpHeights = prebakedBumpHeights;
            BakeResolution = bakeRes;
            EvalTime = evalTime ?? ShaderEvalTime.Default;
            Device = device;
            DeviceContext = deviceContext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool PushGuard(int nodeId)
        {
            if (_activeStack.TryGetValue(nodeId, out uint tok) && tok == _evalToken)
                return false;
            _activeStack[nodeId] = _evalToken;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PopGuard(int nodeId) => _activeStack[nodeId] = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginPoint(float u, float v, int px = -1, int py = -1)
        {
            if (++_evalToken == 0)
            {
                _evalToken = 1;
                _activeStack.Clear();
            }
            if (PointCache.Count > 0) PointCache.Clear();
            CurU = u;
            CurV = v;
            GridU = u;
            GridV = v;
            CurPx = px;
            CurPy = py;
        }

        public float CurU;
        public float CurV;
        public float GridU;
        public float GridV;
        public int CurPx = -1;
        public int CurPy = -1;
    }

    public static void ClearCaches()
    {
        lock (s_bumpLock)
        {
            s_bumpHeightLru.Clear();
        }
    }

    internal static float EvalFractalNoise3D(
        Vector3 p,
        float detail,
        float roughness,
        float lacunarity,
        string noiseType = "FBM",
        bool normalize = true)
        => EvalFractalNoise(p, detail, roughness, lacunarity, noiseType, normalize);

    internal static float EvalVoronoi2DF1(float x, float y, float randomness = 1f)
    {
        EvalVoronoi2D(x, y, Math.Clamp(randomness, 0f, 1f), 0.5f, "F1", out float distOut, out _);
        return distOut;
    }

    internal static float SampleImageHeight(string path, float u, float v)
    {
        if (string.IsNullOrWhiteSpace(path) || GetOrLoadImage(path) == null)
            return 0.5f;
        Vector4 c = SampleImageBilinear(path, u, v);
        return Math.Clamp(c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f, 0f, 1f);
    }

    public static bool IsGraphTimeDependent(MaterialNodeGraph? graph)
    {
        if (graph == null) return false;
        var outNode = graph.FindOutputNode();
        if (outNode == null) return false;
        var compiled = new CompiledGraph(graph);
        return FindFirstReachableNode(compiled, outNode.Id, "Surface", ShaderNodeType.TimeInfo) != null;
    }

    public static long ComputeQuantizedTimeKey(
        MaterialNodeGraph? graph,
        ShaderEvalTime evalTime,
        out ShaderEvalTime quantizedTime,
        out int? recommendedMaxRes)
    {
        recommendedMaxRes = null;
        if (graph == null)
        {
            quantizedTime = evalTime;
            return 0L;
        }

        var compiled = new CompiledGraph(graph);
        var outNode = graph.FindOutputNode();
        var timeNode = outNode != null
            ? FindFirstReachableNode(compiled, outNode.Id, "Surface", ShaderNodeType.TimeInfo)
            : null;
        if (timeNode == null)
        {
            quantizedTime = evalTime;
            return 0L;
        }

        string fpsChoice = timeNode.GetString("UpdateFps", "15 fps");
        bool animDraft = timeNode.GetBool("AnimDraftRes", true);
        if (animDraft) recommendedMaxRes = 256;

        int srcFps = Math.Max(1, evalTime.Fps);
        int targetFps = fpsChoice switch
        {
            "30 fps" => 30,
            "15 fps" => 15,
            "12 fps" => 12,
            "8 fps" => 8,
            _ => srcFps
        };
        int step = targetFps >= srcFps ? 1 : Math.Max(1, (int)Math.Round((double)srcFps / targetFps));
        long qFrame = step <= 1 ? evalTime.Frame : (evalTime.Frame / step) * step;
        quantizedTime = new ShaderEvalTime(qFrame, evalTime.Length, srcFps);
        return (qFrame << 20) ^ ((long)srcFps << 10) ^ (evalTime.Length & 0x3FF);
    }

    private static ShaderNode? FindFirstReachableNode(
        CompiledGraph compiled,
        int startToNodeId,
        string startToSocket,
        ShaderNodeType targetType)
    {
        if (!compiled.InputLinks.TryGetValue((startToNodeId, startToSocket), out var startLink))
            return null;
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(startLink.FromNodeId);
        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            if (!compiled.NodeMap.TryGetValue(id, out var node)) continue;
            if (node.Type == targetType) return node;
            var spec = ShaderNodeRegistry.GetSpec(node.Type);
            foreach (var inp in spec.Inputs)
            {
                if (compiled.InputLinks.TryGetValue((id, inp.Name), out var link))
                    queue.Enqueue(link.FromNodeId);
            }
        }
        return null;
    }

    public static EvaluatedMaterialScalars EvaluateScalars(MaterialNodeGraph graph)
        => EvaluateScalars(graph, ShaderEvalTime.Default, null, null);

    public static EvaluatedMaterialScalars EvaluateScalars(
        MaterialNodeGraph graph,
        ShaderEvalTime evalTime,
        ID3D11Device? device = null,
        ID3D11DeviceContext? deviceContext = null)
    {
        int res = Math.Clamp(graph.BakeResolution, 64, 1024);
        var outNode = graph.FindOutputNode();
        var compiled = new CompiledGraph(graph);
        if (outNode == null || !compiled.InputLinks.ContainsKey((outNode.Id, "Surface")))
        {
            return new EvaluatedMaterialScalars(
                Vector4.One, Vector4.Zero, 0f, 0.5f, 0f, 1.5f, 1f, false,
                false, false, false, false, false);
        }

        DetectDirectLayerTextures(
            compiled, outNode, evalTime, device, deviceContext,
            out int layerAlbedoPlusOne, out int layerEmissivePlusOne,
            out Vector4 layerUvTransform, out float layerUvRotRad, out bool layerFlipV);

        AnalyzeSpatialChannels(
            compiled, outNode,
            out bool spatialColor, out bool spatialAlpha, out bool spatialRough,
            out bool spatialMetal, out bool spatialNormal, out bool spatialEmission, out bool spatialTrans,
            layerAlbedoPlusOne > 0, layerEmissivePlusOne > 0);

        bool needBaseTex = spatialColor || spatialAlpha;
        bool needOrmTex = spatialRough || spatialMetal;
        bool timeDep = IsGraphTimeDependent(graph);

        var probeCtx = new EvalContext(compiled, res, null, evalTime, device, deviceContext);
        probeCtx.BeginPoint(0.5f, 0.5f);
        var centerSurf = EvalSurfaceInput(probeCtx, outNode.Id, "Surface");

        BuildGpuFeatures(
            compiled, outNode, res, evalTime, device, deviceContext,
            needBaseTex, ref centerSurf,
            out int lutMode, out float lutParamA, out float lutParamB, out float lutEmMax,
            out byte[]? lutBgra, out string? lutHash,
            out int wireMode, out float wireSize, out bool wirePixelSize,
            out Vector4 wireBaseColor, out Vector4 wireEmissiveColor);

        Vector4 emCol;
        if (layerEmissivePlusOne > 0)
        {
            float liveEm = EvalDirectLayerEmissiveStrength(probeCtx, outNode);
            emCol = liveEm > 1e-5f ? new Vector4(1f, 1f, 1f, liveEm) : Vector4.Zero;
        }
        else
        {
            float emStrength = FindMaxComponent(centerSurf.Emission);
            emCol = emStrength > 1e-5f
                ? new Vector4(centerSurf.Emission.X / emStrength, centerSurf.Emission.Y / emStrength, centerSurf.Emission.Z / emStrength, emStrength)
                : Vector4.Zero;
        }

        // LayerWeight/Fresnel が Emissive を制御する場合、静的 Emission は LUT 側へ任せる
        if (lutMode == 2 && lutEmMax > 1e-4f)
            emCol = Vector4.Zero;
        // ShaderToRgb (Toon) モードでは静的 Emission と BaseColor の二重加算を避ける
        if (lutMode == 1)
            emCol = Vector4.Zero;

        Vector4 finalBaseCol = (needBaseTex || layerAlbedoPlusOne > 0)
            ? new Vector4(1f, 1f, 1f, needBaseTex || layerAlbedoPlusOne > 0 ? Math.Clamp(centerSurf.Alpha, 0f, 1f) : 1f)
            : new Vector4(
                Math.Clamp(centerSurf.BaseColor.X, 0f, 1f),
                Math.Clamp(centerSurf.BaseColor.Y, 0f, 1f),
                Math.Clamp(centerSurf.BaseColor.Z, 0f, 1f),
                Math.Clamp(centerSurf.Alpha, 0f, 1f));
        if (needBaseTex) finalBaseCol = Vector4.One;

        return new EvaluatedMaterialScalars(
            finalBaseCol,
            emCol,
            needOrmTex ? 1f : Math.Clamp(centerSurf.Metallic, 0f, 1f),
            needOrmTex ? 1f : Math.Clamp(centerSurf.Roughness, 0.04f, 1f),
            spatialTrans ? 1f : Math.Clamp(centerSurf.Transmission, 0f, 1f),
            Math.Clamp(centerSurf.Ior, 1f, 2.5f),
            1f,
            false,
            needBaseTex,
            needOrmTex,
            spatialNormal,
            spatialEmission,
            spatialTrans,
            coatWeight: centerSurf.CoatWeight,
            coatRoughness: centerSurf.CoatRoughness,
            coatIor: centerSurf.CoatIor,
            diffuseRoughness: centerSurf.DiffuseRoughness,
            thinFilmThickness: centerSurf.ThinFilmThickness,
            thinFilmIor: centerSurf.ThinFilmIor,
            anisotropic: centerSurf.Anisotropic,
            anisotropicRotation: centerSurf.AnisotropicRotation,
            subsurfaceWeight: centerSurf.SubsurfaceWeight,
            subsurfaceScale: centerSurf.SubsurfaceScale,
            subsurfaceColor: centerSurf.SubsurfaceColor,
            sheenWeight: centerSurf.SheenWeight,
            sheenRoughness: centerSurf.SheenRoughness,
            sheenTint: centerSurf.SheenTint,
            isDiffuseOnly: centerSurf.IsDiffuseOnly,
            shaderLutMode: lutMode,
            shaderLutParamA: lutParamA,
            shaderLutParamB: lutParamB,
            shaderLutEmissiveMax: lutEmMax,
            shaderLutBgra: lutBgra,
            shaderLutHash: lutHash,
            wireframeMode: wireMode,
            wireframeSize: wireSize,
            wireframePixelSize: wirePixelSize,
            wireframeBaseColor: wireBaseColor,
            wireframeEmissiveColor: wireEmissiveColor,
            layerTextureAlbedoLayerPlusOne: layerAlbedoPlusOne,
            layerTextureEmissiveLayerPlusOne: layerEmissivePlusOne,
            layerUvTransform: layerUvTransform,
            layerUvRotationRad: layerUvRotRad,
            layerFlipV: layerFlipV,
            hasTimeDependency: timeDep);
    }

    public static string? ComputeSpatialTextureHash(MaterialNodeGraph graph, int effectiveResolution)
        => ComputeSpatialTextureHashEx(graph, effectiveResolution, ShaderEvalTime.Default, null, out _);

    public static string? ComputeSpatialTextureHash(
        MaterialNodeGraph graph,
        int effectiveResolution,
        ShaderEvalTime evalTime,
        ID3D11Device? device = null)
        => ComputeSpatialTextureHashEx(graph, effectiveResolution, evalTime, device, out _);

    internal static string? ComputeSpatialTextureHashEx(
        MaterialNodeGraph graph,
        int effectiveResolution,
        ShaderEvalTime evalTime,
        ID3D11Device? device,
        out bool usesLayerCpu)
    {
        usesLayerCpu = false;
        int res = Math.Clamp(effectiveResolution, 64, 1024);
        var outNode = graph.FindOutputNode();
        if (outNode == null) return null;

        var compiled = new CompiledGraph(graph);
        if (!compiled.InputLinks.ContainsKey((outNode.Id, "Surface")))
            return null;

        DetectDirectLayerTextures(
            compiled, outNode, evalTime, device, null,
            out int layerAlbedoPlusOne, out int layerEmissivePlusOne,
            out _, out _, out _);

        AnalyzeSpatialChannels(
            compiled, outNode,
            out bool spatialColor, out bool spatialAlpha, out bool spatialRough,
            out bool spatialMetal, out bool spatialNormal, out bool spatialEmission, out bool spatialTrans,
            layerAlbedoPlusOne > 0, layerEmissivePlusOne > 0);

        bool needBaseTex = spatialColor || spatialAlpha;
        bool needOrmTex = spatialRough || spatialMetal;
        if (!needBaseTex && !needOrmTex && !spatialNormal && !spatialEmission && !spatialTrans)
            return null;

        var visitedNodes = new HashSet<int>();
        var activeLinks = new List<ShaderNodeLink>();
        var shaderVisited = new HashSet<int>();
        var extraTokens = new List<string>();
        bool usesTime = false;
        bool foundLayerCpu = false;

        void CollectUpstreamNode(int nodeId)
        {
            if (!visitedNodes.Add(nodeId)) return;
            if (!compiled.NodeMap.TryGetValue(nodeId, out var n)) return;
            if (n.Type == ShaderNodeType.TimeInfo) usesTime = true;
            if (n.Type == ShaderNodeType.LayerTexture) foundLayerCpu = true;
            var spec = ShaderNodeRegistry.GetSpec(n.Type);
            foreach (var inp in spec.Inputs)
            {
                if (compiled.InputLinks.TryGetValue((nodeId, inp.Name), out var l))
                {
                    activeLinks.Add(l);
                    CollectUpstreamNode(l.FromNodeId);
                }
            }
        }

        void CollectSocket(int toId, string socketName)
        {
            if (compiled.InputLinks.TryGetValue((toId, socketName), out var l))
            {
                activeLinks.Add(l);
                CollectUpstreamNode(l.FromNodeId);
            }
        }

        void WalkShader(int toId, string toSocket)
        {
            if (!compiled.InputLinks.TryGetValue((toId, toSocket), out var link)) return;
            if (!compiled.NodeMap.TryGetValue(link.FromNodeId, out var src)) return;
            if (!shaderVisited.Add(src.Id)) return;
            activeLinks.Add(link);

            if (src.Type == ShaderNodeType.PrincipledBsdf)
            {
                if (needBaseTex)
                {
                    CollectSocket(src.Id, "BaseColor");
                    CollectSocket(src.Id, "Alpha");
                    if (!compiled.InputLinks.ContainsKey((src.Id, "BaseColor")))
                    {
                        var bc = src.GetColor("BaseColor", new Vector4(0.8f, 0.8f, 0.8f, 1f));
                        extraTokens.Add($"pb_bc:{bc.X:R},{bc.Y:R},{bc.Z:R},{bc.W:R}");
                    }
                    if (!compiled.InputLinks.ContainsKey((src.Id, "Alpha")))
                        extraTokens.Add($"pb_al:{src.GetFloat("Alpha", 1f):R}");
                }
                if (needOrmTex)
                {
                    CollectSocket(src.Id, "Roughness");
                    CollectSocket(src.Id, "Metallic");
                    if (!compiled.InputLinks.ContainsKey((src.Id, "Roughness")))
                        extraTokens.Add($"pb_r:{src.GetFloat("Roughness", 0.5f):R}");
                    if (!compiled.InputLinks.ContainsKey((src.Id, "Metallic")))
                        extraTokens.Add($"pb_m:{src.GetFloat("Metallic", 0f):R}");
                }
                if (spatialNormal)
                    CollectSocket(src.Id, "Normal");
                if (spatialEmission)
                {
                    CollectSocket(src.Id, "EmissionColor");
                    CollectSocket(src.Id, "EmissionStrength");
                    if (!compiled.InputLinks.ContainsKey((src.Id, "EmissionColor")))
                    {
                        var ec = src.GetColor("EmissionColor", Vector4.One);
                        extraTokens.Add($"pb_ec:{ec.X:R},{ec.Y:R},{ec.Z:R},{ec.W:R}");
                    }
                    if (!compiled.InputLinks.ContainsKey((src.Id, "EmissionStrength")))
                        extraTokens.Add($"pb_es:{src.GetFloat("EmissionStrength", 0f):R}");
                }
                if (spatialTrans)
                {
                    CollectSocket(src.Id, "Transmission");
                    if (!compiled.InputLinks.ContainsKey((src.Id, "Transmission")))
                        extraTokens.Add($"pb_tr:{src.GetFloat("Transmission", 0f):R}");
                }
            }
            else if (src.Type == ShaderNodeType.DiffuseBsdf)
            {
                if (needBaseTex)
                {
                    CollectSocket(src.Id, "Color");
                    if (!compiled.InputLinks.ContainsKey((src.Id, "Color")))
                    {
                        var dc = src.GetColor("Color", new Vector4(0.8f, 0.8f, 0.8f, 1f));
                        extraTokens.Add($"db_c:{dc.X:R},{dc.Y:R},{dc.Z:R},{dc.W:R}");
                    }
                }
                if (needOrmTex)
                {
                    CollectSocket(src.Id, "Roughness");
                    if (!compiled.InputLinks.ContainsKey((src.Id, "Roughness")))
                        extraTokens.Add($"db_r:{src.GetFloat("Roughness", 0f):R}");
                }
                if (spatialNormal) CollectSocket(src.Id, "Normal");
            }
            else if (src.Type == ShaderNodeType.Emission)
            {
                if (spatialEmission)
                {
                    CollectSocket(src.Id, "Color");
                    CollectSocket(src.Id, "Strength");
                    if (!compiled.InputLinks.ContainsKey((src.Id, "Color")))
                    {
                        var ec = src.GetColor("Color", Vector4.One);
                        extraTokens.Add($"em_c:{ec.X:R},{ec.Y:R},{ec.Z:R},{ec.W:R}");
                    }
                    if (!compiled.InputLinks.ContainsKey((src.Id, "Strength")))
                        extraTokens.Add($"em_s:{src.GetFloat("Strength", 1f):R}");
                }
            }
            else if (src.Type == ShaderNodeType.MixShader)
            {
                CollectSocket(src.Id, "Fac");
                if (!compiled.InputLinks.ContainsKey((src.Id, "Fac")))
                    extraTokens.Add($"ms_f:{src.GetFloat("Fac", 0.5f):R}");
                WalkShader(src.Id, "Shader1");
                WalkShader(src.Id, "Shader2");
            }
            else if (src.Type == ShaderNodeType.ShaderToRgb)
            {
                WalkShader(src.Id, "Shader");
            }
            else
            {
                // 直接 Color/Float -> MaterialOutput.Surface の暗黙 Emission
                if (spatialEmission) CollectUpstreamNode(src.Id);
            }
        }

        WalkShader(outNode.Id, "Surface");

        usesLayerCpu = foundLayerCpu;
        if (usesTime)
        {
            long tKey = ComputeQuantizedTimeKey(graph, evalTime, out _, out _);
            extraTokens.Add($"time:{tKey}");
        }
        if (foundLayerCpu && device != null)
        {
            long seq = LayerTextureRegistry.GetFrameSequence(device);
            extraTokens.Add($"lseq:{seq}");
            extraTokens.Add($"dev:{device.NativePointer.ToInt64():X}");
        }

        ulong fnv = 14695981039346656037UL;
        fnv = FnvMixInt(fnv, res);
        fnv = FnvMixInt(fnv, (needBaseTex ? 1 : 0) | (needOrmTex ? 2 : 0) | (spatialNormal ? 4 : 0) | (spatialEmission ? 8 : 0) | (spatialTrans ? 16 : 0));

        foreach (int nid in visitedNodes.OrderBy(x => x))
        {
            if (compiled.NodeMap.TryGetValue(nid, out var n))
                fnv = HashSingleNode(fnv, n);
        }
        foreach (var l in activeLinks.OrderBy(x => x.ToNodeId).ThenBy(x => x.ToSocket, StringComparer.Ordinal))
        {
            fnv = FnvMixInt(fnv, l.FromNodeId);
            fnv = FnvMixString(fnv, l.FromSocket);
            fnv = FnvMixInt(fnv, l.ToNodeId);
            fnv = FnvMixString(fnv, l.ToSocket);
        }
        foreach (string tok in extraTokens)
            fnv = FnvMixString(fnv, tok);

        return fnv.ToString("X16", CultureInfo.InvariantCulture);
    }

    public static BakedNodeMaterial EvaluateAndBake(MaterialNodeGraph graph)
        => EvaluateAndBakeAtResolution(graph, null);

    public static BakedNodeMaterial EvaluateAndBakeAtResolution(MaterialNodeGraph graph, int? resolutionOverride)
        => EvaluateAndBakeAtTime(graph, resolutionOverride, ShaderEvalTime.Default, null, null);

    public static BakedNodeMaterial EvaluateAndBakeAtTime(
        MaterialNodeGraph graph,
        int? resolutionOverride,
        ShaderEvalTime evalTime,
        ID3D11Device? device = null,
        ID3D11DeviceContext? deviceContext = null)
    {
        int res = resolutionOverride.HasValue
            ? Math.Clamp(Math.Min(resolutionOverride.Value, graph.BakeResolution), 64, 1024)
            : Math.Clamp(graph.BakeResolution, 64, 1024);

        var outNode = graph.FindOutputNode();
        var compiled = new CompiledGraph(graph);
        if (outNode == null || !compiled.InputLinks.ContainsKey((outNode.Id, "Surface")))
        {
            return new BakedNodeMaterial { Width = res, Height = res };
        }

        DetectDirectLayerTextures(
            compiled, outNode, evalTime, device, deviceContext,
            out int layerAlbedoPlusOne, out int layerEmissivePlusOne,
            out Vector4 layerUvTransform, out float layerUvRotRad, out bool layerFlipV);

        AnalyzeSpatialChannels(
            compiled, outNode,
            out bool spatialColor, out bool spatialAlpha, out bool spatialRough,
            out bool spatialMetal, out bool spatialNormal, out bool spatialEmission, out bool spatialTrans,
            layerAlbedoPlusOne > 0, layerEmissivePlusOne > 0);

        bool needBaseTex = spatialColor || spatialAlpha;
        bool needOrmTex = spatialRough || spatialMetal;
        bool timeDep = IsGraphTimeDependent(graph);

        var probeCtx = new EvalContext(compiled, res, null, evalTime, device, deviceContext);
        probeCtx.BeginPoint(0.5f, 0.5f);
        var centerSurf = EvalSurfaceInput(probeCtx, outNode.Id, "Surface");

        BuildGpuFeatures(
            compiled, outNode, res, evalTime, device, deviceContext,
            needBaseTex, ref centerSurf,
            out int lutMode, out float lutParamA, out float lutParamB, out float lutEmMax,
            out byte[]? lutBgra, out string? lutHash,
            out int wireMode, out float wireSize, out bool wirePixelSize,
            out Vector4 wireBaseColor, out Vector4 wireEmissiveColor);

        if (!needBaseTex && !needOrmTex && !spatialNormal && !spatialEmission && !spatialTrans)
        {
            Vector3 emCol;
            float emStrength;
            if (layerEmissivePlusOne > 0)
            {
                emStrength = EvalDirectLayerEmissiveStrength(probeCtx, outNode);
                emCol = emStrength > 1e-5f ? Vector3.One : Vector3.Zero;
            }
            else
            {
                emStrength = FindMaxComponent(centerSurf.Emission);
                if (emStrength > 1e-5f)
                    emCol = new Vector3(centerSurf.Emission.X / emStrength, centerSurf.Emission.Y / emStrength, centerSurf.Emission.Z / emStrength);
                else
                    emCol = Vector3.Zero;
            }

            if (lutMode == 1 || (lutMode == 2 && lutEmMax > 1e-4f))
            {
                emCol = Vector3.Zero;
                emStrength = 0f;
            }

            return new BakedNodeMaterial
            {
                Width = res,
                Height = res,
                BaseColor = layerAlbedoPlusOne > 0
                    ? Vector4.One
                    : new Vector4(
                        Math.Clamp(centerSurf.BaseColor.X, 0f, 1f),
                        Math.Clamp(centerSurf.BaseColor.Y, 0f, 1f),
                        Math.Clamp(centerSurf.BaseColor.Z, 0f, 1f),
                        Math.Clamp(centerSurf.Alpha, 0f, 1f)),
                Metallic = Math.Clamp(centerSurf.Metallic, 0f, 1f),
                Roughness = Math.Clamp(centerSurf.Roughness, 0.04f, 1f),
                Ior = Math.Clamp(centerSurf.Ior, 1f, 2.5f),
                Transmission = Math.Clamp(centerSurf.Transmission, 0f, 1f),
                EmissiveColor = new Vector4(emCol.X, emCol.Y, emCol.Z, emStrength),
                NormalScale = 1f,
                CoatWeight = centerSurf.CoatWeight,
                CoatRoughness = centerSurf.CoatRoughness,
                CoatIor = centerSurf.CoatIor,
                DiffuseRoughness = centerSurf.DiffuseRoughness,
                ThinFilmThickness = centerSurf.ThinFilmThickness,
                ThinFilmIor = centerSurf.ThinFilmIor,
                Anisotropic = centerSurf.Anisotropic,
                AnisotropicRotation = centerSurf.AnisotropicRotation,
                SubsurfaceWeight = centerSurf.SubsurfaceWeight,
                SubsurfaceScale = centerSurf.SubsurfaceScale,
                SubsurfaceColor = centerSurf.SubsurfaceColor,
                SheenWeight = centerSurf.SheenWeight,
                SheenRoughness = centerSurf.SheenRoughness,
                SheenTint = centerSurf.SheenTint,
                IsDiffuseOnly = centerSurf.IsDiffuseOnly,
                ShaderLutMode = lutMode,
                ShaderLutParamA = lutParamA,
                ShaderLutParamB = lutParamB,
                ShaderLutEmissiveMax = lutEmMax,
                ShaderLutBgra = lutBgra,
                ShaderLutHash = lutHash,
                WireframeMode = wireMode,
                WireframeSize = wireSize,
                WireframePixelSize = wirePixelSize,
                WireframeBaseColor = wireBaseColor,
                WireframeEmissiveColor = wireEmissiveColor,
                LayerTextureAlbedoLayerPlusOne = layerAlbedoPlusOne,
                LayerTextureEmissiveLayerPlusOne = layerEmissivePlusOne,
                LayerUvTransform = layerUvTransform,
                LayerUvRotationRad = layerUvRotRad,
                LayerFlipV = layerFlipV,
                HasTimeDependency = timeDep
            };
        }

        // CPU バックグラウンド参照がある LayerTexture がある場合、メインスレッド上で先に CPU サンプルを確定させる
        if (device != null && deviceContext != null && HasReachableNode(compiled, outNode, ShaderNodeType.LayerTexture))
        {
            PrewarmLayerCpuSamples(compiled, outNode, device, deviceContext);
        }

        Dictionary<int, float[]>? prebakedBumpHeights = spatialNormal
            ? BuildOrFetchBumpHeightGrids(compiled, outNode, res, evalTime, device)
            : null;

        byte[]? baseBgra = needBaseTex ? new byte[res * res * 4] : null;
        byte[]? normBgra = spatialNormal ? new byte[res * res * 4] : null;
        byte[]? ormBgra = needOrmTex ? new byte[res * res * 4] : null;
        float[]? emFloat = spatialEmission ? new float[res * res * 3] : null;
        byte[]? transBgra = spatialTrans ? new byte[res * res * 4] : null;

        float[] rowMaxEm = new float[res];

        Parallel.For(
            0,
            res,
            () => new EvalContext(compiled, res, prebakedBumpHeights, evalTime, device, null),
            (y, _, localCtx) =>
            {
                float v = (y + 0.5f) / res;
                float localEmMax = 0f;

                for (int x = 0; x < res; x++)
                {
                    float u = (x + 0.5f) / res;
                    localCtx.BeginPoint(u, v, x, y);
                    var s = EvalSurfaceInput(localCtx, outNode.Id, "Surface");
                    int pIdx = (y * res + x) * 4;

                    if (baseBgra != null)
                    {
                        baseBgra[pIdx] = ToByte(s.BaseColor.Z);
                        baseBgra[pIdx + 1] = ToByte(s.BaseColor.Y);
                        baseBgra[pIdx + 2] = ToByte(s.BaseColor.X);
                        baseBgra[pIdx + 3] = ToByte(s.Alpha);
                    }

                if (normBgra != null)
                {
                    Vector3 n = s.HasNormal && s.Normal.LengthSquared() > 1e-6f
                        ? Vector3.Normalize(s.Normal)
                        : Vector3.UnitZ;
                    normBgra[pIdx] = ToByte(n.Z * 0.5f + 0.5f);
                    normBgra[pIdx + 1] = ToByte(n.Y * 0.5f + 0.5f);
                    normBgra[pIdx + 2] = ToByte(n.X * 0.5f + 0.5f);
                    normBgra[pIdx + 3] = 255;
                }

                if (ormBgra != null)
                {
                    ormBgra[pIdx] = ToByte(Math.Clamp(s.Metallic, 0f, 1f));
                    ormBgra[pIdx + 1] = ToByte(Math.Clamp(s.Roughness, 0.04f, 1f));
                    ormBgra[pIdx + 2] = 255;
                    ormBgra[pIdx + 3] = 255;
                }

                if (emFloat != null)
                {
                    int eIdx = (y * res + x) * 3;
                    float er = MathF.Max(0f, s.Emission.X);
                    float eg = MathF.Max(0f, s.Emission.Y);
                    float eb = MathF.Max(0f, s.Emission.Z);
                    emFloat[eIdx] = er;
                    emFloat[eIdx + 1] = eg;
                    emFloat[eIdx + 2] = eb;
                    float m = MathF.Max(er, MathF.Max(eg, eb));
                    if (m > localEmMax) localEmMax = m;
                }

                if (transBgra != null)
                {
                    byte tb = ToByte(Math.Clamp(s.Transmission, 0f, 1f));
                    transBgra[pIdx] = tb;
                    transBgra[pIdx + 1] = tb;
                    transBgra[pIdx + 2] = tb;
                    transBgra[pIdx + 3] = 255;
                }
            }
            rowMaxEm[y] = localEmMax;
            return localCtx;
        },
        _ => { });

        byte[]? emBgra = null;
        Vector4 finalEmColor;
        if (emFloat != null)
        {
            float maxEm = rowMaxEm.Length > 0 ? rowMaxEm.Max() : 0f;
            if (maxEm > 1e-4f)
            {
                emBgra = new byte[res * res * 4];
                float invMax = 1f / maxEm;
                for (int i = 0; i < res * res; i++)
                {
                    int eIdx = i * 3;
                    int pIdx = i * 4;
                    float pxStr = MathF.Max(emFloat[eIdx], MathF.Max(emFloat[eIdx + 1], emFloat[eIdx + 2]));
                    if (pxStr <= 1e-6f)
                    {
                        emBgra[pIdx + 3] = 255;
                        continue;
                    }
                    float ratio = pxStr * invMax;
                    float nr = SrgbToLinear(emFloat[eIdx] / pxStr) * ratio;
                    float ng = SrgbToLinear(emFloat[eIdx + 1] / pxStr) * ratio;
                    float nb = SrgbToLinear(emFloat[eIdx + 2] / pxStr) * ratio;
                    emBgra[pIdx] = ToByte(LinearToSrgb(nb));
                    emBgra[pIdx + 1] = ToByte(LinearToSrgb(ng));
                    emBgra[pIdx + 2] = ToByte(LinearToSrgb(nr));
                    emBgra[pIdx + 3] = 255;
                }
                finalEmColor = new Vector4(1f, 1f, 1f, maxEm);
            }
            else
            {
                finalEmColor = Vector4.Zero;
            }
        }
        else if (layerEmissivePlusOne > 0)
        {
            float liveEm = EvalDirectLayerEmissiveStrength(probeCtx, outNode);
            finalEmColor = liveEm > 1e-5f ? new Vector4(1f, 1f, 1f, liveEm) : Vector4.Zero;
        }
        else
        {
            float s = FindMaxComponent(centerSurf.Emission);
            finalEmColor = s > 1e-5f
                ? new Vector4(centerSurf.Emission.X / s, centerSurf.Emission.Y / s, centerSurf.Emission.Z / s, s)
                : Vector4.Zero;
        }

        if (lutMode == 1 || (lutMode == 2 && lutEmMax > 1e-4f))
            finalEmColor = Vector4.Zero;

        return new BakedNodeMaterial
        {
            Width = res,
            Height = res,
            BaseColor = (needBaseTex || layerAlbedoPlusOne > 0)
                ? Vector4.One
                : new Vector4(
                    Math.Clamp(centerSurf.BaseColor.X, 0f, 1f),
                    Math.Clamp(centerSurf.BaseColor.Y, 0f, 1f),
                    Math.Clamp(centerSurf.BaseColor.Z, 0f, 1f),
                    Math.Clamp(centerSurf.Alpha, 0f, 1f)),
            Metallic = needOrmTex ? 1f : Math.Clamp(centerSurf.Metallic, 0f, 1f),
            Roughness = needOrmTex ? 1f : Math.Clamp(centerSurf.Roughness, 0.04f, 1f),
            Ior = Math.Clamp(centerSurf.Ior, 1f, 2.5f),
            Transmission = spatialTrans ? 1f : Math.Clamp(centerSurf.Transmission, 0f, 1f),
            EmissiveColor = finalEmColor,
            NormalScale = 1f,
            CoatWeight = centerSurf.CoatWeight,
            CoatRoughness = centerSurf.CoatRoughness,
            CoatIor = centerSurf.CoatIor,
            DiffuseRoughness = centerSurf.DiffuseRoughness,
            ThinFilmThickness = centerSurf.ThinFilmThickness,
            ThinFilmIor = centerSurf.ThinFilmIor,
            Anisotropic = centerSurf.Anisotropic,
            AnisotropicRotation = centerSurf.AnisotropicRotation,
            SubsurfaceWeight = centerSurf.SubsurfaceWeight,
            SubsurfaceScale = centerSurf.SubsurfaceScale,
            SubsurfaceColor = centerSurf.SubsurfaceColor,
            SheenWeight = centerSurf.SheenWeight,
            SheenRoughness = centerSurf.SheenRoughness,
            SheenTint = centerSurf.SheenTint,
            IsDiffuseOnly = centerSurf.IsDiffuseOnly,
            ShaderLutMode = lutMode,
            ShaderLutParamA = lutParamA,
            ShaderLutParamB = lutParamB,
            ShaderLutEmissiveMax = lutEmMax,
            ShaderLutBgra = lutBgra,
            ShaderLutHash = lutHash,
            WireframeMode = wireMode,
            WireframeSize = wireSize,
            WireframePixelSize = wirePixelSize,
            WireframeBaseColor = wireBaseColor,
            WireframeEmissiveColor = wireEmissiveColor,
            LayerTextureAlbedoLayerPlusOne = layerAlbedoPlusOne,
            LayerTextureEmissiveLayerPlusOne = layerEmissivePlusOne,
            LayerUvTransform = layerUvTransform,
            LayerUvRotationRad = layerUvRotRad,
            LayerFlipV = layerFlipV,
            HasTimeDependency = timeDep,
            BaseColorBgra = baseBgra,
            NormalBgra = normBgra,
            OrmBgra = ormBgra,
            EmissiveBgra = emBgra,
            TransmissionBgra = transBgra
        };
    }

    private static bool HasReachableNode(CompiledGraph compiled, ShaderNode outNode, ShaderNodeType type)
        => FindFirstReachableNode(compiled, outNode.Id, "Surface", type) != null;

    private static void PrewarmLayerCpuSamples(
        CompiledGraph compiled,
        ShaderNode outNode,
        ID3D11Device device,
        ID3D11DeviceContext ctx)
    {
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        if (compiled.InputLinks.TryGetValue((outNode.Id, "Surface"), out var sLink))
            queue.Enqueue(sLink.FromNodeId);

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            if (!compiled.NodeMap.TryGetValue(id, out var n)) continue;
            if (n.Type == ShaderNodeType.LayerTexture)
            {
                int layer = Math.Max(0, (int)MathF.Round(n.GetFloat("Layer", 0f)));
                LayerTextureRegistry.TryGetCpuSample(device, ctx, layer, out _, out _, out _);
            }
            var spec = ShaderNodeRegistry.GetSpec(n.Type);
            foreach (var inp in spec.Inputs)
            {
                if (compiled.InputLinks.TryGetValue((id, inp.Name), out var link))
                    queue.Enqueue(link.FromNodeId);
            }
        }
    }

    private static bool TryExtractDirectLayerFromSocket(
        CompiledGraph compiled,
        int nodeId,
        string socketName,
        ShaderEvalTime evalTime,
        out int layerPlusOne,
        out Vector4 uvTransform,
        out float rotRad,
        out bool flipV)
    {
        layerPlusOne = 0;
        uvTransform = new Vector4(1f, 1f, 0f, 0f);
        rotRad = 0f;
        flipV = false;

        if (!compiled.InputLinks.TryGetValue((nodeId, socketName), out var link))
            return false;
        if (!compiled.NodeMap.TryGetValue(link.FromNodeId, out var lt) || lt.Type != ShaderNodeType.LayerTexture)
            return false;

        int layerIdx = Math.Clamp((int)MathF.Round(lt.GetFloat("Layer", 0f)), 0, 200);
        layerPlusOne = layerIdx + 1;
        flipV = lt.GetBool("FlipV", false);

        if (compiled.InputLinks.TryGetValue((lt.Id, "Vector"), out var vecLink) &&
            compiled.NodeMap.TryGetValue(vecLink.FromNodeId, out var mapNode) &&
            mapNode.Type == ShaderNodeType.Mapping)
        {
            var evalCtx = new EvalContext(compiled, 64, null, evalTime);
            evalCtx.BeginPoint(0f, 0f);
            float lx = EvalFloatInput(evalCtx, mapNode, "LocX", 0f);
            float ly = EvalFloatInput(evalCtx, mapNode, "LocY", 0f);
            float rot = EvalFloatInput(evalCtx, mapNode, "RotZ", 0f) * (MathF.PI / 180f);
            float sx = EvalFloatInput(evalCtx, mapNode, "ScaleX", 1f);
            float sy = EvalFloatInput(evalCtx, mapNode, "ScaleY", 1f);
            uvTransform = new Vector4(sx, sy, lx, ly);
            rotRad = rot;
        }
        return true;
    }

    private static void DetectDirectLayerTextures(
        CompiledGraph compiled,
        ShaderNode outNode,
        ShaderEvalTime evalTime,
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        out int layerAlbedoPlusOne,
        out int layerEmissivePlusOne,
        out Vector4 uvTransform,
        out float rotRad,
        out bool flipV)
    {
        layerAlbedoPlusOne = 0;
        layerEmissivePlusOne = 0;
        uvTransform = new Vector4(1f, 1f, 0f, 0f);
        rotRad = 0f;
        flipV = false;

        if (!compiled.InputLinks.TryGetValue((outNode.Id, "Surface"), out var surfLink) ||
            !compiled.NodeMap.TryGetValue(surfLink.FromNodeId, out var shader))
        {
            return;
        }

        if (shader.Type == ShaderNodeType.PrincipledBsdf)
        {
            if (TryExtractDirectLayerFromSocket(compiled, shader.Id, "BaseColor", evalTime, out int albL, out var albUv, out float albRot, out bool albFlip))
            {
                layerAlbedoPlusOne = albL;
                uvTransform = albUv;
                rotRad = albRot;
                flipV = albFlip;
            }
            if (TryExtractDirectLayerFromSocket(compiled, shader.Id, "EmissionColor", evalTime, out int emL, out var emUv, out float emRot, out bool emFlip))
            {
                layerEmissivePlusOne = emL;
                if (layerAlbedoPlusOne == 0)
                {
                    uvTransform = emUv;
                    rotRad = emRot;
                    flipV = emFlip;
                }
            }
        }
        else if (shader.Type == ShaderNodeType.DiffuseBsdf)
        {
            if (TryExtractDirectLayerFromSocket(compiled, shader.Id, "Color", evalTime, out int albL, out var albUv, out float albRot, out bool albFlip))
            {
                layerAlbedoPlusOne = albL;
                uvTransform = albUv;
                rotRad = albRot;
                flipV = albFlip;
            }
        }
        else if (shader.Type == ShaderNodeType.Emission)
        {
            if (TryExtractDirectLayerFromSocket(compiled, shader.Id, "Color", evalTime, out int emL, out var emUv, out float emRot, out bool emFlip))
            {
                layerEmissivePlusOne = emL;
                uvTransform = emUv;
                rotRad = emRot;
                flipV = emFlip;
            }
        }
        else if (shader.Type == ShaderNodeType.LayerTexture)
        {
            if (TryExtractDirectLayerFromSocket(compiled, outNode.Id, "Surface", evalTime, out int emL, out var emUv, out float emRot, out bool emFlip))
            {
                layerEmissivePlusOne = emL;
                uvTransform = emUv;
                rotRad = emRot;
                flipV = emFlip;
            }
        }
    }

    private static float EvalDirectLayerEmissiveStrength(EvalContext ctx, ShaderNode outNode)
    {
        if (!ctx.Compiled.InputLinks.TryGetValue((outNode.Id, "Surface"), out var surfLink) ||
            !ctx.Compiled.NodeMap.TryGetValue(surfLink.FromNodeId, out var shader))
        {
            return 0f;
        }

        return shader.Type switch
        {
            ShaderNodeType.PrincipledBsdf => MathF.Max(0f, EvalFloatInput(ctx, shader, "EmissionStrength", 0f)),
            ShaderNodeType.Emission => MathF.Max(0f, EvalFloatInput(ctx, shader, "Strength", 1f)),
            ShaderNodeType.LayerTexture => 1f,
            _ => 0f
        };
    }

    internal const int ShaderLutWidth = 1024;

    private static void BuildGpuFeatures(
        CompiledGraph compiled,
        ShaderNode outNode,
        int res,
        ShaderEvalTime evalTime,
        ID3D11Device? device,
        ID3D11DeviceContext? ctx,
        bool needBaseTex,
        ref SurfaceSample centerSurf,
        out int lutMode,
        out float lutParamA,
        out float lutParamB,
        out float lutEmMax,
        out byte[]? lutBgra,
        out string? lutHash,
        out int wireMode,
        out float wireSize,
        out bool wirePixelSize,
        out Vector4 wireBaseColor,
        out Vector4 wireEmissiveColor)
    {
        lutMode = 0;
        lutParamA = 0f;
        lutParamB = 0f;
        lutEmMax = 0f;
        lutBgra = null;
        lutHash = null;

        wireMode = 0;
        wireSize = 1.5f;
        wirePixelSize = true;
        wireBaseColor = new Vector4(1f, 1f, 1f, 1f);
        wireEmissiveColor = Vector4.Zero;

        var wfNode = FindFirstReachableNode(compiled, outNode.Id, "Surface", ShaderNodeType.Wireframe);
        if (wfNode != null)
        {
            wireMode = 1;
            var wfCtx = new EvalContext(compiled, res, null, evalTime, device, ctx);
            wfCtx.BeginPoint(0.5f, 0.5f);
            wfCtx.CurWire = 0f;
            wireSize = Math.Clamp(EvalFloatInput(wfCtx, wfNode, "Size", 1.5f), 0.01f, 20f);
            wirePixelSize = wfNode.GetBool("PixelSize", true);
            centerSurf = EvalSurfaceInput(wfCtx, outNode.Id, "Surface");

            wfCtx.BeginPoint(0.5f, 0.5f);
            wfCtx.CurWire = 1f;
            var sWire = EvalSurfaceInput(wfCtx, outNode.Id, "Surface");
            wireBaseColor = new Vector4(
                Math.Clamp(sWire.BaseColor.X, 0f, 1f),
                Math.Clamp(sWire.BaseColor.Y, 0f, 1f),
                Math.Clamp(sWire.BaseColor.Z, 0f, 1f),
                1f);
            float wEmMax = FindMaxComponent(sWire.Emission);
            wireEmissiveColor = wEmMax > 1e-5f
                ? new Vector4(sWire.Emission.X / wEmMax, sWire.Emission.Y / wEmMax, sWire.Emission.Z / wEmMax, wEmMax)
                : Vector4.Zero;
        }

        var s2rNode = FindFirstReachableNode(compiled, outNode.Id, "Surface", ShaderNodeType.ShaderToRgb);
        if (s2rNode != null)
        {
            lutMode = 1;
            var lutCtx = new EvalContext(compiled, res, null, evalTime, device, ctx);

            // Base shader illumination input
            lutCtx.BeginPoint(0.5f, 0.5f);
            var baseShader = EvalSurfaceInput(lutCtx, s2rNode.Id, "Shader");

            var baseCols = new Vector3[ShaderLutWidth];
            var emCols = new Vector3[ShaderLutWidth];
            float maxBaseLum = 0f;
            float maxEm = 0f;
            for (int i = 0; i < ShaderLutWidth; i++)
            {
                float t = i / (ShaderLutWidth - 1f);
                lutCtx.BeginPoint(0.5f, 0.5f);
                lutCtx.ShaderToRgbOverride = t;
                var samp = EvalSurfaceInput(lutCtx, outNode.Id, "Surface");
                baseCols[i] = new Vector3(samp.BaseColor.X, samp.BaseColor.Y, samp.BaseColor.Z);
                emCols[i] = samp.Emission;
                maxBaseLum = MathF.Max(maxBaseLum, FindMaxComponent(baseCols[i]));
                maxEm = MathF.Max(maxEm, FindMaxComponent(emCols[i]));
            }

            bool hasBase = maxBaseLum > 1e-4f;
            bool hasEm = maxEm > 1e-4f;
            bool isEmissionOut = hasEm && !hasBase;
            lutBgra = new byte[ShaderLutWidth * 2 * 4];

            if (isEmissionOut)
            {
                float scale = MathF.Max(1f, maxEm);
                for (int i = 0; i < ShaderLutWidth; i++)
                {
                    int p0 = i * 4;
                    lutBgra[p0] = ToByte(emCols[i].Z / scale);
                    lutBgra[p0 + 1] = ToByte(emCols[i].Y / scale);
                    lutBgra[p0 + 2] = ToByte(emCols[i].X / scale);
                    lutBgra[p0 + 3] = 255;
                    int p1 = (ShaderLutWidth + i) * 4;
                    lutBgra[p1 + 3] = 255;
                }
                lutParamA = 1f;
                lutParamB = scale;
                lutEmMax = 0f;
                centerSurf = baseShader;
            }
            else
            {
                float emScale = MathF.Max(1f, maxEm);
                for (int i = 0; i < ShaderLutWidth; i++)
                {
                    int p0 = i * 4;
                    lutBgra[p0] = ToByte(baseCols[i].Z);
                    lutBgra[p0 + 1] = ToByte(baseCols[i].Y);
                    lutBgra[p0 + 2] = ToByte(baseCols[i].X);
                    lutBgra[p0 + 3] = 255;
                    int p1 = (ShaderLutWidth + i) * 4;
                    if (hasEm)
                    {
                        lutBgra[p1] = ToByte(emCols[i].Z / emScale);
                        lutBgra[p1 + 1] = ToByte(emCols[i].Y / emScale);
                        lutBgra[p1 + 2] = ToByte(emCols[i].X / emScale);
                    }
                    lutBgra[p1 + 3] = 255;
                }
                lutParamA = 0f;
                lutParamB = 0f;
                lutEmMax = hasEm ? emScale : 0f;
            }
            lutHash = ComputeLutHash(lutMode, lutParamA, lutParamB, lutEmMax, lutBgra);
            return;
        }

        var viewNode = FindFirstReachableNode(compiled, outNode.Id, "Surface", ShaderNodeType.LayerWeight)
            ?? FindFirstReachableNode(compiled, outNode.Id, "Surface", ShaderNodeType.Fresnel);
        if (viewNode != null)
        {
            lutMode = 2;
            var lutCtx = new EvalContext(compiled, res, null, evalTime, device, ctx);
            var baseCols = new Vector3[ShaderLutWidth];
            var emCols = new Vector3[ShaderLutWidth];
            float maxEm = 0f;
            Vector3 minB = new(float.MaxValue), maxB = new(float.MinValue);

            for (int i = 0; i < ShaderLutWidth; i++)
            {
                float ndv = i / (ShaderLutWidth - 1f);
                lutCtx.BeginPoint(0.5f, 0.5f);
                lutCtx.CurNdv = ndv;
                var s = EvalSurfaceInput(lutCtx, outNode.Id, "Surface");
                var b = new Vector3(Math.Clamp(s.BaseColor.X, 0f, 1f), Math.Clamp(s.BaseColor.Y, 0f, 1f), Math.Clamp(s.BaseColor.Z, 0f, 1f));
                baseCols[i] = b;
                minB = Vector3.Min(minB, b);
                maxB = Vector3.Max(maxB, b);
                emCols[i] = s.Emission;
                maxEm = MathF.Max(maxEm, FindMaxComponent(s.Emission));
            }

            bool baseVaries = (maxB - minB).LengthSquared() > 1e-4f;
            lutBgra = new byte[ShaderLutWidth * 2 * 4];
            for (int i = 0; i < ShaderLutWidth; i++)
            {
                int p0 = i * 4;
                lutBgra[p0] = ToByte(baseCols[i].Z);
                lutBgra[p0 + 1] = ToByte(baseCols[i].Y);
                lutBgra[p0 + 2] = ToByte(baseCols[i].X);
                lutBgra[p0 + 3] = 255;
                int p1 = (ShaderLutWidth + i) * 4;
                if (maxEm > 1e-4f)
                {
                    lutBgra[p1] = ToByte(emCols[i].Z / maxEm);
                    lutBgra[p1 + 1] = ToByte(emCols[i].Y / maxEm);
                    lutBgra[p1 + 2] = ToByte(emCols[i].X / maxEm);
                }
                lutBgra[p1 + 3] = 255;
            }
            lutParamA = baseVaries ? 1f : 0f;
            lutParamB = needBaseTex ? 1f : 0f;
            lutEmMax = maxEm;
            lutHash = ComputeLutHash(lutMode, lutParamA, lutParamB, lutEmMax, lutBgra);
            return;
        }

        var aoNode = FindFirstReachableNode(compiled, outNode.Id, "Surface", ShaderNodeType.AmbientOcclusion);
        if (aoNode != null)
        {
            lutMode = 3;
            var lutCtx = new EvalContext(compiled, res, null, evalTime, device, ctx);
            lutCtx.BeginPoint(0.5f, 0.5f);
            float aoDist = Math.Clamp(EvalFloatInput(lutCtx, aoNode, "Distance", 1f), 0.01f, 20f);
            bool aoInside = aoNode.GetBool("Inside", false);

            lutBgra = new byte[ShaderLutWidth * 2 * 4];
            for (int i = 0; i < ShaderLutWidth; i++)
            {
                float ao = i / (ShaderLutWidth - 1f);
                lutCtx.BeginPoint(0.5f, 0.5f);
                lutCtx.CurAo = ao;
                var s = EvalSurfaceInput(lutCtx, outNode.Id, "Surface");
                int p0 = i * 4;
                lutBgra[p0] = ToByte(s.BaseColor.Z);
                lutBgra[p0 + 1] = ToByte(s.BaseColor.Y);
                lutBgra[p0 + 2] = ToByte(s.BaseColor.X);
                lutBgra[p0 + 3] = 255;
                int p1 = (ShaderLutWidth + i) * 4;
                lutBgra[p1 + 3] = 255;
            }
            lutParamA = needBaseTex ? 1f : 0f;
            lutParamB = aoDist;
            lutEmMax = aoInside ? 1f : 0f;
            lutHash = ComputeLutHash(lutMode, lutParamA, lutParamB, lutEmMax, lutBgra);
        }
    }

    private static string ComputeLutHash(int mode, float pA, float pB, float emMax, byte[] bgra)
    {
        ulong fnv = 14695981039346656037UL;
        fnv = FnvMixInt(fnv, mode);
        fnv = FnvMixInt(fnv, BitConverter.SingleToInt32Bits(pA));
        fnv = FnvMixInt(fnv, BitConverter.SingleToInt32Bits(pB));
        fnv = FnvMixInt(fnv, BitConverter.SingleToInt32Bits(emMax));
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int px = bgra[i] | (bgra[i + 1] << 8) | (bgra[i + 2] << 16) | (bgra[i + 3] << 24);
            fnv = FnvMixInt(fnv, px);
        }
        return fnv.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static void AnalyzeSpatialChannels(
        CompiledGraph ctx,
        ShaderNode outNode,
        out bool color,
        out bool alpha,
        out bool rough,
        out bool metal,
        out bool normal,
        out bool emission,
        out bool trans,
        bool directLayerAlbedo = false,
        bool directLayerEmissive = false)
    {
        bool spColor = false, spAlpha = false, spRough = false, spMetal = false, spNormal = false, spEm = false, spTrans = false;
        var visited = new HashSet<int>();
        void WalkShader(int nodeId, string socketName)
        {
            if (!ctx.InputLinks.TryGetValue((nodeId, socketName), out var link)) return;
            if (!ctx.NodeMap.TryGetValue(link.FromNodeId, out var src)) return;
            if (!visited.Add(src.Id)) return;

            if (src.Type == ShaderNodeType.PrincipledBsdf)
            {
                if (!directLayerAlbedo && ctx.IsSocketSpatial(src.Id, "BaseColor")) spColor = true;
                if (ctx.IsSocketSpatial(src.Id, "Alpha")) spAlpha = true;
                if (ctx.IsSocketSpatial(src.Id, "Roughness")) spRough = true;
                if (ctx.IsSocketSpatial(src.Id, "Metallic")) spMetal = true;
                if (ctx.IsSocketSpatial(src.Id, "Normal")) spNormal = true;
                if (ctx.IsSocketSpatial(src.Id, "Transmission")) spTrans = true;
                if ((!directLayerEmissive && ctx.IsSocketSpatial(src.Id, "EmissionColor")) || ctx.IsSocketSpatial(src.Id, "EmissionStrength"))
                    spEm = true;
            }
            else if (src.Type == ShaderNodeType.DiffuseBsdf)
            {
                if (!directLayerAlbedo && ctx.IsSocketSpatial(src.Id, "Color")) spColor = true;
                if (ctx.IsSocketSpatial(src.Id, "Normal")) spNormal = true;
            }
            else if (src.Type == ShaderNodeType.Emission)
            {
                if ((!directLayerEmissive && ctx.IsSocketSpatial(src.Id, "Color")) || ctx.IsSocketSpatial(src.Id, "Strength"))
                    spEm = true;
            }
            else if (src.Type == ShaderNodeType.MixShader)
            {
                if (ctx.IsSocketSpatial(src.Id, "Fac"))
                {
                    spColor = spRough = spMetal = spEm = true;
                    if (ShaderUsesTransmission(ctx, src.Id, "Shader1") || ShaderUsesTransmission(ctx, src.Id, "Shader2"))
                        spTrans = true;
                }
                WalkShader(src.Id, "Shader1");
                WalkShader(src.Id, "Shader2");
            }
            else if (src.Type == ShaderNodeType.ShaderToRgb)
            {
                WalkShader(src.Id, "Shader");
            }
            else if (!directLayerEmissive && ctx.IsNodeSpatial(src.Id))
            {
                spEm = true;
            }
        }
        WalkShader(outNode.Id, "Surface");
        color = spColor;
        alpha = spAlpha;
        rough = spRough;
        metal = spMetal;
        normal = spNormal;
        emission = spEm;
        trans = spTrans;
    }

    private static bool ShaderUsesTransmission(CompiledGraph ctx, int nodeId, string socketName, HashSet<int>? visited = null)
    {
        if (!ctx.InputLinks.TryGetValue((nodeId, socketName), out var link)) return false;
        if (!ctx.NodeMap.TryGetValue(link.FromNodeId, out var src)) return false;
        visited ??= new HashSet<int>();
        if (!visited.Add(src.Id)) return false;

        if (src.Type == ShaderNodeType.PrincipledBsdf)
            return ctx.IsSocketSpatial(src.Id, "Transmission") || src.GetFloat("Transmission", 0f) > 1e-4f;
        if (src.Type == ShaderNodeType.MixShader)
            return ShaderUsesTransmission(ctx, src.Id, "Shader1", visited) || ShaderUsesTransmission(ctx, src.Id, "Shader2", visited);
        return false;
    }

    private static Dictionary<int, float[]>? BuildOrFetchBumpHeightGrids(
        CompiledGraph compiled,
        ShaderNode outNode,
        int res,
        ShaderEvalTime evalTime,
        ID3D11Device? device)
    {
        var bumps = new List<ShaderNode>();
        var shaderVisited = new HashSet<int>();
        var nodeVisited = new HashSet<int>();

        void CollectUpstreamBumps(int nodeId)
        {
            if (!nodeVisited.Add(nodeId)) return;
            if (!compiled.NodeMap.TryGetValue(nodeId, out var n)) return;
            if (n.Type == ShaderNodeType.Bump)
                bumps.Add(n);
            var spec = ShaderNodeRegistry.GetSpec(n.Type);
            foreach (var inp in spec.Inputs)
            {
                if (compiled.InputLinks.TryGetValue((nodeId, inp.Name), out var l))
                    CollectUpstreamBumps(l.FromNodeId);
            }
        }

        void WalkShaders(int nodeId, string sock)
        {
            if (!compiled.InputLinks.TryGetValue((nodeId, sock), out var l)) return;
            if (!compiled.NodeMap.TryGetValue(l.FromNodeId, out var src)) return;
            if (!shaderVisited.Add(src.Id)) return;
            if ((src.Type == ShaderNodeType.PrincipledBsdf || src.Type == ShaderNodeType.DiffuseBsdf) &&
                compiled.InputLinks.TryGetValue((src.Id, "Normal"), out var nLink))
            {
                CollectUpstreamBumps(nLink.FromNodeId);
            }
            else if (src.Type == ShaderNodeType.MixShader)
            {
                WalkShaders(src.Id, "Shader1");
                WalkShaders(src.Id, "Shader2");
            }
            else if (src.Type == ShaderNodeType.ShaderToRgb)
            {
                WalkShaders(src.Id, "Shader");
            }
        }

        WalkShaders(outNode.Id, "Surface");
        if (bumps.Count == 0) return null;

        Dictionary<int, float[]>? result = null;
        foreach (var bump in bumps)
        {
            if (!compiled.InputLinks.TryGetValue((bump.Id, "Height"), out var hLink)) continue;

            // ネストした Bump がある場合は個別評価へフォールバックする
            var subNodes = new HashSet<int>();
            var subLinks = new List<ShaderNodeLink>();
            bool hasNestedBump = false;
            bool subUsesTime = false;
            bool subUsesLayer = false;
            void CollectSub(int id)
            {
                if (!subNodes.Add(id)) return;
                if (!compiled.NodeMap.TryGetValue(id, out var sn)) return;
                if (sn.Type == ShaderNodeType.TimeInfo) subUsesTime = true;
                if (sn.Type == ShaderNodeType.LayerTexture) subUsesLayer = true;
                if (sn.Type == ShaderNodeType.Bump)
                {
                    hasNestedBump = true;
                    return;
                }
                var spec = ShaderNodeRegistry.GetSpec(sn.Type);
                foreach (var inp in spec.Inputs)
                {
                    if (compiled.InputLinks.TryGetValue((id, inp.Name), out var sl))
                    {
                        subLinks.Add(sl);
                        CollectSub(sl.FromNodeId);
                    }
                }
            }
            subLinks.Add(hLink);
            CollectSub(hLink.FromNodeId);
            if (hasNestedBump) continue;

            ulong fnv = 14695981039346656037UL;
            fnv = FnvMixInt(fnv, res);
            fnv = FnvMixString(fnv, hLink.FromSocket);
            if (subUsesTime)
            {
                long tk = ComputeQuantizedTimeKey(compiled.Graph, evalTime, out _, out _);
                fnv = FnvMixInt(fnv, (int)tk);
                fnv = FnvMixInt(fnv, (int)(tk >> 32));
            }
            if (subUsesLayer && device != null)
            {
                long seq = LayerTextureRegistry.GetFrameSequence(device);
                fnv = FnvMixInt(fnv, (int)seq);
                long devBits = device.NativePointer.ToInt64();
                fnv = FnvMixInt(fnv, (int)devBits);
                fnv = FnvMixInt(fnv, (int)(devBits >> 32));
            }
            foreach (int nid in subNodes.OrderBy(x => x))
            {
                if (compiled.NodeMap.TryGetValue(nid, out var sn))
                    fnv = HashSingleNode(fnv, sn);
            }
            foreach (var sl in subLinks.OrderBy(x => x.ToNodeId).ThenBy(x => x.ToSocket, StringComparer.Ordinal))
            {
                fnv = FnvMixInt(fnv, sl.FromNodeId);
                fnv = FnvMixString(fnv, sl.FromSocket);
                fnv = FnvMixInt(fnv, sl.ToNodeId);
                fnv = FnvMixString(fnv, sl.ToSocket);
            }
            string key = fnv.ToString("X16", CultureInfo.InvariantCulture);

            float[]? grid = null;
            lock (s_bumpLock)
            {
                for (int i = 0; i < s_bumpHeightLru.Count; i++)
                {
                    if (string.Equals(s_bumpHeightLru[i].Key, key, StringComparison.Ordinal))
                    {
                        var e = s_bumpHeightLru[i];
                        s_bumpHeightLru.RemoveAt(i);
                        s_bumpHeightLru.Add(e);
                        grid = e.Heights;
                        break;
                    }
                }
            }

            if (grid == null)
            {
                grid = new float[res * res];
                Parallel.For(
                    0,
                    res,
                    () => new EvalContext(compiled, res, null, evalTime, device, null),
                    (y, _, localCtx) =>
                    {
                        float v = (y + 0.5f) / res;
                        int rowOff = y * res;
                        for (int x = 0; x < res; x++)
                        {
                            float u = (x + 0.5f) / res;
                            localCtx.BeginPoint(u, v, x, y);
                            Vector4 hv = EvalOutputAt(localCtx, hLink.FromNodeId, hLink.FromSocket, u, v, out var hKind);
                            grid[rowOff + x] = hKind == ShaderSocketKind.Color
                                ? (hv.X * 0.2126f + hv.Y * 0.7152f + hv.Z * 0.0722f)
                                : hv.X;
                        }
                        return localCtx;
                    },
                    _ => { });

                lock (s_bumpLock)
                {
                    bool dup = false;
                    for (int i = 0; i < s_bumpHeightLru.Count; i++)
                    {
                        if (string.Equals(s_bumpHeightLru[i].Key, key, StringComparison.Ordinal))
                        {
                            dup = true;
                            grid = s_bumpHeightLru[i].Heights;
                            break;
                        }
                    }
                    if (!dup)
                    {
                        s_bumpHeightLru.Add((key, grid));
                        while (s_bumpHeightLru.Count > MaxBumpHeightEntries)
                            s_bumpHeightLru.RemoveAt(0);
                    }
                }
            }

            result ??= new Dictionary<int, float[]>();
            result[bump.Id] = grid;
        }
        return result;
    }

    private static ulong HashSingleNode(ulong fnv, ShaderNode n)
    {
        fnv = FnvMixInt(fnv, n.Id);
        fnv = FnvMixInt(fnv, (int)n.Type);
        foreach (var kv in n.FloatParams.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            fnv = FnvMixString(fnv, kv.Key);
            fnv = FnvMixInt(fnv, BitConverter.SingleToInt32Bits(kv.Value));
        }
        foreach (var kv in n.ColorParams.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            fnv = FnvMixString(fnv, kv.Key);
            if (kv.Value != null)
            {
                for (int i = 0; i < kv.Value.Length; i++)
                    fnv = FnvMixInt(fnv, BitConverter.SingleToInt32Bits(kv.Value[i]));
            }
        }
        foreach (var kv in n.StringParams.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            fnv = FnvMixString(fnv, kv.Key);
            fnv = FnvMixString(fnv, kv.Value ?? string.Empty);
            if (kv.Key == "FilePath" && !string.IsNullOrWhiteSpace(kv.Value))
            {
                try
                {
                    var fi = new FileInfo(kv.Value);
                    if (fi.Exists)
                    {
                        fnv = FnvMixInt(fnv, (int)fi.LastWriteTimeUtc.Ticks);
                        fnv = FnvMixInt(fnv, (int)(fi.LastWriteTimeUtc.Ticks >> 32));
                        fnv = FnvMixInt(fnv, (int)fi.Length);
                    }
                }
                catch { }
            }
        }
        foreach (var kv in n.BoolParams.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            fnv = FnvMixString(fnv, kv.Key);
            fnv = FnvMixInt(fnv, kv.Value ? 1 : 0);
        }
        fnv = FnvMixInt(fnv, n.RampStops.Count);
        foreach (var st in n.RampStops)
        {
            if (st == null) continue;
            fnv = FnvMixInt(fnv, BitConverter.SingleToInt32Bits(st.Position));
            if (st.Color != null)
            {
                for (int i = 0; i < st.Color.Length; i++)
                    fnv = FnvMixInt(fnv, BitConverter.SingleToInt32Bits(st.Color[i]));
            }
        }
        return fnv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FnvMixInt(ulong h, int v)
    {
        unchecked
        {
            h ^= (uint)v;
            h *= 1099511628211UL;
            return h;
        }
    }

    private static ulong FnvMixString(ulong h, string? s)
    {
        if (string.IsNullOrEmpty(s)) return FnvMixInt(h, 0);
        unchecked
        {
            h = FnvMixInt(h, s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 1099511628211UL;
            }
            return h;
        }
    }

    private static SurfaceSample EvalSurfaceInput(EvalContext ctx, int toNodeId, string toSocket)
    {
        if (!ctx.InputLinks.TryGetValue((toNodeId, toSocket), out var link) ||
            !ctx.NodeMap.TryGetValue(link.FromNodeId, out var src) ||
            !ctx.PushGuard(src.Id))
        {
            return new SurfaceSample(new Vector4(0.8f, 0.8f, 0.8f, 1f), 0f, 0.5f, 1.5f, 1f, Vector3.UnitZ, false, 0f, Vector3.Zero);
        }

        try
        {
            switch (src.Type)
            {
                case ShaderNodeType.PrincipledBsdf:
                {
                    Vector4 bc = EvalColorInput(ctx, src, "BaseColor", new Vector4(0.8f, 0.8f, 0.8f, 1f));
                    float metal = Math.Clamp(EvalFloatInput(ctx, src, "Metallic", 0f), 0f, 1f);
                    float rough = Math.Clamp(EvalFloatInput(ctx, src, "Roughness", 0.5f), 0.04f, 1f);
                    float ior = Math.Clamp(EvalFloatInput(ctx, src, "Ior", 1.5f), 1f, 2.5f);
                    float alpha = Math.Clamp(EvalFloatInput(ctx, src, "Alpha", 1f), 0f, 1f);
                    float trans = Math.Clamp(EvalFloatInput(ctx, src, "Transmission", 0f), 0f, 1f);
                    float diffRough = Math.Clamp(EvalFloatInput(ctx, src, "DiffuseRoughness", 0f), 0f, 1f);
                    float sssW = Math.Clamp(EvalFloatInput(ctx, src, "SubsurfaceWeight", 0f), 0f, 1f);
                    float sssS = Math.Clamp(EvalFloatInput(ctx, src, "SubsurfaceScale", 0.25f), 0f, 2f);
                    Vector4 sssC = EvalColorInput(ctx, src, "SubsurfaceColor", new Vector4(1f, 0.35f, 0.25f, 1f));
                    float aniso = Math.Clamp(EvalFloatInput(ctx, src, "Anisotropic", 0f), 0f, 1f);
                    float anisoRot = EvalFloatInput(ctx, src, "AnisotropicRotation", 0f);
                    float coatW = Math.Clamp(EvalFloatInput(ctx, src, "CoatWeight", 0f), 0f, 1f);
                    float coatR = Math.Clamp(EvalFloatInput(ctx, src, "CoatRoughness", 0.05f), 0.03f, 1f);
                    float coatIor = Math.Clamp(EvalFloatInput(ctx, src, "CoatIor", 1.5f), 1f, 2.5f);
                    float sheenW = Math.Clamp(EvalFloatInput(ctx, src, "SheenWeight", 0f), 0f, 1f);
                    float sheenR = Math.Clamp(EvalFloatInput(ctx, src, "SheenRoughness", 0.5f), 0.05f, 1f);
                    Vector4 sheenTint = EvalColorInput(ctx, src, "SheenTint", Vector4.One);
                    float filmThick = Math.Clamp(EvalFloatInput(ctx, src, "ThinFilmThickness", 0f), 0f, 1200f);
                    float filmIor = Math.Clamp(EvalFloatInput(ctx, src, "ThinFilmIor", 1.33f), 1f, 2.5f);
                    Vector4 emCol = EvalColorInput(ctx, src, "EmissionColor", Vector4.One);
                    float emStr = MathF.Max(0f, EvalFloatInput(ctx, src, "EmissionStrength", 0f));
                    Vector3 em = new Vector3(emCol.X, emCol.Y, emCol.Z) * emStr;

                    bool hasNorm = ctx.InputLinks.ContainsKey((src.Id, "Normal"));
                    Vector3 n = hasNorm ? EvalNormalInput(ctx, src, "Normal") : Vector3.UnitZ;

                    return new SurfaceSample(
                        bc, metal, rough, ior, alpha, n, hasNorm, trans, em,
                        coatW, coatR, coatIor, diffRough, filmThick, filmIor, aniso, anisoRot,
                        sssW, sssS, new Vector3(sssC.X, sssC.Y, sssC.Z),
                        sheenW, sheenR, new Vector3(sheenTint.X, sheenTint.Y, sheenTint.Z), false);
                }

                case ShaderNodeType.DiffuseBsdf:
                {
                    Vector4 bc = EvalColorInput(ctx, src, "Color", new Vector4(0.8f, 0.8f, 0.8f, 1f));
                    float rough = Math.Clamp(EvalFloatInput(ctx, src, "Roughness", 0f), 0f, 1f);
                    bool hasNorm = ctx.InputLinks.ContainsKey((src.Id, "Normal"));
                    Vector3 n = hasNorm ? EvalNormalInput(ctx, src, "Normal") : Vector3.UnitZ;
                    return new SurfaceSample(
                        bc, 0f, Math.Clamp(0.85f + 0.15f * rough, 0.85f, 1f), 1.5f, 1f, n, hasNorm, 0f, Vector3.Zero,
                        diffuseRoughness: rough, isDiffuseOnly: true);
                }

                case ShaderNodeType.Emission:
                {
                    Vector4 c = EvalColorInput(ctx, src, "Color", Vector4.One);
                    float str = MathF.Max(0f, EvalFloatInput(ctx, src, "Strength", 1f));
                    return new SurfaceSample(
                        new Vector4(0f, 0f, 0f, 1f), 0f, 1f, 1.5f, 1f, Vector3.UnitZ, false, 0f,
                        new Vector3(c.X, c.Y, c.Z) * str);
                }

                case ShaderNodeType.MixShader:
                {
                    float fac = Math.Clamp(EvalFloatInput(ctx, src, "Fac", 0.5f), 0f, 1f);
                    var s1 = EvalSurfaceInput(ctx, src.Id, "Shader1");
                    var s2 = EvalSurfaceInput(ctx, src.Id, "Shader2");
                    Vector3 n = (s1.HasNormal || s2.HasNormal)
                        ? SafeNormalize(Vector3.Lerp(s1.Normal, s2.Normal, fac), Vector3.UnitZ)
                        : Vector3.UnitZ;
                    return new SurfaceSample(
                        Vector4.Lerp(s1.BaseColor, s2.BaseColor, fac),
                        Lerp(s1.Metallic, s2.Metallic, fac),
                        Lerp(s1.Roughness, s2.Roughness, fac),
                        Lerp(s1.Ior, s2.Ior, fac),
                        Lerp(s1.Alpha, s2.Alpha, fac),
                        n,
                        s1.HasNormal || s2.HasNormal,
                        Lerp(s1.Transmission, s2.Transmission, fac),
                        Vector3.Lerp(s1.Emission, s2.Emission, fac),
                        Lerp(s1.CoatWeight, s2.CoatWeight, fac),
                        Lerp(s1.CoatRoughness, s2.CoatRoughness, fac),
                        Lerp(s1.CoatIor, s2.CoatIor, fac),
                        Lerp(s1.DiffuseRoughness, s2.DiffuseRoughness, fac),
                        Lerp(s1.ThinFilmThickness, s2.ThinFilmThickness, fac),
                        Lerp(s1.ThinFilmIor, s2.ThinFilmIor, fac),
                        Lerp(s1.Anisotropic, s2.Anisotropic, fac),
                        Lerp(s1.AnisotropicRotation, s2.AnisotropicRotation, fac),
                        Lerp(s1.SubsurfaceWeight, s2.SubsurfaceWeight, fac),
                        Lerp(s1.SubsurfaceScale, s2.SubsurfaceScale, fac),
                        Vector3.Lerp(s1.SubsurfaceColor, s2.SubsurfaceColor, fac),
                        Lerp(s1.SheenWeight, s2.SheenWeight, fac),
                        Lerp(s1.SheenRoughness, s2.SheenRoughness, fac),
                        Vector3.Lerp(s1.SheenTint, s2.SheenTint, fac),
                        s1.IsDiffuseOnly && s2.IsDiffuseOnly);
                }

                default:
                {
                    // MaterialOutput.Surface へ Color/Float を直結した場合は Emission として扱う (Blender互換)
                    Vector4 val = EvalOutputAt(ctx, src.Id, link.FromSocket, ctx.CurU, ctx.CurV, out _);
                    return new SurfaceSample(
                        new Vector4(0f, 0f, 0f, 1f), 0f, 1f, 1.5f, 1f, Vector3.UnitZ, false, 0f,
                        new Vector3(MathF.Max(0f, val.X), MathF.Max(0f, val.Y), MathF.Max(0f, val.Z)));
                }
            }
        }
        finally
        {
            ctx.PopGuard(src.Id);
        }
    }

    private static float EvalFloatInput(EvalContext ctx, ShaderNode node, string socketName, float defaultFallback)
    {
        if (ctx.InputLinks.TryGetValue((node.Id, socketName), out var link))
        {
            Vector4 v = EvalOutputAt(ctx, link.FromNodeId, link.FromSocket, ctx.CurU, ctx.CurV, out var kind);
            return kind == ShaderSocketKind.Color
                ? (v.X * 0.2126f + v.Y * 0.7152f + v.Z * 0.0722f)
                : v.X;
        }
        return node.GetFloat(socketName, defaultFallback);
    }

    private static Vector4 EvalColorInput(EvalContext ctx, ShaderNode node, string socketName, Vector4 defaultFallback)
    {
        if (ctx.InputLinks.TryGetValue((node.Id, socketName), out var link))
        {
            Vector4 v = EvalOutputAt(ctx, link.FromNodeId, link.FromSocket, ctx.CurU, ctx.CurV, out var kind);
            if (kind == ShaderSocketKind.Float)
                return new Vector4(v.X, v.X, v.X, 1f);
            return v;
        }
        return node.GetColor(socketName, defaultFallback);
    }

    private static Vector3 EvalVectorInput(EvalContext ctx, ShaderNode node, string socketName, Vector3 defaultFallback)
    {
        if (ctx.InputLinks.TryGetValue((node.Id, socketName), out var link))
        {
            Vector4 v = EvalOutputAt(ctx, link.FromNodeId, link.FromSocket, ctx.CurU, ctx.CurV, out _);
            return new Vector3(v.X, v.Y, v.Z);
        }
        return defaultFallback;
    }

    private static Vector3 EvalNormalInput(EvalContext ctx, ShaderNode node, string socketName)
    {
        if (!ctx.InputLinks.TryGetValue((node.Id, socketName), out var link))
            return Vector3.UnitZ;
        Vector4 v = EvalOutputAt(ctx, link.FromNodeId, link.FromSocket, ctx.CurU, ctx.CurV, out var kind);
        if (kind == ShaderSocketKind.Float)
            return SafeNormalize(new Vector3(0f, 0f, 1f), Vector3.UnitZ);
        return SafeNormalize(new Vector3(v.X, v.Y, MathF.Max(0.001f, v.Z)), Vector3.UnitZ);
    }

    private static Vector4 EvalOutputAt(
        EvalContext ctx, int nodeId, string socketName, float u, float v, out ShaderSocketKind outKind)
    {
        if (!ctx.NodeMap.TryGetValue(nodeId, out var node))
        {
            outKind = ShaderSocketKind.Float;
            return Vector4.Zero;
        }

        if (!ctx.OutputKindMap.TryGetValue((nodeId, socketName), out outKind))
            outKind = ShaderSocketKind.Float;

        var cacheKey = (nodeId, socketName, u, v);
        if (ctx.PointCache.TryGetValue(cacheKey, out var cachedVal))
            return cachedVal;

        if (!ctx.PushGuard(nodeId))
            return Vector4.Zero;

        float prevU = ctx.CurU, prevV = ctx.CurV;
        ctx.CurU = u;
        ctx.CurV = v;
        try
        {
            Vector4 result = EvalNodeOutputCore(ctx, node, socketName, u, v);
            ctx.PointCache[cacheKey] = result;
            return result;
        }
        finally
        {
            ctx.CurU = prevU;
            ctx.CurV = prevV;
            ctx.PopGuard(nodeId);
        }
    }

    private static Vector4 EvalNodeOutputCore(EvalContext ctx, ShaderNode node, string socketName, float u, float v)
    {
        switch (node.Type)
        {
            case ShaderNodeType.TexCoord:
                return socketName switch
                {
                    "Object" => new Vector4(u * 2f - 1f, v * 2f - 1f, 0f, 0f),
                    "Generated" => new Vector4(u, v, 0.5f, 0f),
                    _ => new Vector4(u, v, 0f, 0f)
                };

            case ShaderNodeType.Value:
                return new Vector4(node.GetFloat("Value", 0.5f), 0f, 0f, 1f);

            case ShaderNodeType.RgbColor:
                return node.GetColor("Color", Vector4.One);

            case ShaderNodeType.Mapping:
            {
                Vector3 vec = EvalVectorInput(ctx, node, "Vector", new Vector3(u, v, 0f));
                float lx = EvalFloatInput(ctx, node, "LocX", 0f);
                float ly = EvalFloatInput(ctx, node, "LocY", 0f);
                float rot = EvalFloatInput(ctx, node, "RotZ", 0f) * (MathF.PI / 180f);
                float sx = EvalFloatInput(ctx, node, "ScaleX", 1f);
                float sy = EvalFloatInput(ctx, node, "ScaleY", 1f);
                float px = vec.X * sx;
                float py = vec.Y * sy;
                float c = MathF.Cos(rot), s = MathF.Sin(rot);
                return new Vector4(px * c - py * s + lx, px * s + py * c + ly, vec.Z, 0f);
            }

            case ShaderNodeType.ImageTexture:
            {
                Vector3 coord = EvalVectorInput(ctx, node, "Vector", new Vector3(u, v, 0f));
                string path = node.GetString("FilePath");
                bool flipV = node.GetBool("FlipV", false);
                Vector4 px = SampleImageBilinear(path, coord.X, flipV ? 1f - coord.Y : coord.Y);
                return socketName == "Alpha" ? new Vector4(px.W, 0f, 0f, 1f) : px;
            }

            case ShaderNodeType.NoiseTexture:
            {
                Vector3 coord = EvalVectorInput(ctx, node, "Vector", new Vector3(u, v, 0f));
                float scale = MathF.Max(0.01f, EvalFloatInput(ctx, node, "Scale", 5f));
                float detail = Math.Clamp(EvalFloatInput(ctx, node, "Detail", 4f), 0f, 12f);
                float rough = Math.Clamp(EvalFloatInput(ctx, node, "Roughness", 0.55f), 0f, 1f);
                float lac = MathF.Max(1f, EvalFloatInput(ctx, node, "Lacunarity", 2f));
                float distort = EvalFloatInput(ctx, node, "Distortion", 0f);
                bool is2D = string.Equals(node.GetString("Dimension", "3D"), "2D", StringComparison.OrdinalIgnoreCase);
                string nType = node.GetString("NoiseType", "fBM");
                bool normalize = node.GetBool("Normalize", true);

                Vector3 p = new Vector3(coord.X * scale, coord.Y * scale, is2D ? 0.37f : (coord.Z + 0.37f) * scale);
                if (MathF.Abs(distort) > 1e-4f)
                {
                    float dx = PerlinNoise3D(p + new Vector3(13.1f, 7.7f, 3.3f)) * 2f - 1f;
                    float dy = PerlinNoise3D(p + new Vector3(29.3f, 5.1f, 19.7f)) * 2f - 1f;
                    float dz = PerlinNoise3D(p + new Vector3(41.9f, 11.3f, 23.9f)) * 2f - 1f;
                    p += new Vector3(dx, dy, dz) * distort;
                }

                float fac = EvalFractalNoise(p, detail, rough, lac, nType, normalize);
                if (socketName == "Color")
                {
                    float g = EvalFractalNoise(p + new Vector3(31.7f, 17.3f, 47.1f), detail, rough, lac, nType, normalize);
                    float b = EvalFractalNoise(p + new Vector3(73.1f, 11.9f, 29.3f), detail, rough, lac, nType, normalize);
                    return new Vector4(fac, g, b, 1f);
                }
                return new Vector4(fac, 0f, 0f, 1f);
            }

            case ShaderNodeType.VoronoiTexture:
            {
                Vector3 coord = EvalVectorInput(ctx, node, "Vector", new Vector3(u, v, 0f));
                float scale = MathF.Max(0.01f, EvalFloatInput(ctx, node, "Scale", 8f));
                float smooth = Math.Clamp(EvalFloatInput(ctx, node, "Smoothness", 0.5f), 0f, 1f);
                float rand = Math.Clamp(EvalFloatInput(ctx, node, "Randomness", 1f), 0f, 1f);
                string feat = node.GetString("Feature", "F1");
                EvalVoronoi2D(coord.X * scale, coord.Y * scale, rand, smooth, feat, out float dist, out Vector3 cellCol);
                return socketName == "Color" ? new Vector4(cellCol, 1f) : new Vector4(dist, 0f, 0f, 1f);
            }

            case ShaderNodeType.CheckerTexture:
            {
                Vector3 coord = EvalVectorInput(ctx, node, "Vector", new Vector3(u, v, 0f));
                float scale = MathF.Max(0.01f, EvalFloatInput(ctx, node, "Scale", 8f));
                int ix = (int)MathF.Floor(coord.X * scale);
                int iy = (int)MathF.Floor(coord.Y * scale);
                bool even = ((ix + iy) & 1) == 0;
                float fac = even ? 0f : 1f;
                if (socketName == "Fac") return new Vector4(fac, 0f, 0f, 1f);
                return even
                    ? EvalColorInput(ctx, node, "Color1", new Vector4(0.8f, 0.8f, 0.8f, 1f))
                    : EvalColorInput(ctx, node, "Color2", new Vector4(0.18f, 0.18f, 0.18f, 1f));
            }

            case ShaderNodeType.WaveTexture:
            {
                Vector3 coord = EvalVectorInput(ctx, node, "Vector", new Vector3(u, v, 0f));
                float scale = MathF.Max(0.01f, EvalFloatInput(ctx, node, "Scale", 6f));
                float distort = EvalFloatInput(ctx, node, "Distortion", 2f);
                float detail = Math.Clamp(EvalFloatInput(ctx, node, "Detail", 2f), 0f, 8f);
                bool rings = string.Equals(node.GetString("WaveType", "Bands"), "Rings", StringComparison.OrdinalIgnoreCase);
                string prof = node.GetString("Profile", "Sine");

                float val = rings
                    ? MathF.Sqrt((coord.X - 0.5f) * (coord.X - 0.5f) + (coord.Y - 0.5f) * (coord.Y - 0.5f)) * scale * 2f
                    : (coord.X + coord.Y) * scale;
                if (MathF.Abs(distort) > 1e-4f)
                    val += distort * EvalFractalNoise(new Vector3(coord.X * scale * 0.5f, coord.Y * scale * 0.5f, 0.5f), detail, 0.5f, 2f, "fBM", true);

                float phase = val - MathF.Floor(val);
                float fac = prof switch
                {
                    "Saw" => phase,
                    "Triangle" => 1f - MathF.Abs(2f * phase - 1f),
                    _ => 0.5f + 0.5f * MathF.Sin(val * MathF.PI * 2f)
                };
                return new Vector4(fac, fac, fac, 1f);
            }

            case ShaderNodeType.GradientTexture:
            {
                Vector3 coord = EvalVectorInput(ctx, node, "Vector", new Vector3(u, v, 0f));
                string gType = node.GetString("GradientType", "Linear");
                float fac = gType switch
                {
                    "Spherical" => Math.Clamp(1f - MathF.Sqrt((coord.X - 0.5f) * (coord.X - 0.5f) + (coord.Y - 0.5f) * (coord.Y - 0.5f)) * 2f, 0f, 1f),
                    "Radial" => (MathF.Atan2(coord.Y - 0.5f, coord.X - 0.5f) + MathF.PI) / (2f * MathF.PI),
                    _ => Math.Clamp(coord.X, 0f, 1f)
                };
                return new Vector4(fac, fac, fac, 1f);
            }

            case ShaderNodeType.BrickTexture:
            {
                Vector3 coord = EvalVectorInput(ctx, node, "Vector", new Vector3(u, v, 0f));
                float scale = MathF.Max(0.01f, EvalFloatInput(ctx, node, "Scale", 6f));
                float mortarSize = Math.Clamp(EvalFloatInput(ctx, node, "MortarSize", 0.025f), 0f, 0.45f);
                float bw = MathF.Max(0.05f, EvalFloatInput(ctx, node, "BrickWidth", 0.5f));
                float rh = MathF.Max(0.05f, EvalFloatInput(ctx, node, "RowHeight", 0.25f));

                float sx = coord.X * scale / bw;
                float sy = coord.Y * scale / rh;
                int row = (int)MathF.Floor(sy);
                if ((row & 1) != 0) sx += 0.5f;
                int col = (int)MathF.Floor(sx);
                float fx = sx - col;
                float fy = sy - row;

                float mx = mortarSize / bw;
                float my = mortarSize / rh;
                bool isMortar = fx < mx * 0.5f || fx > 1f - mx * 0.5f || fy < my * 0.5f || fy > 1f - my * 0.5f;
                if (isMortar)
                {
                    return socketName == "Fac"
                        ? new Vector4(0f, 0f, 0f, 1f)
                        : EvalColorInput(ctx, node, "Mortar", new Vector4(0.14f, 0.14f, 0.14f, 1f));
                }

                if (socketName == "Fac") return new Vector4(1f, 0f, 0f, 1f);
                float tint = Hash21(col, row);
                Vector4 c1 = EvalColorInput(ctx, node, "Color1", new Vector4(0.72f, 0.28f, 0.18f, 1f));
                Vector4 c2 = EvalColorInput(ctx, node, "Color2", new Vector4(0.52f, 0.18f, 0.12f, 1f));
                return Vector4.Lerp(c1, c2, tint);
            }

            case ShaderNodeType.MixColor:
            {
                float fac = Math.Clamp(EvalFloatInput(ctx, node, "Fac", 0.5f), 0f, 1f);
                Vector4 a = EvalColorInput(ctx, node, "A", new Vector4(0.5f, 0.5f, 0.5f, 1f));
                Vector4 b = EvalColorInput(ctx, node, "B", new Vector4(0.8f, 0.8f, 0.8f, 1f));
                string blend = node.GetString("BlendType", "Mix");
                Vector4 blended = blend switch
                {
                    "Multiply" => new Vector4(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W),
                    "Add" => new Vector4(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W),
                    "Subtract" => new Vector4(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W),
                    "Screen" => new Vector4(1f - (1f - a.X) * (1f - b.X), 1f - (1f - a.Y) * (1f - b.Y), 1f - (1f - a.Z) * (1f - b.Z), a.W),
                    "Overlay" => new Vector4(OverlayCh(a.X, b.X), OverlayCh(a.Y, b.Y), OverlayCh(a.Z, b.Z), a.W),
                    _ => b
                };
                Vector4 res = Vector4.Lerp(a, blended, fac);
                if (node.GetBool("Clamp", true))
                    res = Vector4.Clamp(res, Vector4.Zero, Vector4.One);
                return res;
            }

            case ShaderNodeType.ColorRamp:
            {
                float rawFac = EvalFloatInput(ctx, node, "Fac", 0.5f);
                float fac = float.IsFinite(rawFac) ? Math.Clamp(rawFac, 0f, 1f) : 0f;
                Vector4 col = EvalColorRamp(ctx, node, fac);
                return socketName == "Alpha" ? new Vector4(col.W, 0f, 0f, 1f) : col;
            }

            case ShaderNodeType.HueSaturation:
            {
                float hShift = EvalFloatInput(ctx, node, "Hue", 0.5f) - 0.5f;
                float sMult = MathF.Max(0f, EvalFloatInput(ctx, node, "Saturation", 1f));
                float vMult = MathF.Max(0f, EvalFloatInput(ctx, node, "Value", 1f));
                float fac = Math.Clamp(EvalFloatInput(ctx, node, "Fac", 1f), 0f, 1f);
                Vector4 c = EvalColorInput(ctx, node, "Color", Vector4.One);
                RgbToHsv(c.X, c.Y, c.Z, out float h, out float s, out float val);
                h = (h + hShift + 10f) % 1f;
                s = Math.Clamp(s * sMult, 0f, 1f);
                val = MathF.Max(0f, val * vMult);
                HsvToRgb(h, s, val, out float rr, out float gg, out float bb);
                return Vector4.Lerp(c, new Vector4(rr, gg, bb, c.W), fac);
            }

            case ShaderNodeType.InvertColor:
            {
                float fac = Math.Clamp(EvalFloatInput(ctx, node, "Fac", 1f), 0f, 1f);
                Vector4 c = EvalColorInput(ctx, node, "Color", Vector4.One);
                return Vector4.Lerp(c, new Vector4(1f - c.X, 1f - c.Y, 1f - c.Z, c.W), fac);
            }

            case ShaderNodeType.BrightContrast:
            {
                Vector4 c = EvalColorInput(ctx, node, "Color", Vector4.One);
                float br = EvalFloatInput(ctx, node, "Bright", 0f);
                float ct = EvalFloatInput(ctx, node, "Contrast", 0f);
                float k = 1f + ct;
                float r = Math.Clamp((c.X - 0.5f) * k + 0.5f + br, 0f, 1f);
                float g = Math.Clamp((c.Y - 0.5f) * k + 0.5f + br, 0f, 1f);
                float b = Math.Clamp((c.Z - 0.5f) * k + 0.5f + br, 0f, 1f);
                return new Vector4(r, g, b, c.W);
            }

            case ShaderNodeType.NormalMap:
            {
                float strength = MathF.Max(0f, EvalFloatInput(ctx, node, "Strength", 1f));
                Vector4 col = EvalColorInput(ctx, node, "Color", new Vector4(0.5f, 0.5f, 1f, 1f));
                bool flipY = node.GetBool("FlipY", false);
                float nx = (col.X * 2f - 1f) * strength;
                float ny = (col.Y * 2f - 1f) * strength * (flipY ? -1f : 1f);
                float nz = MathF.Max(0.001f, col.Z * 2f - 1f);
                Vector3 n = SafeNormalize(new Vector3(nx, ny, nz), Vector3.UnitZ);
                return new Vector4(n, 0f);
            }

            case ShaderNodeType.Bump:
            {
                float strength = Math.Clamp(EvalFloatInput(ctx, node, "Strength", 1f), 0f, 1f);
                float distance = MathF.Max(0f, EvalFloatInput(ctx, node, "Distance", 0.25f));
                bool invert = node.GetBool("Invert", false);
                Vector3 baseN = EvalNormalInput(ctx, node, "Normal");

                if (!ctx.InputLinks.TryGetValue((node.Id, "Height"), out var hLink) || strength <= 1e-4f || distance <= 1e-5f)
                    return new Vector4(baseN, 0f);

                int res = ctx.BakeResolution;
                float eps = 1f / res;
                float dhdu, dhdv;

                if (ctx.PrebakedBumpHeights != null &&
                    ctx.CurPx >= 0 && ctx.CurPy >= 0 &&
                    u == ctx.GridU && v == ctx.GridV &&
                    ctx.PrebakedBumpHeights.TryGetValue(node.Id, out var hGrid))
                {
                    int px = ctx.CurPx, py = ctx.CurPy;
                    int xL = px > 0 ? px - 1 : 0;
                    int xR = px + 1 < res ? px + 1 : res - 1;
                    int yD = py > 0 ? py - 1 : 0;
                    int yU = py + 1 < res ? py + 1 : res - 1;
                    int rowMid = py * res;
                    dhdu = (hGrid[rowMid + xR] - hGrid[rowMid + xL]) / (Math.Max(1, xR - xL) * eps);
                    dhdv = (hGrid[yU * res + px] - hGrid[yD * res + px]) / (Math.Max(1, yU - yD) * eps);
                }
                else
                {
                    float EvalHAt(float su, float sv)
                    {
                        Vector4 hv = EvalOutputAt(ctx, hLink.FromNodeId, hLink.FromSocket, su, sv, out var hKind);
                        return hKind == ShaderSocketKind.Color
                            ? (hv.X * 0.2126f + hv.Y * 0.7152f + hv.Z * 0.0722f)
                            : hv.X;
                    }

                    float hL = EvalHAt(u - eps, v);
                    float hR = EvalHAt(u + eps, v);
                    float hD = EvalHAt(u, v - eps);
                    float hU = EvalHAt(u, v + eps);

                    dhdu = (hR - hL) / (2f * eps);
                    dhdv = (hU - hD) / (2f * eps);
                }

                // UV [0..1] 空間での勾配を適度なタンジェント摂動スケールへ変換
                float bumpFactor = 0.14f * strength * distance * (invert ? -1f : 1f);
                float dx = Math.Clamp(-dhdu * bumpFactor, -2.5f, 2.5f);
                float dy = Math.Clamp(-dhdv * bumpFactor, -2.5f, 2.5f);

                Vector3 perturbed = SafeNormalize(new Vector3(baseN.X + dx, baseN.Y + dy, baseN.Z), Vector3.UnitZ);
                return new Vector4(perturbed, 0f);
            }

            case ShaderNodeType.Math:
            {
                float a = EvalFloatInput(ctx, node, "Value1", 0.5f);
                float b = EvalFloatInput(ctx, node, "Value2", 0.5f);
                string op = node.GetString("Operation", "Add");
                float val = op switch
                {
                    "Subtract" => a - b,
                    "Multiply" => a * b,
                    "Divide" => MathF.Abs(b) > 1e-6f ? a / b : 0f,
                    "Power" => MathF.Pow(MathF.Max(0f, a), b),
                    "Sine" => MathF.Sin(a),
                    "Cosine" => MathF.Cos(a),
                    "Abs" => MathF.Abs(a),
                    "Min" => MathF.Min(a, b),
                    "Max" => MathF.Max(a, b),
                    "GreaterThan" => a > b ? 1f : 0f,
                    "LessThan" => a < b ? 1f : 0f,
                    "Modulo" => MathF.Abs(b) > 1e-6f ? a % b : 0f,
                    "PingPong" => MathF.Abs(b) > 1e-6f ? b - MathF.Abs(MathF.Abs(a % (2f * b)) - b) : 0f,
                    "SmoothMin" => SmoothMin(a, b, 0.25f),
                    _ => a + b
                };
                if (node.GetBool("Clamp", false)) val = Math.Clamp(val, 0f, 1f);
                return new Vector4(val, 0f, 0f, 1f);
            }

            case ShaderNodeType.MapRange:
            {
                float val = EvalFloatInput(ctx, node, "Value", 0.5f);
                float fMin = EvalFloatInput(ctx, node, "FromMin", 0f);
                float fMax = EvalFloatInput(ctx, node, "FromMax", 1f);
                float tMin = EvalFloatInput(ctx, node, "ToMin", 0f);
                float tMax = EvalFloatInput(ctx, node, "ToMax", 1f);
                float t = MathF.Abs(fMax - fMin) > 1e-6f ? (val - fMin) / (fMax - fMin) : 0f;
                if (node.GetBool("Clamp", true)) t = Math.Clamp(t, 0f, 1f);
                return new Vector4(Lerp(tMin, tMax, t), 0f, 0f, 1f);
            }

            case ShaderNodeType.SeparateColor:
            {
                Vector4 c = EvalColorInput(ctx, node, "Color", new Vector4(0.5f, 0.5f, 0.5f, 1f));
                float ch = socketName switch { "Green" => c.Y, "Blue" => c.Z, _ => c.X };
                return new Vector4(ch, 0f, 0f, 1f);
            }

            case ShaderNodeType.CombineColor:
                return new Vector4(
                    EvalFloatInput(ctx, node, "Red", 0f),
                    EvalFloatInput(ctx, node, "Green", 0f),
                    EvalFloatInput(ctx, node, "Blue", 0f),
                    1f);

            case ShaderNodeType.RgbToBw:
            {
                Vector4 c = EvalColorInput(ctx, node, "Color", new Vector4(0.5f, 0.5f, 0.5f, 1f));
                return new Vector4(c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f, 0f, 0f, 1f);
            }

            case ShaderNodeType.TimeInfo:
            {
                float speed = EvalFloatInput(ctx, node, "Speed", 1f);
                float offset = EvalFloatInput(ctx, node, "Offset", 0f);
                float vOut = socketName switch
                {
                    "Frame" => ctx.EvalTime.Frame * speed + offset,
                    "Progress" => ctx.EvalTime.Progress * speed + offset,
                    _ => ctx.EvalTime.Seconds * speed + offset
                };
                return new Vector4(vOut, 0f, 0f, 1f);
            }

            case ShaderNodeType.LayerTexture:
            {
                Vector3 coord = EvalVectorInput(ctx, node, "Vector", new Vector3(u, v, 0f));
                int layer = Math.Max(0, (int)MathF.Round(node.GetFloat("Layer", 0f)));
                bool flipV = node.GetBool("FlipV", false);
                float sy = flipV ? 1f - coord.Y : coord.Y;
                if (LayerTextureRegistry.TryGetCpuSample(ctx.Device, ctx.DeviceContext, layer, out int w, out int h, out var bgra) &&
                    bgra != null && w > 0 && h > 0)
                {
                    Vector4 px = SampleBgraBilinear(bgra, w, h, coord.X, sy);
                    return socketName == "Alpha" ? new Vector4(px.W, 0f, 0f, 1f) : px;
                }
                return socketName == "Alpha" ? new Vector4(1f, 0f, 0f, 1f) : Vector4.One;
            }

            case ShaderNodeType.LayerWeight:
            {
                float blend = Math.Clamp(EvalFloatInput(ctx, node, "Blend", 0.5f), 0f, 0.9999f);
                float ndv = Math.Clamp(ctx.CurNdv, 0f, 1f);
                if (socketName == "Facing")
                {
                    float power = blend < 0.5f ? 2f * blend : (blend < 0.9999f ? 0.5f / (1f - blend) : 100f);
                    float facing = 1f - MathF.Pow(ndv, MathF.Max(1e-4f, power));
                    return new Vector4(Math.Clamp(facing, 0f, 1f), 0f, 0f, 1f);
                }
                else
                {
                    float eta = 1f / MathF.Max(1f - blend, 1e-4f);
                    float f = EvalDielectricFresnel(ndv, eta);
                    return new Vector4(Math.Clamp(f, 0f, 1f), 0f, 0f, 1f);
                }
            }

            case ShaderNodeType.Fresnel:
            {
                float ior = Math.Clamp(EvalFloatInput(ctx, node, "Ior", 1.45f), 1.0001f, 4f);
                float ndv = Math.Clamp(ctx.CurNdv, 0f, 1f);
                float f = EvalDielectricFresnel(ndv, ior);
                return new Vector4(Math.Clamp(f, 0f, 1f), 0f, 0f, 1f);
            }

            case ShaderNodeType.AmbientOcclusion:
            {
                float ao = Math.Clamp(ctx.CurAo, 0f, 1f);
                if (socketName == "AO")
                    return new Vector4(ao, 0f, 0f, 1f);
                Vector4 c = EvalColorInput(ctx, node, "Color", Vector4.One);
                return new Vector4(c.X * ao, c.Y * ao, c.Z * ao, c.W);
            }

            case ShaderNodeType.Wireframe:
                return new Vector4(Math.Clamp(ctx.CurWire, 0f, 1f), 0f, 0f, 1f);

            case ShaderNodeType.ShaderToRgb:
            {
                if (ctx.ShaderToRgbOverride.HasValue)
                {
                    float t = Math.Clamp(ctx.ShaderToRgbOverride.Value, 0f, 1f);
                    return new Vector4(t, t, t, 1f);
                }
                var samp = EvalSurfaceInput(ctx, node.Id, "Shader");
                if (socketName == "Alpha")
                    return new Vector4(samp.Alpha, 0f, 0f, 1f);
                // CPU プレビューフォールバック (通常は GPU LUT でリアルタイム評価される)
                return new Vector4(samp.BaseColor.X * 0.75f, samp.BaseColor.Y * 0.75f, samp.BaseColor.Z * 0.75f, samp.Alpha);
            }

            case ShaderNodeType.VectorMath:
            {
                Vector3 a = EvalVectorInput(ctx, node, "Vector1", new Vector3(u, v, 0f));
                Vector3 b = EvalVectorInput(ctx, node, "Vector2", Vector3.Zero);
                float scale = EvalFloatInput(ctx, node, "Scale", 1f);
                string op = node.GetString("Operation", "Add");
                Vector3 vecOut = Vector3.Zero;
                float valOut = 0f;
                switch (op)
                {
                    case "Subtract": vecOut = a - b; break;
                    case "Multiply": vecOut = a * b; break;
                    case "Divide":
                        vecOut = new Vector3(
                            MathF.Abs(b.X) > 1e-6f ? a.X / b.X : 0f,
                            MathF.Abs(b.Y) > 1e-6f ? a.Y / b.Y : 0f,
                            MathF.Abs(b.Z) > 1e-6f ? a.Z / b.Z : 0f);
                        break;
                    case "Cross": vecOut = Vector3.Cross(a, b); break;
                    case "Dot": valOut = Vector3.Dot(a, b); break;
                    case "Normalize": vecOut = SafeNormalize(a, Vector3.Zero); break;
                    case "Length": valOut = a.Length(); break;
                    case "Distance": valOut = Vector3.Distance(a, b); break;
                    case "Scale": vecOut = a * scale; break;
                    case "Reflect":
                    {
                        Vector3 n = SafeNormalize(b, Vector3.UnitZ);
                        vecOut = Vector3.Reflect(a, n);
                        break;
                    }
                    case "Project":
                    {
                        float bb = Vector3.Dot(b, b);
                        vecOut = bb > 1e-8f ? b * (Vector3.Dot(a, b) / bb) : Vector3.Zero;
                        break;
                    }
                    case "Snap":
                        vecOut = new Vector3(
                            MathF.Abs(b.X) > 1e-6f ? MathF.Floor(a.X / b.X) * b.X : 0f,
                            MathF.Abs(b.Y) > 1e-6f ? MathF.Floor(a.Y / b.Y) * b.Y : 0f,
                            MathF.Abs(b.Z) > 1e-6f ? MathF.Floor(a.Z / b.Z) * b.Z : 0f);
                        break;
                    case "Modulo":
                        vecOut = new Vector3(
                            MathF.Abs(b.X) > 1e-6f ? a.X % b.X : 0f,
                            MathF.Abs(b.Y) > 1e-6f ? a.Y % b.Y : 0f,
                            MathF.Abs(b.Z) > 1e-6f ? a.Z % b.Z : 0f);
                        break;
                    case "Absolute": vecOut = Vector3.Abs(a); break;
                    case "Minimum": vecOut = Vector3.Min(a, b); break;
                    case "Maximum": vecOut = Vector3.Max(a, b); break;
                    case "Sine": vecOut = new Vector3(MathF.Sin(a.X), MathF.Sin(a.Y), MathF.Sin(a.Z)); break;
                    case "Cosine": vecOut = new Vector3(MathF.Cos(a.X), MathF.Cos(a.Y), MathF.Cos(a.Z)); break;
                    case "Floor": vecOut = new Vector3(MathF.Floor(a.X), MathF.Floor(a.Y), MathF.Floor(a.Z)); break;
                    case "Fraction":
                        vecOut = new Vector3(a.X - MathF.Floor(a.X), a.Y - MathF.Floor(a.Y), a.Z - MathF.Floor(a.Z));
                        break;
                    default: vecOut = a + b; break;
                }
                if (socketName == "Value")
                {
                    if (op is not ("Dot" or "Length" or "Distance"))
                        valOut = vecOut.Length();
                    return new Vector4(valOut, 0f, 0f, 1f);
                }
                return new Vector4(vecOut, 0f);
            }

            case ShaderNodeType.SeparateXyz:
            {
                Vector3 vec = EvalVectorInput(ctx, node, "Vector", new Vector3(u, v, 0f));
                float ch = socketName switch { "Y" => vec.Y, "Z" => vec.Z, _ => vec.X };
                return new Vector4(ch, 0f, 0f, 1f);
            }

            case ShaderNodeType.CombineXyz:
                return new Vector4(
                    EvalFloatInput(ctx, node, "X", 0f),
                    EvalFloatInput(ctx, node, "Y", 0f),
                    EvalFloatInput(ctx, node, "Z", 0f),
                    0f);

            case ShaderNodeType.MixValue:
            {
                float fac = EvalFloatInput(ctx, node, "Fac", 0.5f);
                if (node.GetBool("ClampFactor", true))
                    fac = Math.Clamp(fac, 0f, 1f);
                string dt = node.GetString("DataType", "Float");
                if (string.Equals(dt, "Vector", StringComparison.OrdinalIgnoreCase) || socketName == "Vector")
                {
                    Vector3 va = EvalVectorInput(ctx, node, "VecA", Vector3.Zero);
                    Vector3 vb = EvalVectorInput(ctx, node, "VecB", Vector3.One);
                    return new Vector4(Vector3.Lerp(va, vb, fac), 0f);
                }
                float fa = EvalFloatInput(ctx, node, "A", 0f);
                float fb = EvalFloatInput(ctx, node, "B", 1f);
                return new Vector4(Lerp(fa, fb, fac), 0f, 0f, 1f);
            }

            default:
                return Vector4.Zero;
        }
    }

    private static float EvalDielectricFresnel(float ndv, float eta)
    {
        float c = Math.Clamp(ndv, 0f, 1f);
        float g2 = eta * eta - 1f + c * c;
        if (g2 <= 0f) return 1f;
        float g = MathF.Sqrt(g2);
        float a = (g - c) / (g + c);
        float b = (c * (g + c) - 1f) / (c * (g - c) + 1f);
        return 0.5f * a * a * (1f + b * b);
    }

    private static Vector4 SampleBgraBilinear(byte[] bgra, int width, int height, float u, float v)
    {
        float wu = u - MathF.Floor(u);
        float wv = v - MathF.Floor(v);
        float px = wu * (width - 1);
        float py = wv * (height - 1);
        int x0 = (int)MathF.Floor(px);
        int y0 = (int)MathF.Floor(py);
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        float tx = px - x0;
        float ty = py - y0;

        Vector4 ReadPx(int ix, int iy)
        {
            int idx = (iy * width + ix) * 4;
            return new Vector4(
                bgra[idx + 2] / 255f,
                bgra[idx + 1] / 255f,
                bgra[idx] / 255f,
                bgra[idx + 3] / 255f);
        }

        return Vector4.Lerp(
            Vector4.Lerp(ReadPx(x0, y0), ReadPx(x1, y0), tx),
            Vector4.Lerp(ReadPx(x0, y1), ReadPx(x1, y1), tx),
            ty);
    }

    private static float EvalFractalNoise(Vector3 p, float detail, float roughness, float lacunarity, string noiseType, bool normalize)
    {
        float octaves = Math.Clamp(detail, 0f, 12f) + 1f;
        int full = (int)MathF.Floor(octaves);
        float frac = octaves - full;

        float amp = 1f;
        float freq = 1f;
        float sum = 0f;
        float maxAmp = 0f;
        float multAcc = 1f;

        for (int i = 0; i < full; i++)
        {
            float n = PerlinNoise3D(p * freq);
            if (noiseType == "Ridged")
            {
                float r = 1f - MathF.Abs(n * 2f - 1f);
                sum += r * r * amp;
            }
            else if (noiseType == "Multifractal")
            {
                multAcc *= (n * 2f * amp + (1f - amp));
                sum = multAcc;
            }
            else
            {
                sum += n * amp;
            }
            maxAmp += amp;
            freq *= lacunarity;
            amp *= roughness;
        }

        if (frac > 1e-4f)
        {
            float n = PerlinNoise3D(p * freq);
            float partialAmp = amp * frac;
            if (noiseType == "Ridged")
            {
                float r = 1f - MathF.Abs(n * 2f - 1f);
                sum += r * r * partialAmp;
            }
            else if (noiseType == "Multifractal")
            {
                sum = Lerp(sum, sum * (n * 2f), frac);
            }
            else
            {
                sum += n * partialAmp;
            }
            maxAmp += partialAmp;
        }

        if (noiseType == "Multifractal")
            return Math.Clamp(sum * 0.5f, 0f, 1f);

        float val = normalize && maxAmp > 1e-6f ? (sum / maxAmp) : sum;
        return Math.Clamp(val, 0f, 1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PerlinNoise3D(Vector3 p)
    {
        int ix = (int)p.X; if (p.X < ix) ix--;
        int iy = (int)p.Y; if (p.Y < iy) iy--;
        int iz = (int)p.Z; if (p.Z < iz) iz--;

        float fx = p.X - ix;
        float fy = p.Y - iy;
        float fz = p.Z - iz;

        float u = Fade(fx);
        float v = Fade(fy);
        float w = Fade(fz);

        int X = ix & 255, Y = iy & 255, Z = iz & 255;

        int a = s_perm[X] + Y;
        int aa = s_perm[a] + Z;
        int ab = s_perm[a + 1] + Z;
        int b = s_perm[X + 1] + Y;
        int ba = s_perm[b] + Z;
        int bb = s_perm[b + 1] + Z;

        float res = Lerp(
            Lerp(
                Lerp(Grad(s_perm[aa], fx, fy, fz), Grad(s_perm[ba], fx - 1, fy, fz), u),
                Lerp(Grad(s_perm[ab], fx, fy - 1, fz), Grad(s_perm[bb], fx - 1, fy - 1, fz), u),
                v),
            Lerp(
                Lerp(Grad(s_perm[aa + 1], fx, fy, fz - 1), Grad(s_perm[ba + 1], fx - 1, fy, fz - 1), u),
                Lerp(Grad(s_perm[ab + 1], fx, fy - 1, fz - 1), Grad(s_perm[bb + 1], fx - 1, fy - 1, fz - 1), u),
                v),
            w);

        return Math.Clamp(res * 0.5f + 0.5f, 0f, 1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Grad(int hash, float x, float y, float z)
    {
        int h = hash & 15;
        float u = h < 8 ? x : y;
        float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    private static void EvalVoronoi2D(float x, float y, float randomness, float smoothness, string feature, out float distOut, out Vector3 colorOut)
    {
        int xi = (int)MathF.Floor(x);
        int yi = (int)MathF.Floor(y);
        float fx = x - xi;
        float fy = y - yi;

        float d1 = 8f, d2 = 8f;
        float smoothAcc = 0f;
        Vector3 bestCol = Vector3.Zero;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = xi + dx, cy = yi + dy;
                float ox = dx + randomness * Hash21(cx, cy);
                float oy = dy + randomness * Hash21(cx + 17, cy + 53);
                float ddx = ox - fx;
                float ddy = oy - fy;
                float d = MathF.Sqrt(ddx * ddx + ddy * ddy);

                if (d < d1)
                {
                    d2 = d1;
                    d1 = d;
                    bestCol = new Vector3(Hash21(cx + 31, cy + 7), Hash21(cx + 97, cy + 19), Hash21(cx + 13, cy + 103));
                }
                else if (d < d2)
                {
                    d2 = d;
                }

                if (feature == "SmoothF1")
                    smoothAcc += MathF.Exp(-MathF.Max(1f, (1f - smoothness) * 16f) * d);
            }
        }

        distOut = feature switch
        {
            "F2" => Math.Clamp(d2, 0f, 1f),
            "SmoothF1" => Math.Clamp(-MathF.Log(MathF.Max(1e-6f, smoothAcc)) / MathF.Max(1f, (1f - smoothness) * 16f), 0f, 1f),
            _ => Math.Clamp(d1, 0f, 1f)
        };
        colorOut = bestCol;
    }

    private static Vector4 EvalColorRamp(EvalContext ctx, ShaderNode node, float fac)
    {
        if (!ctx.SortedRampStops.TryGetValue(node.Id, out var stops) || stops.Length == 0)
            return new Vector4(fac, fac, fac, 1f);
        if (fac <= stops[0].Position) return ToVec4(stops[0].Color);
        if (fac >= stops[^1].Position) return ToVec4(stops[^1].Color);

        bool isConst = string.Equals(node.GetString("Interpolation", "Linear"), "Constant", StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < stops.Length - 1; i++)
        {
            var a = stops[i];
            var b = stops[i + 1];
            if (fac >= a.Position && fac <= b.Position)
            {
                if (isConst || MathF.Abs(b.Position - a.Position) < 1e-6f)
                    return ToVec4(a.Color);
                float t = (fac - a.Position) / (b.Position - a.Position);
                return Vector4.Lerp(ToVec4(a.Color), ToVec4(b.Color), t);
            }
        }
        return ToVec4(stops[^1].Color);
    }

    private static Vector4 SampleImageBilinear(string path, float u, float v)
    {
        var img = GetOrLoadImage(path);
        if (img == null || img.Width <= 0 || img.Height <= 0)
            return new Vector4(1f, 0f, 1f, 1f); // 未設定時マゼンタではなく画像なし判別用フォールバックでも扱いやすい値

        float wu = u - MathF.Floor(u);
        float wv = v - MathF.Floor(v);
        float px = wu * (img.Width - 1);
        float py = wv * (img.Height - 1);
        int x0 = (int)MathF.Floor(px);
        int y0 = (int)MathF.Floor(py);
        int x1 = Math.Min(x0 + 1, img.Width - 1);
        int y1 = Math.Min(y0 + 1, img.Height - 1);
        float tx = px - x0;
        float ty = py - y0;

        Vector4 ReadPx(int ix, int iy)
        {
            int idx = (iy * img.Width + ix) * 4;
            return new Vector4(
                img.Pixels[idx + 2] / 255f,
                img.Pixels[idx + 1] / 255f,
                img.Pixels[idx] / 255f,
                img.Pixels[idx + 3] / 255f);
        }

        Vector4 c00 = ReadPx(x0, y0);
        Vector4 c10 = ReadPx(x1, y0);
        Vector4 c01 = ReadPx(x0, y1);
        Vector4 c11 = ReadPx(x1, y1);
        return Vector4.Lerp(Vector4.Lerp(c00, c10, tx), Vector4.Lerp(c01, c11, tx), ty);
    }

    private static CachedImagePixels? GetOrLoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            string full = info.FullName;
            long ticks = info.LastWriteTimeUtc.Ticks;
            long len = info.Length;

            lock (s_imgLock)
            {
                for (int i = 0; i < s_imageCache.Count; i++)
                {
                    var e = s_imageCache[i];
                    if (string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase) &&
                        e.WriteTicks == ticks && e.Length == len)
                    {
                        s_imageCache.RemoveAt(i);
                        s_imageCache.Add(e);
                        return e;
                    }
                }
            }

            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (dec.Frames.Count == 0) return null;
            BitmapSource src = dec.Frames[0];
            if (src.Format != PixelFormats.Bgra32)
                src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            int maxDim = 1024;
            if (src.PixelWidth > maxDim || src.PixelHeight > maxDim)
            {
                double s = (double)maxDim / Math.Max(src.PixelWidth, src.PixelHeight);
                src = new TransformedBitmap(src, new ScaleTransform(s, s));
            }

            int w = src.PixelWidth, h = src.PixelHeight;
            byte[] buf = new byte[w * h * 4];
            src.CopyPixels(buf, w * 4, 0);

            var item = new CachedImagePixels { Path = full, WriteTicks = ticks, Length = len, Width = w, Height = h, Pixels = buf };
            lock (s_imgLock)
            {
                s_imageCache.Add(item);
                while (s_imageCache.Count > MaxCachedImages)
                    s_imageCache.RemoveAt(0);
            }
            return item;
        }
        catch
        {
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Hash21(int x, int y)
    {
        uint n = (uint)(x * 374761393 + y * 668265263);
        n = (n ^ (n >> 13)) * 1274126177u;
        return ((n ^ (n >> 16)) & 0x00FFFFFFu) / 16777215f;
    }

    private static float OverlayCh(float a, float b)
        => a < 0.5f ? (2f * a * b) : (1f - 2f * (1f - a) * (1f - b));

    private static float SmoothMin(float a, float b, float k)
    {
        float h = Math.Clamp(0.5f + 0.5f * (b - a) / k, 0f, 1f);
        return Lerp(b, a, h) - k * h * (1f - h);
    }

    private static void RgbToHsv(float r, float g, float b, out float h, out float s, out float v)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min;
        v = max;
        s = max > 1e-6f ? d / max : 0f;
        if (d <= 1e-6f) { h = 0f; return; }
        if (max == r) h = ((g - b) / d) % 6f;
        else if (max == g) h = (b - r) / d + 2f;
        else h = (r - g) / d + 4f;
        h /= 6f;
        if (h < 0f) h += 1f;
    }

    private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        float c = v * s;
        float x = c * (1f - MathF.Abs((h * 6f) % 2f - 1f));
        float m = v - c;
        float rf, gf, bf;
        float h6 = h * 6f;
        if (h6 < 1f) { rf = c; gf = x; bf = 0; }
        else if (h6 < 2f) { rf = x; gf = c; bf = 0; }
        else if (h6 < 3f) { rf = 0; gf = c; bf = x; }
        else if (h6 < 4f) { rf = 0; gf = x; bf = c; }
        else if (h6 < 5f) { rf = x; gf = 0; bf = c; }
        else { rf = c; gf = 0; bf = x; }
        r = rf + m; g = gf + m; b = bf + m;
    }

    private static Vector4 ToVec4(float[]? c)
        => c is { Length: >= 3 } ? new Vector4(c[0], c[1], c[2], c.Length >= 4 ? c[3] : 1f) : Vector4.One;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float lenSq = v.LengthSquared();
        return lenSq > 1e-10f ? v / MathF.Sqrt(lenSq) : fallback;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float FindMaxComponent(Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));
    private static float SrgbToLinear(float x) => x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    private static float LinearToSrgb(float x) => x <= 0.0031308f ? 12.92f * x : 1.055f * MathF.Pow(MathF.Max(0f, x), 1f / 2.4f) - 0.055f;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByte(float v) => (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);
}
