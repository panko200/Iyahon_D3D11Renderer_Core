using System;
using YukkuriMovieMaker.Plugin;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

public class Viewport3DToolPlugin : IToolPlugin
{
    public string Name => "D3D11 3Dビューポート";
    public Type ViewModelType => typeof(Viewport3DViewModel);
    public Type ViewType => typeof(Viewport3DControl);
    public bool AllowMultipleInstances => true;
}
