using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public abstract partial class PageViewModelBase : ViewModelBase
{
    public abstract string Title { get; }
    public abstract string Icon { get; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public virtual void OnNavigatedTo() { }

    protected bool RequireProject(out string sitePath)
    {
        sitePath = ServiceHost.Current?.Project.ProjectPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sitePath)
            || !Directory.Exists(sitePath)
            || ServiceHost.Current?.Project.IsHexoProject != true)
        {
            StatusMessage = "請先在「環境設定」開啟、建立，或從遠端 Git 複製 Hexo 專案。";
            return false;
        }

        return true;
    }
}



