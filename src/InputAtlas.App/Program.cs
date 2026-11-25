using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using Forms = System.Windows.Forms;

namespace InputAtlas.App;

internal static class Program
{
    // 只请求一次 UAC，避免受本机策略限制时重复启动。
    private const string ElevationRequested = "--elevation-requested";

    public static bool PerMonitorV2Enabled { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // 登录启动项以当前用户身份运行，先完成授权，再创建窗口和输入采集器。
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            if (args.Contains(ElevationRequested, StringComparer.Ordinal))
            {
                MessageBox.Show("Windows 未授予管理员权限，InputAtlas 已退出。", "InputAtlas");
                return 5;
            }

            return StartAsAdministrator(args);
        }

        // 必须在创建任何 WPF 对象前设置；自定义 manifest 与 SDK 的 DPI 清单生成不能安全合并。
        var requestAccepted = Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        PerMonitorV2Enabled = requestAccepted || IsCurrentThreadPerMonitorV2();
        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }

    // 保留启动参数。安装器需要等待升级关闭命令完成，常规启动则直接交给新进程。
    private static int StartAsAdministrator(string[] args)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "InputAtlas.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(ElevationRequested);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return 1;
            }

            if (args.Contains("--shutdown-for-update", StringComparer.OrdinalIgnoreCase))
            {
                process.WaitForExit();
                return process.ExitCode;
            }

            return 0;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // 用户取消 UAC 时直接退出，不启动统计或再次弹窗。
            return exception.NativeErrorCode;
        }
        catch (Win32Exception exception)
        {
            MessageBox.Show($"请求管理员权限失败：{exception.Message}", "InputAtlas");
            return exception.NativeErrorCode;
        }
    }

    private static bool IsCurrentThreadPerMonitorV2() =>
        AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-4));

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
}
