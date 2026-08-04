using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace OracleHost.Helpers;

/// <summary>
/// Protects browser-session token files with Windows DPAPI for the current user.
/// Plaintext OCI CLI session files remain readable; OracleHost can migrate them
/// when the EncryptSessionTokens setting is enabled.
/// </summary>
public static class SessionTokenProtector
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ORACLEHOST-DPAPI-V1\n");
    private static readonly byte[] AdditionalEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("OracleHost/session-tokens/v1"));

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsProtectedFile(string path)
    {
        if (!File.Exists(path)) return false;

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < Magic.Length) return false;

            Span<byte> header = stackalloc byte[Magic.Length];
            return stream.Read(header) == Magic.Length && header.SequenceEqual(Magic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads either a plaintext token or an OracleHost DPAPI-protected token.</summary>
    public static string ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!HasMagic(bytes))
            return Encoding.UTF8.GetString(bytes);

        EnsureSupported();
        try
        {
            var encrypted = bytes[Magic.Length..];
            var plaintext = ProtectedData.Unprotect(
                encrypted, AdditionalEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Could not decrypt the OracleHost session file '{path}'. " +
                "It was protected for a different Windows user or machine; sign in again to create a new session.", ex);
        }
    }

    /// <summary>
    /// Writes a token as plaintext or DPAPI-protected data. Existing protected files
    /// stay protected even if the setting is later turned off.
    /// </summary>
    public static void WriteFile(string path, string value, bool protect)
    {
        if (protect) EnsureSupported();

        var plaintext = Encoding.UTF8.GetBytes(value);
        var output = protect
            ? Combine(Magic, ProtectedData.Protect(
                plaintext, AdditionalEntropy, DataProtectionScope.CurrentUser))
            : plaintext;
        WriteBytesAtomically(path, output);
    }

    /// <summary>
    /// Creates a current-user-only directory for temporary SDK material and
    /// removes abandoned session directories from earlier crashed runs.
    /// </summary>
    public static (string Directory, FileStream OwnerLock) CreatePrivateTemporaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "OracleHost");
        Directory.CreateDirectory(root);
        CleanupStaleTemporaryDirectories(root);

        var directory = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        FileStream? ownerLock = null;
        try
        {
            Directory.CreateDirectory(directory);
            if (IsSupported)
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                var currentUser = WindowsIdentity.GetCurrent().User
                    ?? throw new InvalidOperationException("Could not determine the current Windows user.");
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                new DirectoryInfo(directory).SetAccessControl(security);
            }

            // Acquire ownership before returning the directory. Startup cleanup
            // can now distinguish this live directory from crash leftovers.
            ownerLock = new FileStream(
                Path.Combine(directory, ".owner.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return (directory, ownerLock);
        }
        catch
        {
            ownerLock?.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
            throw;
        }
    }

    private static void CleanupStaleTemporaryDirectories(string root)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "session-*"))
            {
                try
                {
                    // A newly created directory has not had time to become
                    // stale, which also closes the tiny gap before its lock is
                    // acquired.
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        var lockPath = Path.Combine(directory, ".owner.lock");
                        if (File.Exists(lockPath))
                        {
                            // An active OracleHost keeps this lock open with
                            // FileShare.None; opening it exclusively fails.
                            using var ownerLock = new FileStream(
                                lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        }
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch { /* another OracleHost process may still own it */ }
            }
        }
        catch { /* best-effort startup cleanup */ }
    }

    /// <summary>
    /// Writes a temporary plaintext token without creating a second plaintext
    /// staging file. The caller must remove the file when the SDK is disposed.
    /// </summary>
    public static void WriteTemporaryPlaintextFile(string path, string value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // This file is intentionally plaintext because the OCI SDK requires a
        // path. Avoid WriteFile's atomic .tmp staging here: a crash could leave
        // an extra plaintext token behind in the temp directory.
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, options: FileOptions.SequentialScan);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(value);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Encrypts an existing plaintext file in place; already protected files are unchanged.</summary>
    public static void ProtectFileInPlace(string path)
    {
        if (!File.Exists(path) || IsProtectedFile(path)) return;
        WriteFile(path, File.ReadAllText(path, Encoding.UTF8), protect: true);
    }

    private static void EnsureSupported()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "Windows DPAPI session encryption is available only on Windows.");
    }

    private static bool HasMagic(byte[] bytes) =>
        bytes.Length >= Magic.Length && bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    private static byte[] Combine(byte[] first, byte[] second)
    {
        var combined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
        return combined;
    }

    private static void WriteBytesAtomically(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch { /* best-effort cleanup */ }
        }
    }
}
