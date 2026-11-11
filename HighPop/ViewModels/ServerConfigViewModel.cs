using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighPop.Games;
using HighPop.Models;
using HighPop.Services;

namespace HighPop.ViewModels;

/// <summary>
/// Server configuration tools for one server: the Config files editor, config presets and
/// server templates. Owned by <see cref="ServerViewModel"/>, which creates it the first time
/// the Config files or Settings page needs it.
/// </summary>
public partial class ServerConfigViewModel : BaseViewModel
{
    private readonly ServerViewModel     _owner;
    private readonly ConfigEditorService _configEditor;
    private readonly ConfigPresetService _presets;
    private readonly TemplateService     _templates;
    private readonly ConfigService       _config;

    internal ServerConfigViewModel(ServerViewModel owner, ConfigEditorService configEditor,
        ConfigPresetService presets, TemplateService templates, ConfigService config)
    {
        _owner        = owner;
        _configEditor = configEditor;
        _presets      = presets;
        _templates    = templates;
        _config       = config;
        AvailablePresets = _presets.GetPresetsForGame(Server.GameId);
        SelectedPreset   = AvailablePresets.FirstOrDefault();
    }

    private GameServer   Server    => _owner.Server;
    private IGamePlugin? Plugin    => _owner.Plugin;
    private bool         IsRunning => _owner.IsRunning;
    private void AppendLog(string text, ConsoleMessageType type = ConsoleMessageType.Info) => _owner.AppendLog(text, type);
    private void AddActionLog(string action) => _owner.AddActionLog(action);
    private Task SendRconOrConsole(string cmd) => _owner.SendRconOrConsole(cmd);
    private static string SanitizeFileName(string name) => ServerViewModel.SanitizeFileName(name);
    private bool   HasCustomImage  => _owner.HasCustomImage;
    private string CustomImagePath => _owner.CustomImagePath;
    // Config editor
    [ObservableProperty] private List<Services.ConfigFileEntry> _configFiles = [];
    [ObservableProperty] private Services.ConfigFileEntry? _selectedConfigFile;
    [ObservableProperty] private string _configContent = string.Empty;

    // Config Presets
    [ObservableProperty] private List<Services.ConfigPreset> _availablePresets = [];
    [ObservableProperty] private Services.ConfigPreset? _selectedPreset;


    // ── Template filter ───────────────────────────────────────────────────────
    [ObservableProperty] private string _templateFilterCategory = string.Empty;
    [ObservableProperty] private string _templateFilterTag      = string.Empty;

    partial void OnTemplateFilterCategoryChanged(string _) => OnPropertyChanged(nameof(FilteredTemplates));
    partial void OnTemplateFilterTagChanged(string _)      => OnPropertyChanged(nameof(FilteredTemplates));

