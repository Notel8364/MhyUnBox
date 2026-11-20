using System.Collections;
using System.Reflection;

// 布吉岛专用代理的门禁回归校验（反射读已编译的 MhyunBox.dll，不启动 GUI）。
//
// 目的：确保下面两组事实在每次改动后仍然成立，防止「仅布吉岛生效」被悄悄破坏：
//   1. ServerPresets.All 里内置了布吉岛（GameId 4661334467366178884，host pc.bjdmc.net:25565），
//      且它被认定为内置预设（不可删除、不写回 discovered_servers.json）；
//   2. BjdGate 的判定：只对 4661334467366178884 / pc.bjdmc.net 为真，
//      对其它 GameId、其它 host、null 一律为假。
//
// 用法：
//   dotnet run --project tools\bjdgate_test -c Release
//   dotnet run --project tools\bjdgate_test -c Release -- <MhyunBox.dll 完整路径>
//   也可用环境变量 MHYUNBOX_DLL 指定。
//
// 退出码：0 = 全部通过；1 = 有断言失败；2 = 找不到 MhyunBox.dll。

const long BjdGameId = 4661334467366178884L;
const long YszsGameId = 4658261307332877912L;
const string BjdPresetName = "布吉岛·新玩法上线";
const string BjdHost = "pc.bjdmc.net";
const string OtherHost = "netease1.yszscraft.cn";

string? dll = ResolveAssemblyPath(args);
if (dll is null)
{
    Console.WriteLine("找不到 MhyunBox.dll。请先 dotnet build MhyunBox.csproj -c Release，或显式传路径。");
    return 2;
}
Console.WriteLine("目标程序集: " + dll);

string dir = Path.GetDirectoryName(Path.GetFullPath(dll))!;
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    string simple = (new AssemblyName(e.Name).Name ?? "unknown") + ".dll";
    string candidate = Path.Combine(dir, simple);
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

Assembly asm = Assembly.LoadFrom(dll);

// 可选：--export 时顺带做一次「一键导出问题日志」的端到端冒烟测试（会真的在桌面生成 zip）
if (args.Contains("--export"))
{
    Console.WriteLine();
    Console.WriteLine("=== 一键导出问题日志（冒烟测试） ===");
    try
    {
        Type exporter = asm.GetType("MhyunBox.Services.LogExporter", throwOnError: true)!;
        MethodInfo export = exporter.GetMethod("ExportToDesktop", BindingFlags.Public | BindingFlags.Static)!;
        string zipPath = (string)export.Invoke(null, null)!;
        Console.WriteLine("  生成: " + zipPath);
        Console.WriteLine("  大小: " + new FileInfo(zipPath).Length + " 字节");
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            Console.WriteLine("    - " + entry.FullName);
        }
        Console.WriteLine("  [PASS] 导出成功（应包含 环境信息.txt 与 布吉岛代理payload日志/）");
    }
    catch (Exception ex)
    {
        Console.WriteLine("  [FAIL] 导出失败: " + ex.GetType().Name + ": " + ex.Message);
    }
}
Type presetsType = asm.GetType("MhyunBox.Services.ServerPresets", throwOnError: true)!;
Type presetType = asm.GetType("MhyunBox.Services.ServerPreset", throwOnError: true)!;
Type gateType = asm.GetType("MhyunBox.Services.BjdProxy.BjdGate", throwOnError: true)!;

var presets = new List<object>();
foreach (object p in (IEnumerable)presetsType.GetField("All", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!)
{
    presets.Add(p);
}

object Prop(object obj, string name) => obj.GetType().GetProperty(name)!.GetValue(obj)!;
long GameIdOf(object p) => Convert.ToInt64(Prop(p, "GameId"));

var failures = new List<string>();
void Check(string label, bool ok)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
    if (!ok) failures.Add(label);
}

Console.WriteLine();
Console.WriteLine("=== ServerPresets.All ===");
foreach (object p in presets)
{
    Console.WriteLine($"  {Prop(p, "Name")} | {Prop(p, "Host")}:{Prop(p, "Port")} | GameId={Prop(p, "GameId")} | IsPending={Prop(p, "IsPending")}");
}

Console.WriteLine();
Console.WriteLine("=== 断言 1：布吉岛已内置 ===");
object? bjd = presets.FirstOrDefault(p => GameIdOf(p) == BjdGameId);
Check("存在 GameId=4661334467366178884 的预设", bjd is not null);
if (bjd is not null)
{
    Check($"预设名 = {BjdPresetName}", (string)Prop(bjd, "Name") == BjdPresetName);
    Check($"Host = {BjdHost}", (string)Prop(bjd, "Host") == BjdHost);
    Check("Port = 25565", Convert.ToInt32(Prop(bjd, "Port")) == 25565);
    Check("IsPending = false（已解析，不阻塞代理启动）", (bool)Prop(bjd, "IsPending") == false);

    MethodInfo isBuiltin = presetsType.GetMethod("IsBuiltin", BindingFlags.Public | BindingFlags.Static)!;
    Check("IsBuiltin = true（不可删除、不写回 discovered_servers.json）", (bool)isBuiltin.Invoke(null, new[] { bjd })!);
}

