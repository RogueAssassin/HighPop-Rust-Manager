using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace HighPop.Services;

/// <summary>
/// Protects saved passwords and tokens.
/// <list type="bullet">
/// <item>Windows: DPAPI for the current user. Values are plain Base64, exactly as before.</item>
/// <item>Linux: AES-256-GCM with a random key kept in a private file (<c>~/.config/highpop/secret.key</c>,
/// owner-only). Values start with <c>lnx1:</c>.</item>
/// </list>
/// A value written on one OS cannot be read on the other; it decrypts to an empty string, which the
/// config loader already treats as "undecryptable" and keeps a copy of the original file.
/// </summary>
public static class EncryptionService
{
    private const string LinuxPrefix = "lnx1:";
    private const int KeySize = 32, NonceSize = 12, TagSize = 16;
    private static readonly object KeyLock = new();
    private static byte[]? _linuxKey;

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        var data = Encoding.UTF8.GetBytes(plainText);
        if (OperatingSystem.IsWindows())
            return Convert.ToBase64String(ProtectWindows(data));
        return LinuxPrefix + Convert.ToBase64String(EncryptWithKey(data, GetLinuxKey()));
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;
        try
        {
            var isLinuxValue = cipherText.StartsWith(LinuxPrefix, StringComparison.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                if (isLinuxValue) return string.Empty; // written on Linux; cannot be read here
                return Encoding.UTF8.GetString(UnprotectWindows(Convert.FromBase64String(cipherText)));
            }
            if (!isLinuxValue) return string.Empty;    // DPAPI value from Windows
            var payload = Convert.FromBase64String(cipherText[LinuxPrefix.Length..]);
            return Encoding.UTF8.GetString(DecryptWithKey(payload, GetLinuxKey()));
        }
        catch { return string.Empty; }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data)
        => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data)
        => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);

    /// <summary>Layout: 12-byte nonce, 16-byte tag, ciphertext.</summary>
    internal static byte[] EncryptWithKey(byte[] data, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag);
        var result = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        cipher.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    /// <summary>Throws when the key is wrong or the data was changed.</summary>
    internal static byte[] DecryptWithKey(byte[] payload, byte[] key)
    {
        if (payload.Length < NonceSize + TagSize) throw new CryptographicException("Encrypted value is too short.");
        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipher = payload.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private static byte[] GetLinuxKey()
    {
        lock (KeyLock)
            return _linuxKey ??= LoadOrCreateKey(DefaultKeyPath());
    }

    internal static string DefaultKeyPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("HIGHPOP_KEY_FILE");
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "highpop", "secret.key");
    }

    /// <summary>
    /// Reads the key, or creates it with owner-only permissions. A key file of the wrong size is an
    /// error rather than being replaced, because replacing it would make every saved secret unreadable.
    /// </summary>
    internal static byte[] LoadOrCreateKey(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (!File.Exists(path))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using var stream = new FileStream(path, options);
                stream.Write(RandomNumberGenerator.GetBytes(KeySize));
            }
            catch (IOException) when (File.Exists(path)) { /* another process created it first */ }
        }

        var key = File.ReadAllBytes(path);
        if (key.Length != KeySize)
            throw new InvalidDataException($"The key file {path} is not {KeySize} bytes. Restore it from a backup; HighPop will not replace it.");
        return key;
    }
}