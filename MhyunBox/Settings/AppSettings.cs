using System;
using System.Collections.Generic;
using System.IO;

namespace MhyunBox.Settings;

public class AppSettings
{
	public string LastSyncedRoleName { get; set; } = "";

	public string ThemeMode { get; set; } = "system";

	public string BackgroundPath { get; set; } = "";

	public double BackgroundOpacity { get; set; } = 0.5;

	public double SwingAmplitude { get; set; } = 50.0;

	public string MusicFolder { get; set; } = "";

	public string MusicPath { get; set; } = "";

	public string PlaybackMode { get; set; } = "random";

	public string VideoFolder { get; set; } = "";

	public string MusicSource { get; set; } = "audio";

	public List<string> MusicOrder { get; set; } = new List<string>();

	public List<string> SkippedMusic { get; set; } = new List<string>();

	public double MusicVolume { get; set; } = 0.5;

	public bool MinimizeToTray { get; set; } = true;

	public bool ShowTrayIcon { get; set; } = true;

	public bool CloseKeepsRunning { get; set; } = true;

	public bool AutoPlayOnStartup { get; set; } = true;

	public bool MusicPaused { get; set; }

	public bool AutoStartWithSystem { get; set; }

	public bool AutoStartHiddenWindow { get; set; }

	public bool AutoStartProxyOnStartup { get; set; }

	public string ClientUuidOverride { get; set; } = "";

	public string ProxyPresetName { get; set; } = "元素之诗";

	public int ProxyListenPort { get; set; } = 25565;

	public string LastProxyPresetName { get; set; } = "";

	public List<SavedProxyBinding> LastProxyBindings { get; set; } = new List<SavedProxyBinding>();

	public List<string> LastAutoStartGroupNames { get; set; } = new List<string>();

	public string LastLaunchSource { get; set; } = "";

	public string LastLaunchSummary { get; set; } = "";

	public List<SavedProxyBinding> LastLaunchBindings { get; set; } = new List<SavedProxyBinding>();

	public Dictionary<string, bool> GroupRoleChecked { get; set; } = new Dictionary<string, bool>();

	public int GroupPortBase { get; set; } = 20000;

	public List<SavedProxyBinding> RemovedAccountPortMemory { get; set; } = new List<SavedProxyBinding>();

	public List<ProxyGroup> ProxyGroups { get; set; } = new List<ProxyGroup>();

	public string ConfigUrl { get; set; } = "";

	public double FontSize { get; set; } = 14.0;

	public bool FontBold { get; set; }

	public string PclPath { get; set; } = "";

	public bool PclAutoLaunchGame { get; set; }

	public int PclJoinPort { get; set; }

	public bool AutoStartPcl { get; set; }

	/// <summary>PCL 启动客户端时是否注入 CPTD 模组（关闭=把客户端 mods 里的 CPTD 改名禁用，不再反复注入）</summary>
	public bool PclInjectCptd { get; set; } = true;

	public string ModSourceDir { get; set; } = "";

	// 元素客户端 DragonCore 目录记忆（增量包合并目标），裁剪/合并流程自动复用
	public string DragonCoreTargetDir { get; set; } = "";

	// 失落世界 PCL 客户端实例目录记忆（更新客户端第二合并目标）
	public string PclClientRootDir { get; set; } = "";

	// 资源包下载目录（空=默认 本机「下载」目录\MhyunBox资源包）
	public string ResPkgDownloadDir { get; set; } = "";

	// 最近一次使用的资源包 URL（网速测试页可直接复用来测到网易 CDN 的真实速度）
	public string LastResPkgUrl { get; set; } = "";

	// —— 更新/合并裁剪后的自动清理 ——
	// 主开关（默认关闭）：开启后，安装成功的资源包、合并成功的增量 zip 会被移入回收站（可还原，不做不可逆物理删除）
	public bool AutoCleanupEnabled { get; set; }

	// DragonCore 冗余备份目录（DragonCore_冗余备份_*）保留天数，超期移入回收站；0 = 永不清理
	public int RedundantBackupKeepDays { get; set; } = 7;

	public bool AllowLanListen { get; set; }

	public string AutoStartFolder { get; set; } = "";

	public List<string> AutoStartNapCatFiles { get; set; } = new List<string>();

	public bool AutoStartFired { get; set; }

