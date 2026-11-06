using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace DevicePower;

public partial class AboutWindow : Window
{
    private const string RepositoryUrl = "https://github.com/xujiucun/external-device-power";

    public AboutWindow()
    {
        InitializeComponent();

        VersionText.Text = "版本 V" + ReadVersion();
        RepoText.Text = RepositoryUrl;
        RuntimeText.Text = $".NET {Environment.Version.Major} · Windows {Environment.OSVersion.Version.Build}";
        PathText.Text = CurrentExePath();
    }

    private static string ReadVersion()
    {
        string version = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(version)) return "0.0.1";

        int cut = version.IndexOf('+');
        return cut > 0 ? version[..cut] : version;
    }

    private static string CurrentExePath()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.MainModule?.FileName ?? AppContext.BaseDirectory;
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(RepositoryUrl);
            CopyHint.Text = "已复制";
        }
        catch
        {
            CopyHint.Text = "复制失败，请手动选中";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
