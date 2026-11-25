using System.Windows;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.CameraPath;

internal class D3DCameraBezierAnimationEditorAttribute : BezierAnimationEditorAttribute
{
    public D3DCameraBezierAnimationEditorAttribute()
    {
        PropertyEditorSize = PropertyEditorSize.FullWidth;
        MinHeight = 200;
    }

    public override FrameworkElement Create()
    {
        var editor = (BezierAnimationEditor)base.Create();
        editor.Height = 200;
        editor.MinHeight = 200;
        return editor;
    }
}
