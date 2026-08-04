using System.Text;
using Newtonsoft.Json;
using OracleHost.Helpers;
using OracleHost.Models;

namespace OracleHost.Services;

/// <summary>
/// Loads and saves OracleHost configuration (JSON format, matching Python version).
/// </summary>
public static class ConfigService
{
    /// <summary>
    /// Safe explanation of the most recent AccountInfo discovery failure.
    /// It never contains PEM contents or tokens.
    /// </summary>
    public static string? LastAccountInfoDiagnostic { get; private set; }

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Include
    };

    public static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OracleHost", "config.json");

    public static AppConfig Load(string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;
        if (!File.Exists(configPath))
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(configPath, Encoding.UTF8);
            return JsonConvert.DeserializeObject<AppConfig>(json, JsonSettings) ?? new AppConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Could not parse config at {configPath}: {ex.Message}", ex);
        }
    }

    public static void Save(AppConfig config, string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;
        var dir = Path.GetDirectoryName(configPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(config, JsonSettings);
        var tempPath = $"{configPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            // Write and flush a complete replacement before swapping it into
            // place. Startup migration updates config.json automatically, so a
            // direct write could otherwise leave it truncated after a crash.
            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(configPath))
            {
                try
                {
                    File.Replace(tempPath, configPath, destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    // File.Replace is unavailable on a few filesystems; both
                    // paths are on the same config directory, so this fallback
                    // preserves the best supported replacement behavior there.
                    File.Move(tempPath, configPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, configPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // A leftover temp file is harmless and will be overwritten on
                // the next save; never mask the original save result.
            }
        }
    }

    /// <summary>
    /// Reads the local AccountInfo/information.md file plus its adjacent PEM files.
    /// The private key is returned only as a path; it is read only in memory when
    /// necessary to verify that it matches the public key.
    /// </summary>
    public static AppConfig? ReadAccountInfo(string? directory = null)
    {
        LastAccountInfoDiagnostic = null;
        var accountDirectory = directory ?? Path.Combine(AppContext.BaseDirectory, "AccountInfo");
        var infoPath = Path.Combine(accountDirectory, "information.md");
        if (!File.Exists(infoPath))
        {
            LastAccountInfoDiagnostic = $"AccountInfo metadata was not found at {infoPath}.";
            DiagnosticLog.Warn("AccountInfo", LastAccountInfoDiagnostic);
            return null;
        }

        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Tenancy OCID", "Home region", "User OCID", "Compartment OCID",
                "Subnet OCID", "Fingerprint"
            };
            string? currentLabel = null;
            foreach (var rawLine in File.ReadAllLines(infoPath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (labels.Contains(line))
                {
                    currentLabel = line;
                    continue;
                }
                if (currentLabel != null && !values.ContainsKey(currentLabel))
                    values[currentLabel] = line;
            }

            var privateKeys = Directory.EnumerateFiles(accountDirectory, "*.pem")
                .Where(path => !Path.GetFileName(path)
                    .Contains("public", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var publicKeys = Directory.EnumerateFiles(accountDirectory, "*public*.pem")
                .ToArray();
            if (privateKeys.Length != 1)
            {
                LastAccountInfoDiagnostic = $"Expected exactly one private PEM in {accountDirectory}, found {privateKeys.Length}.";
                DiagnosticLog.Warn("AccountInfo", LastAccountInfoDiagnostic);
                return null;
            }
            if (publicKeys.Length > 1)
            {
                LastAccountInfoDiagnostic = $"Expected at most one public PEM in {accountDirectory}, found {publicKeys.Length}.";
                DiagnosticLog.Warn("AccountInfo", LastAccountInfoDiagnostic);
                return null;
            }
            var privateKey = privateKeys[0];
            var publicKey = publicKeys.SingleOrDefault();

            var tenancy = GetAccountValue(values, "Tenancy OCID");
            var user = GetAccountValue(values, "User OCID");
            var compartment = GetAccountValue(values, "Compartment OCID");
            var subnet = GetAccountValue(values, "Subnet OCID");
            var homeRegion = GetAccountValue(values, "Home region");
            var declaredFingerprint = GetAccountValue(values, "Fingerprint");
            var missing = new[]
            {
                ("Tenancy OCID", tenancy), ("User OCID", user),
                ("Compartment OCID", compartment), ("Subnet OCID", subnet)
            }.Where(item => string.IsNullOrWhiteSpace(item.Item2)).Select(item => item.Item1).ToArray();
            if (missing.Length > 0)
            {
                LastAccountInfoDiagnostic = $"AccountInfo is missing: {string.Join(", ", missing)}.";
                DiagnosticLog.Warn("AccountInfo", LastAccountInfoDiagnostic);
                return null;
            }

            var region = homeRegion?.Trim() switch
            {
                "IAD" => "us-ashburn-1",
                _ => homeRegion?.Trim() ?? "us-ashburn-1"
            };
            var derivedFingerprint = publicKey == null
                ? CryptoHelper.ComputeOciFingerprint(privateKey)
                : CryptoHelper.ComputeOciFingerprint(publicKey, privateKey);

            if (!string.IsNullOrWhiteSpace(declaredFingerprint) &&
                !string.Equals(declaredFingerprint.Trim(), derivedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The fingerprint in information.md ({declaredFingerprint.Trim()}) does not match the supplied PEM key pair (derived {derivedFingerprint}).");
            }

            DiagnosticLog.Info("AccountInfo", $"Parsed OCI metadata for region {region}; PEM fingerprint verified as {derivedFingerprint}.");
            LastAccountInfoDiagnostic = $"Local PEM pair verified. Fingerprint: {derivedFingerprint}.";
            return new AppConfig
            {
                Region = region,
                CompartmentOcid = compartment!,
                SubnetOcid = subnet!,
                UserOcid = user!,
                TenancyOcid = tenancy!,
                KeyFilePath = Path.GetFullPath(privateKey),
                Fingerprint = derivedFingerprint
            };
        }
        catch (Exception ex)
        {
            LastAccountInfoDiagnostic = $"AccountInfo could not be used: {ex.Message}";
            DiagnosticLog.Exception("AccountInfo", $"Read credential metadata from {infoPath}", ex);
            return null;
        }
    }

    private static string? GetAccountValue(Dictionary<string, string> values, string label) =>
        values.TryGetValue(label, out var value) ? value : null;

    /// <summary>
    /// Reads the OCI config file (~/.oci/config) and extracts credentials.
    /// </summary>
    public static (string UserOcid, string TenancyOcid, string Fingerprint, string KeyFilePath, string Region)? ReadOciConfig(string? ociConfigPath = null)
    {
        var path = ociConfigPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oci", "config");
        if (!File.Exists(path))
            return null;

        try
        {
            var lines = File.ReadAllLines(path);
            string? user = null, tenancy = null, fingerprint = null, keyFile = null, region = null;
            bool inDefault = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("[DEFAULT]") || line.StartsWith("[default]"))
                {
                    inDefault = true;
                    continue;
                }
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inDefault = false;
                    continue;
                }
                if (!inDefault && user != null) continue; // already parsed DEFAULT section

                var eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = line[..eqIdx].Trim();
                var value = line[(eqIdx + 1)..].Trim();

                switch (key.ToLowerInvariant())
                {
                    case "user": user = value; break;
                    case "tenancy": tenancy = value; break;
                    case "fingerprint": fingerprint = value; break;
                    case "key_file": keyFile = value; break;
                    case "region": region = value; break;
                }
            }

            if (user != null && tenancy != null && fingerprint != null && keyFile != null)
                return (user, tenancy, fingerprint, keyFile, region ?? "us-ashburn-1");

            return null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("OCI Config", $"Read OCI config from {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// Writes a new OCI config file.
    /// </summary>
    public static void WriteOciConfig(string userOcid, string tenancyOcid, string fingerprint, string keyFilePath, string region, string? existingPath = null)
    {
        var path = existingPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oci", "config");
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("[DEFAULT]");
        sb.AppendLine($"user={userOcid}");
        sb.AppendLine($"fingerprint={fingerprint}");
        sb.AppendLine($"tenancy={tenancyOcid}");
        sb.AppendLine($"region={region}");
        sb.AppendLine($"key_file={keyFilePath}");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

        // Try to set restrictive permissions on non-Windows
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* ignore permission errors */ }
        }
    }
}
