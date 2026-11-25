using System;
using System.Windows;
using System.Windows.Data;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Views.Converters;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

internal class BezierCurvePointEditorAttribute : PropertyEditorAttribute2
{
    public BezierCurvePointEditorAttribute() => PropertyEditorSize = PropertyEditorSize.FullWidth;

    public override FrameworkElement Create() => new BezierCurvePointEditor();

    public override void SetBindings(FrameworkElement control, ItemProperty[] itemProperties)
    {
        if (control is not BezierCurvePointEditor editor)
            throw new ArgumentException("control is not BezierCurvePointEditor");

        control.SetBinding(
            BezierCurvePointEditor.CurveJsonProperty,
            ItemPropertiesBinding.Create2(itemProperties));

        editor.Properties = itemProperties;
    }

    public override void ClearBindings(FrameworkElement control)
    {
        if (control is not BezierCurvePointEditor editor)
            throw new ArgumentException("control is not BezierCurvePointEditor");

        BindingOperations.ClearBinding(control, BezierCurvePointEditor.CurveJsonProperty);
        editor.Properties = null;
    }
}
