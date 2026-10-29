using System.Runtime.InteropServices;

namespace Microsoft.Windows.Foundation.UndockedRegFreeWinRTCS;

internal static class NativeMethods
{
	[DllImport("Microsoft.WindowsAppRuntime.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	internal static extern int WindowsAppRuntime_EnsureIsLoaded();

	// 完整初始化 Windows App SDK：在已注册的 Microsoft.WindowsAppRuntime.1.8 框架包上
	// 建立“动态依赖”，从而把 WinUI 框架的 XAML 资源
	// （ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml）注册进本进程的
	// 资源管理器，使其可被解析。majorMinorVersion=0x00010008 即 1.8，
	// versionTag=null、minVersion=0 表示接受任意已安装的 1.8 框架版本。
	[DllImport("Microsoft.WindowsAppRuntime.Bootstrap.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	internal static extern int MddBootstrapInitialize(uint majorMinorVersion, [MarshalAs(UnmanagedType.LPWStr)] string? versionTag, ulong minVersion);
}
