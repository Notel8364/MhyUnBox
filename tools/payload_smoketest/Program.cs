using System.Diagnostics;
using System.Reflection;

// 端到端冒烟测试：用 MhyunBox 自带的托管注入栈，把一个 payload DLL 反射注入到一个
// **没有 JVM 的 dummy 进程** 里，然后看 payload 自己有没有写出 "DllMain entry" 日志。
//
// 为什么要做这个：我们改用了 llvm-mingw 重新编译 payload，产物带 .tls 段；
// 而 ReflectiveLoader.c 只处理 export/import/basereloc，不处理 TLS 目录。
// 如果静态运行时的 TLS 没被初始化，反射加载后会立刻崩 —— 这个测试能直接看出来。
//
// 用法：
//   dotnet run --project tools\payload_smoketest -c Release
//   dotnet run --project tools\payload_smoketest -c Release -- <MhyunBox.dll> <payload.dll>
//
// 退出码：0 = 注入成功且 payload 写出了日志；1 = 失败；2 = 参数/环境问题。

string mhyunBoxDll = args.Length > 0
    ? args[0]
    : FindUpward(Path.Combine("bin", "Release", "net8.0-windows10.0.19041.0", "MhyunBox.dll"))
      ?? FindUpward(Path.Combine("bin", "x64", "Release", "net8.0-windows10.0.19041.0", "MhyunBox.dll"))
      ?? "";
string payload = args.Length > 1
    ? args[1]
    : FindUpward(Path.Combine("payloads", "WtfNel.Payload.Proxy.dll")) ?? "";

if (!File.Exists(mhyunBoxDll))
{
    Console.WriteLine("找不到 MhyunBox.dll，请先 dotnet build MhyunBox.csproj -c Release，或显式传路径。");
    return 2;
}
if (!File.Exists(payload))
{
    Console.WriteLine("找不到 payload：" + payload);
    return 2;
}

Console.WriteLine("MhyunBox.dll: " + mhyunBoxDll);
Console.WriteLine("payload     : " + payload);

string baseDir = Path.GetDirectoryName(Path.GetFullPath(mhyunBoxDll))!;
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    string simple = (new AssemblyName(e.Name).Name ?? "unknown") + ".dll";
    string candidate = Path.Combine(baseDir, simple);
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};
Assembly asm = Assembly.LoadFrom(mhyunBoxDll);

// payload 日志目录（与 BjdPayloadLog 相同的解析规则）
string logDir = Environment.GetEnvironmentVariable("WTFNEL_LOG_DIR");
if (string.IsNullOrWhiteSpace(logDir))
{
    logDir = Path.Combine(Path.GetTempPath(), "WtfNel", "logs");
}
Directory.CreateDirectory(logDir);
int logBefore = CountMarker(logDir, "DllMain entry");

// 起一个 x64 dummy 进程（无 JVM），它会被注入
Process target = Process.Start(new ProcessStartInfo
{
    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
    Arguments = "/c ping -n 40 127.0.0.1 >nul",
    UseShellExecute = false,
    CreateNoWindow = true,
})!;
try
{
    Thread.Sleep(500);
    Console.WriteLine($"目标进程: pid {target.Id} ({Path.GetFileName(target.MainModule?.FileName ?? "?")})");

    // 反射调用 MhyunBox 的 BjdInjector.Inject(int, string, InjectionOptions?)
    Type injectorType = asm.GetType("MhyunBox.Services.BjdProxy.BjdInjector", throwOnError: true)!;
    Type optionsType = asm.GetType("MhyunBox.Services.BjdProxy.Native.InjectionOptions", throwOnError: true)!;
    Type strategyType = asm.GetType("MhyunBox.Services.BjdProxy.Native.InjectionStrategy", throwOnError: true)!;

    object options = Activator.CreateInstance(optionsType)!;
    // Strategy = Auto（默认），FallbackToLoadLibrary = true（默认），WaitMs = 20000
    optionsType.GetProperty("WaitMs")!.SetValue(options, 20000);
    optionsType.GetProperty("Log")!.SetValue(options, new Action<string>(m => Console.WriteLine("  [inject] " + m)));

    // 可选：--manualmap / --loadlibrary 强制指定注入策略（用于对比反射加载失败的情况）
    string mode = args.Length > 2 ? args[2] : "";
    if (mode == "--manualmap" || mode == "--loadlibrary")
    {
        object strategy = Enum.Parse(strategyType, mode == "--manualmap" ? "ManualMap" : "LoadLibrary");
        optionsType.GetProperty("Strategy")!.SetValue(options, strategy);
        Console.WriteLine("策略被强制为: " + strategy);
    }

    MethodInfo inject = injectorType.GetMethod("Inject", BindingFlags.NonPublic | BindingFlags.Static)!;
    object result = inject.Invoke(null, new object[] { target.Id, payload, options })!;
    bool ok = (bool)result.GetType().GetProperty("Success")!.GetValue(result)!;
    string detail = (string?)result.GetType().GetProperty("Detail")!.GetValue(result) ?? "";
    Console.WriteLine($"注入结果: Success={ok}  {detail}");

    // 等 payload 的 DllMain 把日志写出来。用「计数增量」判定，避免被同一日志文件里
    // 其它进程（比如正在跑的游戏 A）的历史行干扰。
    Thread.Sleep(3000);
    int afterCount = CountMarker(logDir, "DllMain entry");
    bool logged = afterCount > logBefore;
    Console.WriteLine($"payload 日志目录: {logDir}");
    Console.WriteLine($"新增 DllMain entry: {logBefore} -> {afterCount}（{logged}）");

    if (!ok)
    {
        Console.WriteLine("FAIL: 注入本身失败");
        return 1;
    }
    if (!logged)
    {
        Console.WriteLine("FAIL: 注入返回成功，但 payload 没有写出启动日志（很可能是 TLS 未初始化导致 DllMain 崩掉）");
        Console.WriteLine("---- 日志尾部 ----");
        Console.WriteLine(Tail(SnapshotLog(logDir), 40));
        return 1;
    }
    Console.WriteLine("PASS: 反射注入成功，payload 已执行到自己的启动代码");
    Console.WriteLine("---- 日志尾部 ----");
    Console.WriteLine(Tail(SnapshotLog(logDir), 25));
    return 0;
}
finally
{
    try { if (!target.HasExited) target.Kill(true); } catch { }
}

static int CountMarker(string dir, string marker)
{
    string text = SnapshotLog(dir);
    int count = 0, idx = 0;
    while (true)
    {
        idx = text.IndexOf(marker, idx, StringComparison.Ordinal);
        if (idx < 0) break;
        count++;
        idx += marker.Length;
    }
    return count;
}

static string SnapshotLog(string dir)
{
    try
    {
        string? newest = Directory.EnumerateFiles(dir, "proxy-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (newest is null) return "";
        using var fs = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
    catch
    {
        return "";
    }
}

static string Tail(string text, int lines)
{
    string[] all = text.Replace("\r\n", "\n").Split('\n');
    int take = Math.Min(lines, all.Length);
    return string.Join(Environment.NewLine, all, all.Length - take, take);
}

static string? FindUpward(string relative)
{
    foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        DirectoryInfo? dir = new DirectoryInfo(start);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
    }
    return null;
}
