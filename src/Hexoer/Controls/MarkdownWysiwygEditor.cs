using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Hexoer.Services;

namespace Hexoer.Controls;

/// <summary>
/// CKEditor 5-style Markdown WYSIWYG surface: markdown in, markdown out, edited as rich text.
/// </summary>
public sealed class MarkdownWysiwygEditor : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownWysiwygEditor, string?>(nameof(Markdown));

    public static readonly StyledProperty<string?> SitePathProperty =
        AvaloniaProperty.Register<MarkdownWysiwygEditor, string?>(nameof(SitePath));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly NativeWebView? _webView;
    private readonly TextBlock _placeholder;
    private readonly Grid _root;
    private bool _ready;
    private bool _updatingFromHtml;
    private bool _loadedHtml;
    private string? _initError;
    private TaskCompletionSource<string>? _flushWaiter;

    public event EventHandler? MarkdownChanged;
    public event EventHandler? SaveRequested;
    public event EventHandler? ToggleModeRequested;
    public event EventHandler<string>? EditorFailed;

    public MarkdownWysiwygEditor()
    {
        _placeholder = new TextBlock
        {
            Text = "載入 WYSIWYG 編輯器…",
            Opacity = 0.55,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontStyle = FontStyle.Italic
        };

        _root = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#0D1218"))
        };
        _root.Children.Add(_placeholder);

        try
        {
            _webView = new NativeWebView { IsVisible = false };
            _webView.NavigationCompleted += OnNavigationCompleted;
            _webView.WebMessageReceived += OnWebMessageReceived;
            _root.Children.Add(_webView);
        }
        catch (Exception ex)
        {
            _initError = ex.Message;
            _placeholder.Text = "無法啟動 WYSIWYG 編輯器（需要 WebView2）。請改用原始碼模式。";
        }

        Content = _root;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string? SitePath
    {
        get => GetValue(SitePathProperty);
        set => SetValue(SitePathProperty, value);
    }

    public bool IsReady => _ready;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == MarkdownProperty || change.Property == SitePathProperty) && !_updatingFromHtml)
            PushMarkdownToWebView();
    }

    public async Task ExecAsync(string command, string? argument = null)
    {
        if (!_ready || _webView is null)
            return;
        var script =
            $"window.hugoerCommand({JsonSerializer.Serialize(command)}, {JsonSerializer.Serialize(argument ?? string.Empty)})";
        await _webView.InvokeScript(script);
    }

    public async Task AddMediaAsync(IReadOnlyDictionary<string, string> media)
    {
        if (!_ready || _webView is null || media.Count == 0)
            return;
        await _webView.InvokeScript($"window.hugoerAddMedia({JsonSerializer.Serialize(media)})");
    }

    public async Task FocusEditorAsync()
    {
        if (!_ready || _webView is null)
            return;
        await _webView.InvokeScript("window.hugoerFocus()");
    }

    public async Task FlushAsync()
    {
        if (!_ready || _webView is null)
            return;

        _flushWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _webView.InvokeScript("window.hugoerFlush()");
        var completed = await Task.WhenAny(_flushWaiter.Task, Task.Delay(750));
        if (completed == _flushWaiter.Task)
            ApplyHtml(_flushWaiter.Task.Result, notify: true);
        _flushWaiter = null;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_loadedHtml)
            return;
        _loadedHtml = true;

        if (_webView is null)
        {
            ShowFailure("無法啟動 WYSIWYG 編輯器（需要 WebView2）。已可改用原始碼模式。", _initError ?? "webview unavailable");
            return;
        }

        try
        {
            _webView.NavigateToString(LoadEditorHtml());
        }
        catch (Exception ex)
        {
            ShowFailure("無法啟動 WYSIWYG 編輯器（需要 WebView2）。已可改用原始碼模式。", ex.Message);
        }
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowFailure("WYSIWYG 編輯器載入失敗。已可改用原始碼模式。", "navigation failed");
            return;
        }

        if (_webView is null)
            return;
        _webView.IsVisible = true;
        _placeholder.IsVisible = false;
        _ready = true;
        await _webView.InvokeScript("window.hugoerFocus()");
        PushMarkdownToWebView();
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Body))
            return;

        WysiwygMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<WysiwygMessage>(e.Body, JsonOptions);
        }
        catch
        {
            return;
        }

        if (message is null || string.IsNullOrWhiteSpace(message.Type))
            return;

        Dispatcher.UIThread.Post(() => HandleMessage(message));
    }

    private void HandleMessage(WysiwygMessage message)
    {
        switch (message.Type.ToLowerInvariant())
        {
            case "ready":
                _ready = true;
                if (_webView is not null)
                    _webView.IsVisible = true;
                _placeholder.IsVisible = false;
                PushMarkdownToWebView();
                break;
            case "change":
                ApplyHtml(message.Html, notify: true);
                break;
            case "flush":
                _flushWaiter?.TrySetResult(message.Html ?? string.Empty);
                ApplyHtml(message.Html, notify: true);
                break;
            case "save":
                ApplyHtml(message.Html, notify: true);
                SaveRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "toggleMode":
                ApplyHtml(message.Html, notify: true);
                ToggleModeRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void ApplyHtml(string? html, bool notify)
    {
        var markdown = MarkdownWysiwygConverter.FromEditableHtml(
            MediaAssetService.FromPreviewHtml(html ?? string.Empty, SitePath));
        if (string.Equals(Markdown, markdown, StringComparison.Ordinal))
            return;

        _updatingFromHtml = true;
        try
        {
            SetCurrentValue(MarkdownProperty, markdown);
        }
        finally
        {
            _updatingFromHtml = false;
        }

        if (notify)
            MarkdownChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PushMarkdownToWebView()
    {
        if (!_ready || _webView is null)
            return;

        var html = MarkdownWysiwygConverter.ToEditableHtml(Markdown ?? string.Empty);
        var media = MediaAssetService.BuildPreviewMediaMap(html, SitePath);
        var script = media.Count == 0
            ? $"window.hugoerSetHtml({JsonSerializer.Serialize(html)})"
            : $"window.hugoerSetHtml({JsonSerializer.Serialize(html)}, {JsonSerializer.Serialize(media)})";
        _ = _webView.InvokeScript(script);
    }

    private void ShowFailure(string userMessage, string detail)
    {
        _ready = false;
        if (_webView is not null)
            _webView.IsVisible = false;
        _placeholder.Text = userMessage;
        _placeholder.IsVisible = true;
        EditorFailed?.Invoke(this, detail);
    }

    private static string LoadEditorHtml()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Hexoer/Assets/editor/wysiwyg.html"));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class WysiwygMessage
    {
        public string Type { get; set; } = string.Empty;
        public string? Html { get; set; }
    }
}
