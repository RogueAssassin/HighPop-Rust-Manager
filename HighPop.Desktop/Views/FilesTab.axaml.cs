using Avalonia.Controls;
using Avalonia.Input;
using HighPop.ViewModels;

namespace HighPop.Desktop.Views;

public partial class FilesTab : UserControl
{
    public FilesTab() => InitializeComponent();

    private void FileList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ServerViewModel { FileBrowser.SelectedItem: { } item } vm)
            vm.FileBrowser.OpenCommand.Execute(item);
    }
}
