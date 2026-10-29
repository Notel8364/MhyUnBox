using System;

namespace Microsoft.Windows.Foundation.UndockedRegFreeWinRTCS;

internal class AutoInitialize
{
	internal static void AccessWindowsAppSDK()
	{
		// 纯 reg-free WinRT 加载（原作者的方式）：把框架原生运行时放在输出目录旁，
		// 通过 MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY 让 WindowsAppRuntime 自包含地
		// 加载（含 XAML 主题资源所需的 Microsoft.UI.pri / resources.pri）。
		// 注意：本机已注册的 Microsoft.WindowsAppRuntime.1.8 框架为 2504 系（8000.994），
		// 而本工程编译所依赖的 WinAppSDK 引导程序为 2509 系，二者版本不一致；
		// 因此不走 MddBootstrapInitialize（会 FailFast），改为完整的本地 reg-free 框架。
		Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
		try
		{
			NativeMethods.WindowsAppRuntime_EnsureIsLoaded();
		}
		catch
		{
		}
	}
}
