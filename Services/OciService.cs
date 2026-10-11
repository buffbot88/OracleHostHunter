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
    public sealed record TenancyUsageSnapshot(
        IReadOnlyList<Oci.CoreService.Models.Instance> Instances,
        long BootVolumeGb,
        long BlockVolumeGb);

    public sealed record NetworkDiagnosticCheck(string Title, string State, string Detail);

    public sealed record NetworkDiagnosticReport(
        string InstanceId,
        IReadOnlyList<NetworkDiagnosticCheck> Checks)
    {
        public bool HasFailures => Checks.Any(check => check.State == "ISSUE");
    }

    private ComputeClient? _compute;
    private IdentityClient? _identity;
    private VirtualNetworkClient? _network;
    private BlockstorageClient? _blockStorage;
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
            _blockStorage = new BlockstorageClient(authProvider);
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
        var instances = new List<Oci.CoreService.Models.Instance>();
        string? page = null;
        do
        {
            var request = new ListInstancesRequest { CompartmentId = compartmentId, Page = page };
            var response = await _compute!.ListInstances(request, cancellationToken: ct);
            instances.AddRange(response.Items);
            page = response.OpcNextPage;
        } while (!string.IsNullOrEmpty(page));
        return instances;
    }

    /// <summary>
    /// Inventories compute instances and boot-volume storage across the tenancy's
    /// visible compartments in this region. Any list failure is propagated so
    /// preflight never claims the plan is safe with an incomplete inventory.
    /// </summary>
    public async Task<TenancyUsageSnapshot> GetTenancyUsageAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(_tenancyId))
            throw new InvalidOperationException("Tenancy OCID is unavailable; tenancy-wide resource usage cannot be checked.");

        var compartments = new List<string> { _tenancyId };
        string? compartmentPage = null;
        do
        {
            var request = new ListCompartmentsRequest
            {
                CompartmentId = _tenancyId,
                CompartmentIdInSubtree = true,
                AccessLevel = ListCompartmentsRequest.AccessLevelEnum.Any,
                Page = compartmentPage
            };
            var response = await _identity!.ListCompartments(request, cancellationToken: ct);
            compartments.AddRange(response.Items
                .Where(compartment => compartment.LifecycleState == Compartment.LifecycleStateEnum.Active)
                .Select(compartment => compartment.Id!)
                .Where(id => !string.IsNullOrWhiteSpace(id)));
            compartmentPage = response.OpcNextPage;
        } while (!string.IsNullOrEmpty(compartmentPage));

        compartments = compartments.Distinct(StringComparer.Ordinal).ToList();
        var allAds = await ListAvailabilityDomainsAsync(ct);
        var instances = new List<Oci.CoreService.Models.Instance>();
        long bootVolumeGb = 0;
        long blockVolumeGb = 0;
        foreach (var compartmentId in compartments)
        {
            instances.AddRange(await ListInstancesAsync(compartmentId, ct));
            foreach (var availabilityDomain in allAds)
            {
                string? page = null;
                do
                {
                    var request = new ListBootVolumesRequest
                    {
                        CompartmentId = compartmentId,
                        AvailabilityDomain = availabilityDomain,
                        Page = page
                    };
                    var response = await _blockStorage!.ListBootVolumes(request, cancellationToken: ct);
                    bootVolumeGb += response.Items
                        .Where(volume => !IsDeletedVolume(volume.LifecycleState?.ToString()))
                        .Sum(volume => (long)(volume.SizeInGBs ?? 0));
                    page = response.OpcNextPage;
                } while (!string.IsNullOrEmpty(page));

                page = null;
                do
                {
                    var request = new ListVolumesRequest
                    {
                        CompartmentId = compartmentId,
                        AvailabilityDomain = availabilityDomain,
                        Page = page
                    };
                    var response = await _blockStorage!.ListVolumes(request, cancellationToken: ct);
                    blockVolumeGb += response.Items
                        .Where(volume => !IsDeletedVolume(volume.LifecycleState?.ToString()))
                        .Sum(volume => (long)(volume.SizeInGBs ?? 0));
                    page = response.OpcNextPage;
                } while (!string.IsNullOrEmpty(page));
            }
        }

        return new TenancyUsageSnapshot(instances, bootVolumeGb, blockVolumeGb);
    }

    private static bool IsDeletedVolume(string? lifecycleState) =>
        string.Equals(lifecycleState, "Terminated", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(lifecycleState, "Terminating", StringComparison.OrdinalIgnoreCase);

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

    public async Task<NetworkDiagnosticReport> DiagnoseInstanceNetworkAsync(
        string compartmentId, string instanceId, string? sshSourceIp = null, CancellationToken ct = default)
    {
        EnsureConfigured();
        var checks = new List<NetworkDiagnosticCheck>();
        void Add(string title, bool passed, string detail) =>
            checks.Add(new NetworkDiagnosticCheck(title, passed ? "OK" : "ISSUE", detail));

        var instance = await GetInstanceAsync(instanceId, ct)
            ?? throw new InvalidOperationException("Instance could not be read. Verify its OCID and your Compute permissions.");
        Add("Instance state", instance.LifecycleState == Oci.CoreService.Models.Instance.LifecycleStateEnum.Running,
            $"Current state: {instance.LifecycleState}.");

        var attachments = new List<VnicAttachment>();
        string? page = null;
        do
        {
            var response = await _compute!.ListVnicAttachments(new ListVnicAttachmentsRequest
            {
                CompartmentId = compartmentId,
                InstanceId = instanceId,
                Page = page
            }, cancellationToken: ct);
            attachments.AddRange(response.Items);
            page = response.OpcNextPage;
        } while (!string.IsNullOrEmpty(page));

        if (attachments.Count == 0)
            throw new InvalidOperationException("No VNIC attachments were returned for this instance. Check compartment and permissions.");

        var hasInternetRoute = false;
        var hasOutboundRoute = false;
        var hasGeneralEgress = false;
        var sshMatches = false;
        var sshIsOpenToAll = false;
        var usableInternetGatewayRoute = false;
        var usableNatGatewayRoute = false;
        var routeTargetKinds = new List<string>();
        var privateAddresses = new List<string>();
        var publicAddresses = new List<string>();
        var subnetDescriptions = new List<string>();
        var routeDescriptions = new List<string>();
        var securityListNames = new List<string>();
        var nsgNames = new List<string>();

        foreach (var attachment in attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.VnicId)) continue;
            var vnicResponse = await _network!.GetVnic(new GetVnicRequest { VnicId = attachment.VnicId }, cancellationToken: ct);
            var vnic = vnicResponse.Vnic;
            if (!string.IsNullOrWhiteSpace(vnic.PrivateIp)) privateAddresses.Add(vnic.PrivateIp);
            if (!string.IsNullOrWhiteSpace(vnic.PublicIp))
            {
                publicAddresses.Add(vnic.PublicIp);
            }

            var subnetResponse = await _network.GetSubnet(new GetSubnetRequest { SubnetId = vnic.SubnetId }, cancellationToken: ct);
            var subnet = subnetResponse.Subnet;
            subnetDescriptions.Add($"{subnet.DisplayName ?? subnet.Id} ({subnet.CidrBlock})");
            var prohibitPublicIp = subnet.ProhibitPublicIpOnVnic == true;
            var vnicHasNatRoute = false;

            var routeResponse = await _network.GetRouteTable(new GetRouteTableRequest { RtId = subnet.RouteTableId }, cancellationToken: ct);
            var internetRules = routeResponse.RouteTable.RouteRules
                .Where(rule => IsDefaultIpv4Route(rule.CidrBlock ?? rule.Destination) &&
                               string.Equals(rule.DestinationType?.ToString(), "CidrBlock", StringComparison.OrdinalIgnoreCase))
                .ToList();
            hasOutboundRoute |= internetRules.Count > 0;
            foreach (var route in internetRules)
            {
                var targetId = route.NetworkEntityId;
                if (string.IsNullOrWhiteSpace(targetId)) continue;
                try
                {
                    var gatewayResponse = await _network.GetInternetGateway(
                        new GetInternetGatewayRequest { IgId = targetId }, cancellationToken: ct);
                    if (gatewayResponse.InternetGateway.IsEnabled == true &&
                        gatewayResponse.InternetGateway.LifecycleState == InternetGateway.LifecycleStateEnum.Available)
                    {
                        hasInternetRoute = true;
                        usableInternetGatewayRoute = true;
                        routeTargetKinds.Add("Internet Gateway");
                        routeDescriptions.Add($"0.0.0.0/0 → enabled Internet Gateway {gatewayResponse.InternetGateway.DisplayName ?? targetId}");
                    }
                    else
                        routeDescriptions.Add($"0.0.0.0/0 targets an Internet Gateway that is disabled or not Available ({targetId}).");
                }
                catch (Oci.Common.Model.OciException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    try
                    {
                        var natResponse = await _network.GetNatGateway(
                            new GetNatGatewayRequest { NatGatewayId = targetId }, cancellationToken: ct);
                        var nat = natResponse.NatGateway;
                        routeTargetKinds.Add("NAT Gateway");
                        var natReady = nat.LifecycleState == NatGateway.LifecycleStateEnum.Available && nat.BlockTraffic != true;
                        hasInternetRoute |= natReady;
                        usableNatGatewayRoute |= natReady;
                        vnicHasNatRoute |= natReady;
                        routeDescriptions.Add(natReady
                            ? $"0.0.0.0/0 → available NAT Gateway {nat.DisplayName ?? targetId} (outbound only; no public IP required)."
                            : $"0.0.0.0/0 targets a NAT Gateway that is unavailable or blocking traffic ({targetId}).");
                    }
                    catch (Oci.Common.Model.OciException natException) when (natException.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        routeTargetKinds.Add("other target");
                        routeDescriptions.Add($"0.0.0.0/0 targets {targetId}, not a recognized Internet or NAT Gateway.");
                    }
                }
            }
            if (internetRules.Count == 0)
                routeDescriptions.Add($"No 0.0.0.0/0 route in subnet route table {routeResponse.RouteTable.DisplayName ?? routeResponse.RouteTable.Id}.");

            foreach (var securityListId in subnet.SecurityListIds ?? new List<string>())
            {
                var slResponse = await _network.GetSecurityList(
                    new GetSecurityListRequest { SecurityListId = securityListId }, cancellationToken: ct);
                var securityList = slResponse.SecurityList;
                securityListNames.Add(securityList.DisplayName ?? securityList.Id);
                hasGeneralEgress |= (securityList.EgressSecurityRules ?? new List<EgressSecurityRule>())
                    .Any(rule => IsInternetCidr(rule.Destination, rule.DestinationType?.ToString()) &&
                                 AllowsInternetEgress(rule.Protocol, rule.TcpOptions));
                foreach (var rule in securityList.IngressSecurityRules ?? new List<IngressSecurityRule>())
                {
                    if (!IsSshRule(rule.Protocol, rule.TcpOptions) ||
                        !IsInternetCidr(rule.Source, rule.SourceType?.ToString())) continue;
                    if (rule.Source == "0.0.0.0/0") sshIsOpenToAll = true;
                    if (string.IsNullOrWhiteSpace(sshSourceIp) || CidrContains(rule.Source, sshSourceIp)) sshMatches = true;
                }
            }

            foreach (var nsgId in vnic.NsgIds ?? new List<string>())
            {
                nsgNames.Add(nsgId);
                foreach (var direction in new[] { ListNetworkSecurityGroupSecurityRulesRequest.DirectionEnum.Egress,
                                                    ListNetworkSecurityGroupSecurityRulesRequest.DirectionEnum.Ingress })
                {
                    page = null;
                    do
                    {
                        var rulesResponse = await _network.ListNetworkSecurityGroupSecurityRules(
                            new ListNetworkSecurityGroupSecurityRulesRequest
                            {
                                NetworkSecurityGroupId = nsgId,
                                Direction = direction,
                                Page = page
                            }, cancellationToken: ct);
                        foreach (var rule in rulesResponse.Items)
                        {
                            if (rule.IsValid == false) continue;
                            if (direction == ListNetworkSecurityGroupSecurityRulesRequest.DirectionEnum.Egress &&
                                IsInternetCidr(rule.Destination, rule.DestinationType?.ToString()) &&
                                AllowsInternetEgress(rule.Protocol, rule.TcpOptions))
                                hasGeneralEgress = true;
                            if (direction == ListNetworkSecurityGroupSecurityRulesRequest.DirectionEnum.Ingress &&
                                IsSshRule(rule.Protocol, rule.TcpOptions) &&
                                IsInternetCidr(rule.Source, rule.SourceType?.ToString()))
                            {
                                if (rule.Source == "0.0.0.0/0") sshIsOpenToAll = true;
                                if (string.IsNullOrWhiteSpace(sshSourceIp) || CidrContains(rule.Source, sshSourceIp)) sshMatches = true;
                            }
                        }
                        page = rulesResponse.OpcNextPage;
                    } while (!string.IsNullOrEmpty(page));
                }
            }

            if (!prohibitPublicIp && !string.IsNullOrWhiteSpace(vnic.PublicIp))
                Add("VNIC public address", true, $"Public IP {vnic.PublicIp} is assigned; private IP {vnic.PrivateIp ?? "unknown"}.");
            else if (vnicHasNatRoute)
                checks.Add(new NetworkDiagnosticCheck("VNIC public address", "OK", prohibitPublicIp
                    ? "This private subnet prohibits public IPs; the active NAT Gateway route provides outbound-only internet."
                    : "No public IP is assigned. This is compatible with the active NAT Gateway route for outbound-only access."));
            else
                Add("VNIC public address", false, prohibitPublicIp
                    ? "Subnet prohibits public IPs; use an active NAT Gateway route for outbound-only internet or move the VM to a public subnet."
                    : "No public IP on this VNIC. Direct Internet Gateway access needs a public IP; verify an active NAT Gateway route for private outbound-only access.");
        }

        Add("Subnet", true, string.Join("; ", subnetDescriptions.Distinct()));
        Add("Outbound route", hasInternetRoute,
            routeDescriptions.Count == 0 ? "No route information returned." : string.Join("; ", routeDescriptions.Distinct()));
        Add("Outbound security rules", hasGeneralEgress,
            hasGeneralEgress
                ? "At least one attached subnet security list or VNIC NSG allows HTTPS (TCP 443) or all-protocol egress to 0.0.0.0/0. Stateful replies are covered by OCI."
                : "No attached security list or VNIC NSG allows HTTPS (TCP 443) or all-protocol egress to 0.0.0.0/0; add an egress rule for required destinations/ports.");
        if (usableNatGatewayRoute && !usableInternetGatewayRoute)
            checks.Add(new NetworkDiagnosticCheck("Direct inbound internet", "INFO",
                "This subnet routes through NAT; inbound connections from the internet are not provided by that gateway."));
        else if (!usableInternetGatewayRoute && hasOutboundRoute)
            checks.Add(new NetworkDiagnosticCheck("Direct inbound internet", "INFO",
                "The default route target is not an enabled Internet Gateway; direct inbound internet access is not established."));
        if (!string.IsNullOrWhiteSpace(sshSourceIp))
            Add("SSH ingress from your IP", sshMatches,
                sshMatches
                    ? $"An attached security list or VNIC NSG allows TCP port 22 from {sshSourceIp}."
                    : $"No attached security list or VNIC NSG SSH ingress rule matches {sshSourceIp}; allow stateful TCP 22 from your current public IP using /32.");
        else
            checks.Add(new NetworkDiagnosticCheck("SSH ingress", "INFO", "Enter your current public IPv4 address above to check whether an ingress rule permits SSH from your location."));
        if (sshIsOpenToAll)
            checks.Add(new NetworkDiagnosticCheck("SSH exposure", "WARNING", "TCP 22 is open from 0.0.0.0/0. Restrict it to your public IP (/32) or use a bastion/VPN."));
        checks.Add(new NetworkDiagnosticCheck("Attached security lists", "INFO", securityListNames.Count == 0 ? "No subnet security lists were found." : string.Join(", ", securityListNames.Distinct())));
        if (nsgNames.Count > 0)
            checks.Add(new NetworkDiagnosticCheck("Attached network security groups", "INFO", string.Join(", ", nsgNames.Distinct())));
        if (publicAddresses.Count > 0)
            checks.Add(new NetworkDiagnosticCheck("Public IPs", "INFO", string.Join(", ", publicAddresses.Distinct())));
        if (privateAddresses.Count > 0)
            checks.Add(new NetworkDiagnosticCheck("Private IPs", "INFO", string.Join(", ", privateAddresses.Distinct())));

        return new NetworkDiagnosticReport(instanceId, checks);
    }

    private static bool IsDefaultIpv4Route(string? cidr) => cidr?.Trim() == "0.0.0.0/0";
    private static bool IsInternetCidr(string? cidr) => IsInternetCidr(cidr, "CidrBlock");
    private static bool IsInternetCidr(string? cidr, string? destinationType) =>
        cidr?.Trim() == "0.0.0.0/0" &&
        string.Equals(destinationType, "CidrBlock", StringComparison.OrdinalIgnoreCase);
    private static bool AllowsInternetEgress(string? protocol, TcpOptions? tcpOptions)
    {
        if (protocol == "all") return true;
        if (protocol != "6") return false;
        var ports = tcpOptions?.DestinationPortRange;
        return ports == null || (ports.Min ?? 0) <= 443 && (ports.Max ?? 65535) >= 443;
    }

    private static bool IsSshRule(string? protocol, TcpOptions? tcpOptions)
    {
        if (protocol != "6") return false;
        var ports = tcpOptions?.DestinationPortRange;
        return ports == null ||
               (ports.Min ?? 0) <= 22 && (ports.Max ?? 65535) >= 22;
    }

    private static bool CidrContains(string? cidr, string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var address)) return false;
        if (cidr == "0.0.0.0/0") return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        if (string.IsNullOrWhiteSpace(cidr)) return false;
        var parts = cidr.Split('/', 2);
        if (!System.Net.IPAddress.TryParse(parts[0], out var network) ||
            network.AddressFamily != address.AddressFamily) return false;
        var prefix = parts.Length == 2
            ? int.TryParse(parts[1], out var parsed) ? parsed : -1
            : network.GetAddressBytes().Length * 8;
        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (prefix < 0 || prefix > addressBytes.Length * 8) return false;
        for (var i = 0; i < addressBytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            var mask = bits == 0 ? 0 : 0xff << (8 - bits);
            if ((addressBytes[i] & mask) != (networkBytes[i] & mask)) return false;
        }
        return true;
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
        _blockStorage?.Dispose();
        _limits?.Dispose();
        // Clean up temp config
        if (_tempConfigPath != null && File.Exists(_tempConfigPath))
        {
            try { File.Delete(_tempConfigPath); } catch { }
        }
    }
}
