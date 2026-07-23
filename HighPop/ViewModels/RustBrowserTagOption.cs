using CommunityToolkit.Mvvm.ComponentModel;

namespace HighPop.ViewModels;

public partial class RustBrowserTagOption : ObservableObject
{
    public string Value { get; }
    public string Label { get; }
    public string Group { get; }

    [ObservableProperty] private bool _isSelected;

    public event Action<RustBrowserTagOption>? SelectionChanged;

    public RustBrowserTagOption(string value, string label, string group = "")
    {
        Value = value;
        Label = label;
        Group = group;
    }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this);
}
