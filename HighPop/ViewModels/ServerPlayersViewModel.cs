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
/// The admin actions on the Players page of one server: moderation (kick, ban, unban,
/// whitelist, staff notes, bulk actions) and the Rust player tools (give menu, player actions,
/// teleport, roles, loadouts). Owned by <see cref="ServerViewModel"/>, which creates it the
/// first time the page needs it. The live player list, history, stats, chat and reports stay
/// on the owner because WebRCON polling and events feed them.
/// </summary>
public partial class ServerPlayersViewModel : BaseViewModel
{
    private readonly ServerViewModel       _owner;
    private readonly ServerManagerService  _manager;
    private readonly RustModerationService _moderation;
    private readonly GroupBanListService   _groupBans;

    internal ServerPlayersViewModel(ServerViewModel owner, ServerManagerService manager,
        RustModerationService moderation, GroupBanListService groupBans)
    {
        _owner      = owner;
        _manager    = manager;
        _moderation = moderation;
        _groupBans  = groupBans;
    }

    public bool IsRunning         => _owner.IsRunning;
    public bool HasPlayerCommands => _owner.HasPlayerCommands;
    internal void NotifyRunningChanged() => OnPropertyChanged(nameof(IsRunning));

    private GameServer   Server => _owner.Server;
    private IGamePlugin? Plugin => _owner.Plugin;
    private List<Models.OnlinePlayer> OnlinePlayers
    {
        get => _owner.OnlinePlayers;
        set => _owner.OnlinePlayers = value;
    }
    private void AppendLog(string text, ConsoleMessageType type = ConsoleMessageType.Info) => _owner.AppendLog(text, type);
    private void AddActionLog(string action) => _owner.AddActionLog(action);
    private Task SendRconOrConsole(string cmd) => _owner.SendRconOrConsole(cmd);
    private Task FetchOnlinePlayersAsync() => _owner.FetchOnlinePlayersAsync();
    private Task<RconService?> EnsureRconForCommandAsync() => _owner.EnsureRconForCommandAsync();
    private void RefreshModerationProfiles() => _owner.RefreshModerationProfiles();
    private static void OpenTrustedUrl(string? url, string host) => ServerViewModel.OpenTrustedUrl(url, host);

    [ObservableProperty] private string _kickReason  = string.Empty;
    [ObservableProperty] private string _banReason   = string.Empty;
    [ObservableProperty] private string _banDuration = string.Empty;
    [ObservableProperty] private string _playerNote  = string.Empty;
    [ObservableProperty] private string _moderationStatus = string.Empty;

    [ObservableProperty] private string _playerToolOutput = string.Empty;
    [ObservableProperty] private string _giveCategory = RustItemCatalog.AllCategories;
    [ObservableProperty] private string _giveSearch = string.Empty;
    [ObservableProperty] private IReadOnlyList<RustCatalogItem> _giveItems = RustItemCatalog.Items;
    [ObservableProperty] private RustCatalogItem? _selectedGiveItem;
    [ObservableProperty] private string _giveShortName = string.Empty;
    [ObservableProperty] private string _giveAmount = "1";
    [ObservableProperty] private string _giveSkin = string.Empty;
    [ObservableProperty] private string _teleportDestination = string.Empty;
    [ObservableProperty] private string _loadoutName = string.Empty;

    public IReadOnlyList<string> GiveCategories => RustItemCatalog.Categories;

    partial void OnGiveCategoryChanged(string value) => FilterGiveItems();
    partial void OnGiveSearchChanged(string value) => FilterGiveItems();
    partial void OnSelectedGiveItemChanged(RustCatalogItem? value)
    {
        if (value == null) return;
        GiveShortName = value.ShortName;
        GiveAmount = value.DefaultAmount.ToString();
    }

    private void FilterGiveItems() => GiveItems = RustItemCatalog.Filter(GiveCategory, GiveSearch);

    /// <summary>The player the tools act on: a 17-digit Steam64 ID when known, else the typed name.</summary>
    private string ToolTarget()
    {
        var (steamId, name) = ResolvePlayer(PlayerInput);
        return RustModerationCommands.IsSteamId(steamId) ? steamId : name;
    }

    private string ToolTargetName() => ResolvePlayer(PlayerInput).PlayerName;

