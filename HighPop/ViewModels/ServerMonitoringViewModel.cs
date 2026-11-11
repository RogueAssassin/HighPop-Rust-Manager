using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighPop.Games;
using HighPop.Models;
using HighPop.Services;

namespace HighPop.ViewModels;

/// <summary>
/// The Monitoring charts of one server: the CPU/RAM and network/player PlotModels, the time
/// range and the chart summary. Owned by <see cref="ServerViewModel"/>, which creates it the
/// first time the Charts page needs it. The owner's perf timer keeps sampling (it also feeds
/// the sidebar CPU and RAM) and tells this view-model when a new sample is recorded.
/// </summary>
public partial class ServerMonitoringViewModel : BaseViewModel
{
    private readonly ServerViewModel    _owner;
    private readonly PerfHistoryService _perfHistory;

    internal ServerMonitoringViewModel(ServerViewModel owner, PerfHistoryService perfHistory)
    {
        _owner       = owner;
        _perfHistory = perfHistory;
    }

    private GameServer Server            => _owner.Server;
    private bool       IsWorkspaceActive => _owner.IsWorkspaceActive;

    /// <summary>Set by the Charts section while it is on screen; charts are not redrawn otherwise.</summary>
    [ObservableProperty] private bool _chartsVisible;
    [ObservableProperty] private OxyPlot.PlotModel _perfPlot = CreateEmptyPlot();
    [ObservableProperty] private OxyPlot.PlotModel _operationsPlot = CreateEmptyPlot();
    private OxyPlot.PlotModel?          _perfModel;
    private OxyPlot.PlotModel?          _operationsModel;
    private OxyPlot.Series.LineSeries?  _cpuSeries;
    private OxyPlot.Series.LineSeries?  _memSeries;
    private OxyPlot.Series.LineSeries?  _netInSeries;
    private OxyPlot.Series.LineSeries?  _netOutSeries;
    private OxyPlot.Series.LineSeries?  _playersSeries;
    [ObservableProperty] private string _chartSummary = "No samples yet";
    [ObservableProperty] private int _perfRangeMinutes = 15;
    partial void OnPerfRangeMinutesChanged(int _) => UpdatePerfChart();
    public IReadOnlyList<int> PerfRangeOptions { get; } = [5, 15, 30, 60];

    private int _perfChartRenderPending;
    private long _nextPerfChartRefreshUtcTicks;

    partial void OnChartsVisibleChanged(bool value)
    {
        if (value) UpdatePerfChart();
    }

    internal void OnWorkspaceActivated()
    {
        if (ChartsVisible) UpdatePerfChart();
    }

