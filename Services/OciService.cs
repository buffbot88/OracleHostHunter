using Oci.Common;
using Oci.Common.Auth;
using Oci.CoreService;
using Oci.CoreService.Models;
using Oci.CoreService.Requests;
using Oci.IdentityService;
using Oci.IdentityService.Models;
using Oci.IdentityService.Requests;
using Oci.LimitsService;
using Oci.LimitsService.Requests;
using OracleHost.Helpers;
using OracleHost.Models;
using Image = Oci.CoreService.Models.Image;
using System.IO;

namespace OracleHost.Services;

/// <summary>
/// Wraps the OCI .NET SDK for compute, networking, identity, and limits operations.
/// Auth uses inline API keys (from AccountInfo), then ~/.oci/config.
/// </summary>
public class OciService : IDisposable
{
    private ComputeClient? _compute;
    private IdentityClient? _identity;
    private VirtualNetworkClient? _network;
    private LimitsClient? _limits;
    private string? _tenancyId;
    private string? _tempConfigPath;

    public bool IsConfigured => _compute != null && _identity != null;
    public string? Region { get; private set; }

    public void Initialize(AppConfig config)
    {
        try
        {
            var authProvider = CreateAuthProvider(config);
            _compute = new ComputeClient(authProvider);
            _identity = new IdentityClient(authProvider);
            _network = new VirtualNetworkClient(authProvider);
            _limits = new LimitsClient(authProvider);
            Region = config.Region ?? ExtractFromConfig(GetOciConfigPath(config), "region") ?? "us-ashburn-1";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("OCI Auth", "Initialize OCI clients", ex);
            // If provider construction fails after writing a temp config, do
            // not leave it behind while the caller handles the error.
            Dispose();
            throw;
        }
    }

    private IAuthenticationDetailsProvider CreateAuthProvider(AppConfig config)
    {
        // 1) Inline API-key credentials: verify the selected private key really
        // produces the configured fingerprint before asking OCI to sign anything.
        // This prevents a stale config path/fingerprint pair from producing the
        // SDK's vague "authentication was incorrect" response.
        if (config.HasCredentials)
        {
            ValidateApiKeyCredentials(config);
            _tenancyId = config.TenancyOcid;
            _tempConfigPath = WriteTempConfig(config);
            return new ConfigFileAuthenticationDetailsProvider(_tempConfigPath, "DEFAULT");
        }

        // 2) Existing ~/.oci/config file
        var resolvedPath = GetOciConfigPath(config);
        if (File.Exists(resolvedPath))
        {
            try
            {
                var provider = new ConfigFileAuthenticationDetailsProvider(resolvedPath, "DEFAULT");
                _tenancyId = ExtractFromConfig(resolvedPath, "tenancy");
                return provider;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Exception("OCI Auth", $"Load OCI config provider from {resolvedPath}", ex);
            }
        }

        DiagnosticLog.Error("OCI Auth", "No usable OCI credentials were found after checking inline API credentials and ~/.oci/config.");
        throw new InvalidOperationException(
            "No OCI credentials found. Provide the AccountInfo folder or configure ~/.oci/config.");
    }

    private static void ValidateApiKeyCredentials(AppConfig config)
    {
        var keyPath = Environment.ExpandEnvironmentVariables(config.KeyFilePath!);
        if (!File.Exists(keyPath))
            throw new FileNotFoundException($"OCI private API key was not found at {keyPath}.");

        string derivedFingerprint;
        try
        {
            derivedFingerprint = CryptoHelper.ComputeOciFingerprint(keyPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"OCI private API key could not be read as a valid PEM key ({DiagnosticLog.SafeFileName(keyPath)}). " +
                "Select the private API key file, not the .pub file.", ex);
        }

        var configuredFingerprint = NormalizeFingerprint(config.Fingerprint);
        if (!string.Equals(configuredFingerprint, NormalizeFingerprint(derivedFingerprint), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The configured OCI fingerprint ({config.Fingerprint}) does not match the selected private key " +
                $"({derivedFingerprint}). Select the matching key or update the fingerprint registered in OCI.");
        }

        DiagnosticLog.Info("OCI Auth",
            $"Using API-key credentials: user={DiagnosticLog.RedactIdentifier(config.UserOcid)}, " +
            $"tenancy={DiagnosticLog.RedactIdentifier(config.TenancyOcid)}, " +
            $"fingerprint={derivedFingerprint}, key file={DiagnosticLog.SafeFileName(keyPath)}.");
    }

