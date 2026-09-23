using System.IO;
namespace OracleHost.Models;

/// <summary>
/// Configuration model for OracleHost.
/// </summary>
public class AppConfig
{
    // Supported Always Free shapes.
    // VM.Standard.E2.1.Micro: fixed 1/8 OCPU / 1 GB RAM (no shape config at launch).
    // VM.Standard.A1.Flex: ARM Ampere, Always Free allotment of 2 OCPU / 12 GB total.
    // (Oracle reduced the allotment from 4 OCPU / 24 GB.)
    public const string MicroShape = "VM.Standard.E2.1.Micro";
    public const string MicroLimitName = "standard-e2-micro-core-count";
    public const string A1FlexShape = "VM.Standard.A1.Flex";
    public const string A1FlexLimitName = "standard-a1-core-count";
    public const int AlwaysFreeMicroInstanceCount = 2;
    public const int AlwaysFreeA1OcpuCount = 2;
    public const int AlwaysFreeA1MemoryGb = 12;
    public const int DefaultMinRetrySeconds = 30;
    public const int DefaultMaxRetrySeconds = 60;

    public const int AlwaysFreeBootVolumeSizeGb = 50;
    public const int AlwaysFreeBlockStorageGb = 200;

    public string? Region { get; set; }
    public string CompartmentOcid { get; set; } = "";
    public string SubnetOcid { get; set; } = "";
    public string OciConfigPath { get; set; } = "~/.oci/config";
    public string SshPublicKeyPath { get; set; } = "~/.ssh/id_rsa.pub";
    public string Shape { get; set; } = MicroShape;
    /// <summary>OCPU count for flexible shapes (A1.Flex). Ignored by fixed shapes.</summary>
    public int? Ocpus { get; set; }
    /// <summary>Memory in GB for flexible shapes (A1.Flex). Ignored by fixed shapes.</summary>
    public int? MemoryInGbs { get; set; }
    public string ImageOs { get; set; } = "Oracle Linux";
    public string ImageVersion { get; set; } = "latest";
    public string DisplayName { get; set; } = "free-tier-micro";
    public bool AssignPublicIp { get; set; } = true;
    public int? BootVolumeSizeGb { get; set; }
    public string AvailabilityDomains { get; set; } = "all";
    public int MinIntervalSeconds { get; set; } = DefaultMinRetrySeconds;
    public int MaxIntervalSeconds { get; set; } = DefaultMaxRetrySeconds;
    public bool StopOnLimit { get; set; } = true;
    public int MaxAttempts { get; set; } = 0;
    public bool AllowExisting { get; set; } = false;

