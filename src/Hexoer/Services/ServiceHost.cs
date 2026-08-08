namespace Hexoer.Services;

/// <summary>
/// Simple composition root for the desktop app (no DI container needed).
/// </summary>
public sealed class ServiceHost
{
    public ProjectContext Project { get; } = new();
    public ProcessRunner ProcessRunner { get; } = new();
    public HexoService Hexo { get; }
    public ThemeService Themes { get; }
    public ConfigService Config { get; }
    public ContentService Content { get; }
    public GitHubService GitHub { get; }
    public HexoServerService Server { get; }
    public MarkdownPreviewService MarkdownPreview { get; } = new();

    public ServiceHost()
    {
        Hexo = new HexoService(ProcessRunner, Project);
        Themes = new ThemeService(ProcessRunner, Project, Hexo);
        Config = new ConfigService(Project);
        Content = new ContentService(Project, Hexo);
        GitHub = new GitHubService(Project, Config, Hexo);
        Server = new HexoServerService(Project, ProcessRunner);
        Project.RestoreLastProject();
    }
}
