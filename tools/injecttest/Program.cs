using System;
using System.IO;
using System.Linq;
using MhyunBox.Services;
using MhyunBox.Settings;

namespace InjectTest;

/// <summary>
/// 用法：injecttest &lt;伪造PCL根目录&gt; &lt;程序Mods源目录&gt;
/// 对伪造客户端执行一次 InjectAll，然后打印 mods 目录最终内容。
/// 期望：每个族（moreshow / MoreShow_Extra / CPTD）最终只有一个 jar。
/// </summary>
internal static class Program
{
	private static int Main(string[] args)
	{
		if (args.Length < 2)
		{
			Console.WriteLine("usage: injecttest <pclRoot> <modSourceDir>");
			return 2;
		}
		string root = args[0];
		string source = args[1];
		try
		{
			SettingsManager.Instance.ModSourceDir = source;
		}
		catch (Exception ex)
		{
			Console.WriteLine("设置 ModSourceDir 失败: " + ex.Message);
		}

		string pclExe = Path.Combine(root, "PCL.exe");
		var (kind, message) = ModInjectService.InjectAll(pclExe, true);
		Console.WriteLine("KIND = " + kind);
		Console.WriteLine("MSG  = " + message);
		Console.WriteLine("--- 第二遍（幂等性检查：不应再复制任何文件）---");
		var (kind2, message2) = ModInjectService.InjectAll(pclExe, true);
		Console.WriteLine("KIND2 = " + kind2);
		Console.WriteLine("MSG2  = " + message2);

		Console.WriteLine("--- mods 目录最终内容 ---");
		string mods = Path.Combine(root, ".minecraft", "versions", "元素之诗-1.12.2", "mods");
		if (!Directory.Exists(mods))
		{
			Console.WriteLine("(mods 目录不存在) " + mods);
			return 1;
		}
		foreach (string f in Directory.GetFiles(mods).OrderBy(x => x))
		{
			Console.WriteLine(Path.GetFileName(f) + "  " + new FileInfo(f).Length + " B");
		}

		// 断言：三族各只有一个文件
		string[] names = Directory.GetFiles(mods).Select(Path.GetFileName).ToArray();
		int mains = names.Count(n => n.StartsWith("moreshow", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("MoreShow_Extra", StringComparison.OrdinalIgnoreCase));
		int extras = names.Count(n => n.StartsWith("MoreShow_Extra", StringComparison.OrdinalIgnoreCase));
		int cptd = names.Count(n => n.ToLowerInvariant().Contains("cptd"));
		Console.WriteLine($"--- 计数 --- 主模组={mains} 附属={extras} CPTD={cptd}");
		bool ok = mains == 1 && extras == 1 && cptd == 1;
		Console.WriteLine(ok ? "RESULT = PASS（每族恰好一个文件）" : "RESULT = FAIL（存在重复或缺失）");
		return ok ? 0 : 1;
	}
}
