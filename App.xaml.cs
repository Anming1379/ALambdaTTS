using System.Windows;
using ALambdaTTS.Services;

namespace ALambdaTTS;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureDirectories();
        // 上次异常退出可能留下缓存，这里先清一遍（本次会话缓存也会在退出时清掉）
        AppPaths.ClearSessionCache();

        var settings = SettingsStore.Load();
        ThemeManager.Apply(settings.ThemeMode);
        ThemeManager.WatchSystemChanges();

        // 单实例：已有窗口时激活它，然后退出本次启动
        _singleInstanceMutex = SingleInstance.Acquire("ALambdaTTS.SingleInstance");
        if (_singleInstanceMutex is null)
        {
            SingleInstance.ActivateExisting();
            Shutdown();
            return;
        }

        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppPaths.ClearSessionCache();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
