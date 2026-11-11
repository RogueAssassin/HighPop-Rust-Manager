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
/// The Rust page's server.cfg variable editor and server browser tags for one server. Owned by
/// <see cref="ServerViewModel"/>, which creates it the first time the page needs it. The owner
/// still reads server.cfg when it is created, so a server started without opening this page
/// uses the variables on disk; the server profile and Steam branch stay on the owner because
/// the Info page and framework detection use them too.
/// </summary>
public partial class ServerRustSettingsViewModel : BaseViewModel
{
    private readonly ServerViewModel      _owner;
    private readonly ServerManagerService _manager;

    internal ServerRustSettingsViewModel(ServerViewModel owner, ServerManagerService manager, string initialStatus)
    {
        _owner   = owner;
        _manager = manager;
        // Set the backing field so the status from the owner's startup read is shown as is.
        _rustVariableStatus = initialStatus;
        Server.RustServerVariables.CollectionChanged += (_, _) => TrackRustVariableChanges();
        TrackRustVariableChanges();
        InitializeRustBrowserTags();
    }

    public string ServerConfigPath => _owner.ServerConfigPath;

    /// <summary>The identity folder changed, so server.cfg now lives somewhere else.</summary>
    internal void NotifyIdentityChanged(string status)
    {
        OnPropertyChanged(nameof(ServerConfigPath));
        RustVariableStatus = status;
    }

    private GameServer Server    => _owner.Server;
    private bool       IsRunning => _owner.IsRunning;
    private bool       IsRust    => _owner.IsRust;
    private void AppendLog(string text, ConsoleMessageType type = ConsoleMessageType.Info) => _owner.AppendLog(text, type);
    private void AddActionLog(string action) => _owner.AddActionLog(action);

    public ObservableCollection<RustBrowserTagOption> RustBrowserTags { get; } = [];

    [ObservableProperty] private string _rustVariableStatus = "server.cfg has not been read yet.";
    [ObservableProperty] private int _rustVariablePendingCount;
    [ObservableProperty] private string _rustVariableFilter = string.Empty;
    public IEnumerable<RustServerVariable> FilteredRustServerVariables =>
        string.IsNullOrWhiteSpace(RustVariableFilter)
            ? Server.RustServerVariables
            : Server.RustServerVariables.Where(variable =>
                variable.Name.Contains(RustVariableFilter, StringComparison.OrdinalIgnoreCase)
                || variable.Description.Contains(RustVariableFilter, StringComparison.OrdinalIgnoreCase));
    partial void OnRustVariableFilterChanged(string value) =>
        OnPropertyChanged(nameof(FilteredRustServerVariables));
    private readonly HashSet<RustServerVariable> _trackedRustVariables = [];
    private bool _syncingRustTags;

    private void InitializeRustBrowserTags()
    {
        if (!IsRust) return;
        var definitions = new (string Value, string Label, string Group)[]
        {
            ("monthly", "Monthly", "wipe"),
            ("biweekly", "Biweekly", "wipe"),
            ("weekly", "Weekly", "wipe"),
            ("vanilla", "Vanilla", "difficulty"),
            ("hardcore", "Hardcore", "difficulty"),
            ("softcore", "Softcore", "difficulty"),
            ("pve", "PvE", ""),
            ("roleplay", "Roleplay", ""),
            ("creative", "Creative", ""),
            ("minigame", "Minigame", ""),
            ("training", "Combat Training", ""),
            ("battlefield", "Battlefield", ""),
            ("broyale", "Battle Royale", ""),
            ("builds", "Build Server", ""),
            ("tut", "Tutorial", ""),
            ("premium", "Premium", ""),
            ("NA", "North America", "region"),
            ("SA", "South America", "region"),
            ("EU", "Europe", "region"),
            ("WA", "West Asia", "region"),
            ("EA", "East Asia", "region"),
            ("OC", "Oceania", "region"),
            ("AF", "Africa", "region"),
        };
        var selected = Server.GameSpecificSettings
            .GetValueOrDefault("tags", "monthly,vanilla")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _syncingRustTags = true;
        foreach (var (value, label, group) in definitions)
        {
            var option = new RustBrowserTagOption(value, label, group)
            {
                IsSelected = selected.Contains(value),
            };
            option.SelectionChanged += OnRustBrowserTagChanged;
            RustBrowserTags.Add(option);
        }
        _syncingRustTags = false;
    }

    private void OnRustBrowserTagChanged(RustBrowserTagOption changed)
    {
        if (_syncingRustTags) return;
        _syncingRustTags = true;
        try
        {
            if (changed.IsSelected && !string.IsNullOrEmpty(changed.Group))
            {
                foreach (var option in RustBrowserTags.Where(o =>
                    o != changed && o.Group.Equals(changed.Group, StringComparison.Ordinal)))
                    option.IsSelected = false;
            }
            if (changed.IsSelected && RustBrowserTags.Count(o => o.IsSelected) > 4)
            {
                changed.IsSelected = false;
                AppendLog("[Rust] Rust displays at most four browser tags. Deselect one before adding another.",
                    ConsoleMessageType.Warning);
            }
            Server.GameSpecificSettings["tags"] = string.Join(",",
                RustBrowserTags.Where(o => o.IsSelected).Select(o => o.Value));
            AddActionLog($"Rust browser tags changed to {Server.GameSpecificSettings["tags"]}");
        }
        finally { _syncingRustTags = false; }
    }

