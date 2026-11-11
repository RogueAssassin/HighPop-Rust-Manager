using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighPop.Games;
using HighPop.Models;
using HighPop.Services;

namespace HighPop.ViewModels;

/// <summary>
/// The Mods page of one server: Oxide/Carbon and RogueRust installs, framework folder
/// shortcuts and the RogueRust plugin catalog. Owned by <see cref="ServerViewModel"/>, which
/// creates it the first time the page needs it. Framework detection (installed frameworks
/// and plugins) stays on the owner because the Info page and server start-up use it too.
/// </summary>
public partial class ServerModsViewModel : BaseViewModel
{
    private readonly ServerViewModel               _owner;
    private readonly ModManagerService             _mods;
    private readonly RogueRustPluginCatalogService _pluginCatalog;
    private readonly ServerManagerService          _manager;

    internal ServerModsViewModel(ServerViewModel owner, ModManagerService mods,
        RogueRustPluginCatalogService pluginCatalog, ServerManagerService manager)
    {
        _owner         = owner;
        _mods          = mods;
        _pluginCatalog = pluginCatalog;
        _manager       = manager;
        Refresh();
    }

    private GameServer   Server            => _owner.Server;
    private IGamePlugin? Plugin            => _owner.Plugin;
    private bool         IsRunning         => _owner.IsRunning;
    private bool         IsStopped         => _owner.IsStopped;
    private bool         IsOxideInstalled  => _owner.IsOxideInstalled;
    private bool         IsCarbonInstalled => _owner.IsCarbonInstalled;
    private void AppendLog(string text, ConsoleMessageType type = ConsoleMessageType.Info) => _owner.AppendLog(text, type);
    private void AddActionLog(string action) => _owner.AddActionLog(action);
    private void RefreshInstalledPlugins() => _owner.RefreshInstalledPlugins();
    private static void OpenTrustedUrl(string? url, string host) => ServerViewModel.OpenTrustedUrl(url, host);

    [ObservableProperty] private string _modStatusText   = string.Empty;
    [ObservableProperty] private bool   _modBusy;
    [ObservableProperty] private string _rogueRustStatus = "Not installed";

    public IReadOnlyList<string> RogueRustChannelOptions { get; } = ["Stable", "Testing"];

    public bool CanInstallOxide => IsStopped && !ModBusy && !IsCarbonInstalled;
    public bool CanInstallCarbon => IsStopped && !ModBusy && !IsOxideInstalled;

