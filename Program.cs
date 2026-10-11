using System.IO;
using OracleHost.Helpers;
using OracleHost.Models;
using OracleHost.Services;
using OracleHost.Windows;

namespace OracleHost;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        DiagnosticLog.StartSession();
        RegisterGlobalExceptionHandlers(app);

        try
        {
            DiagnosticLog.Info("Startup", "Loading OracleHost configuration.");
            var config = ConfigService.Load();
            DiagnosticLog.Info("Startup", $"Configuration loaded. Has API credentials: {config.HasCredentials}.");

            ApplyStartupMigrations(config);

            if (!TryAdoptAccountInfoCredentials(ref config, out var adoptedAccountInfo))
                return; // an explicit AccountInfo bundle exists but is unusable

            if (!config.HasCredentials)
            {
                DiagnosticLog.Warn("Startup", "No usable credentials after AccountInfo discovery; showing the AccountInfo report.");
                new AccountInfoReportWindow(
                    AccountInfoReport.Inspect(FindAccountInfoDirectory())).ShowDialog();
                return;
            }

            // The OCI API key is separate from the SSH key used inside the VM.
            // Create a local SSH pair only when the user does not already have one;
            // it is never uploaded to Oracle account settings.
            EnsureSshPublicKey(config);
            if (adoptedAccountInfo)
            {
                // Persist the adopted configuration only when the AccountInfo
                // bundle actually changed since the last successful run; an
                // unchanged bundle means config.json is already current.
                var previousFingerprint = LoadSavedAccountInfoFingerprint();
                var currentFingerprint = config.AccountInfoFingerprint;
                if (!string.Equals(previousFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    ConfigService.Save(config);
                    DiagnosticLog.Info("Startup", previousFingerprint == null
                        ? "Discovered AccountInfo configuration saved for the next launch."
                        : "AccountInfo bundle changed; saved the updated configuration.");
                }
                else
                {
                    DiagnosticLog.Info("Startup", "AccountInfo bundle unchanged since the last run; config.json was not rewritten.");
                }
            }

            // Build the effective per-instance plan from the shared template so
            // downstream code can reason about multi-instance goals without
            // re-deriving them from DesiredInstanceCount + DisplayNameBase.
            //
            // This is informational only: launch and hunt still use the shared
            // template directly and create instances one at a time.
            var plan = BuildEffectiveInstancePlan(config);
            DiagnosticLog.Info("Startup", $"Shared template will create up to {plan.Count} instance(s): {string.Join(", ", plan.Select(p => p.DisplayName))}.");

            var accountInfoComplete = adoptedAccountInfo &&
                !string.IsNullOrWhiteSpace(config.CompartmentOcid) &&
                !string.IsNullOrWhiteSpace(config.SubnetOcid);
            if (!accountInfoComplete && !RunSetupWizardIfIncomplete(ref config))
                return; // wizard was cancelled

            if (!ValidateConfig(accountInfoComplete, config))
                return; // configuration problems are reported by the validator

            DiagnosticLog.Info("Startup", $"Selected OCI credentials: user={DiagnosticLog.RedactIdentifier(config.UserOcid)}, tenancy={DiagnosticLog.RedactIdentifier(config.TenancyOcid)}, fingerprint={config.Fingerprint}, key file={DiagnosticLog.SafeFileName(config.KeyFilePath)}.");
            DiagnosticLog.Info("Startup", "Launching main dashboard and scheduling an authentication preflight.");
            // Application.Run starts the WPF dispatcher loop and keeps the
            // process alive. Showing a window without it returns from Main and
            // the app exits immediately.
            var mainWindow = new MainWindow(config, runStartupPreflight: true);
            app.Run(mainWindow);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Startup", "Startup sequence", ex);
            System.Windows.MessageBox.Show(
                $"OracleHost could not start.\n\n{DiagnosticLog.Redact(ex.Message)}\n\nDetailed diagnostics were saved to:\n{DiagnosticLog.LogPath}",
                "OracleHost Startup Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Builds the planned instance list for the current shared template, after
    /// clamping the desired count to what the Always Free pools support. Used
    /// for logging/telemetry only; the actual launch loop uses
    /// <see cref="AppConfig.EffectiveInstanceCount"/> and
    /// <see cref="AppConfig.InstanceDisplayName(int)"/>.
    /// </summary>
    private static IReadOnlyList<(string DisplayName, string Shape, int? Ocpus, int? MemoryInGbs, int BootVolumeSizeGb)> BuildEffectiveInstancePlan(AppConfig config)
    {
        var count = config.EffectiveInstanceCount;
        if (count <= 0) return Array.Empty<(string, string, int?, int?, int)>();

        return Enumerable.Range(1, count).Select(i => (
            config.InstanceDisplayName(i),
            config.Shape,
            config.IsFlexibleShape ? config.Ocpus : null,
            config.IsFlexibleShape ? config.MemoryInGbs : null,
            config.BootVolumeSizeGb ?? AppConfig.AlwaysFreeBootVolumeSizeGb
        )).ToList();
    }

    private static void RegisterGlobalExceptionHandlers(System.Windows.Application app)
    {
        // WPF surfaces UI-thread faults through DispatcherUnhandledException,
        // not the WinForms ThreadException event.
        app.DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticLog.Exception("UI", "Unhandled UI exception", args.Exception);
            System.Windows.MessageBox.Show(
                $"OracleHost encountered an unexpected error.\n\n{args.Exception.Message}\n\n" +
                $"Detailed diagnostics were saved to:\n{DiagnosticLog.LogPath}",
                "OracleHost Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            args.Handled = true;
        };

        System.Windows.Forms.Application.ThreadException += (_, args) =>
        {
            DiagnosticLog.Exception("UI", "Unhandled WinForms interop exception", args.Exception);
            System.Windows.MessageBox.Show(
                $"OracleHost encountered an unexpected error.\n\n{args.Exception.Message}\n\n" +
                $"Detailed diagnostics were saved to:\n{DiagnosticLog.LogPath}",
                "OracleHost Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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
    /// Reads the AccountInfo fingerprint recorded in the saved config.json
    /// without disturbing the in-memory working configuration.
    /// </summary>
    private static string? LoadSavedAccountInfoFingerprint()
    {
        try
        {
            return ConfigService.Load().AccountInfoFingerprint;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Startup", "Read saved AccountInfo fingerprint", ex);
            return null;
        }
    }

    /// <summary>
    /// Migrates older saved A1 hunter sizing to the current micro-only shape and
    /// safe retry intervals, persisting the result when possible. Only these
    /// resource fields are changed; credentials, OCIDs, and security options are preserved.
    /// </summary>
    private static void ApplyStartupMigrations(AppConfig config)
    {
        var migratedShape = MigrateLegacyShape(config);
        var migratedRetryIntervals = MigrateRetryIntervals(config);
        if (!migratedShape && !migratedRetryIntervals) return;

        try
        {
            ConfigService.Save(config);
            if (migratedShape)
                DiagnosticLog.Info("Startup",
                    $"Migrated saved hunter shape to {AppConfig.MicroShape} (fixed 1/8 OCPU / 1 GB RAM).");
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
                // Show the field-by-field report so the user can see exactly
                // which items are missing; the log line above explains any
                // non-obvious failure such as a fingerprint mismatch.
                new AccountInfoReportWindow(
                    AccountInfoReport.Inspect(accountInfoDirectory)).ShowDialog();
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
        accountInfo.MemoryInGbs = config.MemoryInGbs;
        accountInfo.ImageOs = config.ImageOs;
        accountInfo.ImageVersion = config.ImageVersion;
        accountInfo.DisplayNameBase = config.DisplayNameBase;
        accountInfo.DesiredInstanceCount = config.DesiredInstanceCount;
        accountInfo.AssignPublicIp = config.AssignPublicIp;
        accountInfo.MinIntervalSeconds = config.MinIntervalSeconds;
        accountInfo.MaxIntervalSeconds = config.MaxIntervalSeconds;
        accountInfo.StopOnLimit = config.StopOnLimit;
        accountInfo.MaxAttempts = config.MaxAttempts;
        accountInfo.AllowExisting = config.AllowExisting;
        accountInfo.HasSeenOnboarding = config.HasSeenOnboarding;
        accountInfo.SshPublicKeyPath = config.SshPublicKeyPath;
        accountInfo.OciConfigPath = config.OciConfigPath;
        accountInfo.BootVolumeSizeGb = config.BootVolumeSizeGb;
        accountInfo.AvailabilityDomains = config.AvailabilityDomains;
        config = accountInfo;
        config.AccountInfoFingerprint =
            ConfigService.ComputeAccountInfoFingerprint(accountInfoDirectory);
        adopted = true;
        return true;
    }

    /// <summary>Shows the Setup Wizard when compartment or subnet is missing. Returns false when cancelled.</summary>
    private static bool RunSetupWizardIfIncomplete(ref AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CompartmentOcid) &&
            !string.IsNullOrWhiteSpace(config.SubnetOcid))
            return true;

        DiagnosticLog.Info("Startup", "Opening Settings because compartment or subnet is missing.");
        var settingsWindow = new SettingsWindow(config);
        if (settingsWindow.ShowDialog() != true)
        {
            DiagnosticLog.Warn("Startup", "Settings were cancelled.");
            return false;
        }
        config = settingsWindow.ResultConfig;
        return true;
    }

    /// <summary>
    /// Validates the final configuration. A complete AccountInfo bundle already
    /// provides every field the wizard would set, so failures are reported as a
    /// single error instead of reopening the Setup Wizard.
    /// </summary>
    private static bool ValidateConfig(bool fromCompleteAccountInfo, AppConfig config)
    {
        DiagnosticLog.Info("Startup", "Validating final instance configuration.");
        var problems = config.Validate();
        if (problems.Count == 0)
            return true;

        var message = "Configuration errors:\n\n" + string.Join("\n", problems.Select(p => "• " + p));
        if (!fromCompleteAccountInfo)
            message += "\n\nRun the Setup Wizard to fix these issues.";
        else
            message += "\n\nFix the AccountInfo bundle or the saved configuration and restart.";
        DiagnosticLog.Warn("Startup", $"Configuration validation failed: {string.Join(" | ", problems)}");
        System.Windows.MessageBox.Show(message + $"\\n\\nDetailed diagnostics: {DiagnosticLog.LogPath}", "OracleHost - Configuration Error",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        return false;
    }

    private static bool MigrateLegacyShape(AppConfig config)
    {
        // Shape is a supported, user-selectable setting (E2.1.Micro or
        // A1.Flex). Nothing to migrate; validation enforces the allowed values.
        return false;
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
            if (!File.Exists(publicPath))
            {
                if (File.Exists(privatePath))
                {
                    // The private half survived but the public half is gone
                    // (e.g. after a partial cleanup). Derive it instead of
                    // generating a brand-new pair so existing VMs stay usable.
                    CryptoHelper.DeriveSshPublicKey(privatePath, publicPath);
                }
                else
                {
                    CryptoHelper.GenerateSshKeyPair(privatePath);
                }
            }
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
