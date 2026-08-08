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
}
