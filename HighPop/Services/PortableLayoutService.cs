using System.IO;

namespace HighPop.Services;

public sealed record PortableLayoutMigrationResult(int MovedEntries, int Conflicts);

public static class PortableLayoutService
{
    public static PortableLayoutMigrationResult MigrateServerRoot(
        string legacyRoot,
        string serverRoot)
    {
        if (!Directory.Exists(legacyRoot))
        {
            Directory.CreateDirectory(serverRoot);
            return new PortableLayoutMigrationResult(0, 0);
        }

        Directory.CreateDirectory(serverRoot);
        var moved = 0;
        var conflicts = 0;

        foreach (var source in Directory.EnumerateFileSystemEntries(legacyRoot))
        {
            var destination = Path.Combine(serverRoot, Path.GetFileName(source));
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                conflicts++;
                continue;
            }

            try
            {
                if (Directory.Exists(source)) Directory.Move(source, destination);
                else File.Move(source, destination);
                moved++;
            }
            catch (IOException) { conflicts++; }
            catch (UnauthorizedAccessException) { conflicts++; }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(legacyRoot).Any())
                Directory.Delete(legacyRoot);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new PortableLayoutMigrationResult(moved, conflicts);
    }

    public static string ResolveInstallPath(
        string installPath,
        string legacyRoot,
        string serverRoot)
    {
        if (string.IsNullOrWhiteSpace(installPath)) return installPath;

        try
        {
            var legacyFull = Path.GetFullPath(legacyRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var installFull = Path.GetFullPath(installPath);
            var prefix = legacyFull + Path.DirectorySeparatorChar;
            if (!installFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return installPath;

            var relative = Path.GetRelativePath(legacyFull, installFull);
            var migrated = Path.Combine(serverRoot, relative);
            return !Directory.Exists(installFull) && Directory.Exists(migrated)
                ? migrated
                : installPath;
        }
        catch { return installPath; }
    }

    public static string FlattenGeneratedGamePath(
        string installPath,
        string serverRoot,
        string gameId)
    {
        if (string.IsNullOrWhiteSpace(installPath)
            || string.IsNullOrWhiteSpace(gameId)) return installPath;

        try
        {
            var rootFull = Path.GetFullPath(serverRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var installFull = Path.GetFullPath(installPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = Path.GetRelativePath(rootFull, installFull);
            var segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            // v0.8.2 briefly generated exactly Servers/<game-id>/<server-name>. Do not
            // reinterpret deeper paths or custom locations selected by an operator.
            if (segments.Length != 2
                || !segments[0].Equals(gameId, StringComparison.OrdinalIgnoreCase))
                return installPath;

            var flattened = Path.Combine(rootFull, segments[1]);
            if (Directory.Exists(installFull) && !Directory.Exists(flattened))
            {
                Directory.Move(installFull, flattened);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(installFull));
                return flattened;
            }

            return !Directory.Exists(installFull) && Directory.Exists(flattened)
                ? flattened
                : installPath;
        }
        catch (IOException) { return installPath; }
        catch (UnauthorizedAccessException) { return installPath; }
        catch (ArgumentException) { return installPath; }
        catch (NotSupportedException) { return installPath; }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
