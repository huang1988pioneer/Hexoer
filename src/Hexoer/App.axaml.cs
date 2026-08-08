using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Hexoer.ViewModels;
using Hexoer.Views;

namespace Hexoer;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainViewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _mainViewModel,
            };

            desktop.Exit += (_, _) =>
            {
                _mainViewModel?.Dispose();
                _mainViewModel = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
