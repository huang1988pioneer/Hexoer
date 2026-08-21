using System;
using System.Diagnostics;
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

        var cts = new CancellationTokenSource();
        _cts = cts;

        var command = $"npx --yes hexo server -p {Port}";
        var psi = CreateShellStartInfo(command, _context.ProjectPath!);

        var process = new Process
        {
            StartInfo = psi,
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
            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                    _process = null;
            }
            readyTcs.TrySetResult(false);
            StateChanged?.Invoke();
            OutputReceived?.Invoke("[hexo server] 已結束");
        };

        OutputReceived?.Invoke($"> {command}");
        OutputReceived?.Invoke($"  cwd: {_context.ProjectPath}");

        if (!process.Start())
            throw new InvalidOperationException("無法啟動 hexo server 程序。");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_gate)
            _process = process;

        StateChanged?.Invoke();

        // Wait until server reports ready, or timeout / exit
        var completed = await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(45), cts.Token))
            .ConfigureAwait(false);

        if (completed != readyTcs.Task)
        {
            // Timeout: still treat as started if process alive (hexo may not print expected line)
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
            LastError = "hexo server 啟動後立即結束，請查看日誌。";
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

        // Typical: "INFO  Hexo is running at http://localhost:4000/ . Press Ctrl+C to stop."
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

    private static ProcessStartInfo CreateShellStartInfo(string command, string workingDirectory)
    {
        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "-lc \"" + command.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        psi.Environment["PATH"] = path;
        psi.Environment["FORCE_COLOR"] = "0";
        psi.Environment["NO_COLOR"] = "1";
        return psi;
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
}
