using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OracleHost.Helpers;

public enum SshProbeResult
{
    /// <summary>TCP connection to port 22 succeeded; key auth still needs verification.</summary>
    Reachable,
    /// <summary>TCP connection failed/refused/timed out; instance preserved.</summary>
    Unreachable,
    /// <summary>Instance reached RUNNING recently; first-boot config not yet verified.</summary>
    Pending,
    /// <summary>TCP port reachable AND key authentication actually succeeded.</summary>
    LoginVerified
}

public enum SshAuthResult
{
    Verified,
    FailedAuth,
    NoKey,
    NotAttempted,
    Error,
    HostKeyUntrusted,
    HostKeyChanged
}

/// <summary>Approval prompt for a NEW host key (first trust) or a CHANGED
/// host key (potential MITM). The delegate returns true only on explicit
/// user approval; the key is never stored otherwise.</summary>
public sealed record HostKeyPrompt(string InstanceOcid, string Host, IReadOnlyList<DiscoveredHostKey> Keys, bool IsChange, IReadOnlyList<DiscoveredHostKey> PreviousKeys);
public sealed record DiscoveredHostKey(string KeyType, string Base64, string Fingerprint);
public static class SshHostKeyStore
{
    public static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OracleHost", "known_hosts");

    /// <summary>Exact-OCID match (suffix) so replacing one instance's entries
    /// cannot touch another instance whose OCID shares a prefix.</summary>
    public static bool IsOcidLine(string line, string ocid) =>
        !string.IsNullOrWhiteSpace(ocid) && line.TrimEnd().EndsWith($"#oraclehost:{ocid}", StringComparison.Ordinal);
    /// <summary>Valid OpenSSH known_hosts line: "host keytype base64 [#comment]".
    /// The OCID lives only in the trailing comment, which OpenSSH treats as a
    /// comment and ignores during matching.</summary>
    public static bool IsValidKnownHostsLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) return false;
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        try { Convert.FromBase64String(parts[2]); return parts[1] is "ssh-ed25519" or "ssh-rsa" or "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521"; }
        catch { return false; }
    }
    public static List<string> ReadLines(string ocid)
    {
        if (!File.Exists(StorePath)) return new List<string>();
        return File.ReadAllLines(StorePath).Where(l => IsOcidLine(l, ocid) && IsValidKnownHostsLine(l)).ToList();
    }
    public static bool HasEntry(string ocid) => ReadLines(ocid).Count > 0;
    public static void Append(string ocid, string host, IEnumerable<(string Type, string Base64)> keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var lines = keys.Select(k => $"{host} {k.Type} {k.Base64} #oraclehost:{ocid}").ToList();
        File.AppendAllLines(StorePath, lines);
    }
    public static void Replace(string ocid, string host, IEnumerable<(string Type, string Base64)> keys)
    {
        var kept = File.Exists(StorePath)
            ? File.ReadAllLines(StorePath).Where(l => !IsOcidLine(l, ocid)).ToList()
            : new List<string>();
        kept.AddRange(keys.Select(k => $"{host} {k.Type} {k.Base64} #oraclehost:{ocid}"));
        File.WriteAllLines(StorePath, kept);
    }
    public static string Fingerprint(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }
    public static List<DiscoveredHostKey> ParseKeyscan(string output, string host)
    {
        var result = new List<DiscoveredHostKey>();
        if (string.IsNullOrWhiteSpace(output)) return result;
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            var (h, type, b64) = (parts[0], parts[1], parts[2]);
            if (!h.Contains(host)) continue;
            try { result.Add(new DiscoveredHostKey(type, b64, Fingerprint(b64))); }
            catch { }
        }
        return result;
    }
    /// <summary>Renders ONLY this instance's trusted entries into a temp
    /// known_hosts file for a single ssh invocation. Trust itself stays in the
    /// shared store (nothing is auto-accepted); this prevents a recycled public
    /// IP from matching a different instance's entry. Caller deletes the file.</summary>
    public static string? WriteTrustView(string ocid)
    {
        if (string.IsNullOrWhiteSpace(ocid)) return null;
        var lines = ReadLines(ocid);
        if (lines.Count == 0) return null;
        var path = Path.Combine(Path.GetTempPath(), $"oraclehost-view-{Guid.NewGuid():N}");
        File.WriteAllLines(path, lines);
        return path;
    }

    public static List<(string Type, string Base64)> ParseStored(string ocid)
    {
        var list = new List<(string, string)>();
        foreach (var l in ReadLines(ocid))
        {
            var parts = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3) list.Add((parts[1], parts[2]));
        }
        return list;
    }
}

