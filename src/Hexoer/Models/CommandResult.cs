namespace Hexoer.Models;

public sealed class CommandResult
{
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public bool Success => ExitCode == 0;
    public string CombinedOutput =>
        string.IsNullOrWhiteSpace(StandardError)
            ? StandardOutput
            : string.IsNullOrWhiteSpace(StandardOutput)
                ? StandardError
                : StandardOutput + "\n" + StandardError;

    public static CommandResult Ok(string output = "") => new()
    {
        ExitCode = 0,
        StandardOutput = output,
        StandardError = string.Empty
    };

    public static CommandResult Fail(string error, int exitCode = -1, string output = "") => new()
    {
        ExitCode = exitCode == 0 ? -1 : exitCode,
        StandardOutput = output,
        StandardError = error
    };
}
