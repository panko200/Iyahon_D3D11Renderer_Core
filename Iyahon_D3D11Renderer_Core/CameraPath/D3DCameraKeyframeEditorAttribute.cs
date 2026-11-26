using System;
using System.Windows;
using System.Windows.Data;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Views.Converters;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.CameraPath;

internal class D3DCameraKeyframeEditorAttribute : PropertyEditorAttribute2
{
    public D3DCameraKeyframeEditorAttribute() => PropertyEditorSize = PropertyEditorSize.FullWidth;

    public override FrameworkElement Create() => new D3DCameraKeyframeEditor();

    public override void SetBindings(FrameworkElement control, ItemProperty[] itemProperties)
    {
        if (control is not D3DCameraKeyframeEditor editor)
            throw new ArgumentException("control is not D3DCameraKeyframeEditor");

        control.SetBinding(
            D3DCameraKeyframeEditor.KeyframesProperty,
            ItemPropertiesBinding.Create2(itemProperties));

        editor.Properties = itemProperties;
        editor.SelectKeyframe(0);
    }

    public override void ClearBindings(FrameworkElement control)
    {
        if (control is not D3DCameraKeyframeEditor editor)
            throw new ArgumentException("control is not D3DCameraKeyframeEditor");

        BindingOperations.ClearBinding(control, D3DCameraKeyframeEditor.KeyframesProperty);
        editor.Properties = null;
    }
}
