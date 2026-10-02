using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            byte[]? mergedBytes = null;
            if (!shouldWrite && previousState.TryGetValue(fileName, out var previousHash))
            {
                var installedBytes = File.ReadAllBytes(targetPath);
                var installedHash = Hash(installedBytes);
                shouldWrite = installedHash.Equals(previousHash, StringComparison.OrdinalIgnoreCase)
                              && !installedHash.Equals(bundledHash, StringComparison.OrdinalIgnoreCase);

                // An operator-edited built-in remains authoritative, but new keys introduced by
                // a later HighPop release still need to become available. Merge only missing
                // Values entries; never replace or remove an existing operator value.
                if (!shouldWrite
                    && !installedHash.Equals(bundledHash, StringComparison.OrdinalIgnoreCase)
                    && TryMergeMissingValues(installedBytes, bundledBytes, out mergedBytes))
                    shouldWrite = true;
            }

            if (shouldWrite)
            {
                WriteAtomic(targetPath, mergedBytes ?? bundledBytes);
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

    private static bool TryMergeMissingValues(byte[] installedBytes, byte[] bundledBytes, out byte[]? mergedBytes)
    {
        mergedBytes = null;
        try
        {
            var installed = JsonNode.Parse(installedBytes) as JsonObject;
            var bundled = JsonNode.Parse(bundledBytes) as JsonObject;
            if (installed?["Values"] is not JsonObject installedValues
                || bundled?["Values"] is not JsonObject bundledValues)
                return false;

            var changed = false;
            foreach (var (key, value) in bundledValues)
            {
                if (installedValues.ContainsKey(key)) continue;
                installedValues[key] = value?.DeepClone();
                changed = true;
            }

            if (!changed) return false;
            mergedBytes = JsonSerializer.SerializeToUtf8Bytes(installed,
                new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch (JsonException)
        {
            // Preserve hand-edited or commented JSON exactly when it cannot be merged safely.
            return false;
        }
    }
}
