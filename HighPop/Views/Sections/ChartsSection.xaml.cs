using Binding = System.Windows.Data.Binding;
using HighPop.ViewModels;
using OxyPlot.Wpf;

namespace HighPop.Views.Sections;

public partial class ChartsSection : System.Windows.Controls.UserControl
{
    public ChartsSection()
    {
        InitializeComponent();
        // Charts are only redrawn while someone is looking at them.
        Loaded += (_, _) =>
        {
            AttachPlots();
            if (DataContext is ServerViewModel vm) vm.ChartsVisible = true;
        };
        Unloaded += (_, _) =>
        {
            DetachPlots();
            if (DataContext is ServerViewModel vm) vm.ChartsVisible = false;
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ServerViewModel old) old.ChartsVisible = false;
            if (IsLoaded && e.NewValue is ServerViewModel now) now.ChartsVisible = true;
        };
    }

    // An OxyPlot PlotModel can belong to only one PlotView. A section that is
    // off screen (or a server view that was rebuilt, e.g. after an RDP
    // reconnect) must let go of the server's models, or the next PlotView that
    // binds them throws "This PlotModel is already in use".
    private void AttachPlots()
    {
        PerfView.SetBinding(PlotViewBase.ModelProperty, new Binding(nameof(ServerViewModel.PerfPlot)));
        OperationsView.SetBinding(PlotViewBase.ModelProperty, new Binding(nameof(ServerViewModel.OperationsPlot)));
    }

    private void DetachPlots()
    {
        PerfView.ClearValue(PlotViewBase.ModelProperty);
        OperationsView.ClearValue(PlotViewBase.ModelProperty);
    }
}
