using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MhyUnBoxSetup;

internal static class Program
{
	[STAThread]
	private static int Main(string[] args)
	{
		ApplicationConfiguration.Initialize();

		// 静默安装：--silent [--dir <路径>] [--no-desktop] [--no-launch]
		if (args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)))
		{
			return RunSilent(args);
		}

		Application.Run(new InstallForm());
		return 0;
	}

	private static int RunSilent(string[] args)
	{
		var opt = new InstallOptions();
		string logFile = Path.Combine(Path.GetTempPath(), "MhyUnBoxSetup-silent.log");
		var sb = new System.Text.StringBuilder();
		var utf8Bom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
		var log = new Progress<string>(delegate(string line)
		{
			string text = DateTime.Now.ToString("HH:mm:ss") + " " + line;
			sb.AppendLine(text);
			try
			{
				File.AppendAllText(logFile, text + Environment.NewLine, utf8Bom);
			}
			catch
			{
			}
		});
		for (int i = 0; i < args.Length; i++)
		{
			if (string.Equals(args[i], "--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
			{
				opt.InstallDir = args[i + 1];
			}
			else if (string.Equals(args[i], "--no-desktop", StringComparison.OrdinalIgnoreCase))
			{
				opt.DesktopShortcut = false;
			}
			else if (string.Equals(args[i], "--no-launch", StringComparison.OrdinalIgnoreCase))
			{
				opt.LaunchAfterInstall = false;
			}
		}
		InstallResult result = InstallerEngine
			.InstallAsync(opt, log, new Progress<int>(delegate { }), CancellationToken.None)
			.GetAwaiter()
			.GetResult();
		return result.Success ? 0 : 1;
	}
}

/// <summary>
/// 安装界面：单窗口，安装过程写进中部日志区。
/// 布局顺序（自上而下）：标题栏 → 安装过程日志 → 进度条/状态 → 选项 → 安装位置（底部）→ 按钮。
/// 「安装完成后启动」只在装完之后出现，安装阶段不显示。
/// </summary>
internal sealed class InstallForm : Form
{
	private readonly TextBox _dirBox = new TextBox();

	private readonly CheckBox _desktop = new CheckBox();

	private readonly CheckBox _launch = new CheckBox();

	private readonly TextBox _logBox = new TextBox();

	private readonly ProgressBar _bar = new ProgressBar();

	private readonly Button _install = new Button();

	private readonly Button _close = new Button();

	private readonly Label _status = new Label();

	private readonly Label _dirLabel = new Label();

	private readonly Label _logLabel = new Label();

	private readonly Button _browse = new Button();

	private bool _running;

	private string _installDir = "";

	public InstallForm()
	{
		Text = "BakaBox";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		StartPosition = FormStartPosition.CenterScreen;
		MaximizeBox = false;
		ClientSize = new Size(660, 512);
		Font = new Font("Microsoft YaHei UI", 9f);
		BackColor = Color.White;
		try
		{
			Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
		}
		catch
		{
		}

		var header = new Panel
		{
			Dock = DockStyle.Top,
			Height = 84,
			BackColor = Color.FromArgb(245, 247, 250)
		};
		var title = new Label
		{
			Text = "MhyUnBox",
			Font = new Font("Microsoft YaHei UI", 19f, FontStyle.Bold),
			ForeColor = Color.FromArgb(28, 34, 48),
			Location = new Point(24, 22),
			AutoSize = true
		};
		var iconBox = new PictureBox
		{
			Size = new Size(48, 48),
			Location = new Point(588, 18),
			SizeMode = PictureBoxSizeMode.Zoom
		};
		try
		{
			iconBox.Image = Icon?.ToBitmap();
		}
		catch
		{
		}
		header.Controls.AddRange(new Control[] { title, iconBox });

		_logLabel.Text = "安装过程";
		_logLabel.Location = new Point(24, 96);
		_logLabel.AutoSize = true;

		_logBox.Location = new Point(24, 118);
		_logBox.Size = new Size(612, 186);
		_logBox.Multiline = true;
		_logBox.ReadOnly = true;
		_logBox.ScrollBars = ScrollBars.Vertical;
		_logBox.BackColor = Color.FromArgb(248, 249, 251);
		_logBox.Font = new Font("Consolas", 8.5f);

		_bar.Location = new Point(24, 314);
		_bar.Size = new Size(612, 14);
		_bar.Minimum = 0;
		_bar.Maximum = 100;

		_status.Text = "准备就绪。";
		_status.Location = new Point(24, 334);
		_status.Size = new Size(612, 22);
		_status.ForeColor = Color.FromArgb(110, 118, 132);

		_desktop.Text = "创建桌面快捷方式";
		_desktop.Location = new Point(24, 362);
		_desktop.AutoSize = true;
		_desktop.Checked = true;

		_launch.Text = "立即启动 MhyUnBox";
		_launch.Location = new Point(24, 362);
		_launch.AutoSize = true;
		_launch.Checked = true;
		_launch.Visible = false;

		_dirLabel.Text = "安装位置";
		_dirLabel.Location = new Point(24, 388);
		_dirLabel.AutoSize = true;

		_dirBox.Location = new Point(24, 410);
		_dirBox.Size = new Size(516, 26);
		// 记住上次选择的路径：升级时直接沿用，想换随时点「浏览…」
		_dirBox.Text = SetupInfo.GetRememberedInstallDir();

		_browse.Text = "浏览…";
		_browse.Location = new Point(548, 408);
		_browse.Size = new Size(88, 29);
		_browse.Click += OnBrowse;

		_install.Text = "开始安装";
		_install.Location = new Point(440, 456);
		_install.Size = new Size(112, 32);
		_install.Click += OnInstall;

		_close.Text = "退出";
		_close.Location = new Point(552, 456);
		_close.Size = new Size(84, 32);
		_close.Click += delegate { Close(); };

		Controls.AddRange(new Control[]
		{
			header, _logLabel, _logBox, _bar, _status, _desktop, _launch,
			_dirLabel, _dirBox, _browse, _install, _close
		});
		AcceptButton = _install;
		CancelButton = _close;

		ShowInstalledState();
	}

	private void ShowInstalledState()
	{
		System.Version installed = SetupInfo.GetInstalledVersion(_dirBox.Text.Trim());
		if (installed != null)
		{
			_status.Text = "检测到已安装 v" + installed + "，本次将升级/覆盖安装到下方位置。";
			_install.Text = "重新安装";
		}
		else if (!SamePath(_dirBox.Text.Trim(), SetupInfo.DefaultInstallDir))
		{
			_status.Text = "将安装到上次选择的位置，如需更改可点「浏览…」。";
		}
		string runtime = InstallerEngine.FindDotNet8DesktopRuntime();
		Append("运行时检测：" + (runtime != null
			? ".NET 8 桌面运行时 " + runtime + " 已安装，无需下载"
			: "未安装 .NET 8 桌面运行时，安装时将自动下载（约 55 MB）"));
		Append("程序将安装到用户目录，不需要管理员权限；仅安装运行时时会请求一次管理员授权。");
	}

	private static bool SamePath(string a, string b)
	{
		try
		{
			return string.Equals(
				Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
				Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private void OnBrowse(object sender, EventArgs e)
	{
		using var dlg = new FolderBrowserDialog
		{
			Description = "选择 MhyUnBox 的安装位置",
			UseDescriptionForTitle = true,
			SelectedPath = Directory.Exists(_dirBox.Text) ? _dirBox.Text : SetupInfo.DefaultInstallDir
		};
		if (dlg.ShowDialog(this) == DialogResult.OK)
		{
			_dirBox.Text = dlg.SelectedPath;
		}
	}

	private void Append(string line)
	{
		if (_logBox.IsDisposed)
		{
			return;
		}
		_logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
	}

	private async void OnInstall(object sender, EventArgs e)
	{
		if (_running)
		{
			return;
		}
		string dir = _dirBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(dir))
		{
			MessageBox.Show(this, "请先选择安装位置。", "MhyUnBox 安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}
		try
		{
			Path.GetFullPath(dir);
		}
		catch
		{
			MessageBox.Show(this, "安装路径不合法，请重新选择。", "MhyUnBox 安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		_running = true;
		_install.Enabled = false;
		_close.Enabled = false;
		_install.Text = "安装中…";
		_dirBox.Enabled = false;
		_desktop.Enabled = false;
		_browse.Enabled = false;
		_bar.Value = 0;
		_status.Text = "正在安装…";

		var opt = new InstallOptions
		{
			InstallDir = dir,
			DesktopShortcut = _desktop.Checked,
			// 启动交给「完成」按钮，这里先不启动
			LaunchAfterInstall = false
		};
		var log = new Progress<string>(Append);
		var pct = new Progress<int>(delegate(int p)
		{
			_bar.Value = Math.Max(0, Math.Min(100, p));
		});

		InstallResult result = await Task.Run(delegate
		{
			return InstallerEngine.InstallAsync(opt, log, pct, CancellationToken.None);
		});

		_running = false;
		_close.Enabled = true;

		if (result.Success)
		{
			_installDir = dir;
			_bar.Value = 100;
			_status.Text = "安装完成，可点击「完成」退出。";
			EnterFinishedState();
		}
		else
		{
			_status.Text = "安装未完成：" + result.Message;
			_install.Text = "重试";
			_install.Enabled = true;
			_dirBox.Enabled = true;
			_desktop.Enabled = true;
			_browse.Enabled = true;
			MessageBox.Show(this, "安装未完成：\n" + result.Message, "MhyUnBox 安装", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	/// <summary>装完后的界面：隐藏路径与桌面快捷方式，出现「立即启动」，主按钮变「完成」。</summary>
	private void EnterFinishedState()
	{
		_dirLabel.Visible = false;
		_dirBox.Visible = false;
		_browse.Visible = false;
		_desktop.Visible = false;
		_launch.Visible = true;
		_launch.Location = new Point(24, 362);
		_install.Text = "完成";
		_install.Enabled = true;
		_install.Click -= OnInstall;
		_install.Click += OnFinish;
		AcceptButton = _install;
	}

	private void OnFinish(object sender, EventArgs e)
	{
		if (_launch.Checked && !string.IsNullOrWhiteSpace(_installDir))
		{
			try
			{
				string exe = Path.Combine(_installDir, SetupInfo.AppExe);
				if (File.Exists(exe))
				{
					Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = _installDir, UseShellExecute = true });
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "启动失败：" + ex.Message + "\n可直接从开始菜单打开 MhyUnBox。", "MhyUnBox",
					MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		}
		Close();
	}
}
