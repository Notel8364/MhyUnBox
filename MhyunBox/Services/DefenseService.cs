using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MhyunBox.Services;

public sealed class DefenseService : IDisposable
{
	private sealed class PotSpec
	{
		public string FileName = "";

		public string Content = "";
	}

	private sealed class TamperSnapshot
	{
		public bool RealtimeDisabled;

		public bool AntiSpywareDisabled;

		public HashSet<string> Exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public Dictionary<string, string> IfeoDebuggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public HashSet<string> DisallowRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public long HostsSignature;
	}

	public const string Marker = "【MhyunBox 蜜罐诱饵文件 · 非真实数据 · 请勿写入真实信息】";

	private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyunBox", "HoneyPots");

	private static readonly string ManifestPath = Path.Combine(DataDir, "honeypots.txt");

	private static readonly string IfeoRoot = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options";

	private static readonly string DisallowRunPath = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer\\DisallowRun";

	private static readonly string HostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");

	private const string GuardScriptName = "guard.ps1";

	private const string GuardTaskName = "MhyunBoxDefenseGuard";

	private static readonly string SignaturesPath = Path.Combine(AppContext.BaseDirectory, "KillTools", "特征库.txt");

	private static readonly string GuardAlertLogPath = Path.Combine(DataDir, "defense_alerts.log");

	private static readonly string[] ExclusionSubKeys = new string[3] { "Paths", "Processes", "Extensions" };

	private static readonly string[] IfeoSuspiciousNames = new string[15]
	{
		"360safe.exe", "360tray.exe", "360sd.exe", "360leakfixer.exe", "HipsTray.exe", "HipsDaemon.exe", "usysdiag.exe", "QQPCRTP.exe", "QQPCtray.exe", "avp.exe",
		"msseces.exe", "MsMpEng.exe", "NisSrv.exe", "setup.exe", "installer.exe"
	};

	private static HashSet<string>? _ifeoTargets;

	private static readonly PotSpec[] PotSpecs = new PotSpec[3]
	{
		new PotSpec
		{
			FileName = "账号密码备份.txt",
			Content = "支付宝账号：13800000000\n支付宝密码：Abc123##MhyunBox\n微信支付密码：888777\n银行卡号：6222 0202 0000 0000 111\n银行密码：P@ss_2024mhyunbox\n\n【MhyunBox 蜜罐诱饵文件 · 非真实数据 · 请勿写入真实信息】"
		},
		new PotSpec
		{
			FileName = "网银支付密码.txt",
			Content = "网银登录账号：test_user_2024\n网银登录密码：Test#2024mhyunbox\n支付密码：963852\nU盾密码：741852mhyunbox\n\n【MhyunBox 蜜罐诱饵文件 · 非真实数据 · 请勿写入真实信息】"
		},
		new PotSpec
		{
			FileName = "重要密码记录.txt",
			Content = "Steam账号：mhyunbox_test\nSteam密码：Steam#963mhyunbox\nQQ邮箱：test@mhyunbox.test\n邮箱密码：Mail@741mhyunbox\n\n【MhyunBox 蜜罐诱饵文件 · 非真实数据 · 请勿写入真实信息】"
		}
	};

	private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();

	private readonly Dictionary<string, DateTime> _lastRaise = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private readonly object _lock = new object();

	private volatile bool _suppressRestore;

	private TamperSnapshot? _baseline;

	private Timer? _tamperTimer;

	public static DefenseService Instance { get; } = new DefenseService();

	public bool IsMonitoring { get; private set; }

	private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

	private static string DownloadsDir => Path.Combine(UserProfile, "Downloads");

	public static string GuardOptionsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyunBox", "HoneyPots", "guard_options.txt");

	public event Action<string, string>? FileModified;

	public event Action<string, string>? TamperDetected;