public enum ProvisionLogKind { NotApplicable, Retrieved, Missing, Error }
public enum ProvisionStepOutcome { Success, Failed, Unknown, NotApplicable }

public sealed record ProvisionReport(
    ProvisionLogKind LogKind,
    IReadOnlyDictionary<string, ProvisionStepOutcome> Steps);

/// <summary>Parses <c>key:value</c> lines written by cloud-init runcmd.
/// A missing or malformed line yields <see cref="ProvisionStepOutcome.Unknown"/>,
/// never a silent success. Last line wins on duplicates.</summary>
public static class ProvisionLogParser
{
    public static IReadOnlyDictionary<string, ProvisionStepOutcome> Parse(string content)
    {
        var dict = new Dictionary<string, ProvisionStepOutcome>(StringComparer.OrdinalIgnoreCase)
        {
            ["sshd"] = ProvisionStepOutcome.Unknown,
            ["firewalld"] = ProvisionStepOutcome.Unknown,
            ["firewall_add"] = ProvisionStepOutcome.Unknown,
            ["firewall_query"] = ProvisionStepOutcome.Unknown,
            ["firewall_reload"] = ProvisionStepOutcome.Unknown
        };
        if (string.IsNullOrWhiteSpace(content)) return dict;
        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();
            if (!dict.ContainsKey(key)) continue;
            if (int.TryParse(val, out var code))
                dict[key] = code == 0 ? ProvisionStepOutcome.Success : ProvisionStepOutcome.Failed;
            else
                dict[key] = ProvisionStepOutcome.Unknown;
        }
        return dict;
    }

    /// <summary>Collapses firewall_add + firewall_query into one display row:
    /// Failed if either failed, Success only if both succeeded.</summary>
    public static ProvisionStepOutcome CollapseSshFirewallRule(IReadOnlyDictionary<string, ProvisionStepOutcome> steps)
    {
        var add = steps.TryGetValue("firewall_add", out var a) ? a : ProvisionStepOutcome.Unknown;
        var query = steps.TryGetValue("firewall_query", out var q) ? q : ProvisionStepOutcome.Unknown;
        if (add == ProvisionStepOutcome.Failed || query == ProvisionStepOutcome.Failed) return ProvisionStepOutcome.Failed;
        if (add == ProvisionStepOutcome.Success && query == ProvisionStepOutcome.Success) return ProvisionStepOutcome.Success;
        return ProvisionStepOutcome.Unknown;
    }
}

/// <summary>
/// Best-effort TCP 22 reachability probe. It never terminates or modifies
/// the probed instance; a failure is a diagnostic, not a retry signal.
/// </summary>
public static class SshProbe
{
    public const int SshPort = 22;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingWindow = TimeSpan.FromSeconds(90);
    private const int MaxAttempts = 3;

