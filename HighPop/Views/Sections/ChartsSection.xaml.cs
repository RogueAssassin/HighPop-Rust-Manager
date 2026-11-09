using HighPop.ViewModels;

namespace HighPop.Views.Sections;

public partial class ChartsSection : System.Windows.Controls.UserControl
{
    public ChartsSection()
    {
        InitializeComponent();
        // Charts are only redrawn while someone is looking at them.
        Loaded += (_, _) => { if (DataContext is ServerViewModel vm) vm.ChartsVisible = true; };
        Unloaded += (_, _) => { if (DataContext is ServerViewModel vm) vm.ChartsVisible = false; };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ServerViewModel old) old.ChartsVisible = false;
            if (IsLoaded && e.NewValue is ServerViewModel now) now.ChartsVisible = true;
        };
    }
}
