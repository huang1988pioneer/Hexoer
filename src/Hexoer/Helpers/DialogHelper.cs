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
        var window = GetMainWindow();
        if (window is null) return null;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types.Length > 0 ? types : null
        }).ConfigureAwait(true);

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

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
