using CommunityToolkit.Mvvm.ComponentModel;

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
}
