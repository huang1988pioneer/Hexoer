using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ServiceHost _services;

    public ObservableCollection<PageViewModelBase> Pages { get; } = new();

    [ObservableProperty]
    public partial PageViewModelBase? SelectedPage { get; set; }

    [ObservableProperty]
    public partial string ProjectPathDisplay { get; set; } = "未選擇專案";

    [ObservableProperty]
    public partial string GlobalStatus { get; set; } = "就緒";

    public MainViewModel() : this(new ServiceHost())
    {
    }

    public MainViewModel(ServiceHost services)
    {
        _services = services;
        Pages.Add(new SetupViewModel(services));
        Pages.Add(new ConfigViewModel(services));
        Pages.Add(new ThemeViewModel(services));
        Pages.Add(new ThemeConfigViewModel(services));
        Pages.Add(new ContentViewModel(services));
        Pages.Add(new DeployViewModel(services));

        UpdateProjectDisplay();
        _services.Project.ProjectChanged += _ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateProjectDisplay);
        _services.AppStatusChanged += message =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => GlobalStatus = message);

        SelectedPage = Pages[0];
    }

    partial void OnSelectedPageChanged(PageViewModelBase? value)
    {
        value?.OnNavigatedTo();
        if (value is not null)
            GlobalStatus = value.StatusMessage;
    }

    [RelayCommand]
    private void Navigate(PageViewModelBase page)
    {
        SelectedPage = page;
    }

    private void UpdateProjectDisplay()
    {
        ProjectPathDisplay = string.IsNullOrWhiteSpace(_services.Project.ProjectPath)
            ? "未選擇專案"
            : _services.Project.ProjectPath!;
    }

    public void Dispose()
    {
        try
        {
            _services.Server.Stop();
            foreach (var page in Pages)
            {
                if (page is IDisposable disposable)
                    disposable.Dispose();
            }
        }
        catch
        {
            // ignore shutdown errors
        }
    }
}
