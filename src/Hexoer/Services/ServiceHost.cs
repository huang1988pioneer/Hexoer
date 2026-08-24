using System;

namespace Hexoer.Services;

/// <summary>
/// Simple composition root for the desktop app (no DI container needed).
/// </summary>
public sealed class ServiceHost
{
    public static ServiceHost? Current { get; private set; }

    public ProjectContext Project { get; } = new();
    public ProcessRunner ProcessRunner { get; } = new();
    public HexoService Hexo { get; }
    public ThemeService Themes { get; }
    public ConfigService Config { get; }
    public ContentService Content { get; }
    public MenuService Menus { get; } = new();
    public FrontMatterService FrontMatter { get; } = new();
    public GitHubService GitHub { get; }
    public GitService Git { get; }
    public HexoServerService Server { get; }
    public MarkdownPreviewService MarkdownPreview { get; } = new();
    public DeploymentMonitorService DeploymentMonitor { get; } = new();

    public string? CurrentSitePath => Project.ProjectPath;

    public event Action<string>? AppStatusChanged;

    public ServiceHost()
    {
        Current = this;
        Hexo = new HexoService(ProcessRunner, Project);
        Themes = new ThemeService(ProcessRunner, Project, Hexo);
        Config = new ConfigService(Project);
        Content = new ContentService(Project, Hexo, FrontMatter);
        GitHub = new GitHubService(Project, Config, Hexo, ProcessRunner, DeploymentMonitor);
        Git = new GitService(ProcessRunner, Project, GitHub);
        Server = new HexoServerService(Project, ProcessRunner);
        Project.RestoreLastProject();
    }

    public void SetAppStatus(string message) => AppStatusChanged?.Invoke(message);
}