    partial void OnModBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallOxide));
        OnPropertyChanged(nameof(CanInstallCarbon));
    }

    public string RogueRustChannel
    {
        get => ModManagerService.NormalizeRogueRustChannel(Server.RogueRustChannel);
        set
        {
            var channel = ModManagerService.NormalizeRogueRustChannel(value);
            if (RogueRustChannel == channel) return;
            Server.RogueRustChannel = channel;
            OnPropertyChanged();
            RogueRustStatus = ModManagerService.GetInstalledRogueRustVersion(Server.InstallPath) is { Length: > 0 } version
                ? $"Installed {version} · {channel} channel"
                : $"Not installed · {channel} channel";
            AddActionLog($"RogueRust channel changed to {channel}");
        }
    }

    // ── Mod manager ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task InstallOxideAsync()
    {
        if (Plugin == null || !Plugin.SupportsOxide) return;
        if (IsRunning)
        {
            ModStatusText = "Stop the server before installing or updating Oxide.";
            return;
        }
        if (Plugin.GameId == "rust" && ModManagerService.IsCarbonInstalled(Server.InstallPath))
        {
            ModStatusText = "Carbon is already installed. Use one Rust mod framework at a time.";
            return;
        }
        ModBusy = true;
        try
        {
            var progress = new Progress<(int pct, string msg)>(x =>
                UiDispatcher.Post(() => ModStatusText = $"[{x.pct}%] {x.msg}"));

            await _mods.InstallOxideAsync(Plugin, Server.InstallPath, progress);
            AppendLog("[Mods] ✅ Oxide installed successfully.", ConsoleMessageType.System);
            AddActionLog("Oxide / uMod installed or updated");
            RefreshInstalledPlugins();
        }
        catch (Exception ex)
        {
            AppendLog($"[Mods] ❌ {ex.Message}", ConsoleMessageType.Error);
            UiDispatcher.Post(() => ModStatusText = $"❌ {ex.Message}");
        }
        finally { ModBusy = false; }
    }

    [RelayCommand]
    private async Task InstallCarbonAsync()
    {
        if (Plugin?.GameId != "rust") return;
        if (IsRunning)
        {
            ModStatusText = "Stop the Rust server before installing or updating Carbon.";
            return;
        }
        if (ModManagerService.GetInstalledOxideVersion(Server.InstallPath) != null)
        {
            ModStatusText = "Oxide is already installed. Use one Rust mod framework at a time.";
            return;
        }

        ModBusy = true;
        try
        {
            var progress = new Progress<(int pct, string msg)>(x =>
                UiDispatcher.Post(() => ModStatusText = $"[{x.pct}%] {x.msg}"));
            await _mods.InstallCarbonAsync(Server.InstallPath, progress);
            AppendLog("[Mods] ✅ Carbon installed successfully.", ConsoleMessageType.System);
            AddActionLog("Carbon installed or updated");
            RefreshInstalledPlugins();
        }
        catch (Exception ex)
        {
            AppendLog($"[Mods] ❌ {ex.Message}", ConsoleMessageType.Error);
            UiDispatcher.Post(() => ModStatusText = $"❌ {ex.Message}");
        }
        finally { ModBusy = false; }
    }

    [RelayCommand]
    private async Task InstallRogueRustAsync()
    {
        if (Plugin?.GameId != "rust") return;
        if (IsRunning)
        {
            ModStatusText = "Stop the Rust server before installing or updating RogueRust.";
            return;
        }
        if (ModManagerService.GetRogueRustInstallTargets(Server.InstallPath).Count == 0)
        {
            ModStatusText = "RogueRust requires Oxide/uMod or Carbon. Install a mod framework first.";
            return;
        }

        ModBusy = true;
        try
        {
            var progress = new Progress<(int pct, string msg)>(x =>
                UiDispatcher.Post(() => ModStatusText = $"[{x.pct}%] {x.msg}"));
            var version = await _mods.InstallRogueRustAsync(Server.InstallPath, RogueRustChannel, progress);
            var frameworks = string.Join(" + ", ModManagerService.GetRogueRustInstallTargets(Server.InstallPath)
                .Select(target => target.Framework));
            RogueRustStatus = $"Installed {version} · {RogueRustChannel} · {frameworks}";
            AppendLog($"[Mods] ✅ RogueRust {version} ({RogueRustChannel}) installed for {frameworks} and SHA-256 verified.", ConsoleMessageType.System);
            AddActionLog($"RogueRust {version} ({RogueRustChannel}) installed or updated");
            RefreshInstalledPlugins();
        }
        catch (Exception ex)
        {
            AppendLog($"[Mods] ❌ RogueRust: {ex.Message}", ConsoleMessageType.Error);
            ModStatusText = $"❌ {ex.Message}";
        }
        finally { ModBusy = false; }
    }

    [RelayCommand]
    private async Task CheckRogueRustHealthAsync()
    {
        if (!IsRunning)
        {
            ModStatusText = "Start Rust before sending RogueRust diagnostics.";
            return;
        }
        await _manager.SendCommandAsync(Server.Id, "roguerust.version");
        await _manager.SendCommandAsync(Server.Id, "roguerust.readiness");
        ModStatusText = "RogueRust version and readiness commands sent. Review their responses in the Console tab.";
        AddActionLog("Requested RogueRust readiness diagnostics");
    }

    [RelayCommand]
    private void OpenCarbonPluginFolder()
    {
        var paths = ModManagerService.GetActiveFrameworkPaths(Server.InstallPath);
        if (paths?.Framework != "Carbon"
            || !ModManagerService.OpenExistingFolder(paths.PluginDirectory))
            ModStatusText = "Carbon is not active for this server, or its plugins folder does not exist yet.";
    }

    [RelayCommand]
    private void OpenPluginFolder()
    {
        var paths = ModManagerService.GetActiveFrameworkPaths(Server.InstallPath);
        if (paths?.Framework != "Oxide / uMod"
            || !ModManagerService.OpenExistingFolder(paths.PluginDirectory))
            ModStatusText = "Oxide/uMod is not active for this server, or its plugins folder does not exist yet.";
    }

    [RelayCommand]
    private void OpenActiveFrameworkFolder()
    {
        var paths = ModManagerService.GetActiveFrameworkPaths(Server.InstallPath);
        if (paths == null || !ModManagerService.OpenExistingFolder(paths.RootDirectory))
            ModStatusText = "Install exactly one supported mod framework before opening its folder.";
    }

    [RelayCommand]
    private void OpenActivePluginFolder()
    {
        var paths = ModManagerService.GetActiveFrameworkPaths(Server.InstallPath);
        if (paths == null || !ModManagerService.OpenExistingFolder(paths.PluginDirectory))
            ModStatusText = "The active framework plugins folder does not exist yet.";
    }

    [RelayCommand]
    private void OpenActiveModConfigFolder()
    {
        var paths = ModManagerService.GetActiveFrameworkPaths(Server.InstallPath);
        if (paths == null || !ModManagerService.OpenExistingFolder(paths.ConfigDirectory))
            ModStatusText = "The active framework configuration folder does not exist yet.";
    }

    // ── RogueRust plugin catalog (plugins.roguegaming.com.au) ───────────────

    private IReadOnlyList<RogueRustCatalogPlugin> _catalogSource = [];
    [ObservableProperty] private List<RogueRustCatalogPlugin> _catalogPlugins = [];
    [ObservableProperty] private bool _pluginCatalogLoading;
    [ObservableProperty] private string _pluginCatalogStatus =
        $"Load the catalog to browse RogueRust plugins from {RogueGamingEndpoints.PluginsHost}.";

    [RelayCommand]
    private async Task RefreshPluginCatalogAsync()
    {
        if (PluginCatalogLoading) return;
        PluginCatalogLoading = true;
        PluginCatalogStatus = $"Downloading the plugin catalog from {RogueGamingEndpoints.PluginsHost}…";
        try
        {
            _catalogSource = await _pluginCatalog.LoadAsync(refresh: _catalogSource.Count > 0);
            CatalogPlugins = RogueRustPluginCatalogService.WithInstalledVersions(_catalogSource, Server.InstallPath);
            var updates = CatalogPlugins.Count(plugin => plugin.UpdateAvailable);
            var installed = CatalogPlugins.Count(plugin => plugin.IsInstalled);
            PluginCatalogStatus = $"{CatalogPlugins.Count} plugins listed · {installed} installed · " +
                                  (updates == 0 ? "all up to date" : $"{updates} update(s) available");
        }
        catch (Exception ex)
        {
            PluginCatalogStatus = ex.Message;
        }
        finally { PluginCatalogLoading = false; }
    }

    [RelayCommand]
    private async Task InstallCatalogPluginAsync(RogueRustCatalogPlugin? plugin)
    {
        if (plugin == null || Plugin?.GameId != "rust" || ModBusy) return;
        var paths = ModManagerService.GetActiveFrameworkPaths(Server.InstallPath);
        if (paths == null)
        {
            ModStatusText = "Install exactly one mod framework (Oxide/uMod or Carbon) before adding RogueRust plugins.";
            return;
        }

        ModBusy = true;
        try
        {
            ModStatusText = $"Downloading {plugin.FileName} from {RogueGamingEndpoints.PluginsHost}…";
            var version = await _pluginCatalog.InstallAsync(plugin, Server.InstallPath);
            var requiresExtension = ModManagerService.GetInstalledRogueRustVersion(Server.InstallPath) == null;
            ModStatusText = $"✅ {plugin.Name} v{version} installed to {paths.Framework} plugins." +
                            (IsRunning ? " The framework hot-loads it; no restart is needed." : string.Empty) +
                            (requiresExtension ? " Note: RogueRust plugins need the RogueRust extension installed." : string.Empty);
            AppendLog($"[Mods] ✅ {plugin.FileName} v{version} installed from {RogueGamingEndpoints.PluginsHost}.",
                ConsoleMessageType.System);
            AddActionLog($"RogueRust plugin {plugin.Name} v{version} installed or updated");
            RefreshInstalledPlugins();
        }
        catch (Exception ex)
        {
            AppendLog($"[Mods] ❌ {plugin.FileName}: {ex.Message}", ConsoleMessageType.Error);
            ModStatusText = $"❌ {ex.Message}";
        }
        finally { ModBusy = false; }
    }

    [RelayCommand]
    private void OpenCatalogPluginDocs(RogueRustCatalogPlugin? plugin)
        => OpenTrustedUrl(plugin?.DocumentationUrl, RogueGamingEndpoints.PluginsHost);

    [RelayCommand]
    private void OpenPluginCatalogSite()
        => OpenTrustedUrl(RogueGamingEndpoints.PluginsUrl, RogueGamingEndpoints.PluginsHost);

    [RelayCommand]
    private void OpenActiveExtensionFolder()
    {
        var paths = ModManagerService.GetActiveFrameworkPaths(Server.InstallPath);
        if (paths == null || !ModManagerService.OpenExistingFolder(paths.ExtensionDirectory))
            ModStatusText = "The active framework extension folder does not exist yet.";
    }

    /// <summary>Updates the RogueRust status, catalog install states and install buttons after a rescan.</summary>
    internal void Refresh()
    {
    RogueRustStatus = ModManagerService.GetInstalledRogueRustVersion(Server.InstallPath) is { Length: > 0 } version
        ? $"Installed {version} · {RogueRustChannel} · {string.Join(" + ", ModManagerService.GetRogueRustInstallTargets(Server.InstallPath).Select(target => target.Framework))}"
        : $"Not installed · {RogueRustChannel} channel";
    if (_catalogSource.Count > 0)
        CatalogPlugins = RogueRustPluginCatalogService.WithInstalledVersions(_catalogSource, Server.InstallPath);
        RaiseCanInstallChanged();
    }

    internal void RaiseCanInstallChanged()
    {
        OnPropertyChanged(nameof(CanInstallOxide));
        OnPropertyChanged(nameof(CanInstallCarbon));
    }
}
