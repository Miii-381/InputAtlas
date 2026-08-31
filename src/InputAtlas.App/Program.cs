using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace InputAtlas.App;

internal static class Program
{
    public static bool PerMonitorV2Enabled { get; private set; }

    [STAThread]
    public static void Main()
    {
        // 必须在创建任何 WPF 对象前设置；自定义 manifest 与 SDK 的 DPI 清单生成不能安全合并。
        var requestAccepted = Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        PerMonitorV2Enabled = requestAccepted || IsCurrentThreadPerMonitorV2();
        var application = new App();
        application.InitializeComponent();
        application.Run();
    }

    private static bool IsCurrentThreadPerMonitorV2() =>
        AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-4));

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
}
