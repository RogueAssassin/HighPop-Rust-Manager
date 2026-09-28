using System.IO;
using System.Security.Cryptography;
using HighPop.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HighPop.Services;

public sealed record ServerProfileDocument(
    int SchemaVersion,
    DateTime ExportedUtc,
    string OriginalInstallPath,
    GameServer Server);

/// <summary>Portable, per-server HPRM settings export. Server files and secrets are not embedded.</summary>
public sealed class ServerProfileTransferService
{
    public const string FileExtension = ".hprm-server.json";
    private const string FormatName = "HighPop.ServerProfile";
    private const int CurrentSchemaVersion = 1;
    private const long MaxImportBytes = 5 * 1024 * 1024;

    public void Export(GameServer source, string path)
    {
        var profile = Clone(source);
        RemoveSecrets(profile);
        ResetRuntimeState(profile);

        var document = new JObject
        {
            ["Format"] = FormatName,
            ["SchemaVersion"] = CurrentSchemaVersion,
            ["ExportedUtc"] = DateTime.UtcNow,
            ["OriginalInstallPath"] = source.InstallPath,
            ["OmittedSecrets"] = new JArray(
                nameof(GameServer.RconPassword),
                nameof(GameServer.ServerPassword),
                nameof(GameServer.DiscordWebhookUrl)),
            ["Server"] = JObject.FromObject(profile),
        };

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, document.ToString(Formatting.Indented));
        File.Move(tempPath, fullPath, overwrite: true);
    }

    public ServerProfileDocument Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The HPRM server profile was not found.", path);
        if (file.Length is <= 0 or > MaxImportBytes)
            throw new InvalidDataException("The HPRM server profile is empty or exceeds the 5 MB limit.");

        JObject root;
        try { root = JObject.Parse(File.ReadAllText(file.FullName)); }
        catch (JsonException ex) { throw new InvalidDataException("The selected file is not valid JSON.", ex); }

        if (!string.Equals(root["Format"]?.Value<string>(), FormatName, StringComparison.Ordinal))
            throw new InvalidDataException("The selected file is not an HPRM server profile.");
        var schemaVersion = root["SchemaVersion"]?.Value<int>() ?? 0;
        if (schemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"HPRM server profile schema {schemaVersion} is not supported.");

        var server = root["Server"]?.ToObject<GameServer>()
            ?? throw new InvalidDataException("The HPRM server profile does not contain server settings.");
        if (!string.Equals(server.GameId, "rust", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only Rust server profiles can be imported.");

        NormalizeCollections(server);
        RemoveSecrets(server);
        ResetRuntimeState(server);
        return new ServerProfileDocument(
            schemaVersion,
            root["ExportedUtc"]?.Value<DateTime>() ?? DateTime.MinValue,
            root["OriginalInstallPath"]?.Value<string>() ?? server.InstallPath,
            server);
    }

    public GameServer PrepareImport(ServerProfileDocument document, string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Choose the Rust server installation folder.", nameof(installPath));

        var server = Clone(document.Server);
        server.InstallPath = Path.GetFullPath(installPath.Trim());
        server.Id = Guid.NewGuid().ToString();
        server.CreatedAt = DateTime.Now;
        RemoveSecrets(server);
        ResetRuntimeState(server);
        NormalizeCollections(server);
        return server;
    }

    private static GameServer Clone(GameServer server) =>
        JsonConvert.DeserializeObject<GameServer>(JsonConvert.SerializeObject(server))
        ?? throw new InvalidDataException("The server profile could not be copied.");

    private static void RemoveSecrets(GameServer server)
    {
        server.RconPassword = string.Empty;
        server.ServerPassword = string.Empty;
        server.DiscordWebhookUrl = string.Empty;
    }

    private static void ResetRuntimeState(GameServer server)
    {
        server.Status = ServerStatus.NotInstalled;
        server.RunningPid = 0;
        server.RunningProcessStartedUtc = null;
        server.RunningExecutablePath = string.Empty;
        server.DesiredState = ServerDesiredState.Stopped;
        server.LifecyclePhase = ServerLifecyclePhase.Unknown;
        server.LifecycleGeneration = 0;
        server.LastLifecycleOperationId = string.Empty;
        server.LastLifecycleReason = "Imported profile";
        server.LastLifecycleInitiator = LifecycleInitiator.Unknown;
        server.LastLifecycleTransitionUtc = null;
        server.LifecycleOperationHistory = [];
        server.LastStarted = null;
        server.LastExitAt = null;
        server.LastExitCode = null;
        server.LastExitReason = string.Empty;
        server.GroupId = string.Empty;
    }

    private static void NormalizeCollections(GameServer server)
    {
        server.GameSpecificSettings ??= [];
        server.RustServerVariables ??= RustServerVariable.CreateDefaults();
        server.QuickCommands ??= [];
        server.LogWatchRules ??= [];
        server.LifecycleOperationHistory ??= [];
        server.RogueRustChannel = ModManagerService.NormalizeRogueRustChannel(server.RogueRustChannel);
    }
}
