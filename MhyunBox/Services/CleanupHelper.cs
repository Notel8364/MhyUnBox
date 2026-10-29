using System;
using System.IO;
using System.Runtime.InteropServices;
using MhyunBox.Settings;

namespace MhyunBox.Services;

/// <summary>
/// 更新 / 合并裁剪之后的清理工具。
///
/// 安全原则（重要）：
///   1) 「文件与包」的删除一律先进回收站（可还原），绝不做不可逆的物理删除；
///   2) 受 AppSettings.AutoCleanupEnabled 主开关控制，默认关闭 —— 未开启时只提示、不动文件；
///   3) 程序自己产出的解压暂存目录（stage / stage_dragon / stage_rest）不含用户数据，
///      流程结束后无条件删除，避免磁盘残留。
/// </summary>
public static class CleanupHelper
{
	/// <summary>主开关：默认关闭，用户在设置里开启后才会清理资源包/增量包/冗余备份。</summary>
	public static bool Enabled => SettingsManager.Instance.AutoCleanupEnabled;

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct SHFILEOPSTRUCT
	{
		public IntPtr hwnd;

		public uint wFunc;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string pFrom;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string pTo;

		public ushort fFlags;

		public int fAnyOperationsAborted;

		public IntPtr hNameMappings;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string lpszProgressTitle;
	}

	private const uint FO_DELETE = 3u;

	private const ushort FOF_SILENT = 4;

	private const ushort FOF_NOCONFIRMATION = 16;

	private const ushort FOF_ALLOWUNDO = 64;

	private const ushort FOF_NOERRORUI = 1024;

	/// <summary>把文件或目录移入回收站。成功 true；失败/不存在 false（调用方据此保留原文件）。</summary>
	public static bool ToRecycleBin(string path)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
			{
				return false;
			}
			SHFILEOPSTRUCT lpFileOp = default(SHFILEOPSTRUCT);
			lpFileOp.wFunc = FO_DELETE;
			// pFrom 必须是「双 \0 结尾」的路径串
			lpFileOp.pFrom = Path.GetFullPath(path) + "\0\0";
			lpFileOp.fFlags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI;
			return SHFileOperation(ref lpFileOp) == 0;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// 仅在开启自动清理时把「文件/包」移入回收站；未开启则原样保留并提示。
	/// 返回是否真的清理掉了。
	/// </summary>
	public static bool RecycleIfEnabled(string path, Action<string>? log, string what)
	{
		if (!Enabled)
		{
			log?.Invoke("[自动清理] 已保留" + what + "（如需自动清理，请在设置里开启「更新后自动清理」）；路径：" + path);
			return false;
		}
		if (!ToRecycleBin(path))
		{
			log?.Invoke("[自动清理] 移入回收站失败，已保留" + what + "：" + path);
			return false;
		}
		log?.Invoke("[自动清理] 已移入回收站（可还原）：" + what + " —— " + path);
		return true;
	}

	/// <summary>无条件删除程序自己产出的解压暂存目录（不含用户数据，失败可忽略）。</summary>
	public static void RemoveTempDir(string dir, Action<string>? log)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
			{
				Directory.Delete(dir, recursive: true);
				log?.Invoke("[清理] 已删除解压暂存目录：" + dir);
			}
		}
		catch (Exception ex)
		{
			log?.Invoke("[清理] 删除暂存目录失败（可忽略）：" + ex.Message);
		}
	}

	/// <summary>
	/// 按保留天数清理 DragonCore 冗余备份目录（DragonCore_冗余备份_*）。
	/// keepDays &lt;= 0 表示永不清理；实际删除需主开关开启，且先进回收站。
	/// </summary>
	public static int PurgeRedundantBackups(string parentDir, int keepDays, Action<string>? log)
	{
		int num = 0;
		try
		{
			if (keepDays <= 0 || string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir))
			{
				return 0;
			}
			DateTime threshold = DateTime.Now.AddDays(-keepDays);
			foreach (string dir in Directory.EnumerateDirectories(parentDir, "DragonCore_冗余备份_*"))
			{
				try
				{
					DirectoryInfo directoryInfo = new DirectoryInfo(dir);
					if (directoryInfo.LastWriteTime >= threshold)
					{
						continue;
					}
					if (!Enabled)
					{
						// 未开启：只提示，绝不动用户文件
						log?.Invoke("[自动清理] 冗余备份已超期 " + keepDays + " 天：" + directoryInfo.Name + "（开启「更新后自动清理」后会自动移入回收站）");
						continue;
					}
					if (ToRecycleBin(dir))
					{
						num++;
						log?.Invoke("[自动清理] 冗余备份超期已移入回收站：" + directoryInfo.Name);
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return num;
	}
}
