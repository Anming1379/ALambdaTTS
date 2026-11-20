using System.IO;
using System.Windows;
using System.Windows.Controls;
using ALambdaTTS.Services;
using Microsoft.Win32;

namespace ALambdaTTS.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _suppress;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;

        InitializeComponent();

        AboutText.Text = "ALambda TTS 1.0.0\n"
            + "Edge TTS 桌面客户端 · 语音由微软 Edge 朗读接口合成\n"
            + "技术栈 .NET 10 / WPF · 作者 Anming_cat\n"
            + "代码由 AI 生成，作者负责需求、设计与验收\n"
            + "开源仓库 github.com/Anming1379/ALambdaTTS";

        SourceInitialized += (_, _) => NativeTheme.Apply(this);

        _suppress = true;
        ThemeAuto.IsChecked = settings.ThemeMode == AppThemeMode.Auto;
        ThemeLight.IsChecked = settings.ThemeMode == AppThemeMode.Light;
        ThemeDark.IsChecked = settings.ThemeMode == AppThemeMode.Dark;
        DownloadBox.Text = settings.ResolveDownloadFolder();
        _suppress = false;
    }

    private void ThemeChip_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string ?? "auto";
        var mode = tag switch
        {
            "light" => AppThemeMode.Light,
            "dark" => AppThemeMode.Dark,
            _ => AppThemeMode.Auto,
        };

        _suppress = true;
        ThemeAuto.IsChecked = mode == AppThemeMode.Auto;
        ThemeLight.IsChecked = mode == AppThemeMode.Light;
        ThemeDark.IsChecked = mode == AppThemeMode.Dark;
        _suppress = false;

        if (_settings.ThemeMode != mode)
        {
            _settings.ThemeMode = mode;
            ThemeManager.Apply(mode);
            SettingsStore.Save(_settings);
            NativeTheme.Apply(this);
        }
    }

    private void DownloadBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }

        _settings.DownloadFolder = DownloadBox.Text.Trim();
        SettingsStore.Save(_settings);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择下载文件夹",
            Multiselect = false,
        };

        var current = DownloadBox.Text.Trim();
        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            DownloadBox.Text = dialog.FolderName;
        }
    }

    private void ResetFolder_Click(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        _settings.DownloadFolder = null;
        DownloadBox.Text = _settings.ResolveDownloadFolder();
        _suppress = false;
        SettingsStore.Save(_settings);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