    private static string NormalizeFingerprint(string? fingerprint)
    {
        var normalized = new string((fingerprint ?? string.Empty)
            .Where(c => c != ':' && !char.IsWhiteSpace(c))
            .Select(char.ToLowerInvariant)
            .ToArray());

        if (normalized.Length != 32 || normalized.Any(c =>
                (c < '0' || c > '9') && (c < 'a' || c > 'f')))
        {
            throw new InvalidOperationException(
                "The OCI fingerprint must contain exactly 32 hexadecimal characters " +
                "(normally written as 16 colon-separated byte pairs).");
        }

        return normalized;
    }

    private static string WriteTempConfig(AppConfig config)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OracleHost");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, "oci_config");
        var keyPath = Environment.ExpandEnvironmentVariables(config.KeyFilePath!);
        var content = $"[DEFAULT]\n" +
            $"user={config.UserOcid}\n" +
            $"fingerprint={config.Fingerprint}\n" +
            $"tenancy={config.TenancyOcid}\n" +
            $"region={config.Region ?? "us-ashburn-1"}\n" +
            $"key_file={keyPath}\n";
        File.WriteAllText(tempPath, content);
        return tempPath;
    }

    private static string GetOciConfigPath(AppConfig config)
    {
        var ociConfigPath = Environment.ExpandEnvironmentVariables(config.OciConfigPath ?? "~/.oci/config");
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oci", "config");
        return File.Exists(ociConfigPath) ? ociConfigPath : defaultPath;
    }

    private static string? ExtractFromConfig(string configPath, string key)
    {
        if (!File.Exists(configPath)) return null;
        foreach (var line in File.ReadAllLines(configPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return trimmed[(key.Length + 1)..].Trim();
        }
        return null;
    }

    public async Task<List<string>> ListAvailabilityDomainsAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        var request = new ListAvailabilityDomainsRequest { CompartmentId = _tenancyId };
        var response = await _identity!.ListAvailabilityDomains(request, cancellationToken: ct);
        return response.Items.Select(ad => ad.Name!).ToList();
    }

    public async Task<List<Image>> ListImagesAsync(string compartmentId, string operatingSystem, string shape, CancellationToken ct = default)
    {
        EnsureConfigured();
        var request = new ListImagesRequest
        {
            CompartmentId = compartmentId,
            OperatingSystem = operatingSystem,
            Shape = shape
        };
        var response = await _compute!.ListImages(request, cancellationToken: ct);
        return response.Items.ToList();
    }

    public async Task<List<Oci.CoreService.Models.Instance>> ListInstancesAsync(string compartmentId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var request = new ListInstancesRequest { CompartmentId = compartmentId };
        var response = await _compute!.ListInstances(request, cancellationToken: ct);
        return response.Items.ToList();
    }

    public async Task<Oci.CoreService.Models.Instance> LaunchInstanceAsync(
        AppConfig config, string availabilityDomain, Image image, CancellationToken ct = default)
    {
        EnsureConfigured();
        ValidateAlwaysFreeLaunch(config);
        var sshKey = CryptoHelper.ReadSshPublicKey(config.SshPublicKeyPath);

        var sourceDetails = new InstanceSourceViaImageDetails
        {
            ImageId = image.Id
        };

        // Always specify the conservative minimum instead of relying on an OCI
        // console/default value that could change between launches.
        sourceDetails.BootVolumeSizeInGBs = config.BootVolumeSizeGb ?? AppConfig.AlwaysFreeBootVolumeSizeGb;

        var details = new LaunchInstanceDetails
        {
            AvailabilityDomain = availabilityDomain,
            CompartmentId = config.CompartmentOcid,
            DisplayName = config.InstanceDisplayName(1),
            Shape = config.Shape,
            SourceDetails = sourceDetails,
            SubnetId = config.SubnetOcid,
            // This is an ephemeral public IP assignment on the selected public
            // subnet; OracleHost never creates or requests a reserved IP.
            CreateVnicDetails = new CreateVnicDetails
            {
                AssignPublicIp = config.AssignPublicIp
            },
            Metadata = new Dictionary<string, string> { { "ssh_authorized_keys", sshKey } }
        };

        // Fixed shapes (the E2 micro) must NOT receive a ShapeConfig — their
        // sizing is defined by the shape itself. Flexible shapes (A1.Flex)
        // require it, clamped to the Always Free allotment.
        if (config.IsFlexibleShape)
        {
            var ocpus = Math.Clamp(config.Ocpus ?? AppConfig.AlwaysFreeA1OcpuCount,
                1, AppConfig.AlwaysFreeA1OcpuCount);
            var memory = Math.Clamp(config.MemoryInGbs ?? ocpus * 6, ocpus, ocpus * 6);
            details.ShapeConfig = new LaunchInstanceShapeConfigDetails
            {
                Ocpus = ocpus,
                MemoryInGBs = memory
            };
        }

        var request = new LaunchInstanceRequest { LaunchInstanceDetails = details };
        var response = await _compute!.LaunchInstance(request, cancellationToken: ct);
        return response.Instance;
    }

    private static void ValidateAlwaysFreeLaunch(AppConfig config)
    {
        var isMicro = string.Equals(config.Shape, AppConfig.MicroShape, StringComparison.OrdinalIgnoreCase);
        var isA1 = string.Equals(config.Shape, AppConfig.A1FlexShape, StringComparison.OrdinalIgnoreCase);
        if (!isMicro && !isA1)
            throw new InvalidOperationException(
                $"Always Free protection blocked this launch: only {AppConfig.MicroShape} or {AppConfig.A1FlexShape} is allowed.");
        if (config.BootVolumeSizeGb.HasValue && config.BootVolumeSizeGb.Value < AppConfig.AlwaysFreeBootVolumeSizeGb)
            throw new InvalidOperationException(
                $"Always Free protection blocked this launch: boot volume must be at least {AppConfig.AlwaysFreeBootVolumeSizeGb} GB.");
        if (config.BootVolumeSizeGb.HasValue && config.BootVolumeSizeGb.Value > AppConfig.AlwaysFreeBlockStorageGb)
            throw new InvalidOperationException(
                $"Always Free protection blocked this launch: boot volume exceeds the {AppConfig.AlwaysFreeBlockStorageGb} GB Always Free pool.");
        if (config.IsFlexibleShape)
        {
            var ocpus = config.Ocpus ?? AppConfig.AlwaysFreeA1OcpuCount;
            var memory = config.MemoryInGbs ?? ocpus * 6;
            if (ocpus < 1 || ocpus > AppConfig.AlwaysFreeA1OcpuCount ||
                memory < ocpus || memory > AppConfig.AlwaysFreeA1MemoryGb)
                throw new InvalidOperationException(
                    $"Always Free protection blocked this launch: A1 sizing must be 1-{AppConfig.AlwaysFreeA1OcpuCount} OCPU " +
                    $"and {ocpus}-{ocpus * 6} GB memory (within the {AppConfig.AlwaysFreeA1MemoryGb} GB allotment).");
        }
    }

    public async Task<Oci.CoreService.Models.Instance?> GetInstanceAsync(string instanceId, CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var request = new GetInstanceRequest { InstanceId = instanceId };
            var response = await _compute!.GetInstance(request, cancellationToken: ct);
            return response.Instance;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("OCI", $"Read instance {instanceId}", ex);
            return null;
        }
    }

    public async Task<string?> GetPublicIpAsync(string compartmentId, string instanceId, CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var request = new ListVnicAttachmentsRequest
            {
                CompartmentId = compartmentId,
                InstanceId = instanceId
            };
            var response = await _compute!.ListVnicAttachments(request, cancellationToken: ct);

            foreach (var att in response.Items)
            {
                try
                {
                    var vnicRequest = new GetVnicRequest { VnicId = att.VnicId };
                    var vnicResponse = await _network!.GetVnic(vnicRequest, cancellationToken: ct);
                    if (!string.IsNullOrEmpty(vnicResponse.Vnic.PublicIp))
                        return vnicResponse.Vnic.PublicIp;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Exception("OCI", $"Read VNIC {att.VnicId}", ex);
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("OCI", $"Find public IP for instance {instanceId}", ex);
        }
        return null;
    }

    /// <summary>
    /// Reads the Always Free limit for the configured shape. Returns null when
    /// Oracle reports no usable value for the tenancy.
    /// </summary>
    public async Task<int?> DetectAlwaysFreeLimitAsync(AppConfig config, CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var valuesRequest = new ListLimitValuesRequest
            {
                CompartmentId = _tenancyId!,
                ServiceName = "compute",
                Name = config.ActiveLimitName
            };
            var values = await _limits!.ListLimitValues(valuesRequest, cancellationToken: ct);

            int max = 0;
            foreach (var v in values.Items)
            {
                if (int.TryParse(v.Value?.ToString(), out int val) && val > max)
                    max = val;
            }
            return max > 0 ? max : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("OCI", "Detect Always Free limit", ex);
            throw new InvalidOperationException(
                "Always Free limits could not be checked. Authentication or permission may be missing.", ex);
        }
    }

    /// <summary>
    /// Lists the availability domains that offer the configured shape in this
    /// tenancy. ListShapes is AD-scoped, so the shape is probed once per AD.
    /// </summary>
    public async Task<List<string>> ListShapeAdsWithCapacityAsync(AppConfig config, CancellationToken ct = default)
    {
        EnsureConfigured();
        var adRequest = new ListAvailabilityDomainsRequest { CompartmentId = _tenancyId };
        var ads = await _identity!.ListAvailabilityDomains(adRequest, cancellationToken: ct);

        var capableAds = new List<string>();
        foreach (var ad in ads.Items)
        {
            var shapeRequest = new ListShapesRequest
            {
                CompartmentId = _tenancyId,
                AvailabilityDomain = ad.Name
            };
            var shapes = await _compute!.ListShapes(shapeRequest, cancellationToken: ct);
            if (shapes.Items.Any(s =>
                    string.Equals(s.ShapeProp ?? "", config.Shape, StringComparison.OrdinalIgnoreCase)))
            {
                capableAds.Add(ad.Name!);
            }
        }

        DiagnosticLog.Info("OCI", $"Shape {config.Shape} is offered in {capableAds.Count} of {ads.Items.Count} availability domain(s): {string.Join(", ", capableAds)}");
        return capableAds;
    }

    public async Task<List<Compartment>> ListCompartmentsAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        var request = new ListCompartmentsRequest { CompartmentId = _tenancyId! };
        var response = await _identity!.ListCompartments(request, cancellationToken: ct);
        return response.Items.Where(c => c.LifecycleState == Compartment.LifecycleStateEnum.Active).ToList();
    }

    public async Task<List<Subnet>> ListSubnetsAsync(string compartmentId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var subnets = new List<Subnet>();
        try
        {
            var vcnRequest = new ListVcnsRequest { CompartmentId = compartmentId };
            var vcnResponse = await _network!.ListVcns(vcnRequest, cancellationToken: ct);

            foreach (var vcn in vcnResponse.Items)
            {
                var subnetRequest = new ListSubnetsRequest
                {
                    CompartmentId = compartmentId,
                    VcnId = vcn.Id
                };
                var subnetResponse = await _network!.ListSubnets(subnetRequest, cancellationToken: ct);
                subnets.AddRange(subnetResponse.Items);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("OCI", $"List subnets for compartment {compartmentId}", ex);
            throw new InvalidOperationException(
                "Subnets could not be checked. Verify the compartment permissions and VCN access.", ex);
        }

        return subnets;
    }

    private void EnsureConfigured()
    {
        if (_compute == null || _identity == null)
            throw new InvalidOperationException("OCI service not configured. Call Initialize() first.");
    }

    public void Dispose()
    {
        _compute?.Dispose();
        _identity?.Dispose();
        _network?.Dispose();
        _limits?.Dispose();
        // Clean up temp config
        if (_tempConfigPath != null && File.Exists(_tempConfigPath))
        {
            try { File.Delete(_tempConfigPath); } catch { }
        }
    }
}
