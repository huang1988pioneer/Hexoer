using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hexoer.Services;

namespace Hexoer.Controls;

/// <summary>
/// Live Markdown preview: Markdig HTML in an embedded WebView when available,
/// otherwise a structured Avalonia text fallback.
/// </summary>
public sealed class MarkdownPreviewControl : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownPreviewControl, string?>(nameof(Markdown));

    private readonly NativeWebView? _webView;
    private readonly ScrollViewer _fallbackScroll;
    private readonly StackPanel _fallbackPanel;
    private readonly Grid _root;
    private readonly TextBlock _status;
    private bool _ready;
    private bool _loadedHtml;
    private bool _useFallback;
    private string? _mediaSite;
    private readonly Dictionary<string, string> _sentMedia = new(StringComparer.Ordinal);

    public MarkdownPreviewControl()
    {
        _fallbackPanel = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        _fallbackScroll = new ScrollViewer
        {
            Content = _fallbackPanel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        _status = new TextBlock
        {
            Text = "載入預覽…",
            Opacity = 0.55,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontStyle = FontStyle.Italic
        };

        _root = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#0D1218"))
        };
        _root.Children.Add(_fallbackScroll);
        _root.Children.Add(_status);

        try
        {
            _webView = new NativeWebView { IsVisible = false };
            _webView.NavigationCompleted += OnNavigationCompleted;
            _root.Children.Add(_webView);
        }
        catch
        {
            _useFallback = true;
            _status.IsVisible = false;
        }

        Content = _root;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
            ScheduleRefresh();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_loadedHtml)
            return;
        _loadedHtml = true;

        if (_webView is null || _useFallback)
        {
            _useFallback = true;
            _status.IsVisible = false;
            BuildFallbackPreview(Markdown ?? string.Empty);
            return;
        }

        try
        {
            _webView.NavigateToString(MarkdownPreviewService.PreviewShellDocument());
        }
        catch
        {
            _useFallback = true;
            _webView.IsVisible = false;
            _status.IsVisible = false;
            BuildFallbackPreview(Markdown ?? string.Empty);
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            _useFallback = true;
            if (_webView is not null)
                _webView.IsVisible = false;
            _status.IsVisible = false;
            BuildFallbackPreview(Markdown ?? string.Empty);
            return;
        }

        _ready = true;
        _status.IsVisible = false;
        if (_webView is not null)
            _webView.IsVisible = true;
        _fallbackScroll.IsVisible = false;
        PushHtml();
    }

    private void ScheduleRefresh()
    {
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        if (_useFallback || _webView is null)
        {
            BuildFallbackPreview(Markdown ?? string.Empty);
            return;
        }

        if (_ready)
            PushHtml();
    }

    private void PushHtml()
    {
        if (!_ready || _webView is null)
            return;

        var fragment = MarkdownPreviewService.ToHtmlFragment(Markdown ?? string.Empty);
        var site = AppServices.Instance.CurrentSitePath;
        var media = TakeNewMedia(fragment, site);
        var script = media.Count == 0
            ? $"window.hugoerSetPreview({JsonSerializer.Serialize(fragment)})"
            : $"window.hugoerSetPreview({JsonSerializer.Serialize(fragment)}, {JsonSerializer.Serialize(media)})";
        _ = _webView.InvokeScript(script);
    }

    private Dictionary<string, string> TakeNewMedia(string html, string? sitePath)
    {
        if (!string.Equals(_mediaSite, sitePath, StringComparison.OrdinalIgnoreCase))
        {
            _sentMedia.Clear();
            _mediaSite = sitePath;
        }

        var fresh = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (url, dataUri) in MediaAssetService.BuildPreviewMediaMap(html, sitePath))
        {
            if (_sentMedia.TryGetValue(url, out var previous) && previous == dataUri)
                continue;
            _sentMedia[url] = dataUri;
            fresh[url] = dataUri;
        }

        return fresh;
    }

    private void BuildFallbackPreview(string markdown)
    {
        foreach (var child in _fallbackPanel.Children)
        {
            if (child is Image { Source: IDisposable bitmap })
                bitmap.Dispose();
        }

        _fallbackPanel.Children.Clear();
        _fallbackScroll.IsVisible = true;

        var front = MarkdownPreviewService.ExtractFrontMatter(markdown);
        if (!string.IsNullOrWhiteSpace(front))
        {
            _fallbackPanel.Children.Add(new TextBlock
            {
                Text = "Front Matter",
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("#7CDAF9"),
                FontSize = 12,
                Opacity = 0.9
            });
            _fallbackPanel.Children.Add(new Border
            {
                Background = Brush("#151C26"),
                BorderBrush = Brush("#2A3648"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = new TextBlock
                {
                    Text = front,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    FontSize = 12,
                    Foreground = Brush("#C5D0DC"),
                    TextWrapping = TextWrapping.Wrap
                }
            });
            _fallbackPanel.Children.Add(new Separator { Margin = new Thickness(0, 8) });
        }

        var body = MarkdownPreviewService.StripFrontMatter(markdown);
        if (string.IsNullOrWhiteSpace(body))
        {
            _fallbackPanel.Children.Add(new TextBlock
            {
                Text = "開始輸入 Markdown，預覽會即時更新。",
                Opacity = 0.55,
                FontStyle = FontStyle.Italic
            });
            return;
        }

        RenderBodyLines(body);
    }

    private void RenderBodyLines(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var inCode = false;
        var codeBuffer = new System.Text.StringBuilder();
        var paraBuffer = new System.Text.StringBuilder();

        void FlushPara()
        {
            if (paraBuffer.Length == 0) return;
            var text = paraBuffer.ToString().Trim();
            paraBuffer.Clear();
            if (text.Length == 0) return;
            _fallbackPanel.Children.Add(CreateRichParagraph(text));
        }

        void FlushCode()
        {
            if (codeBuffer.Length == 0) return;
            var code = codeBuffer.ToString().TrimEnd();
            codeBuffer.Clear();
            _fallbackPanel.Children.Add(new Border
            {
                Background = Brush("#121A24"),
                BorderBrush = Brush("#2A3648"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4),
                Child = new SelectableTextBlock
                {
                    Text = code,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    FontSize = 12.5,
                    Foreground = Brush("#E6EDF3"),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        foreach (var raw in lines)
        {
            var line = raw;

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    FlushCode();
                    inCode = false;
                }
                else
                {
                    FlushPara();
                    inCode = true;
                }
                continue;
            }

            if (inCode)
            {
                codeBuffer.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushPara();
                continue;
            }

            var trimmed = line.Trim();
            var imageMatch = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"^!\[(?<alt>[^\]]*)\]\((?<url>[^)]+)\)$");
            if (imageMatch.Success)
            {
                FlushPara();
                _fallbackPanel.Children.Add(
                    CreateMarkdownImage(imageMatch.Groups["alt"].Value, imageMatch.Groups["url"].Value));
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                FlushPara();
                var level = line.TakeWhile(c => c == '#').Count();
                var text = line[level..].Trim();
                _fallbackPanel.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = level switch { 1 => 26, 2 => 22, 3 => 18, _ => 16 },
                    FontWeight = FontWeight.Bold,
                    Foreground = Brush("#7CDAF9"),
                    Margin = new Thickness(0, level == 1 ? 8 : 6, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            if (line.StartsWith("> ", StringComparison.Ordinal) || line == ">")
            {
                FlushPara();
                _fallbackPanel.Children.Add(new Border
                {
                    BorderBrush = Brush("#0E7490"),
                    BorderThickness = new Thickness(4, 0, 0, 0),
                    Background = Brush("#151C26"),
                    Padding = new Thickness(12, 8),
                    Margin = new Thickness(0, 4),
                    Child = CreateRichParagraph(line.TrimStart('>', ' '))
                });
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\.\s"))
            {
                FlushPara();
                var item = System.Text.RegularExpressions.Regex.Replace(line, @"^([-*]|\d+\.)\s+", "• ");
                _fallbackPanel.Children.Add(CreateRichParagraph(item));
                continue;
            }

            if (line.StartsWith("---", StringComparison.Ordinal) && line.Trim().All(c => c == '-'))
            {
                FlushPara();
                _fallbackPanel.Children.Add(new Separator { Margin = new Thickness(0, 10) });
                continue;
            }

            if (paraBuffer.Length > 0)
                paraBuffer.Append(' ');
            paraBuffer.Append(line.Trim());
        }

        if (inCode) FlushCode();
        FlushPara();
    }

    private static Control CreateMarkdownImage(string alt, string url)
    {
        var site = AppServices.Instance.CurrentSitePath;
        if (!string.IsNullOrWhiteSpace(site))
        {
            var staticDir = Path.GetFullPath(Path.Combine(site, MediaAssetService.StaticDirectoryName));
            if (MediaAssetService.TryMapSiteUrlToFile(url, staticDir, out var fileUrl))
            {
                try
                {
                    var path = Uri.UnescapeDataString(new Uri(fileUrl).LocalPath);
                    if (File.Exists(path))
                    {
                        return new Image
                        {
                            Source = new Bitmap(path),
                            MaxWidth = 720,
                            Stretch = Stretch.Uniform,
                            Margin = new Thickness(0, 8),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
                        };
                    }
                }
                catch
                {
                    // Fall through to the caption.
                }
            }
        }

        return new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(alt) ? url : alt,
            Opacity = 0.7,
            FontStyle = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static Control CreateRichParagraph(string text)
    {
        var tb = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14.5,
            LineHeight = 22,
            Foreground = Brush("#E6EDF3")
        };

        var inlines = new Avalonia.Controls.Documents.InlineCollection();
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`|\[[^\]]+\]\([^)]+\))");
        var idx = 0;
        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(text))
        {
            if (m.Index > idx)
                inlines.Add(new Avalonia.Controls.Documents.Run(text[idx..m.Index]));

            var token = m.Value;
            if (token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal))
            {
                inlines.Add(new Avalonia.Controls.Documents.Run(token[2..^2]) { FontWeight = FontWeight.Bold });
            }
            else if (token.StartsWith('*') && token.EndsWith('*'))
            {
                inlines.Add(new Avalonia.Controls.Documents.Run(token[1..^1]) { FontStyle = FontStyle.Italic });
            }
            else if (token.StartsWith('`') && token.EndsWith('`'))
            {
                inlines.Add(new Avalonia.Controls.Documents.Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    Background = Brush("#1A2330")
                });
            }
            else if (token.StartsWith("[", StringComparison.Ordinal))
            {
                var lm = System.Text.RegularExpressions.Regex.Match(token, @"\[([^\]]+)\]\(([^)]+)\)");
                if (lm.Success)
                {
                    inlines.Add(new Avalonia.Controls.Documents.Run(lm.Groups[1].Value)
                    {
                        Foreground = Brush("#5EC8F0"),
                        TextDecorations = TextDecorations.Underline
                    });
                }
                else
                {
                    inlines.Add(new Avalonia.Controls.Documents.Run(token));
                }
            }

            idx = m.Index + m.Length;
        }

        if (idx < text.Length)
            inlines.Add(new Avalonia.Controls.Documents.Run(text[idx..]));

        if (inlines.Count == 0)
            tb.Text = text;
        else
            tb.Inlines = inlines;

        return tb;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