    public static async Task<SshProbeResult> ProbeSshAsync(string? publicIp, TimeSpan? runningFor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(publicIp) || !IPAddress.TryParse(publicIp.Trim(), out var address))
            return SshProbeResult.Unreachable;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(address, SshPort, timeoutCts.Token);
                return SshProbeResult.Reachable;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Retry transient failures; a single refusal is not conclusive.
            }
            if (attempt < MaxAttempts - 1)
            {
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            }
        }

        // Single pending window: sustained failure shortly after RUNNING
        // means first-boot config is not yet verified.
        return runningFor.HasValue && runningFor.Value < PendingWindow
            ? SshProbeResult.Pending
            : SshProbeResult.Unreachable;
    }

    private static async Task<List<DiscoveredHostKey>> RunKeyscanAsync(string host, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "ssh-keyscan", Arguments = $"-p {SshPort} -T 5 -t ed25519,rsa,ecdsa {host}", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc == null) return new List<DiscoveredHostKey>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(15000);
            var cli = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var eli = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            _ = await eli;
            return SshHostKeyStore.ParseKeyscan(await cli, host);
        }
        catch { return new List<DiscoveredHostKey>(); }
    }

    /// <summary>Returns null when trust established; otherwise the failure kind.
    /// Never silently trusts: NEW keys require the delegate; CHANGED keys are
    /// rejected until the delegate approves the replacement.</summary>
    public static async Task<SshAuthResult?> EnsureHostKeyTrustAsync(string host, string? ocid, Func<HostKeyPrompt, Task<bool>>? onPrompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(ocid)) return SshAuthResult.HostKeyUntrusted;
        var previous = SshHostKeyStore.HasEntry(ocid) ? SshHostKeyStore.ParseStored(ocid) : new List<(string, string)>();
        if (previous.Count == 0)
        {
            var discovered = await RunKeyscanAsync(host, ct);
            if (discovered.Count == 0) return SshAuthResult.HostKeyUntrusted;
            if (onPrompt == null) return SshAuthResult.HostKeyUntrusted;
            var approved = await onPrompt(new HostKeyPrompt(ocid, host, discovered, false, new List<DiscoveredHostKey>()));
            if (!approved) return SshAuthResult.HostKeyUntrusted;
            SshHostKeyStore.Append(ocid, host, discovered.Select(k => (k.KeyType, k.Base64)));
            return null;
        }
        var current = await RunKeyscanAsync(host, ct);
        if (current.Count == 0) return SshAuthResult.HostKeyUntrusted;
        var changed = current.Any(k => !previous.Any(p => p.Item1 == k.KeyType && p.Item2 == k.Base64));
        if (!changed) return null;
        if (onPrompt == null) return SshAuthResult.HostKeyChanged;
        var approvedChange = await onPrompt(new HostKeyPrompt(ocid, host, current, true, previous.Select(p => new DiscoveredHostKey(p.Item1, p.Item2, SshHostKeyStore.Fingerprint(p.Item2))).ToList()));
        if (!approvedChange) return SshAuthResult.HostKeyChanged;
        SshHostKeyStore.Replace(ocid, host, current.Select(k => (k.KeyType, k.Base64)));
        return null;
    }

    /// <summary>
    /// Attempts authenticated SSH key login (BatchMode, no password prompt).
    /// This is the ONLY way to claim "Login verified" — a TCP connect alone
    /// proves the port is open, not that the key matches. Returns NotAttempted
    /// when the private key is missing. Never logs key contents.
    /// </summary>
    public static async Task<SshAuthResult> TryAuthenticateAsync(
        string? publicIp, string? privateKeyPath, string user = "opc", CancellationToken ct = default,
        string? instanceOcid = null, Func<HostKeyPrompt, Task<bool>>? onPrompt = null)
    {
        if (string.IsNullOrWhiteSpace(publicIp) || string.IsNullOrWhiteSpace(privateKeyPath))
            return SshAuthResult.NotAttempted;
        var expanded = Environment.ExpandEnvironmentVariables(privateKeyPath);
        if (!File.Exists(expanded)) return SshAuthResult.NoKey;
        if (!IPAddress.TryParse(publicIp.Trim(), out _)) return SshAuthResult.NotAttempted;

        var trustFailure = await EnsureHostKeyTrustAsync(publicIp.Trim(), instanceOcid, onPrompt, ct);
        if (trustFailure != null) return trustFailure.Value;
        var trustView = SshHostKeyStore.WriteTrustView(instanceOcid ?? string.Empty);
        if (trustView == null) return SshAuthResult.HostKeyUntrusted;
        try
        {
            const int timeoutMs = 15000;
            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = $"-i \"{expanded}\" -o BatchMode=yes -o StrictHostKeyChecking=yes -o CheckHostIP=no -o UserKnownHostsFile=\"{trustView}\" -o ConnectTimeout=10 -o PasswordAuthentication=no -p {SshPort} {user}@" + publicIp.Trim() + " echo ORACLEHOST_OK",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process? proc;
            try { proc = Process.Start(psi); }
            catch { return SshAuthResult.Error; }
            using var owned = proc;
            if (owned == null) return SshAuthResult.Error;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);
                var stdoutTask = owned.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = owned.StandardError.ReadToEndAsync(timeoutCts.Token);
                await owned.WaitForExitAsync(timeoutCts.Token);
                var stdout = await stdoutTask;
                // Never surface stderr verbatim (may contain key paths); only exit code matters.
                return owned.ExitCode == 0 && (stdout?.Contains("ORACLEHOST_OK") == true)
                    ? SshAuthResult.Verified
                    : SshAuthResult.FailedAuth;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return SshAuthResult.Error; }
        }
        finally
        {
            try { if (File.Exists(trustView)) File.Delete(trustView); } catch { }
        }
    }

    /// <summary>Read-only retrieval of /var/log/oraclehost-provision.log via
    /// the already-validated key. Must only be called after
    /// <see cref="SshAuthResult.Verified"/> — an auth failure prevents
    /// retrieval. Never restarts services, modifies files, or logs key
    /// material. Returns NotApplicable when cloud-init is disabled without
    /// opening SSH at all.</summary>
    public static async Task<ProvisionReport> TryRetrieveProvisionLogAsync(
        string? publicIp, string? privateKeyPath, bool cloudInitEnabled,
        string user = "opc", CancellationToken ct = default,
        string? instanceOcid = null, Func<HostKeyPrompt, Task<bool>>? onPrompt = null)
    {
        if (!cloudInitEnabled)
            return new ProvisionReport(ProvisionLogKind.NotApplicable, EmptyProvisionSteps());
        if (string.IsNullOrWhiteSpace(publicIp) || string.IsNullOrWhiteSpace(privateKeyPath))
            return new ProvisionReport(ProvisionLogKind.Error, EmptyProvisionSteps());
        var expanded = Environment.ExpandEnvironmentVariables(privateKeyPath);
        if (!File.Exists(expanded) || !IPAddress.TryParse(publicIp.Trim(), out _))
            return new ProvisionReport(ProvisionLogKind.Error, EmptyProvisionSteps());

        var trustFailure = await EnsureHostKeyTrustAsync(publicIp.Trim(), instanceOcid, onPrompt, ct);
        if (trustFailure != null) return new ProvisionReport(ProvisionLogKind.Error, EmptyProvisionSteps());
        var trustView = SshHostKeyStore.WriteTrustView(instanceOcid ?? string.Empty);
        if (trustView == null) return new ProvisionReport(ProvisionLogKind.Error, EmptyProvisionSteps());
        try
        {
            const int timeoutMs = 20000;
            var remoteCmd = "if test -r /var/log/oraclehost-provision.log; then cat /var/log/oraclehost-provision.log; else echo ORACLEHOST_PROVISION_LOG_UNAVAILABLE; fi";
            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = $"-i \"{expanded}\" -o BatchMode=yes -o StrictHostKeyChecking=yes -o CheckHostIP=no -o UserKnownHostsFile=\"{trustView}\" -o ConnectTimeout=10 -o PasswordAuthentication=no -o ServerAliveInterval=5 -o ServerAliveCountMax=2 -p {SshPort} {user}@" + publicIp.Trim() + " \"" + remoteCmd + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process? proc;
            try { proc = Process.Start(psi); }
            catch { return new ProvisionReport(ProvisionLogKind.Error, EmptyProvisionSteps()); }
            using var owned = proc;
            if (owned == null) return new ProvisionReport(ProvisionLogKind.Error, EmptyProvisionSteps());
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var stdoutTask = owned.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = owned.StandardError.ReadToEndAsync(timeoutCts.Token);
            await owned.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            _ = await stderrTask; // never surfaced (may contain paths)
            if (owned.ExitCode != 0)
                return new ProvisionReport(ProvisionLogKind.Error, EmptyProvisionSteps());
            if (stdout != null && stdout.Contains("ORACLEHOST_PROVISION_LOG_UNAVAILABLE"))
                return new ProvisionReport(ProvisionLogKind.Missing, EmptyProvisionSteps());
            var steps = ProvisionLogParser.Parse(stdout ?? string.Empty);
            return new ProvisionReport(ProvisionLogKind.Retrieved, steps);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new ProvisionReport(ProvisionLogKind.Error, EmptyProvisionSteps()); }
        finally
        {
            try { if (File.Exists(trustView)) File.Delete(trustView); } catch { }
        }
    }

    private static IReadOnlyDictionary<string, ProvisionStepOutcome> EmptyProvisionSteps()
        => new Dictionary<string, ProvisionStepOutcome>(StringComparer.OrdinalIgnoreCase)
        {
            ["sshd"] = ProvisionStepOutcome.NotApplicable,
            ["firewalld"] = ProvisionStepOutcome.NotApplicable,
            ["firewall_add"] = ProvisionStepOutcome.NotApplicable,
            ["firewall_query"] = ProvisionStepOutcome.NotApplicable,
            ["firewall_reload"] = ProvisionStepOutcome.NotApplicable
        };
}
