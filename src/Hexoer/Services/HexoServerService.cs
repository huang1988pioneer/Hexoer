using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hexoer.Services;

/// <summary>
/// Manages a long-running <c>hexo server</c> process for local site preview.
/// </summary>
public sealed class HexoServerService : IDisposable
{
    private readonly ProjectContext _context;
    private readonly ProcessRunner _runner;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    private readonly StringBuilder _recentOutput = new();

    public HexoServerService(ProjectContext context, ProcessRunner runner)
    {
        _context = context;
        _runner = runner;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _process is { HasExited: false };
        }
    }

    public int Port { get; private set; } = 4000;
    public string PreviewUrl => $"http://localhost:{Port}/";
    public string? LastError { get; private set; }

    public event Action<string>? OutputReceived;
    public event Action? StateChanged;

    public async Task StartAsync(int port = 4000, bool openBrowser = true)
    {
        if (!_context.IsHexoProject)
            throw new InvalidOperationException("尚未選擇有效的 Hexo 專案。");

        if (IsRunning)
        {
            if (openBrowser)
                OpenBrowser(PreviewUrl);
            return;
        }

        Port = port > 0 ? port : 4000;
        LastError = null;
        lock (_gate)
            _recentOutput.Clear();

        var projectPath = _context.ProjectPath!;
        EnsureLocalHexo(projectPath);

        // Previous launches used `cmd /c npx`, which can exit immediately while node
        // keeps listening. Reclaim that port so a new tracked process can start.
        await ReclaimPortIfNeededAsync(Port).ConfigureAwait(false);

        var start = CreateServerStartInfo(projectPath);
        var cts = new CancellationTokenSource();
        _cts = cts;

        var process = new Process
        {
            StartInfo = start.Psi,
            EnableRaisingEvents = true
        };

        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            HandleOutputLine(e.Data, readyTcs);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            HandleOutputLine(e.Data, readyTcs);
        };
        process.Exited += (_, _) =>
        {
            int? code = null;
            try { if (process.HasExited) code = process.ExitCode; }
            catch { /* ignore */ }

            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                    _process = null;
            }

            readyTcs.TrySetResult(false);
            StateChanged?.Invoke();
            OutputReceived?.Invoke(code is int c
                ? $"[hexo server] 已結束 (exit {c})"
                : "[hexo server] 已結束");
        };

        OutputReceived?.Invoke($"> {start.Display}");
        OutputReceived?.Invoke($"  cwd: {projectPath}");

        lock (_gate)
            _process = process;

        try
        {
            if (!process.Start())
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_process, process))
                        _process = null;
                }

                throw new InvalidOperationException("無法啟動 hexo server 程序。");
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                    _process = null;
            }

            LastError = "無法啟動 Node.js：" + ex.Message;
            throw new InvalidOperationException(LastError, ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        StateChanged?.Invoke();

        var completed = await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(45), cts.Token))
            .ConfigureAwait(false);

        if (completed != readyTcs.Task)
        {
            if (IsRunning)
            {
                OutputReceived?.Invoke($"[hexo server] 逾時未偵測就緒訊息，仍嘗試開啟 {PreviewUrl}");
                if (openBrowser)
                    OpenBrowser(PreviewUrl);
                return;
            }

            LastError = "啟動 hexo server 逾時";
            throw new TimeoutException(LastError);
        }

        if (!await readyTcs.Task.ConfigureAwait(false) && !IsRunning)
        {
            var detail = SnapshotRecentOutput();
            LastError = string.IsNullOrWhiteSpace(detail)
                ? "hexo server 啟動後立即結束，請查看日誌。"
                : "hexo server 啟動後立即結束：\n" + detail;
            throw new InvalidOperationException(LastError);
        }

        if (openBrowser)
            OpenBrowser(PreviewUrl);
    }

    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (process is null)
        {
            StateChanged?.Invoke();
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                OutputReceived?.Invoke("[hexo server] 正在停止…");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke("[hexo server] 停止時發生錯誤：" + ex.Message);
        }
        finally
        {
            try { process.Dispose(); } catch { /* ignore */ }
            StateChanged?.Invoke();
        }
    }

    public void OpenPreviewInBrowser() => OpenBrowser(PreviewUrl);

    public void Dispose() => Stop();

    private void HandleOutputLine(string line, TaskCompletionSource<bool> readyTcs)
    {
        OutputReceived?.Invoke(line);
        _runner.RaiseOutput(line);
        lock (_gate)
        {
            _recentOutput.AppendLine(line);
            if (_recentOutput.Length > 8000)
                _recentOutput.Remove(0, _recentOutput.Length - 4000);
        }

        var m = Regex.Match(line, @"https?://localhost:(\d+)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var detected))
            Port = detected;

        if (line.Contains("Hexo is running", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Server running", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                && line.Contains("running", StringComparison.OrdinalIgnoreCase)))
        {
            readyTcs.TrySetResult(true);
        }
    }

    private string SnapshotRecentOutput()
    {
        lock (_gate)
            return _recentOutput.ToString().Trim();
    }

    private static void EnsureLocalHexo(string projectPath)
    {
        if (FindLocalHexoScript(projectPath) is null)
            throw new InvalidOperationException("專案尚未安裝 Hexo（找不到 node_modules/hexo）。請先執行 npm install。");

        var serverDir = Path.Combine(projectPath, "node_modules", "hexo-server");
        if (!Directory.Exists(serverDir))
            throw new InvalidOperationException("專案缺少 hexo-server，請先執行 npm install。");
    }

    private LaunchPlan CreateServerStartInfo(string workingDirectory)
    {
        var hexoJs = FindLocalHexoScript(workingDirectory)
            ?? throw new InvalidOperationException("找不到 hexo 執行檔。");
        var nodeExe = FindNodeExecutable()
            ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node");

        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add(hexoJs);
        psi.ArgumentList.Add("server");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(Port.ToString(CultureInfo.InvariantCulture));
        ApplyEnv(psi);

        var display = $"{nodeExe} \"{hexoJs}\" server -p {Port}";
        return new LaunchPlan(psi, display);
    }

    private static void ApplyEnv(ProcessStartInfo psi)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        psi.Environment["PATH"] = path;
        psi.Environment["FORCE_COLOR"] = "0";
        psi.Environment["NO_COLOR"] = "1";
    }

    private static string? FindLocalHexoScript(string projectPath)
    {
        string[] candidates =
        [
            Path.Combine(projectPath, "node_modules", "hexo", "bin", "hexo"),
            Path.Combine(projectPath, "node_modules", "hexo-cli", "bin", "hexo")
        ];
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? FindNodeExecutable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var defaultPath = Path.Combine(programFiles, "nodejs", "node.exe");
            if (File.Exists(defaultPath))
                return defaultPath;

            return FindOnPath("node.exe") ?? FindOnPath("node");
        }

        return FindOnPath("node");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(Path.PathSeparator))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // ignore invalid PATH entries
            }
        }

        return null;
    }

    private async Task ReclaimPortIfNeededAsync(int port)
    {
        if (!await IsPortOpenAsync(port).ConfigureAwait(false))
            return;

        OutputReceived?.Invoke($"[hexo server] 埠 {port} 已被占用，嘗試停止先前遺留的 node 程序…");
        if (!TryKillListenerOnPort(port))
            throw new InvalidOperationException($"埠 {port} 已被其他程式占用，請改用其他埠或先關閉占用的程序。");

        for (var i = 0; i < 10 && await IsPortOpenAsync(port).ConfigureAwait(false); i++)
            await Task.Delay(150).ConfigureAwait(false);

        if (await IsPortOpenAsync(port).ConfigureAwait(false))
            throw new InvalidOperationException($"埠 {port} 仍被占用，請改用其他埠。");
    }

    private bool TryKillListenerOnPort(int port)
    {
        var pid = FindPidListeningOnPort(port);
        if (pid is null or 0)
            return false;

        try
        {
            using var existing = Process.GetProcessById(pid.Value);
            var name = existing.ProcessName;
            if (!name.Equals("node", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("node.exe", StringComparison.OrdinalIgnoreCase))
            {
                OutputReceived?.Invoke($"[hexo server] 埠 {port} 由 {name} (PID {pid}) 占用，未自動結束。");
                return false;
            }

            OutputReceived?.Invoke($"[hexo server] 結束占用埠 {port} 的 node (PID {pid})");
            existing.Kill(entireProcessTree: true);
            existing.WaitForExit(3000);
            return true;
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke("[hexo server] 無法結束占用埠的程序：" + ex.Message);
            return false;
        }
    }

    private static async Task<bool> IsPortOpenAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static int? FindPidListeningOnPort(int port)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return FindPidListeningOnPortWindows(port);

            return FindPidListeningOnPortUnix(port);
        }
        catch
        {
            return null;
        }
    }

    private static int? FindPidListeningOnPortWindows(int port)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netstat",
            Arguments = "-ano",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(3000))
            return null;

        var suffix = ":" + port.ToString(CultureInfo.InvariantCulture);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            var local = parts[1];
            if (!local.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (int.TryParse(parts[^1], out var pid) && pid > 0)
                return pid;
        }

        return null;
    }

    private static int? FindPidListeningOnPortUnix(int port)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"-c \"lsof -iTCP:{port} -sTCP:LISTEN -t 2>/dev/null | head -n 1\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEnd().Trim();
        if (!process.WaitForExit(3000))
            return null;
        return int.TryParse(output, out var pid) && pid > 0 ? pid : null;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private readonly record struct LaunchPlan(ProcessStartInfo Psi, string Display);
}
