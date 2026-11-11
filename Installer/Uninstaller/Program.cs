using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;

namespace MhyUnBoxSetup.Uninstaller;

internal static class Program
{
	private const string AppName = "MhyUnBox";
	private const string AppExe = "MhyunBox.exe";
	private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MhyUnBox";

	private static string InstallDir => Path.GetDirectoryName(Application.ExecutablePath) ?? "";

	private static string UserDataDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyunBox");

	[STAThread]
	private static void Main(string[] args)
	{
		ApplicationConfiguration.Initialize();
		try
		{
			bool silent = Array.Exists(args, a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
			bool keepData = Array.Exists(args, a => string.Equals(a, "--keep-data", StringComparison.OrdinalIgnoreCase));
			bool removeData = Array.Exists(args, a => string.Equals(a, "--remove-data", StringComparison.OrdinalIgnoreCase));

			if (!silent)
			{
				using var dlg = new ConfirmForm();
				if (dlg.ShowDialog() != DialogResult.OK)
				{
					return;
				}
				removeData = dlg.RemoveUserData;
			}
			if (keepData)
			{
				removeData = false;
			}

			StopRunningApp();
			RemoveShortcuts();
			RemoveUninstallEntry();
			if (removeData)
			{
				TryDeleteDir(UserDataDir);
			}
			int leftover = DeleteProgramFiles();

			if (!silent)
			{
				MessageBox.Show(
					leftover > 0
						? "已卸载 MhyUnBox。\n\n有 " + leftover + " 个文件正被占用，重启后可手动删除目录：\n" + InstallDir
						: "已卸载 MhyUnBox。" + (removeData ? "\n配置与本地账号数据已一并删除。" : "\n（配置文件保留在 %LOCALAPPDATA%\\MhyunBox）"),
					"MhyUnBox 卸载",
					MessageBoxButtons.OK,
					MessageBoxIcon.Information);
			}
			ScheduleSelfDelete();
		}
		catch (Exception ex)
		{
			if (Array.Exists(args, a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)))
			{
				return;
			}
			MessageBox.Show("卸载过程中出现异常：\n" + ex.Message, "MhyUnBox 卸载", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	private static void StopRunningApp()
	{
		foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)))
		{
			try
			{
				p.Kill();
				p.WaitForExit(5000);
			}
			catch
			{
			}
		}
		Thread.Sleep(600);
	}

	private static void RemoveShortcuts()
	{
		string[] links =
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName + ".lnk"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk")
		};
		foreach (string link in links)
		{
			try
			{
				if (File.Exists(link))
				{
					File.Delete(link);
				}
			}
			catch
			{
			}
		}
	}

	private static void RemoveUninstallEntry()
	{
		try
		{
			Registry.CurrentUser.DeleteSubKeyTree(UninstallSubKey, throwOnMissingSubKey: false);
		}
		catch
		{
		}
		try
		{
			Registry.LocalMachine.DeleteSubKeyTree(UninstallSubKey, throwOnMissingSubKey: false);
		}
		catch
		{
		}
	}

	private static void TryDeleteDir(string dir)
	{
		try
		{
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, recursive: true);
			}
		}
		catch
		{
		}
	}

	/// <summary>删除安装目录下除自身以外的所有内容，返回删除失败的数量。</summary>
	private static int DeleteProgramFiles()
	{
		string self = Path.GetFullPath(Application.ExecutablePath);
		string dir = InstallDir;
		int failed = 0;
		if (!Directory.Exists(dir))
		{
			return 0;
		}
		foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
		{
			try
			{
				if (string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				File.SetAttributes(file, FileAttributes.Normal);
				File.Delete(file);
			}
			catch
			{
				failed++;
			}
		}
		// 自底向上删目录，跳过仍有文件的目录
		foreach (string sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
		{
			try
			{
				if (Directory.GetFileSystemEntries(sub).Length == 0)
				{
					Directory.Delete(sub);
				}
			}
			catch
			{
			}
		}
		return failed;
	}

	/// <summary>自身正在运行，无法自删：交给 cmd 等 2 秒后连目录一起删。</summary>
	private static void ScheduleSelfDelete()
	{
		try
		{
			string dir = InstallDir;
			string self = Application.ExecutablePath;
			var psi = new ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = "/c timeout /t 2 /nobreak >nul & del /f /q \"" + self + "\" & rmdir /s /q \"" + dir + "\"",
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			};
			Process.Start(psi);
		}
		catch
		{
		}
	}

	private sealed class ConfirmForm : Form
	{
		public bool RemoveUserData { get; private set; }

		public ConfirmForm()
		{
			Text = "MhyUnBox 卸载";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition = FormStartPosition.CenterScreen;
			MaximizeBox = false;
			MinimizeBox = false;
			ClientSize = new Size(430, 190);
			Font = new Font("Microsoft YaHei UI", 9f);

			string version = "";
			try
			{
				using RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallSubKey);
				if (key?.GetValue("DisplayVersion") is string v)
				{
					version = v;
				}
			}
			catch
			{
			}

			var title = new Label
			{
				Text = "确定要卸载 MhyUnBox" + (string.IsNullOrEmpty(version) ? "" : " v" + version) + " 吗？",
				Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
				Location = new Point(18, 16),
				AutoSize = true
			};
			var path = new Label
			{
				Text = "安装位置：" + InstallDir,
				Location = new Point(18, 46),
				Size = new Size(395, 34),
				AutoEllipsis = true
			};
			var chk = new CheckBox
			{
				Text = "同时删除配置与本地账号数据",
				Location = new Point(18, 86),
				AutoSize = true
			};
			var ok = new Button
			{
				Text = "卸载",
				DialogResult = DialogResult.OK,
				Location = new Point(238, 132),
				Size = new Size(84, 32)
			};
			var cancel = new Button
			{
				Text = "取消",
				DialogResult = DialogResult.Cancel,
				Location = new Point(330, 132),
				Size = new Size(84, 32)
			};
			ok.Click += delegate { RemoveUserData = chk.Checked; };
			AcceptButton = ok;
			CancelButton = cancel;
			Controls.AddRange(new Control[] { title, path, chk, ok, cancel });
		}
	}
}