Console.WriteLine();
Console.WriteLine("=== 断言 2：BjdGate 只对布吉岛为真 ===");
Check("GameId 常量 = 4661334467366178884", (long)gateType.GetField("GameId", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)! == BjdGameId);
Check($"PresetName 常量 = {BjdPresetName}", (string)gateType.GetField("PresetName", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)! == BjdPresetName);

MethodInfo isBjdLong = gateType.GetMethod("IsBjd", new[] { typeof(long) })!;
MethodInfo isBjdPreset = gateType.GetMethod("IsBjd", new[] { presetType })!;
MethodInfo isTarget = gateType.GetMethod("IsBjdTarget", new[] { typeof(string) })!;

Check("IsBjd(布吉岛 GameId) = true", (bool)isBjdLong.Invoke(null, new object[] { BjdGameId })!);
Check("IsBjd(元素之诗 GameId) = false", !(bool)isBjdLong.Invoke(null, new object[] { YszsGameId })!);
Check("IsBjd(0) = false", !(bool)isBjdLong.Invoke(null, new object[] { 0L })!);
Check($"IsBjdTarget({BjdHost}) = true", (bool)isTarget.Invoke(null, new object[] { BjdHost })!);
Check($"IsBjdTarget({OtherHost}) = false", !(bool)isTarget.Invoke(null, new object[] { OtherHost })!);
Check("IsBjdTarget(null) = false", !(bool)isTarget.Invoke(null, new object[] { null! })!);
Check("IsBjdTarget(\"\") = false", !(bool)isTarget.Invoke(null, new object[] { "" })!);

foreach (object p in presets)
{
    bool expected = GameIdOf(p) == BjdGameId;
    bool actual = (bool)isBjdPreset.Invoke(null, new[] { p })!;
    Check($"IsBjd(preset:{Prop(p, "Name")}) = {expected}", actual == expected);
}

// ---------------------------------------------------------------------------
// 断言 3：BjdConnectionProbe 能按 PID 从内核 TCP 表读到 ESTABLISHED 连接
// （注入前的「A 是否已入服」硬拦靠它；端口字节序错就会永远判成"没入服"）
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 断言 3：TCP 连接表探测（自连自测） ===");
try
{
    Type probeType = asm.GetType("MhyunBox.Services.BjdProxy.BjdConnectionProbe", throwOnError: true)!;
    MethodInfo established = probeType.GetMethod("Established", BindingFlags.Public | BindingFlags.Static)!;
    MethodInfo describe = probeType.GetMethod("Describe", BindingFlags.Public | BindingFlags.Static)!;

    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    int listenPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    using var client = new System.Net.Sockets.TcpClient();
    client.Connect(System.Net.IPAddress.Loopback, listenPort);
    using var accepted = listener.AcceptTcpClient();

    // 让连接进入 ESTABLISHED 稳定态
    Thread.Sleep(120);

    object listObj = established.Invoke(null, new object[] { Environment.ProcessId })!;
    var remotePorts = new List<int>();
    foreach (object? link in (IEnumerable)listObj)
    {
        if (link is null) continue;
        remotePorts.Add(Convert.ToInt32(link.GetType().GetProperty("RemotePort")!.GetValue(link)));
    }

    Console.WriteLine("  本进程 ESTABLISHED 远端端口: " + string.Join(",", remotePorts));
    Console.WriteLine("  Describe(自身 pid): " + describe.Invoke(null, new object[] { Environment.ProcessId }));
    Check($"探测到自连端口 {listenPort}（说明 TCP 表解析与端口字节序正确）", remotePorts.Contains(listenPort));

    object empty = established.Invoke(null, new object[] { 0 })!;
    Check("pid<=0 时返回空列表", !((IEnumerable)empty).Cast<object?>().Any());

    client.Close();
    accepted.Close();
    listener.Stop();
}
catch (Exception ex)
{
    Check("TCP 连接表探测未抛异常（" + ex.GetType().Name + ": " + ex.Message + "）", false);
}

