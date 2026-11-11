using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighPop.Models;
using HighPop.Services;

namespace HighPop.ViewModels;

/// <summary>
/// The Backups page of one server: the backup list, retention settings and the
/// create/clean up/restore/delete commands. Owned by <see cref="ServerViewModel"/>,
/// which creates it the first time the page or a backup command needs it.
/// </summary>
public partial class ServerBackupsViewModel : BaseViewModel
{
    private readonly ServerViewModel     _owner;
    private readonly BackupService       _backup;
    private readonly NotificationService _notifications;

    public ObservableCollection<BackupEntry> Entries { get; } = [];

    [ObservableProperty] private int _backupRetention;
    partial void OnBackupRetentionChanged(int value) => Server.BackupRetention = value;
    [ObservableProperty] private int _backupMaxAgeDays;
    partial void OnBackupMaxAgeDaysChanged(int value) => Server.BackupMaxAgeDays = value;
    [ObservableProperty] private bool _useIncrementalBackups;
    partial void OnUseIncrementalBackupsChanged(bool value) => Server.UseIncrementalBackups = value;
    [ObservableProperty] private int _fullBackupEveryN;
    partial void OnFullBackupEveryNChanged(int value) => Server.FullBackupEveryN = value;

    internal ServerBackupsViewModel(ServerViewModel owner, BackupService backup, NotificationService notifications)
    {
        _owner         = owner;
        _backup        = backup;
        _notifications = notifications;
        // Set the backing fields so loading the saved values does not write them back.
        _backupRetention       = Server.BackupRetention;
        _backupMaxAgeDays      = Server.BackupMaxAgeDays;
        _useIncrementalBackups = Server.UseIncrementalBackups;
        _fullBackupEveryN      = Server.FullBackupEveryN;
        Refresh();
    }

    private GameServer Server => _owner.Server;

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        try
        {
            _owner.AppendLog("[Backup] " + Loc.BackupCreating, ConsoleMessageType.System);
            var entry = await _backup.CreateBackupAsync(Server);
            _owner.AppendLog($"[Backup] {Loc.BackupDone}: {entry.SizeText}", ConsoleMessageType.System);
            _owner.AddActionLog($"Backup created ({entry.SizeText})");
            await _notifications.NotifyAsync($"💾 {Server.DisplayName} {Loc.BackupDone}", entry.SizeText, "#D29922");
            Refresh();
        }
        catch (Exception ex) { _owner.AppendLog("[ERR] " + ex.Message, ConsoleMessageType.Error); }
    }

    [RelayCommand]
    private void CleanupBackupsNow()
    {
        var toDelete = _backup.GetBackupsToDelete(Server, Server.BackupRetention);
        if (toDelete.Count == 0)
        {
            WpfMsgBox.Show("No backups currently match the retention/age limits — nothing to delete.",
                "Clean up backups", WpfMsgBoxButton.OK, WpfMsgBoxImage.Information);
            return;
        }

        var totalSize = toDelete.Sum(b => b.SizeBytes);
        var sizeText  = totalSize >= 1024 * 1024 * 1024
            ? $"{totalSize / (1024.0 * 1024 * 1024):F2} GB"
            : $"{totalSize / (1024.0 * 1024):F1} MB";

        var result = WpfMsgBox.Show(
            $"This will permanently delete {toDelete.Count} backup(s) totaling {sizeText}, based on the current " +
            $"\"Keep at most {Server.BackupRetention}\" / \"older than {Server.BackupMaxAgeDays} days\" settings.\n\n" +
            "This cannot be undone. Continue?",
            "Clean up backups", WpfMsgBoxButton.YesNo, WpfMsgBoxImage.Warning);
        if (result != WpfMsgBoxResult.Yes) return;

        foreach (var b in toDelete)
            _backup.DeleteBackup(b.FilePath);

        _owner.AppendLog($"[Backup] Cleaned up {toDelete.Count} old backup(s) ({sizeText}).", ConsoleMessageType.System);
        Refresh();
    }

    [RelayCommand]
    private async Task RestoreBackupAsync(BackupEntry? entry)
    {
        if (entry == null) return;
        if (_owner.IsRunning)
        {
            _owner.AppendLog("[ERR] " + Loc.RestoreStopFirst, ConsoleMessageType.Error);
            return;
        }
        _owner.AppendLog($"[Restore] {Loc.RestoreStarting} {entry.CreatedAt:dd.MM.yyyy HH:mm}...", ConsoleMessageType.System);
        await _backup.RestoreBackupAsync(Server, entry.FilePath);
        _owner.AppendLog("[Restore] " + Loc.RestoreDone, ConsoleMessageType.System);
        _owner.AddActionLog($"Backup restored: {Path.GetFileName(entry.FilePath)}");
    }

    [RelayCommand]
    private void DeleteBackup(BackupEntry? entry)
    {
        if (entry == null) return;
        _backup.DeleteBackup(entry.FilePath);
        _owner.AddActionLog($"Backup deleted: {Path.GetFileName(entry.FilePath)}");
        Refresh();
    }

    /// <summary>Reloads the backup list from disk. Safe to call from any thread.</summary>
    public void Refresh()
    {
        UiDispatcher.Post(() =>
        {
            Entries.Clear();
            foreach (var b in _backup.GetBackupsForServer(Server))
                Entries.Add(b);
        });
    }
}