    /// <summary>Sends a player-tool command over WebRCON and shows Rust's reply in the tools panel.</summary>
    private async Task SendPlayerToolAsync(string command, string description)
    {
        if (!IsRunning)
        {
            PlayerToolOutput = "Start the server first.";
            return;
        }
        AppendLog("> " + command, ConsoleMessageType.Input);
        AddActionLog(description);
        var rcon = await EnsureRconForCommandAsync();
        if (rcon == null)
        {
            await _manager.SendCommandAsync(Server.Id, command);
            UiDispatcher.Post(() => PlayerToolOutput =
                $"{description}: sent without WebRCON, so Rust's reply is not available. Connect WebRCON on the Console tab.");
            return;
        }

        var reply = await rcon.SendCommandAsync(command);
        if (reply.Length > 0)
            AppendLog(reply, reply.StartsWith("[RCON]", StringComparison.Ordinal)
                ? ConsoleMessageType.Warning
                : ConsoleMessageType.Info);
        UiDispatcher.Post(() => PlayerToolOutput = reply.Length > 0
            ? $"{description}: {UiTextSanitizer.Normalize(reply, 2000)}"
            : $"{description}: done at {DateTime.Now:HH:mm:ss}.");
    }

    private static bool Confirm(string title, string message) =>
        System.Windows.MessageBox.Show(message, "HighPop — " + title,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    private bool TryReadGive(out int amount, out ulong skin)
    {
        skin = 0;
        if (!int.TryParse(GiveAmount, out amount))
        {
            PlayerToolOutput = "Amount must be a whole number.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(GiveSkin) && !ulong.TryParse(GiveSkin.Trim(), out skin))
        {
            PlayerToolOutput = "Skin must be a Workshop skin ID (numbers only), or blank.";
            return false;
        }
        return true;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task GiveItemAsync()
    {
        if (!TryReadGive(out var amount, out var skin)) return;
        if (!RustPlayerCommands.TryGive(ToolTarget(), GiveShortName, amount, skin, out var command, out var error))
        {
            PlayerToolOutput = error;
            return;
        }
        var label = RustItemCatalog.Find(GiveShortName)?.DisplayName ?? GiveShortName.Trim();
        await SendPlayerToolAsync(command, $"Gave {amount:N0} × {label} to {ToolTargetName()}");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task GiveItemToAllAsync()
    {
        if (!TryReadGive(out var amount, out _)) return;
        if (!RustPlayerCommands.TryGiveAll(GiveShortName, amount, out var command, out var error))
        {
            PlayerToolOutput = error;
            return;
        }
        var label = RustItemCatalog.Find(GiveShortName)?.DisplayName ?? GiveShortName.Trim();
        if (!Confirm("Give to everyone",
                $"Give {amount:N0} × {label} to all {OnlinePlayers.Count} connected player(s)?\n\nRust announces this in chat."))
            return;
        await SendPlayerToolAsync(command, $"Gave {amount:N0} × {label} to everyone");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayerActionAsync(string? actionName)
    {
        if (!Enum.TryParse<RustPlayerAction>(actionName, out var action)) return;
        if (!RustPlayerCommands.TryPlayerAction(action, ToolTarget(), out var command, out var error))
        {
            PlayerToolOutput = error;
            return;
        }
        var name = ToolTargetName();
        string? warning = action switch
        {
            RustPlayerAction.Kill => $"Kill {name}? They drop their inventory where they stand.",
            RustPlayerAction.ClearInventory => $"Delete everything in {name}'s inventory? This cannot be undone.",
            RustPlayerAction.UnlockBlueprints => $"Unlock every blueprint for {name}?",
            RustPlayerAction.ResetBlueprints => $"Reset {name}'s learned blueprints to the defaults? This cannot be undone.",
            _ => null,
        };
        var description = action switch
        {
            RustPlayerAction.Mute => $"Muted {name}",
            RustPlayerAction.Unmute => $"Unmuted {name}",
            RustPlayerAction.Kill => $"Killed {name}",
            RustPlayerAction.ClearInventory => $"Cleared the inventory of {name}",
            RustPlayerAction.UnlockBlueprints => $"Unlocked all blueprints for {name}",
            RustPlayerAction.ResetBlueprints => $"Reset blueprints for {name}",
            RustPlayerAction.TeamInfo => $"Team of {name}",
            _ => action.ToString(),
        };
        if (warning != null && !Confirm(action.ToString(), warning)) return;
        await SendPlayerToolAsync(command, description);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TeleportPlayerAsync()
    {
        var (destinationId, destinationName) = ResolvePlayer(TeleportDestination);
        var destination = RustModerationCommands.IsSteamId(destinationId) ? destinationId : destinationName;
        if (!RustPlayerCommands.TryTeleport(ToolTarget(), destination, out var command, out var error))
        {
            PlayerToolOutput = error;
            return;
        }
        await SendPlayerToolAsync(command, $"Teleported {ToolTargetName()} to {destinationName}");
    }

    [RelayCommand]
    private void UseAsTeleportDestination(Models.OnlinePlayer? player)
    {
        if (player == null) return;
        TeleportDestination = RustModerationCommands.IsSteamId(player.SteamId) ? player.SteamId : player.Name;
        PlayerToolOutput = $"Teleport destination: {player.Name}.";
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetModeratorAsync(string? grant)
    {
        var add = grant == "True";
        var (steamId, name) = ResolvePlayer(PlayerInput);
        if (!RustPlayerCommands.TrySetModerator(steamId, name, add, out var commands, out var error))
        {
            PlayerToolOutput = error;
            return;
        }
        if (add && !Confirm("Add moderator",
                $"Make {name} a Rust moderator (auth level 1)?\n\nModerators get the F1 admin console and admin commands."))
            return;
        foreach (var command in commands)
            await SendPlayerToolAsync(command, add ? $"Made {name} a moderator" : $"Removed moderator from {name}");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SkipQueueAsync()
    {
        var (steamId, name) = ResolvePlayer(PlayerInput);
        if (!RustPlayerCommands.TrySkipQueue(steamId, out var command, out var error))
        {
            PlayerToolOutput = error;
            return;
        }
        await SendPlayerToolAsync(command, $"Gave {name} queue skip");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeployLoadoutAsync()
    {
        if (!RustPlayerCommands.TryDeployLoadout(LoadoutName, ToolTarget(), out var command, out var error))
        {
            PlayerToolOutput = error;
            return;
        }
        await SendPlayerToolAsync(command, $"Deployed loadout {LoadoutName.Trim()} to {ToolTargetName()}");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ListLoadoutsAsync() => SendPlayerToolAsync("inventory.listloadouts", "Saved loadouts");

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ListMutesAsync() => SendPlayerToolAsync("mutelist", "Muted players");

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ListBansAsync() => SendPlayerToolAsync("banlistex", "Rust ban list");

    [RelayCommand]
    private void OpenSteamProfile(Models.OnlinePlayer? player) =>
        OpenTrustedUrl(player?.SteamProfileUrl, "steamcommunity.com");

    [RelayCommand]
    private void CopyPlayerSteamId(Models.OnlinePlayer? player)
    {
        if (player == null || player.SteamId.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(player.SteamId);
            ModerationStatus = $"Copied {player.Name}'s Steam64 ID.";
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }
    // ── Player Management ─────────────────────────────────────────────────────

    // Vanhanmallinen tekstisyöttö (yhteensopivuus)
    [ObservableProperty] private string _playerInput = string.Empty;

    [RelayCommand]
    private async Task KickPlayerAsync()
    {
        if (string.IsNullOrWhiteSpace(PlayerInput) || Plugin == null) return;
        var cmd = Plugin.GetKickCommand(PlayerInput,
            string.IsNullOrWhiteSpace(KickReason) ? "Kicked by admin" : KickReason);
        if (cmd == null) { AppendLog("[Players] Kick not supported.", ConsoleMessageType.Warning); return; }
        await SendRconOrConsole(cmd);
        AppendLog($"[Players] Kicked: {PlayerInput}", ConsoleMessageType.System);
        AddActionLog($"Player kicked: {PlayerInput}");
        KickReason = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    [RelayCommand]
    private async Task BanPlayerAsync()
    {
        if (string.IsNullOrWhiteSpace(PlayerInput) || Plugin == null) return;
        var reason = string.IsNullOrWhiteSpace(BanReason) ? "Banned by admin" : BanReason;
        var (steamId, playerName) = ResolvePlayer(PlayerInput);
        if (!await BanTargetAsync(steamId, playerName, reason)) return;
        BanReason = string.Empty;
        BanDuration = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    /// <summary>Records a ban in the server's group ban list and replays it on the group's
    /// other currently-running servers of the same game.</summary>
    private async Task SyncBanToGroupAsync(
        string target,
        string playerName,
        string reason,
        string duration,
        DateTime? expiresAtUtc)
    {
        if (string.IsNullOrEmpty(Server.GroupId) || Plugin == null) return;
        _groupBans.AddBan(Server.GroupId, Server.GameId, target, reason,
            playerName, duration, expiresAtUtc);

        foreach (var sibling in _manager.GetRunningGroupSiblings(Server))
        {
            string? cmd;
            if (RustModerationCommands.IsSteamId(target))
            {
                var remaining = expiresAtUtc is { } expiry
                    ? RustModerationCommands.RemainingDuration(expiry, DateTime.UtcNow)
                    : string.Empty;
                if (!RustModerationCommands.TryBuildBan(target, playerName, reason, remaining,
                    DateTime.UtcNow, out var timedCommand, out _, out _, out _))
                    continue;
                cmd = timedCommand;
            }
            else
                cmd = Plugin.GetBanCommand(target, reason);
            if (cmd == null) continue;
            try { await _manager.SendCommandAsync(sibling.Id, cmd); }
            catch { }
        }
        AppendLog($"[Group Ban] Synced to {Server.GroupId}.", ConsoleMessageType.System);
    }

    // Kick/Ban suoraan pelaajan kortista
    [RelayCommand]
    private async Task KickOnlinePlayerAsync(Models.OnlinePlayer? player)
    {
        if (player == null || Plugin == null) return;
        var target = player.SteamId.Length > 0 ? player.SteamId : player.Name;
        var reason  = string.IsNullOrWhiteSpace(KickReason) ? "Kicked by admin" : KickReason;
        var cmd     = Plugin.GetKickCommand(target, reason);
        if (cmd == null) { AppendLog("[Players] Kick not supported.", ConsoleMessageType.Warning); return; }
        await SendRconOrConsole(cmd);
        AppendLog($"[Players] Kicked {player.Name} ({reason})", ConsoleMessageType.System);
        KickReason = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    [RelayCommand]
    private async Task BanOnlinePlayerAsync(Models.OnlinePlayer? player)
    {
        if (player == null || Plugin == null) return;
        var reason  = string.IsNullOrWhiteSpace(BanReason) ? "Banned by admin" : BanReason;
        if (!await BanTargetAsync(player.SteamId, player.Name, reason)) return;
        BanReason = string.Empty;
        BanDuration = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    private async Task<bool> BanTargetAsync(string steamId, string playerName, string reason)
    {
        if (Plugin == null) return false;

        string? command;
        string normalizedDuration = string.Empty;
        DateTime? expiresAtUtc = null;
        var target = RustModerationCommands.IsSteamId(steamId) ? steamId : playerName;

        if (RustModerationCommands.IsSteamId(steamId))
        {
            if (!RustModerationCommands.TryBuildBan(steamId, playerName, reason, BanDuration,
                DateTime.UtcNow, out var timedCommand, out normalizedDuration, out expiresAtUtc, out var error))
            {
                ModerationStatus = error;
                AppendLog("[Moderation] " + error, ConsoleMessageType.Warning);
                return false;
            }
            command = timedCommand;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(BanDuration))
            {
                ModerationStatus = "Timed bans require a connected player or a 17-digit Steam64 ID.";
                AppendLog("[Moderation] " + ModerationStatus, ConsoleMessageType.Warning);
                return false;
            }
            command = Plugin.GetBanCommand(target, reason);
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            AppendLog("[Players] Ban not supported.", ConsoleMessageType.Warning);
            return false;
        }

        await SendRconOrConsole(command);
        var durationText = string.IsNullOrEmpty(normalizedDuration) ? "permanent" : normalizedDuration;
        AppendLog($"[Players] Banned {playerName} ({reason}; {durationText})", ConsoleMessageType.System);
        AddActionLog($"Player banned: {playerName} ({durationText})");
        ModerationStatus = $"Ban issued for {playerName} ({durationText}).";

        if (RustModerationCommands.IsSteamId(steamId))
            _moderation.RecordBan(Server.Id, steamId, playerName, reason, normalizedDuration, expiresAtUtc);
        await SyncBanToGroupAsync(target, playerName, reason, normalizedDuration, expiresAtUtc);
        RefreshModerationState();
        return true;
    }

    private (string SteamId, string PlayerName) ResolvePlayer(string input)
    {
        input = input.Trim();
        var online = OnlinePlayers.FirstOrDefault(p =>
            p.SteamId.Equals(input, StringComparison.Ordinal)
            || p.Name.Equals(input, StringComparison.OrdinalIgnoreCase));
        return online == null
            ? (RustModerationCommands.IsSteamId(input) ? input : string.Empty, input)
            : (online.SteamId, online.Name);
    }

    [RelayCommand]
    private void SelectAllPlayers()
    {
        foreach (var player in OnlinePlayers) player.IsSelected = true;
        OnlinePlayers = OnlinePlayers.ToList();
        ModerationStatus = $"Selected {OnlinePlayers.Count} online player(s).";
    }

    [RelayCommand]
    private void ClearPlayerSelection()
    {
        foreach (var player in OnlinePlayers) player.IsSelected = false;
        OnlinePlayers = OnlinePlayers.ToList();
        ModerationStatus = "Player selection cleared.";
    }

    [RelayCommand]
    private async Task BulkKickPlayersAsync()
    {
        var selected = OnlinePlayers.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ModerationStatus = "Select at least one online player first.";
            return;
        }
        if (!ConfirmBulkAction("kick", selected.Count)) return;

        var reason = string.IsNullOrWhiteSpace(KickReason) ? "Kicked by admin" : KickReason;
        var completed = 0;
        foreach (var player in selected)
        {
            var target = RustModerationCommands.IsSteamId(player.SteamId) ? player.SteamId : player.Name;
            var command = Plugin?.GetKickCommand(target, reason);
            if (command == null) continue;
            await SendRconOrConsole(command);
            completed++;
        }
        AppendLog($"[Moderation] Bulk kicked {completed} player(s): {reason}", ConsoleMessageType.System);
        AddActionLog($"Bulk kick completed for {completed} player(s)");
        ModerationStatus = $"Bulk kick completed for {completed} player(s).";
        KickReason = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    [RelayCommand]
    private async Task BulkBanPlayersAsync()
    {
        var selected = OnlinePlayers
            .Where(p => p.IsSelected && RustModerationCommands.IsSteamId(p.SteamId))
            .ToList();
        if (selected.Count == 0)
        {
            ModerationStatus = "Select at least one online player with a Steam64 ID first.";
            return;
        }
        if (!ConfirmBulkAction("ban", selected.Count)) return;

        var reason = string.IsNullOrWhiteSpace(BanReason) ? "Banned by admin" : BanReason;
        var completed = 0;
        foreach (var player in selected)
            if (await BanTargetAsync(player.SteamId, player.Name, reason)) completed++;

        AppendLog($"[Moderation] Bulk banned {completed} player(s): {reason}", ConsoleMessageType.System);
        AddActionLog($"Bulk ban completed for {completed} player(s)");
        ModerationStatus = $"Bulk ban completed for {completed} player(s).";
        BanReason = string.Empty;
        BanDuration = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    [RelayCommand]
    private async Task UnbanPlayerAsync()
    {
        var (steamId, playerName) = ResolvePlayer(PlayerInput);
        if (!RustModerationCommands.IsSteamId(steamId))
        {
            ModerationStatus = "Unban requires a valid 17-digit Steam64 ID.";
            return;
        }

        await SendRconOrConsole(RustModerationCommands.Unban(steamId));
        _moderation.RecordUnban(Server.Id, steamId, playerName);
        _groupBans.RemoveBan(Server.GroupId, Server.GameId, steamId);
        ModerationStatus = $"Unban issued for {playerName}.";
        AppendLog($"[Players] Unbanned {playerName} ({steamId})", ConsoleMessageType.System);
        AddActionLog($"Player unbanned: {playerName}");
        RefreshModerationState();
    }

    [RelayCommand]
    private void UsePlayerForModeration(Models.OnlinePlayer? player)
    {
        if (player == null) return;
        PlayerInput = RustModerationCommands.IsSteamId(player.SteamId) ? player.SteamId : player.Name;
        PlayerNote  = player.StaffNotes;
        ModerationStatus = $"Loaded {player.Name} into the moderation workspace.";
    }

    [RelayCommand]
    private void SavePlayerNote()
    {
        var (steamId, playerName) = ResolvePlayer(PlayerInput);
        if (!RustModerationCommands.IsSteamId(steamId))
        {
            ModerationStatus = "Player notes require a valid 17-digit Steam64 ID.";
            return;
        }
        _moderation.SetNote(Server.Id, steamId, playerName, PlayerNote);
        ModerationStatus = $"Saved staff notes for {playerName}.";
        AddActionLog($"Staff note updated for {playerName}");
        RefreshModerationState();
    }

    [RelayCommand]
    private async Task GrantWhitelistAsync()
    {
        var (steamId, playerName) = ResolvePlayer(PlayerInput);
        if (!CanManageWhitelist(steamId)) return;
        await SendRconOrConsole(RustModerationCommands.GrantWhitelist(steamId));
        _moderation.SetWhitelisted(Server.Id, steamId, playerName, allowed: true);
        ModerationStatus = $"Whitelist permission granted to {playerName}.";
        AppendLog($"[Whitelist] Granted whitelist.allow to {playerName} ({steamId})", ConsoleMessageType.System);
        AddActionLog($"Whitelist granted: {playerName}");
        RefreshModerationState();
    }

    [RelayCommand]
    private async Task RevokeWhitelistAsync()
    {
        var (steamId, playerName) = ResolvePlayer(PlayerInput);
        if (!CanManageWhitelist(steamId)) return;
        await SendRconOrConsole(RustModerationCommands.RevokeWhitelist(steamId));
        _moderation.SetWhitelisted(Server.Id, steamId, playerName, allowed: false);
        ModerationStatus = $"Whitelist permission revoked from {playerName}.";
        AppendLog($"[Whitelist] Revoked whitelist.allow from {playerName} ({steamId})", ConsoleMessageType.System);
        AddActionLog($"Whitelist revoked: {playerName}");
        RefreshModerationState();
    }

    private bool CanManageWhitelist(string steamId)
    {
        if (!RustModerationCommands.IsSteamId(steamId))
        {
            ModerationStatus = "Whitelist changes require a valid 17-digit Steam64 ID.";
            return false;
        }
        var hasFramework = ModManagerService.GetInstalledOxideVersion(Server.InstallPath) != null
            || ModManagerService.IsCarbonInstalled(Server.InstallPath);
        if (!hasFramework)
        {
            ModerationStatus = "Install Carbon or Oxide plus the Whitelist plugin before managing whitelist access.";
            return false;
        }
        if (!IsRunning)
        {
            ModerationStatus = "Start Rust and connect WebRCON before changing whitelist access.";
            return false;
        }
        return true;
    }

    private static bool ConfirmBulkAction(string action, int count) =>
        System.Windows.MessageBox.Show(
            $"Confirm bulk {action} for {count} selected player(s)?\n\nThis sends live Rust moderation commands and cannot be undone as a group.",
            $"HighPop — Confirm bulk {action}",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    private void RefreshModerationState()
    {
        RefreshModerationProfiles();
        foreach (var player in OnlinePlayers.Where(p => RustModerationCommands.IsSteamId(p.SteamId)))
        {
            var profile = _moderation.GetRecord(Server.Id, player.SteamId);
            player.StaffNotes     = profile?.Notes ?? string.Empty;
            player.IsWhitelisted = profile?.Whitelisted == true;
        }
        OnlinePlayers = OnlinePlayers.ToList();
    }

    [RelayCommand]
    private async Task ListPlayersAsync()
    {
        if (Plugin == null) return;
        var cmd = Plugin.GetPlayersCommand();
        if (cmd == null) { AppendLog("[Players] Player list not supported.", ConsoleMessageType.Warning); return; }
        await SendRconOrConsole(cmd);
    }
}
