using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Hexoer.Helpers;

public static class DialogHelper
{
    public static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    public static async Task<string?> PickFolderAsync(string title = "選擇資料夾")
    {
        var window = GetMainWindow();
        if (window is null) return null;

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        }).ConfigureAwait(true);

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> PickOpenFileAsync(string title, params FilePickerFileType[] types)
    {
        var files = await PickFilesAsync(title, types, allowMultiple: false).ConfigureAwait(true);
        return files.Count > 0 ? files[0] : null;
    }

    public static Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerFileType>? types = null) =>
        PickOpenFileAsync(title, types is null ? [] : types.ToArray());

    public static async Task<IReadOnlyList<string>> PickFilesAsync(
        string title,
        IReadOnlyList<FilePickerFileType>? types = null,
        bool allowMultiple = true)
    {
        var window = GetMainWindow();
        if (window is null) return [];

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = types
        }).ConfigureAwait(true);

        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
    }

    public static FilePickerFileType Images { get; } = new("圖片")
    {
        Patterns =
        [
            "*.png", "*.jpg", "*.jpeg", "*.jfif", "*.gif", "*.webp", "*.svg",
            "*.avif", "*.bmp", "*.ico", "*.heic", "*.heif", "*.tif", "*.tiff"
        ],
        MimeTypes = ["image/*"]
    };

    public static FilePickerFileType Audio { get; } = new("音訊")
    {
        Patterns = ["*.mp3", "*.flac", "*.wav", "*.aac", "*.ogg", "*.wma", "*.m4a", "*.opus", "*.weba", "*.caf", "*.amr", "*.aiff", "*.aif"],
        MimeTypes = ["audio/*"]
    };

    public static FilePickerFileType Videos { get; } = new("影片")
    {
        Patterns = ["*.mp4", "*.webm", "*.mov", "*.mkv", "*.avi", "*.m4v", "*.ogv"],
        MimeTypes = ["video/*"]
    };

    public static FilePickerFileType Pdf { get; } = new("PDF")
    {
        Patterns = ["*.pdf"],
        MimeTypes = ["application/pdf"]
    };

    public static FilePickerFileType AllFiles { get; } = new("所有檔案")
    {
        Patterns = ["*.*"]
    };

    public static async Task ShowMessageAsync(string title, string message)
    {
        var window = GetMainWindow();
        if (window is null) return;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new Button
                    {
                        Content = "確定",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        MinWidth = 80
                    }
                }
            }
        };

        if (dialog.Content is StackPanel panel && panel.Children.OfType<Button>().FirstOrDefault() is { } btn)
            btn.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(window).ConfigureAwait(true);
    }
}