    /// <summary>Called by the owner's perf timer after each sample. Redraws at most every 5 s, and only while the charts are on screen.</summary>
    internal void OnSampleRecorded()
    {
        if (IsWorkspaceActive && ChartsVisible)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var dueTicks = Interlocked.Read(ref _nextPerfChartRefreshUtcTicks);
            if (nowTicks >= dueTicks
                && Interlocked.CompareExchange(
                    ref _nextPerfChartRefreshUtcTicks,
                    DateTime.UtcNow.AddSeconds(5).Ticks,
                    dueTicks) == dueTicks)
                UpdatePerfChart();
        }
    }

    private void EnsurePerfModel()
    {
        if (_perfModel != null) return;
        _cpuSeries = new OxyPlot.Series.LineSeries
        {
            Title = "CPU %", Color = OxyPlot.OxyColor.Parse("#A855F7"),
            StrokeThickness = 2, MarkerType = OxyPlot.MarkerType.None,
        };
        _memSeries = new OxyPlot.Series.LineSeries
        {
            Title = "RAM MB", Color = OxyPlot.OxyColor.Parse("#3FB950"),
            StrokeThickness = 2, MarkerType = OxyPlot.MarkerType.None, YAxisKey = "mem",
        };
        _perfModel = new OxyPlot.PlotModel
        {
            Background          = OxyPlot.OxyColor.FromArgb(0, 0, 0, 0),
            PlotAreaBorderColor = OxyPlot.OxyColor.Parse("#30363d"),
            TextColor           = OxyPlot.OxyColor.Parse("#8b949e"),
        };
        _perfModel.Axes.Add(new OxyPlot.Axes.DateTimeAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Bottom, StringFormat = "HH:mm:ss",
            AxislineColor = OxyPlot.OxyColor.Parse("#30363d"), TicklineColor = OxyPlot.OxyColor.Parse("#30363d"),
            MajorGridlineColor = OxyPlot.OxyColor.Parse("#21262d"), MajorGridlineStyle = OxyPlot.LineStyle.Solid,
        });
        _perfModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Left, Title = "CPU %", Minimum = 0, Maximum = 100,
            MajorGridlineColor = OxyPlot.OxyColor.Parse("#21262d"), MajorGridlineStyle = OxyPlot.LineStyle.Solid,
        });
        _perfModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Key = "mem", Position = OxyPlot.Axes.AxisPosition.Right, Title = "RAM MB", Minimum = 0,
            MaximumPadding = 0.15, // headroom so peak usage doesn't look pinned to the top edge
        });
        _perfModel.Series.Add(_cpuSeries);
        _perfModel.Series.Add(_memSeries);

        _netInSeries = new OxyPlot.Series.LineSeries
        {
            Title = "Download KB/s", Color = OxyPlot.OxyColor.Parse("#38C976"),
            StrokeThickness = 2, MarkerType = OxyPlot.MarkerType.None,
        };
        _netOutSeries = new OxyPlot.Series.LineSeries
        {
            Title = "Upload KB/s", Color = OxyPlot.OxyColor.Parse("#A855F7"),
            StrokeThickness = 2, MarkerType = OxyPlot.MarkerType.None,
        };
        _playersSeries = new OxyPlot.Series.LineSeries
        {
            Title = "Players", Color = OxyPlot.OxyColor.Parse("#FFB020"),
            StrokeThickness = 2, MarkerType = OxyPlot.MarkerType.None, YAxisKey = "players",
        };
        _operationsModel = new OxyPlot.PlotModel
        {
            Background = OxyPlot.OxyColor.FromArgb(0, 0, 0, 0),
            PlotAreaBorderColor = OxyPlot.OxyColor.Parse("#30363d"),
            TextColor = OxyPlot.OxyColor.Parse("#8b949e"),
        };
        _operationsModel.Axes.Add(new OxyPlot.Axes.DateTimeAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Bottom, StringFormat = "HH:mm:ss",
            AxislineColor = OxyPlot.OxyColor.Parse("#30363d"), TicklineColor = OxyPlot.OxyColor.Parse("#30363d"),
            MajorGridlineColor = OxyPlot.OxyColor.Parse("#21262d"), MajorGridlineStyle = OxyPlot.LineStyle.Solid,
        });
        _operationsModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Left, Title = "Network KB/s", Minimum = 0,
            MajorGridlineColor = OxyPlot.OxyColor.Parse("#21262d"), MajorGridlineStyle = OxyPlot.LineStyle.Solid,
        });
        _operationsModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Key = "players", Position = OxyPlot.Axes.AxisPosition.Right, Title = "Players", Minimum = 0,
            MinimumMajorStep = 1, MajorStep = 10,
        });
        _operationsModel.Series.Add(_netInSeries);
        _operationsModel.Series.Add(_netOutSeries);
        _operationsModel.Series.Add(_playersSeries);

        UiDispatcher.Post(() =>
        {
            PerfPlot = _perfModel;
            OperationsPlot = _operationsModel;
        });
    }

    internal void UpdatePerfChart()
    {
        var cutoff  = DateTime.Now.AddMinutes(-PerfRangeMinutes);
        var samples = _perfHistory.Get(Server.Id).Where(s => s.Time >= cutoff).ToList();
        EnsurePerfModel();

        // Coalesce timer requests and mutate series only on the UI thread. A busy Rust process
        // can otherwise make the sampler synchronously wait behind expensive chart renders.
        if (Interlocked.Exchange(ref _perfChartRenderPending, 1) == 1) return;
        WpfApplication.Current?.Dispatcher?.BeginInvoke(() =>
        {
            try
            {
                _cpuSeries!.Points.Clear();
                _memSeries!.Points.Clear();
                _netInSeries!.Points.Clear();
                _netOutSeries!.Points.Clear();
                _playersSeries!.Points.Clear();
                foreach (var s in samples)
                {
                    var x = OxyPlot.Axes.DateTimeAxis.ToDouble(s.Time);
                    _cpuSeries.Points.Add(new OxyPlot.DataPoint(x, s.Cpu));
                    _memSeries.Points.Add(new OxyPlot.DataPoint(x, s.MemMb));
                    _netInSeries.Points.Add(new OxyPlot.DataPoint(x, s.NetworkInKbps));
                    _netOutSeries.Points.Add(new OxyPlot.DataPoint(x, s.NetworkOutKbps));
                    _playersSeries.Points.Add(new OxyPlot.DataPoint(x, s.Players));
                }
                _perfModel!.InvalidatePlot(true);
                _operationsModel!.InvalidatePlot(true);
                ChartSummary = samples.Count == 0
                    ? "No samples in this range."
                    : $"Peak CPU {samples.Max(s => s.Cpu):F1}%  •  Peak RAM {samples.Max(s => s.MemMb):N0} MB  •  " +
                      $"Peak players {samples.Max(s => s.Players)}  •  Peak network " +
                      $"{samples.Max(s => s.NetworkInKbps + s.NetworkOutKbps):F1} KB/s";
            }
            finally { Interlocked.Exchange(ref _perfChartRenderPending, 0); }
        });
    }

    private static OxyPlot.PlotModel CreateEmptyPlot()
    {
        var m = new OxyPlot.PlotModel { Background = OxyPlot.OxyColor.FromArgb(0, 0, 0, 0) };
        m.Axes.Add(new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Left });
        m.Axes.Add(new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Bottom });
        return m;
    }
}
