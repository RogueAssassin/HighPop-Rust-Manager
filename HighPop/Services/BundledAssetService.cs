using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace HighPop.Services;

/// <summary>
/// Restores application-owned preset files from resources embedded in HighPop.exe.
/// User-modified and custom presets are preserved.
/// </summary>
public static class BundledAssetService
{
    private const string ResourcePrefix = "HighPop.assets.presets.";

    public static int EnsurePresets(string exeDir, Assembly? assembly = null)
    {
        assembly ??= typeof(BundledAssetService).Assembly;
        var assetsDir = Path.Combine(Path.GetFullPath(exeDir), "assets");
        var presetsDir = Path.Combine(assetsDir, "presets");
        var statePath = Path.Combine(assetsDir, "data", "bundled-presets.json");
        Directory.CreateDirectory(presetsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);

        var previousState = ReadState(statePath);
        var nextState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deployed = 0;

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            var fileName = resourceName[ResourcePrefix.Length..];
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"Embedded preset {resourceName} could not be opened.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bundledBytes = memory.ToArray();
            var bundledHash = Hash(bundledBytes);
            var targetPath = Path.Combine(presetsDir, fileName);

            var shouldWrite = !File.Exists(targetPath);
            if (!shouldWrite && previousState.TryGetValue(fileName, out var previousHash))
            {
                var installedHash = Hash(File.ReadAllBytes(targetPath));
                shouldWrite = installedHash.Equals(previousHash, StringComparison.OrdinalIgnoreCase)
                              && !installedHash.Equals(bundledHash, StringComparison.OrdinalIgnoreCase);
            }

            if (shouldWrite)
            {
                WriteAtomic(targetPath, bundledBytes);
                deployed++;
            }

            nextState[fileName] = bundledHash;
        }

        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(nextState,
            new JsonSerializerOptions { WriteIndented = true });
        WriteAtomic(statePath, stateBytes);
        return deployed;
    }

    private static Dictionary<string, string> ReadState(string path)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return saved == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(saved, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}
