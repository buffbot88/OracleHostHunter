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

namespace OracleHost.Services;

/// <summary>
/// Wraps the OCI .NET SDK for compute, networking, identity, and limits operations.
/// Auth prefers a captured browser session, then inline API keys, then ~/.oci/config.
/// </summary>
public class OciService : IDisposable
{
    private ComputeClient? _compute;
    private IdentityClient? _identity;
    private VirtualNetworkClient? _network;
    private LimitsClient? _limits;
    private string? _tenancyId;
    private string? _tempConfigPath;
    private string? _sessionTempDirectory;
    private FileStream? _sessionTempLock;

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
            // If provider construction fails after creating a decrypted token
            // copy, do not leave it behind while the caller handles the error.
            Dispose();
            throw;
        }
    }

    private IAuthenticationDetailsProvider CreateAuthProvider(AppConfig config)
    {
        // 1) Browser-captured session token ("Sign in with Oracle") takes priority
        // A configured path is an explicit user choice even when the file has
        // since been deleted; do not silently replace it with another session.
        var explicitlyConfiguredSession = !string.IsNullOrWhiteSpace(config.SessionConfigPath);
        var session = ResolveSession(config);
        if (explicitlyConfiguredSession && (session == null || !session.IsValid))
        {
            throw new InvalidOperationException(
                "The configured OCI session is missing or incomplete. " +
                "Sign in again from the login screen.");
        }
        if (session != null && session.IsValid)
        {
            try
            {
                _tenancyId = session.TenancyOcid;
                MigrateSessionIfEnabled(session, config.EncryptSessionTokens);
                var provider = CreateSessionProvider(session);

                // If the token is close to expiring, refresh it via the stored refresh token
                if (!provider.IsSessionTokenValid() && !string.IsNullOrEmpty(config.OciIdentityDomain))
                {
                    var renewed = OciBrowserLogin.RenewAsync(
                        session, config.OciIdentityDomain, config.OciOauthClientId ?? "",
                        encryptSessionTokens: config.EncryptSessionTokens)
                        .GetAwaiter().GetResult();
                    if (renewed)
                    {
                        provider = CreateSessionProvider(session);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Your Oracle session has expired and could not be refreshed. " +
                            "Sign in again from the login screen.");
                    }
                }

                return provider;
            }
            catch (InvalidOperationException)
            {
                throw; // explicit, actionable message - don't hide it
            }
            catch (PlatformNotSupportedException)
            {
                throw; // DPAPI was requested but is unavailable; do not bypass it
            }
            catch (Exception ex)
            {
                // Never silently bypass an explicitly selected session or a
                // protected session. Falling back here could hide a DPAPI
                // failure and unexpectedly use a different identity.
                if (explicitlyConfiguredSession || config.EncryptSessionTokens ||
                    SessionTokenProtector.IsProtectedFile(session.TokenPath))
                {
                    throw new InvalidOperationException(
                        "The configured OCI session could not be used. " +
                        "Sign in again if the session is unreadable or expired.", ex);
                }

                // A discovered plaintext OCI CLI session remains compatible with
                // the existing API-key fallback behavior.
                DiagnosticLog.Warn("OCI Auth", $"Discovered session could not be used; falling back to API-key/config authentication: {ex.Message}");
            }
        }

        // 2) Inline API-key credentials: verify the selected private key really
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

        // 3) Existing ~/.oci/config file
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

        DiagnosticLog.Error("OCI Auth", "No usable OCI credentials were found after checking configured sessions, inline API credentials, and ~/.oci/config.");
        throw new InvalidOperationException(
            "No OCI credentials found. Sign in with Oracle, run the Setup Wizard, or configure ~/.oci/config.");
    }

    private SessionTokenAuthenticationDetailsProvider CreateSessionProvider(OciSession session)
    {
        var configPath = session.SessionConfigPath;
        var tokenPath = session.TokenPath;

        if (SessionTokenProtector.IsProtectedFile(session.TokenPath))
        {
            if (_sessionTempDirectory == null)
            {
                var temporarySession = SessionTokenProtector.CreatePrivateTemporaryDirectory();
                _sessionTempDirectory = temporarySession.Directory;
                _sessionTempLock = temporarySession.OwnerLock;
            }
            tokenPath = Path.Combine(_sessionTempDirectory, "token");
            SessionTokenProtector.WriteTemporaryPlaintextFile(
                tokenPath, SessionTokenProtector.ReadFile(session.TokenPath));

            configPath = Path.Combine(_sessionTempDirectory, "config");
            File.WriteAllText(configPath,
                $"[DEFAULT]\n" +
                $"user={session.UserOcid}\n" +
                $"fingerprint={session.Fingerprint}\n" +
                $"tenancy={session.TenancyOcid}\n" +
                $"region={session.Region}\n" +
                $"security_token_file={tokenPath}\n" +
                $"key_file={session.KeyPath}\n");
        }

        return new SessionTokenAuthenticationDetailsProvider(
            configPath, "DEFAULT",
            new FilePrivateKeySupplier(session.KeyPath, new System.Security.SecureString()));
    }

    private static void MigrateSessionIfEnabled(OciSession session, bool encrypt)
    {
        if (!encrypt) return;
        if (!SessionTokenProtector.IsSupported)
            throw new PlatformNotSupportedException(
                "EncryptSessionTokens is enabled, but Windows DPAPI is unavailable.");

        SessionTokenProtector.ProtectFileInPlace(session.TokenPath);
        if (File.Exists(session.RefreshTokenPath))
            SessionTokenProtector.ProtectFileInPlace(session.RefreshTokenPath);
    }

    /// <summary>
    /// Resolves the session to use for auth: the explicitly configured session, or the
    /// most recently used session under ~/.oci/sessions (created here or by the OCI CLI).
    /// </summary>
    private static OciSession? ResolveSession(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.SessionConfigPath))
            return OciBrowserLogin.LoadSession(config.SessionConfigPath);

        // A saved API-key configuration is authoritative unless the user
        // explicitly selected a session path. Do not silently discover an
        // unrelated ~/.oci/sessions identity and use it instead.
        if (config.HasCredentials)
            return null;

        var found = OciBrowserLogin.FindExistingSession();
        if (found != null)
            config.SessionConfigPath = found.SessionConfigPath;
        return found;
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
            DisplayName = config.DisplayName,
            Shape = config.Shape,
            ShapeConfig = new LaunchInstanceShapeConfigDetails
            {
                Ocpus = config.Ocpus,
                MemoryInGBs = config.MemoryInGb
            },
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

        var request = new LaunchInstanceRequest { LaunchInstanceDetails = details };
        var response = await _compute!.LaunchInstance(request, cancellationToken: ct);
        return response.Instance;
    }

    private static void ValidateAlwaysFreeLaunch(AppConfig config)
    {
        if (!string.Equals(config.Shape, "VM.Standard.A1.Flex", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Always Free protection blocked this launch: only VM.Standard.A1.Flex is allowed.");
        if (config.Ocpus != AppConfig.HunterMaxOcpus || config.MemoryInGb != AppConfig.HunterMaxMemoryGb)
            throw new InvalidOperationException(
                $"Hunter safety protection blocked this launch: each launch must use exactly {AppConfig.HunterMaxOcpus} OCPU / {AppConfig.HunterMaxMemoryGb} GB RAM.");
        if (config.BootVolumeSizeGb.HasValue && config.BootVolumeSizeGb.Value < AppConfig.AlwaysFreeBootVolumeSizeGb)
            throw new InvalidOperationException(
                $"Always Free protection blocked this launch: boot volume must be at least {AppConfig.AlwaysFreeBootVolumeSizeGb} GB.");
        if (config.BootVolumeSizeGb.HasValue && config.BootVolumeSizeGb.Value > AppConfig.AlwaysFreeBootVolumeSizeGb)
            throw new InvalidOperationException(
                $"Always Free protection blocked this launch: boot volume exceeds the conservative {AppConfig.AlwaysFreeBootVolumeSizeGb} GB setting.");
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

    public async Task<(int? Ocpus, int? MemoryGb)> DetectFreeLimitsAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var adRequest = new ListAvailabilityDomainsRequest { CompartmentId = _tenancyId };
            var ads = await _identity!.ListAvailabilityDomains(adRequest, cancellationToken: ct);
            int maxOcpus = 0, maxMem = 0;

            foreach (var ad in ads.Items)
            {
                foreach (var (name, bucket) in new[] { ("standard-a1-core-count", "ocpus"), ("standard-a1-memory-count", "memory") })
                {
                    var valuesRequest = new ListLimitValuesRequest
                    {
                        CompartmentId = _tenancyId!,
                        ServiceName = "compute",
                        AvailabilityDomain = ad.Name,
                        Name = name
                    };
                    var values = await _limits!.ListLimitValues(valuesRequest, cancellationToken: ct);

                    foreach (var v in values.Items)
                    {
                        if (int.TryParse(v.Value?.ToString(), out int val))
                        {
                            if (bucket == "ocpus") maxOcpus = Math.Max(maxOcpus, val);
                            else maxMem = Math.Max(maxMem, val);
                        }
                    }
                }
            }

            return (maxOcpus > 0 ? maxOcpus : null, maxMem > 0 ? maxMem : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("OCI", "Detect Always Free limits", ex);
            throw new InvalidOperationException(
                "Always Free limits could not be checked. Authentication or permission may be missing.", ex);
        }
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
        _sessionTempLock?.Dispose();
        _sessionTempLock = null;
        if (_sessionTempDirectory != null && Directory.Exists(_sessionTempDirectory))
        {
            try { Directory.Delete(_sessionTempDirectory, recursive: true); } catch { }
        }
    }
}
