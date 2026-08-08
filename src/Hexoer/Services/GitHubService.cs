using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hexoer.Models;

namespace Hexoer.Services;

public sealed class GitHubService : IDisposable
{
    private readonly ProjectContext _context;
    private readonly ConfigService _config;
    private readonly HexoService _hexo;
    private readonly HttpClient _http;

    public GitHubService(ProjectContext context, ConfigService config, HexoService hexo)
    {
        _context = context;
        _config = config;
        _hexo = hexo;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Hexoer-Avalonia");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public void Dispose() => _http.Dispose();

    public async Task ConfigureDeployAsync(string repoUrl, string branch = "gh-pages", CancellationToken ct = default)
    {
        await _config.ConfigureGitDeployAsync(repoUrl, branch).ConfigureAwait(false);
        await _hexo.InstallDeployerGitAsync(ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> DeployAsync(CancellationToken ct = default)
    {
        await _hexo.GenerateAsync(ct).ConfigureAwait(false);
        return await _hexo.DeployAsync(ct).ConfigureAwait(false);
    }

    public async Task<GitHubPagesStatus> GetPagesStatusAsync(string? owner = null, string? repo = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            var parsed = await TryParseRepoFromConfigAsync().ConfigureAwait(false);
            owner ??= parsed.Owner;
            repo ??= parsed.Repo;
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            return new GitHubPagesStatus
            {
                Success = false,
                Message = "無法從 _config.yml 解析 GitHub repo，請手動填寫 owner/repo。"
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{repo}/pages");
        ApplyAuth(request);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new GitHubPagesStatus
                {
                    Success = false,
                    Status = "not_found",
                    Message = $"未啟用 GitHub Pages，或 repo {owner}/{repo} 不存在 / 無權限。"
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new GitHubPagesStatus
                {
                    Success = false,
                    Message = $"GitHub API 錯誤 ({(int)response.StatusCode}): {body}"
                };
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? sourceBranch = null;
            string? sourcePath = null;
            if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
            {
                sourceBranch = source.TryGetProperty("branch", out var b) ? b.GetString() : null;
                sourcePath = source.TryGetProperty("path", out var p) ? p.GetString() : null;
            }

            DateTime? updated = null;
            if (root.TryGetProperty("updated_at", out var ua) && ua.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ua.GetString(), out var dt))
            {
                updated = dt.ToLocalTime();
            }

            return new GitHubPagesStatus
            {
                Success = true,
                Status = root.TryGetProperty("status", out var st) ? st.GetString() : null,
                HtmlUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() : null,
                Cname = root.TryGetProperty("cname", out var cn) && cn.ValueKind != JsonValueKind.Null ? cn.GetString() : null,
                SourceBranch = sourceBranch,
                SourcePath = sourcePath,
                UpdatedAt = updated,
                Message = $"GitHub Pages 狀態：{root.GetProperty("status").GetString()}"
            };
        }
        catch (Exception ex)
        {
            return new GitHubPagesStatus
            {
                Success = false,
                Message = "查詢失敗：" + ex.Message
            };
        }
    }

    public async Task<(string? Owner, string? Repo)> TryParseRepoFromConfigAsync()
    {
        var (repoUrl, _) = await _config.ReadDeploySettingsAsync().ConfigureAwait(false);
        return ParseOwnerRepo(repoUrl);
    }

    public static (string? Owner, string? Repo) ParseOwnerRepo(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
            return (null, null);

        // git@github.com:owner/repo.git
        var m = Regex.Match(repoUrl, @"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/#\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        if (!m.Success)
            return (null, null);

        return (m.Groups["owner"].Value, m.Groups["repo"].Value);
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        var token = _context.Settings.GitHubToken;
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