    /// <summary>True when the selected shape accepts an OCPU/memory shape config.</summary>
    public bool IsFlexibleShape =>
        string.Equals(Shape, A1FlexShape, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Always Free limit name that applies to the selected shape.</summary>
    public string ActiveLimitName =>
        IsFlexibleShape ? A1FlexLimitName : MicroLimitName;

    /// <summary>
    /// The instance-count/resource allowance for the selected shape, used by
    /// preflight to decide when the Always Free pool is already used up.
    /// </summary>
    public (string Description, int Count) AlwaysFreeAllowance =>
        IsFlexibleShape
            ? ($"{A1FlexShape} {AlwaysFreeA1OcpuCount} OCPU / {AlwaysFreeA1MemoryGb} GB", AlwaysFreeA1OcpuCount)
            : ($"{MicroShape} ×{AlwaysFreeMicroInstanceCount}", AlwaysFreeMicroInstanceCount);

    /// <summary>
    /// OCI authentication details (stored separately from instance config).
    /// These are used when ~/.oci/config is not available.
    /// </summary>
    public string? UserOcid { get; set; }
    public string? TenancyOcid { get; set; }
    public string? Fingerprint { get; set; }
    public string? KeyFilePath { get; set; }

    /// <summary>
    /// Content fingerprint of the AccountInfo bundle that produced this
    /// configuration. Startup rewrites config.json only when this no longer
    /// matches the current bundle.
    /// </summary>
    public string? AccountInfoFingerprint { get; set; }

    /// <summary>
    /// Returns true if we have enough OCI credentials configured.
    /// </summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(UserOcid) &&
        !string.IsNullOrWhiteSpace(TenancyOcid) &&
        !string.IsNullOrWhiteSpace(Fingerprint) &&
        !string.IsNullOrWhiteSpace(KeyFilePath);

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

        if (MinIntervalSeconds < 10 || MinIntervalSeconds > 600)
            problems.Add($"Minimum retry interval ({MinIntervalSeconds}s) is out of valid range (10-600).");
        if (MaxIntervalSeconds < 10 || MaxIntervalSeconds > 600)
            problems.Add($"Maximum retry interval ({MaxIntervalSeconds}s) is out of valid range (10-600).");
        if (MinIntervalSeconds > MaxIntervalSeconds)
            problems.Add($"Minimum retry interval ({MinIntervalSeconds}s) cannot exceed the maximum ({MaxIntervalSeconds}s).");

        // Always Free protection stays shape-aware: both supported shapes are
        // Always Free-eligible, but flexible sizing must stay within the
        // published A1 allotment so nothing billable can be launched.
        var isMicro = string.Equals(Shape, MicroShape, StringComparison.OrdinalIgnoreCase);
        var isA1 = string.Equals(Shape, A1FlexShape, StringComparison.OrdinalIgnoreCase);
        if (!isMicro && !isA1)
            problems.Add($"Always Free protection: only {MicroShape} or {A1FlexShape} is allowed.");

        if (isA1)
        {
            var ocpus = Ocpus ?? AlwaysFreeA1OcpuCount;
            var memory = MemoryInGbs ?? ocpus * 6; // A1 supports 6 GB per OCPU
            if (ocpus < 1 || ocpus > AlwaysFreeA1OcpuCount)
                problems.Add($"Always Free protection: A1 OCPUs must be between 1 and {AlwaysFreeA1OcpuCount}.");
            if (memory < 1 || memory > AlwaysFreeA1MemoryGb)
                problems.Add($"Always Free protection: A1 memory must be between 1 and {AlwaysFreeA1MemoryGb} GB.");
            if (memory < ocpus)
                problems.Add("A1 memory cannot be lower than the OCPU count (OCI minimum is 1 GB per OCPU).");
            if (memory > ocpus * 6)
                problems.Add($"A1 memory cannot exceed 6 GB per OCPU ({ocpus * 6} GB for {ocpus} OCPU).");
        }

        if (BootVolumeSizeGb.HasValue && BootVolumeSizeGb.Value < AlwaysFreeBootVolumeSizeGb)
            problems.Add($"Boot volume must be at least {AlwaysFreeBootVolumeSizeGb} GB because OCI requires a 50 GB minimum.");
        if (BootVolumeSizeGb.HasValue && BootVolumeSizeGb.Value > AlwaysFreeBlockStorageGb)
            problems.Add($"Boot volume cannot exceed {AlwaysFreeBlockStorageGb} GB. " +
                         $"The account-wide Always Free block-storage pool is {AlwaysFreeBlockStorageGb} GB, including boot volumes.");

        var supportedAlwaysFreeImages = new[] { "Oracle Linux", "Ubuntu", "Canonical Ubuntu" };
        if (!supportedAlwaysFreeImages.Any(image =>
                string.Equals(ImageOs, image, StringComparison.OrdinalIgnoreCase)) &&
            !ImageOs.Contains("aarch64", StringComparison.OrdinalIgnoreCase) &&
            !ImageOs.Contains("Oracle Linux", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Always Free protection: select an Always Free-eligible Oracle Linux or Ubuntu image.");
        }

        return problems;
    }
}
