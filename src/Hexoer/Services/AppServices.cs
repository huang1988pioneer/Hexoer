namespace Hexoer.Services;

/// <summary>
/// Compatibility alias so editor controls can reach the composition root.
/// </summary>
public static class AppServices
{
    public static ServiceHost Instance =>
        ServiceHost.Current ?? throw new InvalidOperationException("服務尚未初始化。");
}
