using System.Text.Json;
using LiveAssistant.Config;
using Renci.SshNet;

namespace LiveAssistant.Services;

/// <summary>
/// 软件启动后自动把本机管理后台反向隧道到云服务器，
/// 使 https://xiangyuzhubao.xyz/diangexitong/ 可访问。断线自动重连。
/// </summary>
public sealed class AdminTunnelService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly SystemMessageService _system;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _announcedConnected;
    private readonly object _clientGate = new();
    private SshClient? _activeClient;
    private ForwardedPortRemote? _activeForward;
    private int _disposed;

    public AdminTunnelService(ConfigManager config, LogService log, SystemMessageService system)
    {
        _config = config;
        _log = log;
        _system = system;
    }

    public void Start()
    {
        if (_cts != null || _disposed != 0)
        {
            return;
        }

        if (!_config.Settings.Admin.Enabled)
        {
            return;
        }

        var tunnel = _config.Settings.AdminTunnel;
        if (!tunnel.Enabled)
        {
            _log.Info("云端后台隧道已关闭（adminTunnel.enabled=false）");
            return;
        }

        var endpoint = ResolveEndpoint(tunnel);
        if (endpoint == null)
        {
            _log.Warn("未找到服务器凭证，跳过云端后台自动连接。请放置 Config/DeployCredentials.local.json");
            _system.Add("云端后台未连接：缺少 DeployCredentials.local.json");
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(endpoint, _cts.Token));
    }

    private async Task RunLoopAsync(TunnelEndpoint endpoint, CancellationToken ct)
    {
        _log.Info($"云端后台隧道准备连接 {endpoint.User}@{endpoint.Host} :{endpoint.RemotePort} -> 127.0.0.1:{endpoint.LocalPort}");
        _system.Add("正在自动连接云端后台…");

        var delayMs = Math.Max(2000, _config.Settings.AdminTunnel.ReconnectDelayMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await MaintainTunnelAsync(endpoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _announcedConnected = false;
                _log.Warn($"云端后台隧道异常: {ex.Message}");
                if (!ct.IsCancellationRequested)
                {
                    _system.Add($"云端后台断开，{delayMs / 1000}s 后重连…");
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task MaintainTunnelAsync(TunnelEndpoint endpoint, CancellationToken ct)
    {
        var client = new SshClient(endpoint.Host, endpoint.User, endpoint.Password);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(8);
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);

        lock (_clientGate)
        {
            _activeClient = client;
        }

        try
        {
            // Connect 不可取消，放到后台并允许 Dispose 侧强制 Disconnect
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                client.Connect();
            }, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            var forward = new ForwardedPortRemote(
                "127.0.0.1",
                (uint)endpoint.RemotePort,
                "127.0.0.1",
                (uint)endpoint.LocalPort);

            Exception? forwardError = null;
            forward.Exception += (_, e) =>
            {
                forwardError = e.Exception;
                _log.Warn($"云端后台端口转发异常: {e.Exception.Message}");
            };

            lock (_clientGate)
            {
                _activeForward = forward;
            }

            client.AddForwardedPort(forward);
            forward.Start();

            if (!_announcedConnected)
            {
                _announcedConnected = true;
                _log.Info("云端后台隧道已连通");
                _system.Add("云端后台已连通: https://xiangyuzhubao.xyz/diangexitong/");
            }

            while (!ct.IsCancellationRequested)
            {
                if (!client.IsConnected || !forward.IsStarted || forwardError != null)
                {
                    throw forwardError ?? new InvalidOperationException("SSH 隧道已断开");
                }

                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ForceCloseClient(client);
        }
    }

    private void ForceCloseClient(SshClient? expected = null)
    {
        SshClient? client;
        ForwardedPortRemote? forward;
        lock (_clientGate)
        {
            forward = _activeForward;
            client = _activeClient;
            if (expected != null && !ReferenceEquals(client, expected))
            {
                // 旧连接已被替换
                client = expected;
            }
            else
            {
                _activeForward = null;
                _activeClient = null;
            }
        }

        try
        {
            if (forward is { IsStarted: true })
            {
                forward.Stop();
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            if (client != null)
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }

                client.Dispose();
            }
        }
        catch
        {
            // ignore
        }
    }

    private TunnelEndpoint? ResolveEndpoint(AdminTunnelSettings tunnel)
    {
        var host = tunnel.Host?.Trim() ?? "";
        var user = string.IsNullOrWhiteSpace(tunnel.User) ? "root" : tunnel.User.Trim();
        var password = tunnel.Password ?? "";
        var remotePort = tunnel.RemotePort > 0 ? tunnel.RemotePort : 15088;
        var localPort = tunnel.LocalPort > 0 ? tunnel.LocalPort : _config.Settings.Admin.Port;

        var cred = TryLoadCredentialsFile();
        if (cred?.Server != null)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                host = cred.Server.Host?.Trim() ?? "";
            }

            if (!string.IsNullOrWhiteSpace(cred.Server.User))
            {
                user = cred.Server.User.Trim();
            }

            if (string.IsNullOrWhiteSpace(password) && !string.IsNullOrWhiteSpace(cred.Server.Password))
            {
                password = cred.Server.Password;
            }

            if (cred.Server.AdminTunnelPort is > 0)
            {
                remotePort = cred.Server.AdminTunnelPort.Value;
            }

            if (cred.Server.AdminLocalPort is > 0)
            {
                localPort = cred.Server.AdminLocalPort.Value;
            }
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return new TunnelEndpoint(host, user, password, remotePort, localPort);
    }

    private DeployCredentialsFile? TryLoadCredentialsFile()
    {
        foreach (var path in EnumerateCredentialPaths())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var json = File.ReadAllText(path);
                var cred = JsonSerializer.Deserialize<DeployCredentialsFile>(json, JsonOptions);
                if (cred?.Server != null && !string.IsNullOrWhiteSpace(cred.Server.Host))
                {
                    _log.Info($"已加载云端隧道凭证: {path}");
                    return cred;
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"读取云端隧道凭证失败 {path}: {ex.Message}");
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateCredentialPaths()
    {
        var name = string.IsNullOrWhiteSpace(_config.Settings.AdminTunnel.CredentialsFile)
            ? "DeployCredentials.local.json"
            : _config.Settings.AdminTunnel.CredentialsFile.Trim();

        yield return Path.Combine(_config.DataDirectory, name);
        yield return Path.Combine(AppPaths.ExeDirectory, "Config", name);

        var dir = new DirectoryInfo(AppPaths.ExeDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            yield return Path.Combine(dir.FullName, "Config", name);
            dir = dir.Parent;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        ForceCloseClient();

        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // ignore
        }

        try
        {
            _cts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _cts = null;
    }

    private sealed record TunnelEndpoint(string Host, string User, string Password, int RemotePort, int LocalPort);

    private sealed class DeployCredentialsFile
    {
        public ServerCred? Server { get; set; }
    }

    private sealed class ServerCred
    {
        public string? Host { get; set; }
        public string? User { get; set; }
        public string? Password { get; set; }
        public int? AdminTunnelPort { get; set; }
        public int? AdminLocalPort { get; set; }
    }
}
