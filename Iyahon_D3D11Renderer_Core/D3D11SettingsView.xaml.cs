using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Iyahon_D3D11Renderer_Core;

public partial class D3D11SettingsView : UserControl
{
    public D3D11SettingsView()
    {
        InitializeComponent();
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string presetName })
        {
            D3D11RendererSettings.Default.ApplyQualityPreset(presetName);
        }
    }

    private void BrowseEnvMap_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "HDRI 環境マップ (360° Equirectangular) を選択",
            Filter = "HDRI / 画像ファイル (*.hdr;*.png;*.jpg;*.jpeg;*.dds;*.bmp;*.tif;*.tiff;*.tga)|*.hdr;*.png;*.jpg;*.jpeg;*.dds;*.bmp;*.tif;*.tiff;*.tga|すべてのファイル (*.*)|*.*",
        };
        if (!string.IsNullOrWhiteSpace(D3D11RendererSettings.Default.EnvironmentMapPath))
        {
            try
            {
                dlg.InitialDirectory = System.IO.Path.GetDirectoryName(D3D11RendererSettings.Default.EnvironmentMapPath);
            }
            catch { }
        }
        if (dlg.ShowDialog() == true)
        {
            D3D11RendererSettings.Default.EnvironmentMapPath = dlg.FileName;
        }
    }

    private void ClearEnvMap_Click(object sender, RoutedEventArgs e)
    {
        D3D11RendererSettings.Default.EnvironmentMapPath = string.Empty;
    }
}
