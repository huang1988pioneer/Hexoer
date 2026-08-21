using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexoer.Models;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public partial class ContentViewModel : PageViewModelBase
{
    private readonly ServiceHost _services;
    private CancellationTokenSource? _previewCts;
    private bool _suppressPreview;

    public override string Title => "Markdown 內容";
    public override string Icon => "📝";

    public ObservableCollection<PostInfo> Posts { get; } = new();

    [ObservableProperty] public partial PostInfo? SelectedPost { get; set; }
    [ObservableProperty] public partial string EditorText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewHtml { get; set; } = string.Empty;
    [ObservableProperty] public partial string NewPostTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CreateAsDraft { get; set; }
    [ObservableProperty] public partial bool HasUnsavedChanges { get; set; }
    [ObservableProperty] public partial string CurrentFilePath { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowPreview { get; set; } = true;
    [ObservableProperty] public partial bool IsServerRunning { get; set; }
    [ObservableProperty] public partial string ServerUrl { get; set; } = "http://localhost:4000/";
    [ObservableProperty] public partial int ServerPort { get; set; } = 4000;

    public ContentViewModel(ServiceHost services)
    {
        _services = services;
        _services.Server.StateChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshServerState);
        _services.Server.OutputReceived += line =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrEmpty(line))
                    StatusMessage = line.Length > 120 ? line[..120] + "…" : line;
            });
        RefreshServerState();
        UpdatePreview(EditorText);
    }

    public override void OnNavigatedTo()
    {
        RefreshPosts();
        RefreshServerState();
    }

    partial void OnSelectedPostChanged(PostInfo? value) => _ = LoadSelectedAsync();

    partial void OnEditorTextChanged(string value)
    {
        if (!_suppressPreview)
            HasUnsavedChanges = true;
        SchedulePreviewUpdate(value);
    }

    partial void OnShowPreviewChanged(bool value)
    {
        if (value)
            UpdatePreview(EditorText);
    }

    [RelayCommand]
    private void RefreshPosts()
    {
        Posts.Clear();
        foreach (var p in _services.Content.ListPosts())
            Posts.Add(p);
        StatusMessage = $"共 {Posts.Count} 篇文章";
    }

    [RelayCommand]
    private async Task LoadSelectedAsync()
    {
        if (SelectedPost is null)
        {
            _suppressPreview = true;
            EditorText = string.Empty;
            CurrentFilePath = string.Empty;
            _suppressPreview = false;
            HasUnsavedChanges = false;
            UpdatePreview(string.Empty);
            return;
        }

        try
        {
            IsBusy = true;
            CurrentFilePath = SelectedPost.FilePath;
            var text = await _services.Content.ReadPostAsync(SelectedPost.FilePath);
            _suppressPreview = true;
            EditorText = text;
            _suppressPreview = false;
            HasUnsavedChanges = false;
            UpdatePreview(text);
            StatusMessage = $"已開啟：{SelectedPost.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = "開啟失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentFilePath))
        {
            StatusMessage = "沒有開啟的檔案";
            return;
        }

        try
        {
            IsBusy = true;
            await _services.Content.SavePostAsync(CurrentFilePath, EditorText);
            HasUnsavedChanges = false;
            StatusMessage = "已儲存";
            RefreshPosts();
        }
        catch (Exception ex)
        {
            StatusMessage = "儲存失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreatePostAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPostTitle))
        {
            StatusMessage = "請輸入文章標題";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "建立文章…";
            var path = await _services.Content.CreatePostAsync(NewPostTitle.Trim(), CreateAsDraft);
            NewPostTitle = string.Empty;
            RefreshPosts();
            SelectedPost = null;
            foreach (var p in Posts)
            {
                if (p.FilePath == path)
                {
                    SelectedPost = p;
                    break;
                }
            }

            StatusMessage = "文章已建立：" + path;
        }
        catch (Exception ex)
        {
            StatusMessage = "建立失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedPost is null) return;
        try
        {
            _services.Content.DeletePost(SelectedPost.FilePath);
            _suppressPreview = true;
            EditorText = string.Empty;
            CurrentFilePath = string.Empty;
            SelectedPost = null;
            _suppressPreview = false;
            HasUnsavedChanges = false;
            UpdatePreview(string.Empty);
            RefreshPosts();
            StatusMessage = "已刪除文章";
        }
        catch (Exception ex)
        {
            StatusMessage = "刪除失敗：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task GeneratePreviewAsync()
    {
        try
        {
            IsBusy = true;
            if (HasUnsavedChanges && !string.IsNullOrWhiteSpace(CurrentFilePath))
                await _services.Content.SavePostAsync(CurrentFilePath, EditorText);

            StatusMessage = "hexo generate…";
            var r = await _services.Hexo.GenerateAsync();
            StatusMessage = r.Success ? "已產生靜態檔（public/）" : "generate 失敗";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartServerAsync()
    {
        try
        {
            IsBusy = true;
            if (HasUnsavedChanges && !string.IsNullOrWhiteSpace(CurrentFilePath))
            {
                await _services.Content.SavePostAsync(CurrentFilePath, EditorText);
                HasUnsavedChanges = false;
            }

            StatusMessage = "啟動 hexo server…";
            await _services.Server.StartAsync(ServerPort, openBrowser: true);
            RefreshServerState();
            StatusMessage = $"本機預覽：{_services.Server.PreviewUrl}";
        }
        catch (Exception ex)
        {
            StatusMessage = "啟動 server 失敗：" + ex.Message;
            RefreshServerState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void StopServer()
    {
        _services.Server.Stop();
        RefreshServerState();
        StatusMessage = "已停止 hexo server";
    }

    [RelayCommand]
    private void OpenServerInBrowser()
    {
        _services.Server.OpenPreviewInBrowser();
        StatusMessage = "已在瀏覽器開啟 " + _services.Server.PreviewUrl;
    }

    [RelayCommand]
    private void RefreshMarkdownPreview() => UpdatePreview(EditorText);

    private void SchedulePreviewUpdate(string markdown)
    {
        if (!ShowPreview)
            return;

        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(280, cts.Token).ConfigureAwait(false);
                var html = _services.MarkdownPreview.ToPreviewHtml(markdown);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested)
                        PreviewHtml = html;
                });
            }
            catch (OperationCanceledException)
            {
                // debounce cancelled
            }
            catch
            {
                // ignore preview errors
            }
        }, cts.Token);
    }

    private void UpdatePreview(string markdown)
    {
        PreviewHtml = _services.MarkdownPreview.ToPreviewHtml(markdown);
    }

    private void RefreshServerState()
    {
        IsServerRunning = _services.Server.IsRunning;
        ServerUrl = _services.Server.PreviewUrl;
        ServerPort = _services.Server.Port;
    }
}
