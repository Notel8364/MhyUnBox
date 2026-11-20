using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// 把 MhyunBox 当前运行中的代理角色写成 MhyCPTD-1.20.1 客户端 mod 能读的
/// %APPDATA%\MhyCPTD\roles.json；代理新起/上游切换时写 switch.json（mod 据此自动重连）。
/// 文件格式与 1.12.2 版 MhyCPTD 一致。
/// </summary>
public static class MhyCptdBridge
{
    private static readonly object Gate = new object();
    private static CancellationTokenSource? _cts;
    private static string _lastRoles = "";
    private static Dictionary<int, string> _knownPorts = new Dictionary<int, string>();
    private static readonly List<string> _switchEntries = new List<string>();

    public static string GlobalDir
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MhyCPTD");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            LogHelper.Log("[MhyCPTD] 桥接已启动，输出目录 " + GlobalDir);
            _ = Task.Run(() => LoopAsync(token), token);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    private static async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                WriteRolesOnce();
            }
            catch (Exception ex)
            {
                LogHelper.DebugError(ex, "[MhyCPTD] 写 roles.json 失败");
            }
            try
            {
                await Task.Delay(2000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public static void WriteRolesOnce()
    {
        List<ProxyCaptureService.ActiveBindingInfo> bindings;
        try
        {
            bindings = ProxyCaptureService.ActiveBindingsSnapshot().ToList();
        }
        catch
        {
            return;
        }

        Dictionary<int, string> current = new Dictionary<int, string>();
        List<string> entries = new List<string>();
        foreach (ProxyCaptureService.ActiveBindingInfo b in bindings)
        {
            string role = b.LoginName ?? "";
            string serverName = "";
            try
            {
                string host = b.TargetHost ?? "";
                ServerPreset? preset = ServerPresets.Find(host);
                if (preset == null)
                {
                    preset = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase));
                }
                serverName = preset?.Name ?? host;
            }
            catch
            {
                serverName = b.TargetHost ?? "";
            }
            current[b.ListenPort] = role + "|" + (b.TargetHost ?? "") + "|" + serverName;
            entries.Add("    {\"name\":\"" + Esc(role) + "\",\"port\":" + b.ListenPort
                + ",\"host\":\"127.0.0.1\",\"online\":true,\"serverName\":\"" + Esc(serverName)
                + "\",\"tag\":\"\",\"auth\":\"netease\",\"set\":\"" + Esc(serverName) + "\"}");
        }

        string json = entries.Count == 0
            ? "{\"version\":1,\"game\":\"\",\"host\":\"\",\"roles\":[]}"
            : "{\"version\":1,\"game\":\"\",\"host\":\"\",\"roles\":[\n" + string.Join(",\n", entries) + "\n]}";
        if (!string.Equals(json, _lastRoles, StringComparison.Ordinal))
        {
            try
            {
                File.WriteAllText(Path.Combine(GlobalDir, "roles.json"), json, new UTF8Encoding(false));
                _lastRoles = json;
                LogHelper.Log("[MhyCPTD] roles.json 已更新（" + entries.Count + " 个角色代理）");
            }
            catch (Exception ex)
            {
                LogHelper.DebugError(ex, "[MhyCPTD] roles.json 写入失败");
            }
        }

        // 新出现的代理端口 → 写 switch.json 热切换信号（mod 断线时会自动重连到它）
        foreach (KeyValuePair<int, string> kv in current)
        {
            if (_knownPorts.ContainsKey(kv.Key)) continue;
            string upstream = kv.Value;
            int bar = upstream.IndexOf('|');
            if (bar >= 0) upstream = upstream.Substring(bar + 1);
            WriteSwitch(kv.Key, upstream, "proxy-start");
        }
        _knownPorts = current;
    }

    /// <summary>写一条热切换信号：mod 在断线时若发现 30 秒内的信号，会自动重连该地址。</summary>
    public static void WriteSwitch(int port, string upstream, string reason)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string entry = "  {\"port\":" + port + ",\"address\":\"127.0.0.1:" + port
            + "\",\"upstream\":\"" + Esc(upstream ?? "") + "\",\"reason\":\"" + Esc(reason ?? "hot-swap")
            + "\",\"time\":" + now + "}";
        _switchEntries.Add(entry);
        while (_switchEntries.Count > 20) _switchEntries.RemoveAt(0);
        try
        {
            File.WriteAllText(Path.Combine(GlobalDir, "switch.json"), "[\n" + string.Join(",\n", _switchEntries) + "\n]", new UTF8Encoding(false));
            LogHelper.Log("[MhyCPTD] switch.json 信号：127.0.0.1:" + port + " upstream=" + upstream + " reason=" + reason);
        }
        catch (Exception ex)
        {
            LogHelper.DebugError(ex, "[MhyCPTD] switch.json 写入失败");
        }
    }

    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}