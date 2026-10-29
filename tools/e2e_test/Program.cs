// 端到端测试：网易侧请求最新资源包 → 下载 → 裁剪进 PCL 客户端 DragonCore 目录
// 复用主程序真实服务（AccountStore / NeteaseLogin / NeteaseItemService / ClientUpdater），
// 不走 UI。运行前提：先构建主程序（bin/Release/net8.0-windows10.0.19041.0/MhyunBox.dll）。
using MhyunBox.Services;

const long GameId = 4658261307332877912; // 元素之诗

string InstallRoot()
{
    if (Directory.Exists(@"E:\MCLDownload")) return @"E:\MCLDownload";
    foreach (string drive in new[] { @"E:\", @"D:\", @"C:\" })
    {
        string p = Path.Combine(drive, "MCLDownload");
        if (Directory.Exists(p)) return p;
    }
    return @"E:\MCLDownload";
}

void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

int exitCode = 1;
try
{
    Log("== E2E 测试开始 ==");
    string installRoot = InstallRoot();
    Log($"installRoot: {installRoot}");

    // —— 1. 取绑定账号 ——
    List<AccountStore.LocalAccount> accounts = AccountStore.All
        .Where(a => !string.IsNullOrWhiteSpace(a.CookieJson)).Take(3).ToList();
    Log($"绑定账号数(带cookie): {accounts.Count}" + string.Join(", ", accounts.Select(a => a.Nickname + "/" + a.Account)));
    if (accounts.Count == 0)
    {
        Log("FAIL: 无可用账号，请先在账号绑定页绑定网易账号");
        return 1;
    }

    // —— 2. 刷新网易会话 ——
    string? url = null;
    foreach (AccountStore.LocalAccount acc in accounts)
    {
        Log($"[会话] 使用 {acc.Nickname}/{acc.Account} (channel={acc.Channel}) 刷新...");
        NeteaseLoginResult session;
        using (NeteaseLogin login = new NeteaseLogin())
        {
            session = await login.LoginWithCookieAsync(acc.CookieJson, acc.Channel);
        }
        Log($"[会话] Success={session.Success} EntityId={(string.IsNullOrWhiteSpace(session.EntityId) ? "<空>" : session.EntityId)} Message={session.Message}");
        if (!session.Success || string.IsNullOrWhiteSpace(session.EntityId) || string.IsNullOrWhiteSpace(session.Token)) continue;

        // —— 3. 网易侧请求最新资源包地址 ——
        url = await NeteaseItemService.FetchLatestItemUrlAsync(GameId, session.EntityId, session.Token, Log, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(url)) break;
    }
    if (string.IsNullOrWhiteSpace(url))
    {
        Log("FAIL: 所有账号均未能获取最新资源包地址");
        return 1;
    }
    Log($"[最新地址] {url}");

    // —— 4. HEAD 检测：更新时间 + 是否最新 ——
    string check = await ClientUpdater.CheckAsync(url, installRoot, Log, CancellationToken.None);
    Log($"[检测] {check}");

    // —— 5. 完整更新：下载 → 裁剪/合并 ——
    Log("== 开始下载并安装（16 线程分块 + DragonCore 裁剪）==");
    await ClientUpdater.RunAsync(url, GameId, installRoot, force: true, writeSnapshot: false,
        Log, CancellationToken.None, extraMergeTarget: null);
    Log("== 更新流程完成 ==");
    exitCode = 0;
}
catch (Exception ex)
{
    Log("ERROR: " + ex.GetType().Name + ": " + ex.Message);
    Log(ex.StackTrace);
}
return exitCode;
