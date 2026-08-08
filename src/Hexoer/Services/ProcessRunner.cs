using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hexoer.Models;

namespace Hexoer.Services;

public sealed class ProcessRunner
{
    public event Action<string>? OutputReceived;

    /// <summary>Allow other services to fan-out log lines to the same listeners.</summary>
    public void RaiseOutput(string line) => OutputReceived?.Invoke(line);

    public async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var psi = CreateStartInfo(fileName, arguments, workingDirectory);
        return await ExecuteAsync(psi, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandResult> RunShellAsync(
        string command,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi = CreateStartInfo("cmd.exe", "/c " + command, workingDirectory);
        }
        else
        {
            psi = CreateStartInfo("/bin/bash", "-lc " + Quote(command), workingDirectory);
        }

        return await ExecuteAsync(psi, cancellationToken).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string arguments, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        // Ensure PATH tools work when launched from GUI
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        psi.Environment["PATH"] = path;
        psi.Environment["FORCE_COLOR"] = "0";
        psi.Environment["NO_COLOR"] = "1";

        return psi;
    }

    private async Task<CommandResult> ExecuteAsync(ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        OutputReceived?.Invoke($"> {psi.FileName} {psi.Arguments}");
        if (!string.IsNullOrWhiteSpace(psi.WorkingDirectory))
            OutputReceived?.Invoke($"  cwd: {psi.WorkingDirectory}");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            OutputReceived?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            OutputReceived?.Invoke(e.Data);
        };

        try
        {
            if (!process.Start())
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StandardOutput = string.Empty,
                    StandardError = $"Failed to start process: {psi.FileName}"
                };
            }
        }
        catch (Exception ex)
        {
            return new CommandResult
            {
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = ex.Message
            };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            throw;
        }

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString().TrimEnd(),
            StandardError = stderr.ToString().TrimEnd()
        };
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
