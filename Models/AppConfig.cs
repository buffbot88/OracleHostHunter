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
    // Monthly A1 metering for the Always Free allotment.
    public const int AlwaysFreeA1OcpuHoursPerMonth = 1500;
    public const int AlwaysFreeA1MemoryGbHoursPerMonth = 9000;
    // Oracle Always Free allows at most two VMs for each supported compute tier.
    public const int AlwaysFreeMaxInstancesPerShape = 2;
    // Legacy name retained for config/UI callers; limit applies per selected shape.
    public const int AlwaysFreeMaxInstanceCount = AlwaysFreeMaxInstancesPerShape;
    public const int AlwaysFreeMaxBootVolumePoolGb = 200;
    public const int AlwaysFreeMinBootVolumePerInstanceGb = 50;
    // Up to 5 block/boot volume backups may exist at any time.
    public const int AlwaysFreeMaxVolumeBackups = 5;
    // Combined Standard + Infrequent Access + Archive object storage.
    public const int AlwaysFreeObjectStorageGb = 20;
    // Databases & developer services (informational caps, not enforced at launch).
    public const int AlwaysFreeAutonomousDbCount = 2;
    public const long AlwaysFreeNoSqlReadsWritesPerMonth = 133_000_000;
    public const int AlwaysFreeNoSqlTableCount = 3;
    public const int AlwaysFreeNoSqlStorageGbPerTable = 25;
    public const int AlwaysFreeMysqlStorageGb = 50;
    public const int AlwaysFreeMysqlBackupGb = 50;
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

    /// <summary>
    /// Legacy single-instance display name kept for backward compatibility.
    /// In v2 shared-template mode the active name is derived from
    /// DisplayNameBase plus the instance index, but existing saved configs
    /// continue to round-trip cleanly.
    /// </summary>
    public string DisplayName { get; set; } = "free-tier-micro";

    /// <summary>
    /// Base display name for instances launched from the shared template.
    /// Each instance is suffixed as "{DisplayNameBase}-{n}" so multiple
    /// servers can be told apart in the OCI console and SSH commands.
    /// </summary>
    public string DisplayNameBase { get; set; } = "free-tier-instance";
    public bool AssignPublicIp { get; set; } = true;
    public int? BootVolumeSizeGb { get; set; }
    public string AvailabilityDomains { get; set; } = "all";
    public int MinIntervalSeconds { get; set; } = DefaultMinRetrySeconds;
    public int MaxIntervalSeconds { get; set; } = DefaultMaxRetrySeconds;
    public bool StopOnLimit { get; set; } = true;
    public int MaxAttempts { get; set; } = 0;
    public bool AllowExisting { get; set; } = false;

    /// <summary>
    /// Target number of instances to create in the selected compute tier.
    /// Each tier is capped at two instances; the overall 200 GB boot pool is
    /// shared across tiers and may impose a lower effective count.
    /// </summary>
    public int DesiredInstanceCount { get; set; } = 1;

    /// <summary>True when the selected shape accepts an OCPU/memory shape config.</summary>
    public bool IsFlexibleShape =>
        string.Equals(Shape, A1FlexShape, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Always Free limit name that applies to the selected shape.</summary>
    public string ActiveLimitName =>
        IsFlexibleShape ? A1FlexLimitName : MicroLimitName;

    /// <summary>
    /// The Always Free instance allowance for the selected shape. For the
    /// micro shape this is a fixed instance count; for A1.Flex this is the
    /// OCPU-based allowance, because A1 sizing is flexible.
    /// </summary>
    public (string Description, int Count) AlwaysFreeAllowance =>
        IsFlexibleShape
            ? ($"{A1FlexShape} {AlwaysFreeA1OcpuCount} OCPU / {AlwaysFreeA1MemoryGb} GB", AlwaysFreeA1OcpuCount)
            : ($"{MicroShape} ×{AlwaysFreeMicroInstanceCount}", AlwaysFreeMicroInstanceCount);

    /// <summary>
    /// Practical maximum number of instances the current shared template can
    /// create without exceeding the Always Free boot-volume pool. This assumes
    /// every instance uses the configured boot volume; the minimum enforced
    /// boot volume is 50 GB and the account-wide pool is 200 GB, so the
    /// per-tier ceiling is 2 instances.
    /// </summary>
    public int MaxInstancesFromBootPool =>
        BootVolumeSizeGb.HasValue && BootVolumeSizeGb.Value > 0
            ? Math.Min(AlwaysFreeMaxInstancesPerShape,
                AlwaysFreeMaxBootVolumePoolGb / BootVolumeSizeGb.Value)
            : AlwaysFreeMaxInstancesPerShape;

    /// <summary>
    /// Maximum instances of the selected shape supported by its per-tier and
    /// shared resource allowances using the current per-instance sizing.
    /// </summary>
    public int MaxInstancesForCurrentTemplate
    {
        get
        {
            var max = MaxInstancesFromBootPool;
            if (IsFlexibleShape)
            {
                var ocpus = Ocpus ?? AlwaysFreeA1OcpuCount;
                var memory = MemoryInGbs ?? ocpus * 6;
                if (ocpus > 0)
                    max = Math.Min(max, AlwaysFreeA1OcpuCount / ocpus);
                if (memory > 0)
                    max = Math.Min(max, AlwaysFreeA1MemoryGb / memory);
            }
            else if (string.Equals(Shape, MicroShape, StringComparison.OrdinalIgnoreCase))
            {
                max = Math.Min(max, AlwaysFreeMicroInstanceCount);
            }
            else
            {
                return 0;
            }

            return Math.Max(0, max);
        }
    }

    /// <summary>
    /// OCI authentication details (stored separately from instance config).
    /// These are used when ~/.oci/config is not available.
    /// </summary>
    public string? UserOcid { get; set; }
    public string? TenancyOcid { get; set; }
    public string? Fingerprint { get; set; }
    public string? KeyFilePath { get; set; }

    /// <summary>True when inline API-key credentials are fully populated.</summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(UserOcid) &&
        !string.IsNullOrWhiteSpace(TenancyOcid) &&
        !string.IsNullOrWhiteSpace(Fingerprint) &&
        !string.IsNullOrWhiteSpace(KeyFilePath);

    /// <summary>
    /// Content fingerprint of the AccountInfo bundle that produced this
    /// configuration. Startup rewrites config.json only when this no longer
    /// matches the current bundle.
    /// </summary>
    public string? AccountInfoFingerprint { get; set; }

    /// <summary>True once the first-run walkthrough has been finished or skipped.</summary>
    public bool HasSeenOnboarding { get; set; } = false;

    /// <summary>
    /// Returns the effective target count after clamping to what the Always
    /// Free pools can actually support for the current template.
    /// </summary>
    public int EffectiveInstanceCount =>
        Math.Min(DesiredInstanceCount, MaxInstancesForCurrentTemplate);

    /// <summary>
    /// Builds the per-instance display name for the shared template. The
    /// first instance is "{DisplayNameBase}-1", the second is
    /// "{DisplayNameBase}-2", and so on. Falls back to the legacy single
    /// display name when a caller only wants one instance.
    /// </summary>
    public string InstanceDisplayName(int instanceNumber) =>
        EffectiveInstanceCount > 1
            ? $"{DisplayNameBase}-{instanceNumber}"
            : !string.IsNullOrWhiteSpace(DisplayNameBase)
                ? DisplayNameBase
                : DisplayName;

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

        if (DesiredInstanceCount < 1 || DesiredInstanceCount > AlwaysFreeMaxInstancesPerShape)
            problems.Add($"Desired instance count must be between 1 and {AlwaysFreeMaxInstancesPerShape} for the selected compute tier.");

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

            // A1 sizing is shared by every instance in the current template, so
            // total OCPUs/memory include the desired instance count.
            if (ocpus * DesiredInstanceCount > AlwaysFreeA1OcpuCount)
                problems.Add($"This template uses {ocpus} OCPU(s) per instance " +
                              $"({ocpus * DesiredInstanceCount} OCPU total for {DesiredInstanceCount} instance(s)), " +
                              $"which exceeds the Always Free A1 allowance of {AlwaysFreeA1OcpuCount} OCPU.");
            if (memory * DesiredInstanceCount > AlwaysFreeA1MemoryGb)
                problems.Add($"This template uses {memory} GB memory per instance " +
                              $"({memory * DesiredInstanceCount} GB total for {DesiredInstanceCount} instance(s)), " +
                              $"which exceeds the Always Free A1 allowance of {AlwaysFreeA1MemoryGb} GB.");
        }
        else if (isMicro)
        {
            if (DesiredInstanceCount > AlwaysFreeMicroInstanceCount)
                problems.Add($"The Always Free allowance for {MicroShape} is {AlwaysFreeMicroInstanceCount} instance(s). " +
                              $"Use fewer micro instances or switch the template to {A1FlexShape} if the boot-volume pool still has room.");
        }

        var bootPerInstance = BootVolumeSizeGb ?? AlwaysFreeBootVolumeSizeGb;
        if (DesiredInstanceCount > 0 && DesiredInstanceCount <= AlwaysFreeMaxInstancesPerShape &&
            DesiredInstanceCount > MaxInstancesForCurrentTemplate)
            problems.Add($"The selected shape, per-server sizing, and boot volume support at most {MaxInstancesForCurrentTemplate} instance(s) within the Always Free limits.");
        if (bootPerInstance < AlwaysFreeBootVolumeSizeGb)
            problems.Add($"Boot volume must be at least {AlwaysFreeBootVolumeSizeGb} GB because OCI requires a 50 GB minimum.");
        if (bootPerInstance > AlwaysFreeBlockStorageGb)
            problems.Add($"Boot volume cannot exceed {AlwaysFreeBlockStorageGb} GB. " +
                         $"The account-wide Always Free block-storage pool is {AlwaysFreeBlockStorageGb} GB, including boot volumes.");
        if (bootPerInstance * DesiredInstanceCount > AlwaysFreeBlockStorageGb)
            problems.Add($"Total boot volume ({bootPerInstance * DesiredInstanceCount} GB for " +
                          $"{DesiredInstanceCount} instance(s)) exceeds the {AlwaysFreeBlockStorageGb} GB Always Free pool.");

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