    public List<Models.ServerTemplate> FilteredTemplates
    {
        get
        {
            var all = _templates.ForGame(Server.GameId);
            if (!string.IsNullOrWhiteSpace(TemplateFilterCategory))
                all = all.Where(t => t.Category.Equals(TemplateFilterCategory,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(TemplateFilterTag))
                all = all.Where(t => t.Tags.Contains(TemplateFilterTag,
                    StringComparer.OrdinalIgnoreCase)).ToList();
            return all.ToList();
        }
    }

    public List<string> AvailableCategories =>
        _templates.ForGame(Server.GameId)
                  .Select(t => t.Category)
                  .Where(c => !string.IsNullOrWhiteSpace(c))
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .Prepend("(all)")
                  .ToList();

    // ── Config Presets ───────────────────────────────────────────────────────

    [RelayCommand]
    private void LoadPresets()
    {
        AvailablePresets = _presets.GetPresetsForGame(Server.GameId);
        SelectedPreset   = AvailablePresets.FirstOrDefault();
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        if (SelectedPreset == null) return;

        var result = WpfMsgBox.Show(
            $"Apply preset \"{SelectedPreset.Name}\" to server \"{Server.DisplayName}\"?\n\n" +
            $"{SelectedPreset.Description}\n\n" +
            "The server profile and server.cfg will be updated, and the original config file will be backed up automatically. " +
            "Changing world size can generate a new map on the next start.",
            "Apply Config Preset",
            WpfMsgBoxButton.YesNo,
            WpfMsgBoxImage.Question);

        if (result != WpfMsgBoxResult.Yes) return;

        var backup = _presets.ApplyPreset(Server, SelectedPreset);
        if (backup == null)
        {
            AppendLog($"[HighPop] ⚠ Config file not found: {SelectedPreset.ConfigFile}", ConsoleMessageType.Warning);
            return;
        }

        AppendLog($"[HighPop] ✔ Preset \"{SelectedPreset.Name}\" applied. Backup: {System.IO.Path.GetFileName(backup)}", ConsoleMessageType.System);
        _owner.NotifyServerChanged();
    }

    // ── Config editor ───────────────────────────────────────────────────────

    [RelayCommand]
    private void LoadConfigFiles()
    {
        ConfigFiles = _configEditor.FindConfigs(Server, Plugin);
        if (ConfigFiles.Count > 0) SelectedConfigFile = ConfigFiles[0];
        AppendLog($"[Config] Found {ConfigFiles.Count(f => !f.IsPluginConfig)} server config(s) and " +
                  $"{ConfigFiles.Count(f => f.IsPluginConfig)} plugin config(s).", ConsoleMessageType.System);
    }

    [RelayCommand]
    private void SaveConfigFile()
    {
        if (SelectedConfigFile == null) return;
        SelectedConfigFile.Content = ConfigContent;
        try
        {
            _configEditor.Save(Server, SelectedConfigFile);
            AppendLog($"[Config] Saved {SelectedConfigFile.DisplayName}.", ConsoleMessageType.System);
            AddActionLog($"Config saved: {SelectedConfigFile.DisplayName}");
            RefreshConfigHistory();
        }
        catch (Exception ex) { AppendLog($"[Config] Save failed: {ex.Message}", ConsoleMessageType.Error); }
    }

    [RelayCommand]
    private async Task ReloadSelectedPluginAsync()
    {
        if (SelectedConfigFile?.CanReloadPlugin != true) return;
        if (!IsRunning)
        {
            AppendLog("[Config] Start Rust before reloading a plugin.", ConsoleMessageType.Warning);
            return;
        }

        await SendRconOrConsole(SelectedConfigFile.ReloadCommand);
        AppendLog($"[Config] Reload requested for {SelectedConfigFile.PluginName} via " +
                  $"{SelectedConfigFile.ReloadCommand}.", ConsoleMessageType.System);
        AddActionLog($"Plugin reloaded: {SelectedConfigFile.PluginName}");
    }

    [ObservableProperty] private List<Services.ConfigSnapshot> _configHistory = [];

    private void RefreshConfigHistory()
    {
        ConfigHistory = SelectedConfigFile == null ? [] : _configEditor.GetHistory(Server.Id, SelectedConfigFile.Path);
    }

    [RelayCommand]
    private void RestoreConfigSnapshot(Services.ConfigSnapshot? snap)
    {
        if (snap == null || SelectedConfigFile == null) return;
        var result = WpfMsgBox.Show(
            $"Restore the version from {snap.SavedAt:dd.MM.yyyy HH:mm:ss}? The current content will be overwritten " +
            "(but is itself saved to history first, so you can undo this too).",
            "Restore config version", WpfMsgBoxButton.YesNo, WpfMsgBoxImage.Warning);
        if (result != WpfMsgBoxResult.Yes) return;

        var content = _configEditor.ReadSnapshot(snap.FilePath);
        SelectedConfigFile.Content = content;
        ConfigContent = content;
        try
        {
            _configEditor.Save(Server, SelectedConfigFile);
            AppendLog($"[Config] Restored version from {snap.SavedAt:dd.MM.yyyy HH:mm:ss}.", ConsoleMessageType.System);
            RefreshConfigHistory();
        }
        catch (Exception ex) { AppendLog($"[Config] Restore failed: {ex.Message}", ConsoleMessageType.Error); }
    }

    partial void OnSelectedConfigFileChanged(Services.ConfigFileEntry? value)
    {
        if (value == null) return;
        _configEditor.LoadContent(value);
        ConfigContent = value.Content;
        RefreshConfigHistory();
    }

    // ── Templates ─────────────────────────────────────────────────────────────

    [ObservableProperty] private string _templateName        = string.Empty;
    [ObservableProperty] private string _templateCategory    = string.Empty;
    [ObservableProperty] private string _templateTagsInput   = string.Empty;  // pilkkueroteltu
    [ObservableProperty] private string _templateDescription = string.Empty;

    [RelayCommand]
    private void SaveAsTemplate()
    {
        if (string.IsNullOrWhiteSpace(TemplateName)) return;
        var tags = TemplateTagsInput
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _templates.SaveFromServer(Server, TemplateName, TemplateDescription, TemplateCategory, tags);
        var saved = TemplateName;
        TemplateName        = string.Empty;
        TemplateCategory    = string.Empty;
        TemplateTagsInput   = string.Empty;
        TemplateDescription = string.Empty;
        RefreshTemplates();
        AppendLog($"[Template] Saved as template \"{saved}\".", ConsoleMessageType.System);
    }

    [RelayCommand]
    private void ApplyTemplate(Models.ServerTemplate? template)
    {
        if (template == null) return;
        _templates.ApplyToServer(template, Server);
        AppendLog($"[Template] Applied \"{template.Name}\".", ConsoleMessageType.System);
    }

    [RelayCommand]
    private void CloneTemplate(Models.ServerTemplate? template)
    {
        if (template == null) return;
        var clone = _templates.Clone(template.Id);
        RefreshTemplates();
        AppendLog($"[Template] Cloned as \"{clone.Name}\".", ConsoleMessageType.System);
    }

    [RelayCommand]
    private void DeleteTemplate(Models.ServerTemplate? template)
    {
        if (template == null) return;
        _templates.Delete(template.Id);
        RefreshTemplates();
    }

    [RelayCommand]
    private void ExportTemplate(Models.ServerTemplate? template)
    {
        if (template == null) return;
        var hasImg  = HasCustomImage;
        var ext     = hasImg ? ".hpt" : ".json";
        var filter  = hasImg ? "HighPop template with image|*.hpt|JSON file|*.json"
                             : "JSON file|*.json";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export template",
            Filter     = filter,
            FileName   = SanitizeFileName(template.Name) + ext,
            DefaultExt = ext,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _templates.ExportSingle(template.Id, dlg.FileName,
                hasImg ? CustomImagePath : null);
            AppendLog($"[Template] Exported: {dlg.FileName}", ConsoleMessageType.System);
        }
        catch (Exception ex)
        {
            AppendLog($"[Template] Export failed: {ex.Message}", ConsoleMessageType.Error);
        }
    }

    [RelayCommand]
    private void ImportTemplates()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title       = "Import templates",
            Filter      = "HighPop template|*.hpt;*.wgst;*.json|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        int total = 0;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var imgDir = Path.Combine(_config.AppDataPath, "server_images");
                var (count, imgPath) = _templates.Import(file, imgDir);
                total += count;
                // If image was extracted and a template was imported, rename image to match new template id
                if (imgPath != null && count > 0)
                {
                    var newId = _templates.All.Last().Id;
                    var dest  = Path.Combine(imgDir, newId + ".png");
                    if (File.Exists(imgPath)) File.Move(imgPath, dest, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[Template] Import failed ({Path.GetFileName(file)}): {ex.Message}",
                    ConsoleMessageType.Error);
            }
        }
        if (total > 0)
        {
            RefreshTemplates();
            AppendLog($"[Template] Imported {total} template(s).", ConsoleMessageType.System);
        }
    }

    private void RefreshTemplates()
    {
        OnPropertyChanged(nameof(FilteredTemplates));
        OnPropertyChanged(nameof(AvailableCategories));
    }
}
