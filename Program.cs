using OracleHost.Forms;
using OracleHost.Helpers;
using OracleHost.Models;
using OracleHost.Services;

namespace OracleHost;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        DiagnosticLog.StartSession();
        RegisterGlobalExceptionHandlers();

        try
        {
            DiagnosticLog.Info("Startup", "Loading OracleHost configuration.");
            var config = ConfigService.Load();
            DiagnosticLog.Info("Startup", $"Configuration loaded. Has API credentials: {config.HasCredentials}; has session path: {!string.IsNullOrWhiteSpace(config.SessionConfigPath)}.");

            ApplyStartupMigrations(config);
            ClearStaleSessionPath(config);

            if (!TryAdoptAccountInfoCredentials(ref config, out var adoptedAccountInfo))
                return; // an explicit AccountInfo bundle exists but is unusable

            // The OCI API key is separate from the SSH key used inside the VM.
            // Create a local SSH pair only when the user does not already have one;
            // it is never uploaded to Oracle account settings.
            EnsureSshPublicKey(config);
            if (adoptedAccountInfo)
            {
                ConfigService.Save(config);
                DiagnosticLog.Info("Startup", "Discovered AccountInfo configuration saved for the next launch.");
            }

            if (!RunAuthenticationFlow(ref config, out var browserLoginCompleted))
                return; // login was cancelled

            if (!RunSetupWizardIfIncomplete(ref config))
                return; // wizard was cancelled

            if (!ValidateConfigWithWizardRetries(ref config))
                return; // wizard was cancelled during validation retries

            DiagnosticLog.Info("Startup", $"Selected OCI credentials: user={DiagnosticLog.RedactIdentifier(config.UserOcid)}, tenancy={DiagnosticLog.RedactIdentifier(config.TenancyOcid)}, fingerprint={config.Fingerprint}, key file={DiagnosticLog.SafeFileName(config.KeyFilePath)}.");
            DiagnosticLog.Info("Startup", "Launching main dashboard and scheduling an authentication preflight.");
            // Run one preflight for every authenticated startup, not only browser
            // logins, so API-key failures are visible immediately.
            using var mainForm = new MainForm(config, browserLoginCompleted || config.HasCredentials || config.HasSession);
            Application.Run(mainForm);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Startup", "Startup sequence", ex);
            MessageBox.Show(
                $"OracleHost could not start.\n\n{DiagnosticLog.Redact(ex.Message)}\n\nDetailed diagnostics were saved to:\n{DiagnosticLog.LogPath}",
                "OracleHost Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        Application.ThreadException += (_, args) =>
        {
            DiagnosticLog.Exception("UI", "Unhandled UI exception", args.Exception);
            MessageBox.Show(
                $"OracleHost encountered an unexpected error.\n\n{args.Exception.Message}\n\n" +
                $"Detailed diagnostics were saved to:\n{DiagnosticLog.LogPath}",
                "OracleHost Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                DiagnosticLog.Exception("AppDomain", "Unhandled application exception", ex);
            else
                DiagnosticLog.Error("AppDomain", $"Unhandled object: {args.ExceptionObject}");
        };
    }

    /// <summary>
    /// Migrates older saved hunter sizing and retry intervals to the current safe
    /// defaults, persisting the result when possible. Only these resource fields
    /// are changed; credentials, OCIDs, and security options are preserved.
    /// </summary>
    private static void ApplyStartupMigrations(AppConfig config)
    {
        var migratedHunterSizing = MigrateHunterSizing(config);
        var migratedRetryIntervals = MigrateRetryIntervals(config);
        if (!migratedHunterSizing && !migratedRetryIntervals) return;

        try
        {
            ConfigService.Save(config);
            if (migratedHunterSizing)
                DiagnosticLog.Info("Startup",
                    $"Migrated saved hunter sizing to {AppConfig.HunterMaxOcpus} OCPU / " +
                    $"{AppConfig.HunterMaxMemoryGb} GB RAM.");
            if (migratedRetryIntervals)
                DiagnosticLog.Info("Startup",
                    $"Migrated saved retry interval to randomized {AppConfig.DefaultMinRetrySeconds}–{AppConfig.DefaultMaxRetrySeconds} second delays.");
        }
        catch (Exception ex)
        {
            // The in-memory configuration is already safe for this run.
            // A locked or read-only config file should not prevent startup;
            // log the persistence problem and continue with the safe values.
            DiagnosticLog.Exception("Startup", "Persist migrated hunter settings", ex);
            DiagnosticLog.Warn("Startup",
                "Could not save migrated hunter settings to config.json; the safe values remain active for this run.");
        }
    }

    /// <summary>
    /// A session path can remain in config after its files were deleted or
    /// partially written. Do not let that stale path send the user straight
    /// to the Setup Wizard with a broken auth provider.
    /// </summary>
    private static void ClearStaleSessionPath(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SessionConfigPath)) return;

        DiagnosticLog.Info("Startup", "Validating configured OCI session.");
        var configuredSession = OciBrowserLogin.LoadSession(config.SessionConfigPath);
        if (configuredSession == null || !OciBrowserLogin.IsSessionUsable(configuredSession))
        {
            DiagnosticLog.Warn("Startup", "Configured OCI session was missing, incomplete, or unusable; clearing the stale path.");
            config.SessionConfigPath = null;
        }
        else
            DiagnosticLog.Info("Startup", "Configured OCI session is usable.");
    }

    /// <summary>
    /// Adopts credentials from a private AccountInfo checkout when present.
    /// Returns false only when an explicit AccountInfo bundle exists but cannot
    /// be used, in which case startup must stop with the shown error.
    /// </summary>
    private static bool TryAdoptAccountInfoCredentials(ref AppConfig config, out bool adopted)
    {
        adopted = false;
        // This private checkout may include a local AccountInfo folder. Use it
        // only to fill the local API-key configuration; the private PEM remains
        // on disk and is never printed, copied, or stored in config.json.
        var accountInfoDirectory = FindAccountInfoDirectory();
        DiagnosticLog.Info("Startup", $"Checking AccountInfo credentials in {accountInfoDirectory}.");
        var accountInfo = ConfigService.ReadAccountInfo(accountInfoDirectory);

        if (accountInfo == null)
        {
            var accountInfoMetadataPath = Path.Combine(accountInfoDirectory, "information.md");
            var hasExplicitAccountInfoBundle = File.Exists(accountInfoMetadataPath) &&
                                               Directory.EnumerateFiles(accountInfoDirectory, "*.pem").Any();
            var diagnostic = ConfigService.LastAccountInfoDiagnostic ?? "No complete AccountInfo credential set was discovered.";
            DiagnosticLog.Warn("Startup", diagnostic);
            if (hasExplicitAccountInfoBundle)
            {
                MessageBox.Show(
                    $"OracleHost found the AccountInfo folder, but could not use its credentials.\n\n" +
                    $"{DiagnosticLog.Redact(diagnostic)}\n\nDetailed diagnostics:\n{DiagnosticLog.LogPath}",
                    "OracleHost Credential Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            return true;
        }

        // An AccountInfo folder is an explicit credential bundle for this private
        // checkout. Always prefer its verified key path, even if an old config
        // happens to contain the same fingerprint but points at another file.
        // The fingerprint alone is not enough to prove the selected file is the
        // key that the user intended to use.
        var hasUsableApiCredentials = config.HasCredentials &&
            File.Exists(Environment.ExpandEnvironmentVariables(config.KeyFilePath!));
        DiagnosticLog.Info("Startup", hasUsableApiCredentials
            ? "AccountInfo credentials discovered; replacing the saved credential selection with the verified local PEM pair."
            : "AccountInfo credentials discovered; using the local private-key path without copying key contents.");
        accountInfo.Shape = config.Shape;
        accountInfo.Ocpus = config.Ocpus;
        accountInfo.MemoryInGb = config.MemoryInGb;
        accountInfo.ImageOs = config.ImageOs;
        accountInfo.ImageVersion = config.ImageVersion;
        accountInfo.DisplayName = config.DisplayName;
        accountInfo.AssignPublicIp = config.AssignPublicIp;
        accountInfo.MinIntervalSeconds = config.MinIntervalSeconds;
        accountInfo.MaxIntervalSeconds = config.MaxIntervalSeconds;
        accountInfo.StopOnLimit = config.StopOnLimit;
        accountInfo.MaxAttempts = config.MaxAttempts;
        accountInfo.AllowExisting = config.AllowExisting;
        accountInfo.SshPublicKeyPath = config.SshPublicKeyPath;
        accountInfo.OciConfigPath = config.OciConfigPath;
        accountInfo.BootVolumeSizeGb = config.BootVolumeSizeGb;
        accountInfo.AvailabilityDomains = config.AvailabilityDomains;
        accountInfo.EncryptSessionTokens = config.EncryptSessionTokens;
        // The verified AccountInfo API key is authoritative. Do not let a
        // stale browser session take priority later in OciService.
        accountInfo.SessionConfigPath = null;
        accountInfo.OciIdentityDomain = null;
        accountInfo.OciOauthClientId = null;
        config = accountInfo;
        adopted = true;
        return true;
    }

    /// <summary>
    /// Reuses an existing session or shows the LoginForm when no usable
    /// authentication exists. Returns false if the user cancelled the login.
    /// </summary>
    private static bool RunAuthenticationFlow(ref AppConfig config, out bool browserLoginCompleted)
    {
        browserLoginCompleted = false;
        var hasUsableApiCredentials = config.HasCredentials &&
            File.Exists(Environment.ExpandEnvironmentVariables(config.KeyFilePath!));

        // Only credentials saved in OracleHost's config, or a captured session,
        // count as an authenticated startup. Do not import ~/.oci/config here:
        // LoginForm loads it for API-key convenience, but must still be shown so
        // a new user is directed to the browser sign-in flow.

        // Reuse a previously captured/browser-compatible session only when
        // OracleHost has no saved API-key credentials. If API keys are saved but
        // no session exists, still show LoginForm so browser sign-in is offered.
        if (!hasUsableApiCredentials && !config.HasSession)
        {
            var existingSession = OciBrowserLogin.FindExistingSession();
            if (existingSession != null && OciBrowserLogin.IsSessionUsable(existingSession))
            {
                config.SessionConfigPath = existingSession.SessionConfigPath;
                config.Region ??= existingSession.Region;
                DiagnosticLog.Info("Startup", "Using the newest usable OCI session discovered on disk.");
            }
        }

        // Only show LoginForm when neither API-key credentials nor a captured
        // session exist. AccountInfo PEM credentials are already complete and
        // should go directly to setup/dashboard instead of the OAuth detour.
        if (!hasUsableApiCredentials && !config.HasSession)
        {
            using var loginForm = new LoginForm(config);
            DiagnosticLog.Info("Startup", "Opening LoginForm because no usable authentication was found.");
            if (loginForm.ShowDialog() != DialogResult.OK)
            {
                DiagnosticLog.Warn("Startup", "LoginForm was cancelled.");
                return false;
            }
            config = loginForm.ResultConfig ?? config;
            browserLoginCompleted = loginForm.BrowserLoginCompleted;
        }

        return true;
    }

    /// <summary>Shows the Setup Wizard when compartment or subnet is missing. Returns false when cancelled.</summary>
    private static bool RunSetupWizardIfIncomplete(ref AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CompartmentOcid) &&
            !string.IsNullOrWhiteSpace(config.SubnetOcid))
            return true;

        DiagnosticLog.Info("Startup", "Opening SetupWizard because compartment or subnet is missing.");
        using var wizardForm = new SetupWizardForm(config);
        if (wizardForm.ShowDialog() != DialogResult.OK)
        {
            DiagnosticLog.Warn("Startup", "SetupWizard was cancelled.");
            return false;
        }
        config = wizardForm.ResultConfig;
        return true;
    }

    /// <summary>
    /// Validates the config, re-running the Setup Wizard until it passes so a
    /// malformed field cannot reach the hunt loop. Returns false when cancelled.
    /// </summary>
    private static bool ValidateConfigWithWizardRetries(ref AppConfig config)
    {
        DiagnosticLog.Info("Startup", "Validating final instance configuration.");
        List<string> problems;
        while ((problems = config.Validate()).Count > 0)
        {
            var message = "Configuration errors:\n\n" + string.Join("\n", problems.Select(p => "• " + p));
            message += "\n\nRun the Setup Wizard to fix these issues.";
            DiagnosticLog.Warn("Startup", $"Configuration validation failed: {string.Join(" | ", problems)}");
            MessageBox.Show(message + $"\n\nDetailed diagnostics: {DiagnosticLog.LogPath}", "OracleHost - Configuration Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

            using var wizardForm = new SetupWizardForm(config);
            if (wizardForm.ShowDialog() != DialogResult.OK)
            {
                DiagnosticLog.Warn("Startup", "SetupWizard was cancelled.");
                return false;
            }
            config = wizardForm.ResultConfig;
        }
        return true;
    }

    private static bool MigrateHunterSizing(AppConfig config)
    {
        var changed = config.Ocpus != AppConfig.HunterMaxOcpus ||
                      config.MemoryInGb != AppConfig.HunterMaxMemoryGb;
        if (!changed) return false;

        config.Ocpus = AppConfig.HunterMaxOcpus;
        config.MemoryInGb = AppConfig.HunterMaxMemoryGb;
        return true;
    }

    private static bool MigrateRetryIntervals(AppConfig config)
    {
        // Migrate only the former application defaults. A user who deliberately
        // chose another interval range should keep that choice.
        if (config.MinIntervalSeconds != 60 || config.MaxIntervalSeconds != 180)
            return false;

        config.MinIntervalSeconds = AppConfig.DefaultMinRetrySeconds;
        config.MaxIntervalSeconds = AppConfig.DefaultMaxRetrySeconds;
        return true;
    }

    private static void EnsureSshPublicKey(AppConfig config)
    {
        var configuredPath = Environment.ExpandEnvironmentVariables(config.SshPublicKeyPath);
        if (File.Exists(configuredPath))
        {
            config.SshPublicKeyPath = configuredPath;
            return;
        }

        var sshDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var privatePath = Path.Combine(sshDirectory, "oraclehost_id_rsa");
        var publicPath = privatePath + ".pub";
        try
        {
            if (!File.Exists(publicPath) && !File.Exists(privatePath))
                CryptoHelper.GenerateSshKeyPair(privatePath);
            if (File.Exists(publicPath))
                config.SshPublicKeyPath = publicPath;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Startup", "Ensure SSH public key", ex);
            // Config.Validate() will show the actionable key-generation error.
        }
    }

    private static string FindAccountInfoDirectory()
    {
        // When launched from Visual Studio/dotnet run the working directory is
        // normally the project root. When launched by double-clicking the built
        // EXE, however, AppContext.BaseDirectory is usually bin\\Debug\\net8.0-windows
        // and the private AccountInfo folder remains in the project root. Search
        // upward from both locations, but never copy or publish the PEM files.
        var starts = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var start in starts)
        {
            DirectoryInfo? directory;
            try { directory = new DirectoryInfo(start); }
            catch { continue; }

            for (var depth = 0; directory != null && depth < 8; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "AccountInfo");
                var hasMetadata = File.Exists(Path.Combine(candidate, "information.md"));
                var hasPem = Directory.Exists(candidate) &&
                             Directory.EnumerateFiles(candidate, "*.pem").Any();
                if (hasMetadata && hasPem)
                {
                    DiagnosticLog.Info("Startup", $"Found AccountInfo directory at {candidate}.");
                    return candidate;
                }
            }
        }

        var fallback = Path.Combine(Environment.CurrentDirectory, "AccountInfo");
        DiagnosticLog.Warn("Startup", $"AccountInfo directory was not found. Last checked location: {fallback}.");
        return fallback;
    }
}
