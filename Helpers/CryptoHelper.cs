using System.Security.Cryptography;
using System.Text;

namespace OracleHost.Helpers;

/// <summary>
/// Generates RSA key pairs and computes OCI fingerprints for API authentication.
/// </summary>
public static class CryptoHelper
{
    /// <summary>
    /// Generates a 2048-bit RSA key pair and returns the PEM-encoded private key,
    /// PEM-encoded public key, and the MD5 fingerprint format required by OCI.
    /// </summary>
    public static (string PrivateKeyPem, string PublicKeyPem, string Fingerprint) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);

        var privateKeyPem =
            "-----BEGIN RSA PRIVATE KEY-----\n" +
            Convert.ToBase64String(rsa.ExportRSAPrivateKey(), Base64FormattingOptions.InsertLineBreaks) +
            "\n-----END RSA PRIVATE KEY-----";

        var publicKeyInfo = rsa.ExportSubjectPublicKeyInfo();
        var publicKeyPem = "-----BEGIN PUBLIC KEY-----\n" +
            Convert.ToBase64String(publicKeyInfo, Base64FormattingOptions.InsertLineBreaks) +
            "\n-----END PUBLIC KEY-----";

        // OCI fingerprints are the MD5 digest of the DER-encoded public key.
        var fingerprint = FormatOciFingerprint(publicKeyInfo);

        return (privateKeyPem, publicKeyPem, fingerprint);
    }

    /// <summary>
    /// Computes the OCI API-key fingerprint from a PEM-encoded public key.
    /// The private key is never needed for this calculation.
    /// </summary>
    public static string ComputeOciFingerprint(string publicKeyPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(publicKeyPath);
        if (!File.Exists(expanded))
            throw new FileNotFoundException($"Public API key not found at {expanded}");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(expanded));
        return FormatOciFingerprint(rsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Computes the fingerprint from the public half and verifies that it belongs
    /// to the selected private key. Key contents stay in memory and are never
    /// returned, logged, or copied.
    /// </summary>
    public static string ComputeOciFingerprint(string publicKeyPath, string privateKeyPath)
    {
        var expandedPublic = Environment.ExpandEnvironmentVariables(publicKeyPath);
        var expandedPrivate = Environment.ExpandEnvironmentVariables(privateKeyPath);
        if (!File.Exists(expandedPublic))
            throw new FileNotFoundException($"Public API key not found at {expandedPublic}");
        if (!File.Exists(expandedPrivate))
            throw new FileNotFoundException($"Private API key not found at {expandedPrivate}");

        using var publicRsa = RSA.Create();
        using var privateRsa = RSA.Create();
        publicRsa.ImportFromPem(File.ReadAllText(expandedPublic));
        privateRsa.ImportFromPem(File.ReadAllText(expandedPrivate));

        var publicKeyInfo = publicRsa.ExportSubjectPublicKeyInfo();
        var privateDerivedPublicKeyInfo = privateRsa.ExportSubjectPublicKeyInfo();
        if (!publicKeyInfo.AsSpan().SequenceEqual(privateDerivedPublicKeyInfo))
            throw new InvalidOperationException("The public and private OCI API key files do not match.");

        return FormatOciFingerprint(publicKeyInfo);
    }

    private static string FormatOciFingerprint(byte[] publicKeyInfo)
    {
        var digest = MD5.HashData(publicKeyInfo);
        return string.Join(":", digest.Select(b => b.ToString("x2")));
    }

    /// <summary>
    /// Reads an existing SSH public key from disk.
    /// </summary>
    public static string ReadSshPublicKey(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(expanded))
            throw new FileNotFoundException($"SSH public key not found at {expanded}");

        var key = File.ReadAllText(expanded).Trim();
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException($"SSH public key file is empty: {expanded}");

        if (!key.StartsWith("ssh-rsa") && !key.StartsWith("ssh-ed25519"))
            throw new InvalidOperationException(
                $"SSH key at {expanded} does not look like a public key (must start with 'ssh-rsa' or 'ssh-ed25519').");

        return key;
    }

    /// <summary>
    /// Generates an SSH RSA key pair using ssh-keygen.
    /// </summary>
    public static void GenerateSshKeyPair(string outputPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(outputPath);
        var dir = Path.GetDirectoryName(expanded);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ssh-keygen",
            Arguments = $"-t rsa -b 2048 -f \"{expanded}\" -N \"\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(10000);

        if (process?.ExitCode != 0)
        {
            var error = process?.StandardError.ReadToEnd();
            throw new InvalidOperationException($"ssh-keygen failed: {error}");
        }
    }
}