	public bool AutoStartHidden { get; set; }

	public bool GuardAutoScan { get; set; } = true;

	/// <summary>
	/// 代理模式：normal=正常代理登录（对齐 v3.8.0 的单目标直连，去掉自带的连接节流/风控限制）；
	/// ddos=防DDOS模式（对齐 v7.8.0 的节点轮换、预设 IP 池、健康探测与地址安全策略）。
	/// 默认 normal：防DDOS 的节点轮换与探测流量容易被网易判为异常登录环境。
	/// </summary>
	public string ProxyMode { get; set; } = "normal";

	public bool DebugMode { get; set; }

	public bool DetectGameProcesses { get; set; } = true;

	public string ProxyTargetOverride { get; set; } = "";

	public string CrcSaltUrl { get; set; } = "";

	public string CrcSalt { get; set; } = "";

	/// <summary>Alane 盐值请求地址（盐值源链首位，默认 http://191.40.41.220/api/crc_salt.txt，不可被其他源覆盖）。</summary>
	public string AlaneSaltUrl { get; set; } = "http://191.40.41.220/api/crc_salt.txt";

	/// <summary>404 授权服务器主地址。</summary>
	public string Auth404BaseUrl { get; set; } = "https://404.xthdx.wiki";

	/// <summary>404 授权服务器备用地址。</summary>
	public string Auth404BackupUrl { get; set; } = "http://39.105.154.212:6669";

	/// <summary>404 登录态 token（auth_session 等效）。</summary>
	public string Auth404Token { get; set; } = "";

	/// <summary>404 账号名（HWID 伪装派生种子）。</summary>
	public string Auth404Username { get; set; } = "";

	/// <summary>404 HWID 模式：real=真实（默认） / spoof=按账号伪装 / random=每次随机 / fixed=固定值。</summary>
	public string Auth404HwidMode { get; set; } = "real";

	/// <summary>fixed 模式下的固定 HWID。</summary>
	public string Auth404FixedHwid { get; set; } = "";

	/// <summary>最早第一次成功连接会话的盐值（首页填入框默认值，写入后不再覆盖）。</summary>
	public string LastGoodCrcSalt { get; set; } = "";

	/// <summary>扫盘记忆的官方启动器路径。</summary>
	public string OfficialLauncherPath { get; set; } = "";

	/// <summary>
	/// 内存扫描的盐值黑名单（32 位十六进制，逗号或换行分隔）。
	/// 扫出来的候选里大多是 uuid / accessToken / md5，需要排除已知的非盐值。
	/// </summary>
	public string SaltBlacklist { get; set; } = "";

	/// <summary>扫盘记忆的发烧游戏平台路径（网易现在要先启动它才能再启动我的世界启动器）。</summary>
	public string FeverPlatformPath { get; set; } = "";

	public string LastGoodLauncherVersion { get; set; } = "";

	// ===== 网易登录环境风控相关（2026-11-07 修复）=====

	/// <summary>
	/// 旧版「网易登录请求跟随系统代理」开关的持久化字段。
	/// 2026-11-07 定版：该开关已从 UI 移除，登录请求固定跟随系统代理（对齐 v3.8.0），
	/// 此字段仅用于兼容旧设置文件，代码不再读取。
	/// </summary>
	public bool NeteaseUseSystemProxy { get; set; } = true;

	/// <summary>旧版账密保活开关的持久化字段；自动登录现已内置账密保活，此字段不再读取。</summary>
	public bool EnableCredentialRefresh { get; set; }

	/// <summary>账密保活周期（分钟）。自动登录开启后每 6 小时用保存的账密重登一次。</summary>
	public int CredentialRefreshMinutes { get; set; } = 360;

	/// <summary>旧版 Cookie 级保活间隔（分钟）；保活机制已改为账密重登，此字段不再读取。</summary>
	public int CookieRefreshMinutes { get; set; } = 60;

	public long AutoStartBootTick64 { get; set; }

	public bool GameDetectedThisBoot { get; set; }

	/// <summary>
	/// 最近一次弹出「DLL 构建时间不一致 / 版本落后，提示加群」的日期（yyyy-MM-dd）。
	/// 同一天只提醒一次，避免每次启动都弹。
	/// </summary>
	public string BuildCheckPromptDate { get; set; } = "";
}
