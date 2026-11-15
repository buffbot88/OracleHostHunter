using System.Security.Cryptography;
using System.Text;
using System.IO;

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
    /// Validates an OpenSSH-format authorized_keys line (type + base64 blob,
    /// optional trailing comment). Rejects PEM/OpenSSH private-key blobs.
    /// Returns the normalized key without the trailing comment path handling.
    /// </summary>
    public static (string KeyType, string Base64Blob) ValidateSshPublicKeyFormat(string key, string? displayPath = null)
    {
        var where = string.IsNullOrWhiteSpace(displayPath) ? "SSH public key" : $"SSH key at {displayPath}";
        var trimmed = (key ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidOperationException($"{where} is empty.");

        if (trimmed.Contains("-----BEGIN", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{where} contains a PEM/private-key block, not an OpenSSH public key (must start with 'ssh-rsa' or 'ssh-ed25519').");

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            throw new InvalidOperationException(
                $"{where} is malformed: expected '<ssh-rsa|ssh-ed25519> <base64>' (a 'A base64' line without a key type is not a public key).");

        var keyType = tokens[0];
        var blob = tokens[1];
        if (keyType != "ssh-rsa" && keyType != "ssh-ed25519")
            throw new InvalidOperationException(
                $"{where} does not look like a public key (must start with 'ssh-rsa' or 'ssh-ed25519').");

        byte[] decoded;
        try { decoded = Convert.FromBase64String(blob); }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{where} has an invalid base64 public-key blob.", ex);
        }

        // Verify the blob's embedded algorithm matches the line's type.
        var embeddedAlgo = ReadSshStringPrefix(decoded);
        if (!string.Equals(embeddedAlgo, keyType, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{where} is malformed: the key blob algorithm ({embeddedAlgo ?? "unknown"}) does not match '{keyType}'.");

        if (keyType == "ssh-rsa")
        {
            var bits = TryReadRsaModulusBits(decoded);
            if (bits.HasValue && bits.Value < 2048)
                DiagnosticLog.Warn("SSH", $"RSA SSH key is only {bits.Value} bits; 2048-bit or stronger is recommended.");
        }

        return (keyType, blob);
    }

    private static string? ReadSshStringPrefix(byte[] blob)
    {
        if (blob.Length < 4) return null;
        var len = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
        if (len <= 0 || 4 + len > blob.Length) return null;
        return Encoding.ASCII.GetString(blob, 4, len);
    }

    private static int? TryReadRsaModulusBits(byte[] blob)
    {
        // SSH wire format for ssh-rsa: string algo, mpint e, mpint n.
        try
        {
            var offset = 4;
            if (blob.Length < 8) return null;
            var algoLen = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
            offset = 4 + algoLen;
            if (offset + 4 > blob.Length) return null;
            var eLen = (blob[offset] << 24) | (blob[offset + 1] << 16) | (blob[offset + 2] << 8) | blob[offset + 3];
            offset += 4 + eLen;
            if (offset + 4 > blob.Length) return null;
            var nLen = (blob[offset] << 24) | (blob[offset + 1] << 16) | (blob[offset + 2] << 8) | blob[offset + 3];
            offset += 4;
            if (nLen <= 0 || offset + nLen > blob.Length) return null;
            // n is a big-endian mpint (may have a leading 0x00 byte).
            var firstSignificant = offset;
            while (firstSignificant < offset + nLen && blob[firstSignificant] == 0) firstSignificant++;
            var significantBytes = Math.Max(1, offset + nLen - firstSignificant);
            return significantBytes * 8;
        }
        catch { return null; }
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
        ValidateSshPublicKeyFormat(key, expanded);
        return key;
    }

    /// <summary>
    /// Derives the expected private-key path for a configured public-key path:
    /// explicit path when provided, otherwise the `.pub` suffix stripped
    /// (`id_rsa.pub` → `id_rsa`).
    /// </summary>
    public static string DerivePrivateKeyPath(string publicKeyPath, string? explicitPrivatePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPrivatePath))
            return Environment.ExpandEnvironmentVariables(explicitPrivatePath);
        var expanded = Environment.ExpandEnvironmentVariables(publicKeyPath);
        return expanded.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
            ? expanded[..^4]
            : expanded;
    }

    /// <summary>
    /// Validates that the SSH public key matches the private key that will be
    /// used to log in. When the private half does not exist, only the public
    /// half's format is validated and pairing is reported as unverified.
    /// Key contents are never returned or logged.
    /// </summary>
    public static bool ValidateSshKeyPair(string publicKeyPath, string? privateKeyPath)
    {
        var expandedPublic = Environment.ExpandEnvironmentVariables(publicKeyPath);
        if (!File.Exists(expandedPublic))
            throw new FileNotFoundException($"SSH public key not found at {expandedPublic}");
        var key = File.ReadAllText(expandedPublic).Trim();
        var (keyType, blob) = ValidateSshPublicKeyFormat(key, expandedPublic);

        var expandedPrivate = Environment.ExpandEnvironmentVariables(privateKeyPath ?? DerivePrivateKeyPath(expandedPublic, null));
        if (!File.Exists(expandedPrivate))
        {
            DiagnosticLog.Warn("SSH", $"SSH pairing NOT verified: no private key found at {DiagnosticLog.SafeFileName(expandedPrivate)}; public-key format only. SSH login cannot be proven until the matching private key is present.");
            return false;
        }

        var derived = DerivePublicKeyFromPrivate(expandedPrivate);
        var derivedTokens = derived.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (derivedTokens.Length < 2 || derivedTokens[0] != keyType || derivedTokens[1] != blob)
            throw new InvalidOperationException(
                $"SSH public key at {expandedPublic} will not match the private SSH key at {expandedPrivate}. Select the matching key pair.");
        return true;
    }

    /// <summary>Derives the OpenSSH public line for a private key without writing files.</summary>
    private static string DerivePublicKeyFromPrivate(string expandedPrivate)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ssh-keygen",
            Arguments = $"-y -f \"{expandedPrivate}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        System.Diagnostics.Process? process;
        try { process = System.Diagnostics.Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"ssh-keygen is not available to verify the SSH key pair for {DiagnosticLog.SafeFileName(expandedPrivate)}. Install OpenSSH client tools or verify the key pair manually.", ex);
        }
        using var owned = process;
        process?.WaitForExit(10000);
        if (process?.ExitCode != 0)
        {
            var error = process?.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"ssh-keygen could not verify the SSH key pair for {DiagnosticLog.SafeFileName(expandedPrivate)}: {error}");
        }
        var derived = process!.StandardOutput.ReadToEnd().Trim();
        if (string.IsNullOrEmpty(derived))
            throw new InvalidOperationException("ssh-keygen returned an empty public key.");
        return derived;
    }

    /// <summary>
    /// Regenerates the .pub file from an existing private key. Used when the
    /// public half was deleted but the private half is still present.
    /// </summary>
    public static void DeriveSshPublicKey(string privatePath, string publicPath)
    {
        var expandedPrivate = Environment.ExpandEnvironmentVariables(privatePath);
        var expandedPublic = Environment.ExpandEnvironmentVariables(publicPath);
        if (!File.Exists(expandedPrivate))
            throw new FileNotFoundException($"SSH private key not found at {expandedPrivate}");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ssh-keygen",
            Arguments = $"-y -f \"{expandedPrivate}\"",
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
            throw new InvalidOperationException($"ssh-keygen could not derive the public key: {error}");
        }

        var publicKey = process!.StandardOutput.ReadToEnd().Trim();
        if (string.IsNullOrEmpty(publicKey))
            throw new InvalidOperationException("ssh-keygen returned an empty public key.");

        File.WriteAllText(expandedPublic, publicKey + Environment.NewLine);
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
