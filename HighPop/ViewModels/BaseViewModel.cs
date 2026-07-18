using CommunityToolkit.Mvvm.ComponentModel;
using HighPop.Services;

namespace HighPop.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    public LocalizationService Loc => LocalizationService.Instance;
}
