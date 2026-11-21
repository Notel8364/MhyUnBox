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

	/// <summary>服务器代理（快速启动 / 代理启动页）自启动开关。与机器人代理自启完全独立。</summary>
	public bool AutoStartProxyOnStartup { get; set; }

	/// <summary>机器人代理（会话多开页）自启动总开关：开启后按每张卡片的「自启动」勾选恢复机器人监听。</summary>
	public bool AutoStartBotOnStartup { get; set; }

	/// <summary>首次启动是否已把内置 MythosNexus 模组复制到桌面。</summary>
	public bool ModCopiedToDesktop { get; set; }

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

	/// <summary>
	/// 实例多开的本地端口分配：每个 Nexus 账号（角色）一个 127.0.0.1 监听端口，
	/// 游戏连本地端口 → 启动器中继到对应服务器。键为 user@host:port。
	/// </summary>
	public List<NexusEndpointSetting> NexusEndpoints { get; set; } = new List<NexusEndpointSetting>();

	/// <summary>
	/// 「行情角色」：元素之诗交易行行情模组只对这一个账号生效（单实例里由真人登录，
	/// 程序不再为它创建机器人会话，并把行情重连地址写成它自己的本地端口）。
	/// 值为角色键 user@host:port，空 = 未指定。
	/// </summary>
	public string MarketAccountKey { get; set; } = "";

	/// <summary>
	/// 「行情角色」按快速启动组分别记忆：键 = 组名（不含「全部组」），值 = 角色键。
	/// 切换显示组时自动恢复该组上次指定的行情角色；没有记录则自动用组内第一个角色。
	/// 这样行情角色不会再永远钉在第一个组上。
	/// </summary>
	public Dictionary<string, string> MarketAccountKeyByGroup { get; set; } = new Dictionary<string, string>();

	/// <summary>
	/// 「画面显示角色」：程序把该账号写进对应客户端 mythosnexus.json 的 viewAccount，
	/// 游戏启动后 MythosNexus 会把该机器人的视角直接挂到游戏主画面。
	/// 值为角色键 user@host:port，空 = 不指定。
	/// </summary>
	public string ViewAccountKey { get; set; } = "";

	/// <summary>
	/// 实例多开的风险确认：勾选后表示用户已知晓并同意「后果与责任自负」，才允许创建机器人号代理。
	/// </summary>
	public bool NexusRiskAccepted { get; set; }

	/// <summary>
	/// 实例多开页与管理界面共同显示的「快速启动组」名称；空 = 显示全部组。
	/// </summary>
	public string DisplayGroupName { get; set; } = "";

	/// <summary>
	/// 实例多开管理窗口是否已经自动弹出过一次：只自动弹一次，之后由用户从页面按钮自行打开。
	/// </summary>
	public bool InstancesManagerOpenedOnce { get; set; }

	/// <summary>
	/// 实例多开主页里点「删除卡片」隐藏掉的角色键（user@host:port）。
	/// 只隐藏显示、不删除 Nexus 账号；重新在管理页启用该角色会把它从列表里移除。
	/// </summary>
	public List<string> NexusHiddenCards { get; set; } = new List<string>();

	/// <summary>
	/// 用户明确删除过的机器人卡片（卡片键 = 账号@服务器:端口[#角色名]）与账号键。
	///
	/// 这是「墓碑」：删除后任何自动回写都要跳过它，否则游戏一启动
	/// （GameSessionWatcher → RecreateBotCardsForClient / 启动补齐 / 端口自愈）
	/// 就会把刚删掉的机器人账号重新写回 mythosnexus.json，
	/// 表现就是「删掉了的卡片和角色会话反复出现」。
	/// 只有用户在这张卡上再点一次「启动」才会清掉对应墓碑。
	/// </summary>
	public List<string> NexusBotTombstones { get; set; } = new List<string>();

	/// <summary>已提示过「单客户端 10 会话上限」的客户端目录 → 已提示次数（最多 3 次）。</summary>
	public Dictionary<string, int> SessionLimitWarnCount { get; set; } = new Dictionary<string, int>();

	public string ConfigUrl { get; set; } = "";

	public double FontSize { get; set; } = 14.0;

	public bool FontBold { get; set; }

	/// <summary>按服务器预设分别保存的客户端目录（key=预设名）。</summary>
	public Dictionary<string, string> OfficialClientRootByPreset { get; set; } = new Dictionary<string, string>();

	/// <summary>官方客户端 .minecraft 根目录（脱盒启动用；留空自动查找 C:\MCLDownload\Game\.minecraft）。</summary>
	public string OfficialClientRoot { get; set; } = "";

	/// <summary>启动时是否自动用脱盒方式拉起官方客户端并连接已启动的代理。</summary>
	public bool AutoStartOfficialClient { get; set; }

	/// <summary>简易模式：隐藏左侧导航的 首页/服务器列表/日志/会话多开/网速测试/商城/更新客户端，同时关闭调试模式。</summary>
	public bool SimpleMode { get; set; }

	/// <summary>简易模式下勾选要隐藏的导航页（key=页面 Tag）。默认隐藏 7 个进阶页；
	/// "SettingsPage" 为特殊项：勾选后简易模式会临时隐藏设置入口，5 秒后自动复原（防止把自己锁在外面）。</summary>
	public List<string> SimpleHiddenPages { get; set; } = new List<string>
	{
		"HomePage", "ServerListPage", "SessionLogPage", "InstancesPage", "ClientLaunchPage", "SpeedTestPage", "ShopPage", "ClientUpdatePage"
	};

	/// <summary>脱盒启动使用的服务器预设名（空=自动按布吉岛/首个代理匹配）。</summary>
	public string OfficialClientPreset { get; set; } = "";

	/// <summary>
	/// 兼容网易发烧启动器（2026-11-20 新增，仅在账号绑定页显示）。
	/// 开 = MhyunBox 主动让出会话优先级：切断所有会自动建立新会话的自动链路
	/// （账密定时保活 / 自动兜底重登 / code=22 自动恢复 / 自动角色刷新 / 认证启动重建会话），
	/// 只复用已保存的会话（uni_sauth 校验通过即用），避免顶掉官方启动器的同账号会话；
	/// 手动登录入口不受影响。关（默认）= 脱盒自主模式：自动链路全开，
	/// 会话被顶（code=22）时立即自动重登抢回，优先级高于网易我的世界启动器。
	/// </summary>
	public bool CompatNeteaseLauncher { get; set; }

	/// <summary>脱盒启动使用的 JRE 目录（空=自动查找）。</summary>
	public string OfficialClientJavaHome { get; set; } = "";

	/// <summary>客户端配置与启动下拉选中的 JRE 目录（2026-11-20 新增；空=自动。优先级：启动卡 JavaHome > 自选路径 OfficialClientJavaHome > 此下拉 > 自动）。</summary>
	public string OfficialClientJreSelected { get; set; } = "";

	/// <summary>PCL 客户端内存分配（MB；0 = 自动默认 2000M）。2026-11-20 新增「内存优化」选项。</summary>
	public int OfficialClientMemoryMb { get; set; }

	/// <summary>
	/// 启动客户端内存模式：global=全局配置（统一使用 GlobalMemoryMb）、
	/// auto=自动配置（按本机可用内存自动计算）、custom=自定义（使用 OfficialClientMemoryMb）。
	/// </summary>
	public string OfficialClientMemoryMode { get; set; } = "global";

	/// <summary>全局配置模式下的统一内存大小（MB）。</summary>
	public int OfficialClientGlobalMemoryMb { get; set; } = 4096;

	/// <summary>启动客户端附加 Java 参数（空格分隔，追加在 -Xmx 之后、主类之前）。</summary>
	public string OfficialClientJvmArgs { get; set; } = "";

	/// <summary>快速启动页「角色组 / 客户端卡」栏目布局（2026-11-20 新增）：Groups=只显示角色组，Cards=只显示客户端卡，Both=并排显示。</summary>
	public string QuickCardsLayout { get; set; } = "Both";

	/// <summary>隐藏「客户端配置与启动」卡的页面键（HomePage / GroupsQuickPage）；空 = 全部显示。</summary>
	public List<string> ClientLauncherHiddenPages { get; set; } = new List<string>();

	/// <summary>快速启动页右侧「代理设置」卡是否显示（标题行可切换，带过渡动画）。</summary>
	public bool ShowProxySettingsCard { get; set; } = true;

	/// <summary>直连房间地址（host:port）；留空则使用代理端口自动进服。</summary>
	public string OfficialClientRoomOverride { get; set; } = "";

	/// <summary>按服务器预设保存的客户端直连地址（host:port）；留空表示不自动进服，界面也不显示代理信息。</summary>
	public Dictionary<string, string> OfficialClientDirectByPreset { get; set; } = new Dictionary<string, string>();

	/// <summary>按服务器预设保存的客户端版本目录名（versions\&lt;name&gt;）；空 = 自动选择。</summary>
	public Dictionary<string, string> OfficialClientVersionByPreset { get; set; } = new Dictionary<string, string>();

	/// <summary>勾选了「自启动客户端」的内置服务器预设名；启动卡自己的开关存在 ClientLaunchCard.AutoStart。</summary>
	public List<string> AutoStartClientPresets { get; set; } = new List<string>();

	/// <summary>额外启动卡（每张卡 = 一份独立的客户端启动配置，允许同时多开）。</summary>
	public List<ClientLaunchCard> ClientLaunchCards { get; set; } = new List<ClientLaunchCard>();

	/// <summary>官方客户端脱盒启动时优先使用的角色名（留空用账号昵称）。</summary>
	public string OfficialClientRole { get; set; } = "";	public string PclPath { get; set; } = "";

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
	// 2026-11-20：改为常开（设置页不再提供开关；Load 迁移会把旧值 false 修正为 true）
	// 开启后，安装成功的资源包、合并成功的增量 zip 会被移入回收站（可还原，不做不可逆物理删除）
	public bool AutoCleanupEnabled { get; set; } = true;

	// DragonCore 冗余备份目录（DragonCore_冗余备份_*）保留天数，超期移入回收站；0 = 永不清理
	// 2026-11-20：默认值 7 → 3（Load 迁移会把旧默认 7 修正为 3）
	public int RedundantBackupKeepDays { get; set; } = 3;

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

	// ===== 旧版兼容字段（风控相关开关已移除，字段仅用于读取旧设置文件）=====

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

	/// <summary>DLL 版本不一致时是否弹窗提醒更新（首页可勾选；关闭后只写日志）。</summary>
	public bool DllUpdatePromptEnabled { get; set; } = true;

	/// <summary>
	/// 元素之诗「第 6 个及以后机器人代理」提醒已弹次数。最多弹 3 次，之后静默
	/// （2026-11-17：原先直接在第 5 个之后静默跳过，用户要求改成提醒但不拦）。
	/// </summary>
	public int ElementPoetryWarnCount { get; set; }

	/// <summary>
	/// 会话多开页是否显示「高级信息」（监听数、上下行包、各服务器进服数）。
	/// 默认关闭：主界面只留「已进服数/总数」。
	/// </summary>
	public bool ShowAdvancedSessionInfo { get; set; }

	/// <summary>
	/// 启动时把「首次打开说明」和「元素之诗超限提醒」各弹一遍（验看文案用）。
	/// 弹完立即置回 false，正常使用时不会打扰。
	/// </summary>
	public bool ShowNexusNoticesOnStartup { get; set; }
}