// ---------------------------------------------------------------------------
// 断言 4：BjdPayloadLog 能读到 payload 自己的日志
// （调试模式接入 / 一键导出都依赖它；本机若已注入过，日志就存在）
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 断言 4：payload 日志接入 ===");
try
{
    Type logType = asm.GetType("MhyunBox.Services.BjdProxy.BjdPayloadLog", throwOnError: true)!;
    string logDir = (string)logType.GetProperty("LogDir", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    string? current = (string?)logType.GetProperty("CurrentPath", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
    Console.WriteLine("  日志目录: " + logDir);
    Console.WriteLine("  当前文件: " + (current ?? "（无）"));
    Check("LogDir 非空", !string.IsNullOrWhiteSpace(logDir));

    MethodInfo waitForMarker = logType.GetMethod("WaitForMarker")!;
    MethodInfo tryReadTail = logType.GetMethod("TryReadTail")!;

    if (current is null)
    {
        Console.WriteLine("  （本机还没有 payload 日志，跳过内容断言；注入一次后再跑即可覆盖）");
    }
    else
    {
        // payload 会逐包刷 [C2S PASS]，尾部 20 行可能全是刷屏行；读更大窗口再找关键事件
        object?[] tailArgs = { 50000, null };
        bool tailOk = (bool)tryReadTail.Invoke(null, tailArgs)!;
        string tail = (string?)tailArgs[1] ?? "";
        Check("TryReadTail 能读到内容", tailOk && tail.Length > 0);
        bool hasPayloadKeyEvent = false;
        try
        {
            string fullLog;
            using (FileStream stream = new FileStream(
                current,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream))
            {
                fullLog = reader.ReadToEnd();
            }
            hasPayloadKeyEvent =
                fullLog.Contains("PROCESS_ATTACH", StringComparison.Ordinal)
                || fullLog.Contains("MAIN-GATE", StringComparison.Ordinal)
                || fullLog.Contains("mid-session", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  读取完整 payload 日志失败: " + ex.Message);
        }
        Check("日志里含 payload 关键事件行（PROCESS_ATTACH / MAIN-GATE / mid-session）",
            hasPayloadKeyEvent);

        object?[] markerArgs = { "DllMain PROCESS_ATTACH", TimeSpan.FromSeconds(2), null };
        bool found = (bool)waitForMarker.Invoke(null, markerArgs)!;
        Check("WaitForMarker 命中历史标记", found && !string.IsNullOrEmpty((string?)markerArgs[2]));

        // 逐包刷屏过滤：只吃 [C2S ]/[S2C ]/[VIEW] S2C 行，绝不吞关键事件
        MethodInfo? isNoise = logType.GetMethod("IsPacketNoise", BindingFlags.NonPublic | BindingFlags.Static);
        if (isNoise is null)
        {
            Check("找到 IsPacketNoise 过滤器", false);
        }
        else
        {
            bool Noise(string s) => (bool)isNoise.Invoke(null, new object[] { s })!;
            Check("过滤 [C2S PASS] 逐包行", Noise("[C2S PASS] net.minecraft.network.protocol.game.ServerboundCustomPayloadPacket"));
            Check("过滤 [C2S DROP] 逐包行", Noise("[C2S DROP] net.minecraft.network.protocol.game.ServerboundMovePlayerPacket$Pos"));
            Check("过滤 [S2C PASS] 逐包行", Noise("[S2C PASS] net.minecraft.network.protocol.game.ClientboundLevelChunkWithLightPacket"));
            Check("保留 [MAIN-GATE] 关键行", !Noise("[MAIN-GATE] blocking A Render thread until B reaches PLAY"));
            Check("保留 mid-session 关键行", !Noise("mid-session: captured A's live connection + attached relay (A already in-game)"));
            Check("保留 InstallBServer 关键行", !Noise("ProxyInitWorker: InstallBServer OK — listening on 127.0.0.1:13337"));
        }
    }
}
catch (Exception ex)
{
    Check("payload 日志接入未抛异常（" + ex.GetType().Name + ": " + ex.Message + "）", false);
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("全部断言通过。");
    return 0;
}
Console.WriteLine($"失败 {failures.Count} 条：");
foreach (string f in failures)
{
    Console.WriteLine("  - " + f);
}
return 1;

static string? ResolveAssemblyPath(string[] argv)
{
    if (argv.Length > 0 && !string.IsNullOrWhiteSpace(argv[0]) && File.Exists(argv[0]))
    {
        return argv[0];
    }
    string? env = Environment.GetEnvironmentVariable("MHYUNBOX_DLL");
    if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
    {
        return env;
    }
    // 从当前目录 / 程序目录向上找 bin\{Release,x64\Release}\net8.0-windows10.0.19041.0\MhyunBox.dll
    foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        DirectoryInfo? dir = new DirectoryInfo(start);
        for (int depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
        {
            foreach (string flavour in new[] { "Release", Path.Combine("x64", "Release") })
            {
                string candidate = Path.Combine(dir.FullName, "bin", flavour, "net8.0-windows10.0.19041.0", "MhyunBox.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
    }
    return null;
}
