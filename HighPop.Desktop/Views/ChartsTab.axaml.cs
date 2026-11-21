using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using HighPop.ViewModels;
using OxyPlot.Avalonia;

namespace HighPop.Desktop.Views;

public partial class ChartsTab : UserControl
{
    public ChartsTab()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (VisualRoot != null && DataContext is ServerViewModel vm) vm.Monitoring.ChartsVisible = true;
        };
    }

    // An OxyPlot PlotModel can belong to only one PlotView, so the tab lets go of the
    // server's models whenever it leaves the screen.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PerfView.Bind(PlotView.ModelProperty, new ReflectionBinding("Monitoring.PerfPlot"));
        OperationsView.Bind(PlotView.ModelProperty, new ReflectionBinding("Monitoring.OperationsPlot"));
        if (DataContext is ServerViewModel vm) vm.Monitoring.ChartsVisible = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PerfView.ClearValue(PlotView.ModelProperty);
        OperationsView.ClearValue(PlotView.ModelProperty);
        if (DataContext is ServerViewModel vm) vm.HideCharts();
        base.OnDetachedFromVisualTree(e);
    }
}