    [RelayCommand]
    private void ReloadServerConfigVariables()
    {
        try
        {
            var count = RustPlugin.LoadServerConfigVariables(Server);
            TrackRustVariableChanges();
            _owner.NotifyServerChanged();
            RustVariableStatus = File.Exists(ServerConfigPath)
                ? $"Reloaded {count} active variable(s) from server.cfg."
                : "server.cfg does not exist yet. Save to create it.";
            AddActionLog($"Reloaded {count} Rust variable(s) from server.cfg");
        }
        catch (Exception ex)
        {
            RustVariableStatus = $"Could not read server.cfg: {ex.Message}";
            AppendLog($"[Rust config] {RustVariableStatus}", ConsoleMessageType.Error);
        }
    }

    [RelayCommand]
    private void SaveServerConfigVariables() => TrySaveServerConfigVariables();

    private bool TrySaveServerConfigVariables()
    {
        var validation = RustPlugin.ValidateServerVariables(Server);
        if (validation != null)
        {
            RustVariableStatus = validation;
            WpfMsgBox.Show(validation, "Invalid server.cfg variable",
                WpfMsgBoxButton.OK, WpfMsgBoxImage.Warning);
            return false;
        }

        try
        {
            var migrated = RustPlugin.WriteManagedServerConfig(Server);
            TrackRustVariableChanges();
            _owner.NotifyServerChanged();
            RustVariableStatus = migrated > 0
                ? $"Saved server.cfg and migrated {migrated} legacy variable(s) from serverauto.cfg."
                : "Saved active variables to server.cfg. Changes apply on the next Rust start.";
            AddActionLog(migrated > 0
                ? $"Saved server.cfg and migrated {migrated} legacy serverauto.cfg variable(s)"
                : "Saved Rust custom variables to server.cfg");
            return true;
        }
        catch (Exception ex)
        {
            RustVariableStatus = $"Could not save server.cfg: {ex.Message}";
            WpfMsgBox.Show(RustVariableStatus, "server.cfg save failed",
                WpfMsgBoxButton.OK, WpfMsgBoxImage.Error);
            return false;
        }
    }

    [RelayCommand]
    private void AddRustServerVariable()
    {
        Server.RustServerVariables.Add(new RustServerVariable
        {
            Enabled = false,
            Name = string.Empty,
            Value = string.Empty,
            Description = "Custom Rust console variable",
        });
        _owner.NotifyServerChanged();
        RustVariableStatus = "Unsaved variable row added.";
        AddActionLog("Added a server.cfg variable row");
    }

    [RelayCommand]
    private async Task ApplyRustVariablesLiveAsync()
    {
        var validation = RustPlugin.ValidateServerVariables(Server);
        if (validation != null)
        {
            RustVariableStatus = validation;
            return;
        }

        var changed = Server.RustServerVariables
            .Where(variable => variable.Enabled && variable.HasUnsavedChanges)
            .Select(variable => (variable.Name, variable.Value))
            .ToList();
        if (!TrySaveServerConfigVariables()) return;
        if (!IsRunning)
        {
            RustVariableStatus += " Rust is stopped, so the saved values will apply at next start.";
            return;
        }

        foreach (var (name, value) in changed)
            await _manager.SendCommandAsync(Server.Id, $"{name} \"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");

        RustVariableStatus = changed.Count == 0
            ? "server.cfg is already synchronized; there were no live changes to send."
            : $"Saved server.cfg and applied {changed.Count} changed value(s) to the running Rust console.";
        AddActionLog($"Applied {changed.Count} server.cfg variable change(s) live");
    }

    private void TrackRustVariableChanges()
    {
        foreach (var variable in Server.RustServerVariables.Where(v => _trackedRustVariables.Add(v)))
            variable.PropertyChanged += (_, _) => RefreshRustVariableState();
        RefreshRustVariableState();
        OnPropertyChanged(nameof(FilteredRustServerVariables));
    }

    private void RefreshRustVariableState()
    {
        RustVariablePendingCount = Server.RustServerVariables.Count(variable => variable.HasUnsavedChanges);
        OnPropertyChanged(nameof(FilteredRustServerVariables));
    }

    [RelayCommand]
    private void RemoveRustServerVariable(RustServerVariable? variable)
    {
        if (variable == null) return;
        if (variable.LoadedFromServerConfig)
        {
            variable.Enabled = false;
            try { RustPlugin.WriteManagedServerConfig(Server); }
            catch (Exception ex)
            {
                RustVariableStatus = $"Could not remove {variable.Name} from server.cfg: {ex.Message}";
                return;
            }
        }
        Server.RustServerVariables.Remove(variable);
        _owner.NotifyServerChanged();
        RustVariableStatus = $"Removed {variable.Name}.";
        AddActionLog($"Removed server.cfg variable: {variable.Name}");
    }
}
