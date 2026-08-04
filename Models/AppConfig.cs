namespace OracleHost.Models;

/// <summary>
/// Configuration model for OracleHost. Matches the Python config.json schema.
/// </summary>
public class AppConfig
{
    // Conservative limits from OCI's current Always Free A1 documentation. OCI requires a 50 GB minimum boot volume.
    // Oracle may change account-specific allocations, but this guard intentionally
    // stays below the documented ceiling rather than trying to spend quota.
    // Per-launch limits intentionally chosen by the user to stay conservative.
    public const int HunterMaxOcpus = 1;
    public const int HunterMaxMemoryGb = 4;
    public const int DefaultMinRetrySeconds = 30;
    public const int DefaultMaxRetrySeconds = 60;

    // Conservative account-wide guard used when checking existing A1 usage.
    public const int AlwaysFreeA1Ocpus = 2;
    public const int AlwaysFreeA1MemoryGb = 12;
    public const int AlwaysFreeBootVolumeSizeGb = 50;
    public const int AlwaysFreeBlockStorageGb = 200;

    public string? Region { get; set; }
    public string CompartmentOcid { get; set; } = "";
    public string SubnetOcid { get; set; } = "";
    public string OciConfigPath { get; set; } = "~/.oci/config";
    public string SshPublicKeyPath { get; set; } = "~/.ssh/id_rsa.pub";
    public string Shape { get; set; } = "VM.Standard.A1.Flex";
    public int Ocpus { get; set; } = HunterMaxOcpus;
    public int MemoryInGb { get; set; } = HunterMaxMemoryGb;
    public string ImageOs { get; set; } = "Oracle Linux";
    public string ImageVersion { get; set; } = "latest";
    public string DisplayName { get; set; } = "free-tier-arm";
    public bool AssignPublicIp { get; set; } = true;
    public int? BootVolumeSizeGb { get; set; }
    public string AvailabilityDomains { get; set; } = "all";
    public int MinIntervalSeconds { get; set; } = DefaultMinRetrySeconds;
    public int MaxIntervalSeconds { get; set; } = DefaultMaxRetrySeconds;
    public bool StopOnLimit { get; set; } = true;
    public int MaxAttempts { get; set; } = 0;
    public bool AllowExisting { get; set; } = false;

    /// <summary>
    /// OCI authentication details (stored separately from instance config).
    /// These are used when ~/.oci/config is not available.
    /// </summary>
    public string? UserOcid { get; set; }
    public string? TenancyOcid { get; set; }
    public string? Fingerprint { get; set; }
    public string? KeyFilePath { get; set; }

    /// <summary>
    /// Browser-login (session token) auth. When a session is captured via
    /// "Sign in with Oracle", its config path is stored here and the SDK's
    /// SessionTokenAuthenticationDetailsProvider is used instead of API keys.
    /// </summary>
    public string? SessionConfigPath { get; set; }
    public string? OciIdentityDomain { get; set; }
    public string? OciOauthClientId { get; set; }

    /// <summary>
    /// Protects browser-session token files with Windows DPAPI for the current user,
    /// migrating plaintext sessions on next use; defaults to false for OCI CLI compatibility.
    /// </summary>
    public bool EncryptSessionTokens { get; set; } = false;

    /// <summary>
    /// Returns true if we have enough OCI credentials configured.
    /// </summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(UserOcid) &&
        !string.IsNullOrWhiteSpace(TenancyOcid) &&
        !string.IsNullOrWhiteSpace(Fingerprint) &&
        !string.IsNullOrWhiteSpace(KeyFilePath);

    /// <summary>
    /// Returns true if a browser-captured session token is available on disk.
    /// </summary>
    public bool HasSession =>
        !string.IsNullOrWhiteSpace(SessionConfigPath) && File.Exists(SessionConfigPath);

    /// <summary>
    /// Validates the config and returns a list of problems.
    /// </summary>
    public List<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(CompartmentOcid))
            problems.Add("Compartment OCID is missing.");
        if (string.IsNullOrWhiteSpace(SubnetOcid))
            problems.Add("Subnet OCID is missing.");

        var expandedKeyPath = Environment.ExpandEnvironmentVariables(SshPublicKeyPath);
        if (!File.Exists(expandedKeyPath))
            problems.Add($"SSH public key not found at {expandedKeyPath}. Generate with: ssh-keygen -t rsa -b 2048 -f ~/.ssh/id_rsa");

        if (Ocpus < 1 || Ocpus > 24)
            problems.Add($"OCPUs ({Ocpus}) is out of valid range (1-24).");
        if (MemoryInGb < 1 || MemoryInGb > 256)
            problems.Add($"Memory ({MemoryInGb} GB) is out of valid range (1-256).");
        if (MinIntervalSeconds < 10 || MinIntervalSeconds > 600)
            problems.Add($"Minimum retry interval ({MinIntervalSeconds}s) is out of valid range (10-600).");
        if (MaxIntervalSeconds < 10 || MaxIntervalSeconds > 600)
            problems.Add($"Maximum retry interval ({MaxIntervalSeconds}s) is out of valid range (10-600).");
        if (MinIntervalSeconds > MaxIntervalSeconds)
            problems.Add($"Minimum retry interval ({MinIntervalSeconds}s) cannot exceed the maximum ({MaxIntervalSeconds}s).");

        // OracleHost is intentionally Always Free-only. Do not silently launch
        // a larger shape or storage volume that could become billable.
        if (!string.Equals(Shape, "VM.Standard.A1.Flex", StringComparison.OrdinalIgnoreCase))
            problems.Add("Always Free protection: only VM.Standard.A1.Flex is allowed.");
        if (Ocpus != HunterMaxOcpus)
            problems.Add($"Hunter safety protection: each launch must use exactly {HunterMaxOcpus} OCPU.");
        if (MemoryInGb != HunterMaxMemoryGb)
            problems.Add($"Hunter safety protection: each launch must use exactly {HunterMaxMemoryGb} GB RAM.");
        if (BootVolumeSizeGb.HasValue && BootVolumeSizeGb.Value < AlwaysFreeBootVolumeSizeGb)
            problems.Add($"Boot volume must be at least {AlwaysFreeBootVolumeSizeGb} GB because OCI requires a 50 GB minimum.");
        if (BootVolumeSizeGb.HasValue && BootVolumeSizeGb.Value > AlwaysFreeBootVolumeSizeGb)
            problems.Add($"Always Free protection: boot volume cannot exceed {AlwaysFreeBootVolumeSizeGb} GB. " +
                         $"The account-wide Always Free block-storage pool is {AlwaysFreeBlockStorageGb} GB, including boot volumes.");

        var supportedAlwaysFreeImages = new[] { "Oracle Linux", "Ubuntu" };
        if (!supportedAlwaysFreeImages.Any(image =>
                string.Equals(ImageOs, image, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("Always Free protection: select an Always Free-eligible Oracle Linux or Ubuntu image.");
        }

        return problems;
    }
}