	private static string[] GetPotBaseDirs()
	{
		return new string[3]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
			Environment.GetFolderPath(Environment.SpecialFolder.Personal),
			DownloadsDir
		};
	}

	private IEnumerable<(string Path, string Content)> PotFilePaths()
	{
		string[] dirs = GetPotBaseDirs();
		for (int i = 0; i < PotSpecs.Length; i++)
		{
			yield return (Path: Path.Combine(dirs[i], PotSpecs[i].FileName), Content: PotSpecs[i].Content);
		}
	}

	public (int Deployed, int Skipped) DeployHoneyPots()
	{
		int num = 0;
		int num2 = 0;
		List<string> list = new List<string>();
		string text = Path.Combine(DataDir, "Backup");
		try
		{
			Directory.CreateDirectory(DataDir);
			Directory.CreateDirectory(text);
		}
		catch
		{
		}
		for (int i = 0; i < PotSpecs.Length; i++)
		{
			string text2 = Path.Combine(GetPotBaseDirs()[i], PotSpecs[i].FileName);
			string content = PotSpecs[i].Content;
			try
			{
				string directoryName = Path.GetDirectoryName(text2);
				if (string.IsNullOrEmpty(directoryName) || !Directory.Exists(directoryName))
				{
					num2++;
					continue;
				}
				if (File.Exists(text2) && !File.ReadAllText(text2).Contains("【MhyunBox 蜜罐诱饵文件 · 非真实数据 · 请勿写入真实信息】", StringComparison.Ordinal))
				{
					num2++;
					continue;
				}
				File.WriteAllText(text2, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				try
				{
					File.WriteAllText(Path.Combine(text, PotSpecs[i].FileName), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "写入蜜罐备份失败: " + PotSpecs[i].FileName);
				}
				list.Add(text2);
				num++;
			}
			catch (Exception ex2)
			{
				LogHelper.LogError(ex2, "部署蜜罐文件失败: " + text2);
				num2++;
			}
		}
		try
		{
			File.WriteAllLines(ManifestPath, list, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch (Exception ex3)
		{
			LogHelper.LogError(ex3, "写入蜜罐清单失败");
		}
		return (Deployed: num, Skipped: num2);
	}

	public List<string> DeployedFiles()
	{
		List<string> list = new List<string>();
		if (!File.Exists(ManifestPath))
		{
			return list;
		}
		try
		{
			string[] array = File.ReadAllLines(ManifestPath);
			string[] array2 = array;
			foreach (string text in array2)
			{
				if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
				{
					list.Add(text);
				}
			}
		}
		catch
		{
		}
		return list;
	}

	public int StartMonitoring()
	{
		StopMonitoring();
		List<string> list = DeployedFiles();
		lock (_lock)
		{
			foreach (IGrouping<string, string> item in list.GroupBy<string, string>((string p) => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase))
			{
				HashSet<string> watchFiles = new HashSet<string>(item.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
				FileSystemWatcher fileSystemWatcher;
				try
				{
					fileSystemWatcher = new FileSystemWatcher(item.Key)
					{
						NotifyFilter = (NotifyFilters.FileName | NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.LastWrite),
						Filter = "*.*",
						IncludeSubdirectories = false,
						EnableRaisingEvents = true
					};
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "创建文件监控失败: " + item.Key);
					continue;
				}
				fileSystemWatcher.Changed += delegate(object _, FileSystemEventArgs e)
				{
					OnFileEvent(watchFiles, e.Name, e.FullPath, "修改");
				};
				fileSystemWatcher.Deleted += delegate(object _, FileSystemEventArgs e)
				{
					OnFileEvent(watchFiles, e.Name, e.FullPath, "删除");
				};
				fileSystemWatcher.Renamed += delegate(object _, RenamedEventArgs e)
				{
					OnFileEvent(watchFiles, e.OldName, e.OldFullPath, "重命名");
				};
				_watchers.Add(fileSystemWatcher);
			}
			_baseline = CaptureTamperSnapshot();
			_tamperTimer = new Timer(TamperTimerTick, null, TimeSpan.FromSeconds(8.0), TimeSpan.FromSeconds(10.0));
			IsMonitoring = _watchers.Count > 0 || _tamperTimer != null;
		}
		return list.Count;
	}

	public void StopMonitoring()
	{
		lock (_lock)
		{
			foreach (FileSystemWatcher watcher in _watchers)
			{
				try
				{
					watcher.EnableRaisingEvents = false;
					watcher.Dispose();
				}
				catch
				{
				}
			}
			_watchers.Clear();
			_tamperTimer?.Dispose();
			_tamperTimer = null;
			_baseline = null;
			IsMonitoring = false;
		}
	}

	private void OnFileEvent(HashSet<string> watchFiles, string? name, string fullPath, string kind)
	{
		if (_suppressRestore || string.IsNullOrEmpty(name) || !watchFiles.Contains(name))
		{
			return;
		}
		lock (_lastRaise)
		{
			if (_lastRaise.TryGetValue(fullPath, out var value) && DateTime.Now - value < TimeSpan.FromSeconds(2.0))
			{
				return;
			}
			_lastRaise[fullPath] = DateTime.Now;
		}
		InvokeEvent(FileModified, fullPath, kind);
	}

	private void TamperTimerTick(object? state)
	{
		List<(string, string)> list;
		lock (_lock)
		{
			if (_tamperTimer == null || _baseline == null)
			{
				return;
			}
			TamperSnapshot tamperSnapshot = CaptureTamperSnapshot();
			list = DiffTamper(_baseline, tamperSnapshot);
			if (list.Count > 0)
			{
				_baseline = tamperSnapshot;
			}
		}
		foreach (var (a, b) in list)
		{
			InvokeEvent(TamperDetected, a, b);
		}
	}

	private static List<(string Title, string Detail)> DiffTamper(TamperSnapshot before, TamperSnapshot now)
	{
		List<(string, string)> list = new List<(string, string)>();
		if (!before.RealtimeDisabled && now.RealtimeDisabled)
		{
			list.Add(("Defender 实时保护被关闭", "挖矿/木马通常先关闭实时保护再运行。请运行威胁清理恢复，并确认电脑是否已被入侵。"));
		}
		if (!before.AntiSpywareDisabled && now.AntiSpywareDisabled)
		{
			list.Add(("Defender 防病毒策略被禁用（DisableAntiSpyware）", "系统级策略被写入，杀毒软件可能完全失效。"));
		}
		foreach (string exclusion in now.Exclusions)
		{
			if (!before.Exclusions.Contains(exclusion))
			{
				list.Add(("Defender 排除项被新增", exclusion + "（挖矿木马常把自身目录/进程加入 Defender 排除，避免被杀）"));
			}
		}
		foreach (KeyValuePair<string, string> ifeoDebugger in now.IfeoDebuggers)
		{
			bool flag = !before.IfeoDebuggers.TryGetValue(ifeoDebugger.Key, out var value) || value != ifeoDebugger.Value;
			bool flag2 = ifeoDebugger.Value.Contains("AppData", StringComparison.OrdinalIgnoreCase) || ifeoDebugger.Value.Contains("Temp", StringComparison.OrdinalIgnoreCase);
			bool flag3 = IfeoTargets().Contains(ifeoDebugger.Key);
			if (flag && (flag2 | flag3))
			{
				list.Add(("进程劫持（IFEO Debugger）", ifeoDebugger.Key + " => " + ifeoDebugger.Value + "（杀软/安装器可能因此静默启动失败，导致「防护软件安装无效」）"));
			}
		}
		foreach (string item in now.DisallowRun)
		{
			if (!before.DisallowRun.Contains(item))
			{
				list.Add(("应用被禁运行（DisallowRun）", item + "（木马借此让防护软件安装/启动失败）"));
			}
		}
		if (before.HostsSignature != now.HostsSignature)
		{
			list.Add(("hosts 文件被修改", "可能被写入规则屏蔽杀软更新/下载站点。"));
		}
		return list;
	}

	private TamperSnapshot CaptureTamperSnapshot()
	{
		TamperSnapshot tamperSnapshot = new TamperSnapshot
		{
			RealtimeDisabled = (GetDword("HKLM\\SOFTWARE\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableRealtimeMonitoring") == 1 || GetDword("HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableRealtimeMonitoring") == 1),
			AntiSpywareDisabled = (GetDword("HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender", "DisableAntiSpyware") == 1)
		};
		string[] exclusionSubKeys = ExclusionSubKeys;
		string[] array = exclusionSubKeys;
		foreach (string text in array)
		{
			foreach (string item in EnumerateValueNames("HKLM\\SOFTWARE\\Microsoft\\Windows Defender\\Exclusions\\" + text))
			{
				tamperSnapshot.Exclusions.Add(text + "\\" + item);
			}
		}
		foreach (KeyValuePair<string, string> item2 in ReadIfeoDebuggers())
		{
			tamperSnapshot.IfeoDebuggers[item2.Key] = item2.Value;
		}
		foreach (string item3 in EnumerateValueNames(DisallowRunPath))
		{
			if (Registry.GetValue(DisallowRunPath, item3, "") is string text2 && !string.IsNullOrWhiteSpace(text2))
			{
				tamperSnapshot.DisallowRun.Add(text2);
			}
		}
		tamperSnapshot.HostsSignature = HostsSignature();
		return tamperSnapshot;
	}

	public async Task RestoreFileAsync(string path)
	{
		_suppressRestore = true;
		try
		{
			(string Path, string Content)? spec = PotFilePaths().FirstOrDefault(((string Path, string Content) x) => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
			if (!spec.HasValue)
			{
				return;
			}
			string dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir))
			{
				await Task.Delay(300);
				if (!Directory.Exists(dir))
				{
					Directory.CreateDirectory(dir);
				}
				File.WriteAllText(path, spec.Value.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				try
				{
					string text = Path.Combine(DataDir, "Backup");
					Directory.CreateDirectory(text);
					File.WriteAllText(Path.Combine(text, Path.GetFileName(path)), spec.Value.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "刷新蜜罐备份失败: " + path);
				}
				await Task.Delay(600);
			}
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			LogHelper.LogError(ex3, "恢复蜜罐文件失败: " + path);
		}
		finally
		{
			_suppressRestore = false;
		}
	}

	public async Task<List<(string Name, bool Enabled)>> GetInstalledAntivirusAsync()
	{
		string text = await RunPsAsync("Get-CimInstance -Namespace root\\SecurityCenter2 -ClassName AntiVirusProduct | ForEach-Object{$n=$_.displayName;if($n -is [byte[]] -and $n.Length -gt 4){$n=[System.Text.Encoding]::Unicode.GetString($n,4,$n.Length-4)};$en=((([int]$_.productState)-band 0x1000)-eq 0x1000);\"$n|$en\"}");
		List<(string, bool)> list = new List<(string, bool)>();
		string[] array = text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string[] array2 = array[i].Trim().Split('|');
			if (array2.Length >= 2 && !string.IsNullOrWhiteSpace(array2[0]))
			{
				list.Add(new ValueTuple<string, bool>(item2: array2[1].Trim().StartsWith("True", StringComparison.OrdinalIgnoreCase), item1: array2[0].Trim()));
			}
		}
		return list.Distinct().ToList();
	}

	public async Task<string> CheckDefenseStatusAsync()
	{
		string text = await RunPsAsync("$mp=Get-MpComputerStatus;'Defender.AntivirusEnabled='+$mp.AntivirusEnabled;'Defender.RealTimeProtection='+$mp.RealTimeProtectionEnabled;Get-NetFirewallProfile|ForEach-Object{'Firewall.'+$_.Name+'='+$_.Enabled};$u=Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System' -Name EnableLUA -ErrorAction SilentlyContinue;'UAC.EnableLUA='+$u.EnableLUA;$a=Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer' -Name NoDriveTypeAutoRun -ErrorAction SilentlyContinue;'AutoRun.NoDriveTypeAutoRun='+$a.NoDriveTypeAutoRun;$dlls=@('ntdll.dll','kernel32.dll','kernelbase.dll','user32.dll','gdi32.dll','ws2_32.dll','wininet.dll','dnsapi.dll','shell32.dll','advapi32.dll','ole32.dll','rpcrt4.dll','version.dll','winhttp.dll','wtsapi32.dll','crypt32.dll','cryptbase.dll','samlib.dll');$bad=@();$sys=Join-Path $env:WINDIR 'System32';foreach($d in $dlls){$f=Join-Path $sys $d;if(Test-Path $f){$s=(Get-AuthenticodeSignature $f -ErrorAction SilentlyContinue).Status;if($s -ne 'Valid'){$bad+=$d}}};'SysDll.BadCount='+$bad.Count;if($bad.Count -gt 0){'SysDll.BadList='+($bad -join ',')}");
		StringBuilder sb = new StringBuilder();
		bool flag = false;
		string[] array = text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string text2 = array[i].Trim();
			if (text2.StartsWith("SysDll.BadCount=", StringComparison.OrdinalIgnoreCase))
			{
				if (text2.Substring(text2.IndexOf('=') + 1).Trim() != "0")
				{
					sb.AppendLine("· 系统关键 DLL 签名：发现异常（可能被木马注入修改！建议先断网，再用 DISM/SFC 修复或用杀软离线急救箱）");
				}
				else
				{
					sb.AppendLine("· 系统关键 DLL 签名：全部正常");
				}
			}
			else if (text2.StartsWith("SysDll.BadList=", StringComparison.OrdinalIgnoreCase))
			{
				sb.AppendLine("   异常 DLL：" + text2.Substring("SysDll.BadList=".Length));
			}
			else if (text2.StartsWith("Defender.AntivirusEnabled=", StringComparison.OrdinalIgnoreCase))
			{
				flag = true;
				sb.AppendLine("· Windows Defender 杀毒：已启用");
			}
			else if (text2.StartsWith("Defender.RealTimeProtection=", StringComparison.OrdinalIgnoreCase))
			{
				sb.AppendLine(text2.EndsWith("True", StringComparison.OrdinalIgnoreCase) ? "· Defender 实时保护：已开启" : "· Defender 实时保护：未开启（建议立即加固）");
			}
			else if (text2.StartsWith("Firewall.", StringComparison.OrdinalIgnoreCase))
			{
				string[] array2 = text2.Split('=');
				string text3 = ((array2.Length >= 1 && array2[0].Length > "Firewall.".Length) ? array2[0].Substring("Firewall.".Length) : "?");
				bool flag2 = array2.Length >= 2 && array2[1].StartsWith("True", StringComparison.OrdinalIgnoreCase);
				sb.AppendLine("· 防火墙（" + text3 + "）：" + (flag2 ? "已开启" : "未开启"));
			}
			else if (text2.StartsWith("UAC.EnableLUA=", StringComparison.OrdinalIgnoreCase))
			{
				sb.AppendLine(text2.EndsWith("1", StringComparison.Ordinal) ? "· UAC 用户账户控制：已开启" : "· UAC 用户账户控制：未开启（建议开启）");
			}
			else if (text2.StartsWith("AutoRun.NoDriveTypeAutoRun=", StringComparison.OrdinalIgnoreCase))
			{
				sb.AppendLine(text2.EndsWith("255", StringComparison.Ordinal) ? "· 自动播放（AutoRun）：已禁用（U盘/移动介质防护良好）" : "· 自动播放（AutoRun）：未禁用（建议一键加固）");
			}
		}
		if (!flag)
		{
			sb.AppendLine("· Windows Defender：未检测到（可能使用第三方杀软，请确认已开启实时防护）");
		}
		try
		{
			List<(string, bool)> list = await GetInstalledAntivirusAsync();
			if (list.Count == 0)
			{
				sb.AppendLine("· 已安装杀软：未检测到（防护空窗！请立即安装火绒/360，或开启 Defender）");
			}
			else
			{
				string text4 = string.Join("、", list.Select<(string, bool), string>(delegate((string Name, bool Enabled) a)
				{
					string result;
					if (a.Enabled)
					{
						(result, _) = a;
					}
					else
					{
						result = a.Name + "（已禁用！）";
					}
					return result;
				}));
				sb.AppendLine("· 已安装杀软：" + text4);
			}
		}
		catch
		{
		}
		sb.AppendLine(IsMonitoring ? "· 布防监控：运行中（蜜罐文件 + 防护篡改监控）" : "· 布防监控：未运行");
		return sb.ToString().TrimEnd();
	}

	public string ApplyHardening()
	{
		return RunAdminCommand("Set-MpPreference -DisableRealtimeMonitoring $false -DisableBehaviorMonitoring $false -DisableScriptScanning $false -DisableIOAVProtection $false;if(-not(Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer')){New-Item 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer' -Force|Out-Null};Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer' -Name NoDriveTypeAutoRun -Value 0xFF -Type DWord -Force", "一键加固");
	}

	public string DisconnectNetwork()
	{
		return RunAdminCommand("Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Disable-NetAdapter -Confirm:$false", "紧急断网");
	}

	public string ReconnectNetwork()
	{
		return RunAdminCommand("Get-NetAdapter | Enable-NetAdapter -Confirm:$false", "恢复网络");
	}

	public async Task<string> GetMiningIsolationStatusAsync()
	{
		string text = await RunPsAsync("$iso=@(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'MhyunBox_断网_*' });$ab=@(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'MhyunBox_阻断_*' });'Mining.IsolationEnabled='+(($iso.Count -gt 0)?1:0);'Mining.IsolationRules='+$iso.Count;'Mining.AutoBlockRules='+$ab.Count;if($ab.Count -gt 0){'Mining.AutoBlockPrograms='+(($ab | ForEach-Object { ($_ | Get-NetFirewallApplicationFilter).Program } | Where-Object {$_} | Sort-Object -Unique) -join ';')}");
		bool flag = false;
		int result = 0;
		int result2 = 0;
		string text2 = "";
		string[] array = text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string text3 = array[i].Trim();
			if (text3.StartsWith("Mining.IsolationEnabled=", StringComparison.OrdinalIgnoreCase) && text3.EndsWith("1", StringComparison.Ordinal))
			{
				flag = true;
			}
			else if (text3.StartsWith("Mining.IsolationRules=", StringComparison.OrdinalIgnoreCase))
			{
				int.TryParse(text3.Substring(text3.IndexOf('=') + 1).Trim(), out result);
			}
			else if (text3.StartsWith("Mining.AutoBlockRules=", StringComparison.OrdinalIgnoreCase))
			{
				int.TryParse(text3.Substring(text3.IndexOf('=') + 1).Trim(), out result2);
			}
			else if (text3.StartsWith("Mining.AutoBlockPrograms=", StringComparison.OrdinalIgnoreCase))
			{
				text2 = text3.Substring(text3.IndexOf('=') + 1).Trim();
			}
		}
		string text4 = (flag ? "挖矿断网防护：已开启（默认阻断全部出站，仅白名单可联网，木马换 IP 也连不上）" : "挖矿断网防护：未开启（可点击「开启挖矿断网防护」阻断木马网络通道）");
		if (flag && result > 0)
		{
			text4 = text4 + "，防火墙规则 " + result + " 条";
		}
		if (result2 > 0)
		{
			text4 = text4 + "；特征库自动阻断 " + result2 + " 条程序" + (string.IsNullOrEmpty(text2) ? "" : ("：" + text2));
		}
		return text4;
	}

	public string RegisterGuardTask()
	{
		string text = Path.Combine(AppContext.BaseDirectory, "KillTools", "布防守护.vbs");
		if (!File.Exists(text))
		{
			return "未找到布防守护启动脚本：" + text;
		}
		string text2 = "wscript.exe \"" + text + "\"";
		return RunSchtasks("/Create /F /TN \"MhyunBoxDefenseGuard\" /TR \"" + text2 + "\" /SC MINUTE /MO 30 /RL HIGHEST", "注册布防守护计划任务");
	}

	public string SetGuardAutoScan(bool autoScan)
	{
		try
		{
			string directoryName = Path.GetDirectoryName(GuardOptionsPath);
			if (!string.IsNullOrWhiteSpace(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.WriteAllText(GuardOptionsPath, "AutoScan=" + (autoScan ? "1" : "0") + Environment.NewLine, Encoding.UTF8);
			return autoScan ? "守护自动联动扫描：已开启（异常时静默扫描，不弹窗）" : "守护自动联动扫描：已关闭（异常仅记录告警，不自动扫描）";
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "写入守护选项失败");
			return "守护选项写入失败：" + ex.Message;
		}
	}

	public string UnregisterGuardTask()
	{
		return RunSchtasks("/Delete /F /TN \"MhyunBoxDefenseGuard\"", "删除布防守护计划任务");
	}

	public (List<string> NewLines, int Total) ReadGuardAlerts(int fromLine)
	{
		List<string> list = new List<string>();
		int item = fromLine;
		if (!File.Exists(GuardAlertLogPath))
		{
			return (NewLines: list, Total: 0);
		}
		try
		{
			string[] array = File.ReadAllLines(GuardAlertLogPath);
			item = array.Length;
			for (int i = fromLine; i < array.Length; i++)
			{
				if (!string.IsNullOrWhiteSpace(array[i]))
				{
					list.Add(array[i].TrimEnd());
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "读取守护告警日志失败");
		}
		return (NewLines: list, Total: item);
	}

	private static string RunSchtasks(string args, string label)
	{
		try
		{
			using Process process = Process.Start(new ProcessStartInfo("schtasks.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
				Arguments = args
			});
			string text = process.StandardOutput.ReadToEnd();
			string text2 = process.StandardError.ReadToEnd();
			process.WaitForExit();
			string text3 = (string.IsNullOrWhiteSpace(text) ? text2 : text);
			return label + ((process.ExitCode == 0) ? "：已生效" : "：失败") + Environment.NewLine + text3.Trim();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, label + "失败");
			return label + "失败：" + ex.Message;
		}
	}

	private static string RunAdminCommand(string command, string label)
	{
		ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = true,
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden,
			Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\""
		};
		try
		{
			Process.Start(startInfo);
			return "已发起" + label + "（请在 UAC 弹窗中确认）...";
		}
		catch (Win32Exception)
		{
			return "已取消：" + label + "需要管理员权限（UAC）。";
		}
		catch (Exception ex2)
		{
			LogHelper.LogError(ex2, label + "失败");
			return label + "失败：" + ex2.Message;
		}
	}

	private static void InvokeEvent(Action<string, string>? handler, string a, string b)
	{
		if (handler == null)
		{
			return;
		}
		try
		{
			handler(a, b);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "布防告警回调失败");
		}
	}

	private static int GetDword(string keyPath, string valueName)
	{
		try
		{
			return Convert.ToInt32(Registry.GetValue(keyPath, valueName, 0));
		}
		catch
		{
			return 0;
		}
	}

	private static IEnumerable<string> EnumerateValueNames(string hivePath)
	{
		int num = hivePath.IndexOf('\\');
		if (num < 0)
		{
			yield break;
		}
		string text = hivePath.Substring(0, num);
		string name = hivePath.Substring(num + 1);
		string text2 = text;
		RegistryKey registryKey = ((!(text2 == "HKLM")) ? ((!(text2 == "HKCU")) ? null : Registry.CurrentUser) : Registry.LocalMachine);
		if (registryKey == null)
		{
			yield break;
		}
		using RegistryKey key = registryKey.OpenSubKey(name);
		if (key != null)
		{
			string[] valueNames = key.GetValueNames();
			for (int i = 0; i < valueNames.Length; i++)
			{
				yield return valueNames[i];
			}
		}
	}

	private static HashSet<string> IfeoTargets()
	{
		if (_ifeoTargets != null)
		{
			return _ifeoTargets;
		}
		HashSet<string> hashSet = new HashSet<string>(IfeoSuspiciousNames, StringComparer.OrdinalIgnoreCase);
		try
		{
			if (File.Exists(SignaturesPath))
			{
				string text = "";
				string[] array = File.ReadAllLines(SignaturesPath);
				for (int i = 0; i < array.Length; i++)
				{
					string text2 = array[i].Trim();
					if (text2.StartsWith("[", StringComparison.Ordinal))
					{
						text = text2;
					}
					else if (text == "[IFEO目标]" && text2.Length > 0 && !text2.StartsWith("#", StringComparison.Ordinal))
					{
						hashSet.Add(text2);
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "读取特征库 IFEO 目标失败");
		}
		_ifeoTargets = hashSet;
		return hashSet;
	}

	private static Dictionary<string, string> ReadIfeoDebuggers()
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(IfeoRoot);
		if (registryKey == null)
		{
			return dictionary;
		}
		string[] subKeyNames = registryKey.GetSubKeyNames();
		string[] array = subKeyNames;
		foreach (string text in array)
		{
			try
			{
				using RegistryKey registryKey2 = registryKey.OpenSubKey(text);
				if (registryKey2?.GetValue("Debugger") is string value && !string.IsNullOrWhiteSpace(value))
				{
					dictionary[text] = value;
				}
			}
			catch
			{
			}
		}
		return dictionary;
	}

	private static long HostsSignature()
	{
		try
		{
			FileInfo fileInfo = new FileInfo(HostsPath);
			return fileInfo.Exists ? ((fileInfo.Length * 397) ^ fileInfo.LastWriteTimeUtc.Ticks) : 0;
		}
		catch
		{
			return 0L;
		}
	}

	private static async Task<string> RunPsAsync(string command)
	{
		ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\""
		};
		using Process proc = Process.Start(startInfo);
		string outp = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
		string err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
		await proc.WaitForExitAsync().ConfigureAwait(continueOnCapturedContext: false);
		return string.IsNullOrWhiteSpace(outp) ? err : outp;
	}

	public void Dispose()
	{
		StopMonitoring();
	}
}
