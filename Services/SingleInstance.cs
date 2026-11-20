using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ALambdaTTS.Services;

/// <summary>单实例：再启动一次时把已有窗口提到前台，而不是开第二个。</summary>
public static class SingleInstance
{
    private const int SwRestore = 9;

    public static Mutex? Acquire(string name)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out var createdNew);
            if (createdNew)
            {
                return mutex;
            }

            mutex.Dispose();
            return null;
        }
        catch
        {
            // 拿不到互斥体就当单实例处理，总比启动失败好
            return new Mutex(initiallyOwned: true, "ALambdaTTS.Fallback");
        }
    }

    public static void ActivateExisting()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                ShowWindowAsync(process.MainWindowHandle, SwRestore);
                SetForegroundWindow(process.MainWindowHandle);
                break;
            }
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
