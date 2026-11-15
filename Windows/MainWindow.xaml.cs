using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Oci.CoreService.Models;
using OracleHost.Helpers;
using OracleHost.Models;
using OracleHost.Services;
using Image = Oci.CoreService.Models.Image;
using System.IO;

namespace OracleHost.Windows;

/// <summary>
/// Main hunting dashboard (WPF). Logic is ported 1:1 from the WinForms MainForm;
/// only the view layer changed.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _cts;
    private HuntStatus _status = new();
    private bool _isHunting;
    private bool _operationRunning;
    private bool _startupPreflightPending;
    private bool _trayHintShown;
    private int _tourStep;

    private static readonly (string Title, string Body)[] TourSteps =
    {
        ("1 · Check setup",
         "Start here. Check setup confirms your credentials, finds your Always Free limit, and looks for existing instances. It never creates anything."),
        ("2 · Try once",
         "Try once makes a single launch attempt so you can see whether Oracle has capacity right now. It stops after one try."),
        ("3 · Start hunting",
         "Start hunting retries in the background with randomized pauses until a server is running. Keep the window open or minimize it to the tray; an alarm sounds on success. Press Stop anytime.")
    };
    private readonly DispatcherTimer _uiTimer = new();
    private WinForms.NotifyIcon? _trayIcon;

    public MainWindow(AppConfig config, bool runStartupPreflight = false)
    {
        InitializeComponent();
        _config = config;
        _startupPreflightPending = runStartupPreflight;

        InitializeTrayIcon();
        UpdateEnvironmentStrip();
        AppendLog("👋 How this works: 1) ✓ Preflight verifies your setup → 2) ▶ Start Hunting retries");
        AppendLog("    until Oracle has capacity → 3) leave it running — an alarm sounds on success.");

        _uiTimer.Interval = TimeSpan.FromMilliseconds(250);
        _uiTimer.Tick += (_, _) => UpdateStatusDisplay();
        _uiTimer.Start();

        SourceInitialized += (_, _) => EnableDarkTitleBar();
        StateChanged += (_, _) => HandleMinimizeToTray();
        Closing += (_, e) => OnWindowClosing(e);
        SizeChanged += (_, _) => PositionTour();
    }

    // --- Dark title bar (Win11/10) ---
    private void EnableDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref _trueFalse, sizeof(int));
        }
        catch { /* best effort on older Windows */ }
    }

    private static int _trueFalse = 1;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // --- Environment strip ---
    private void UpdateEnvironmentStrip()
    {
        TxtEnvCompartment.Text = $"Compartment:  {TruncateOcid(_config.CompartmentOcid)}";
        TxtEnvCompartment.ToolTip = _config.CompartmentOcid;
        var sshFile = Path.GetFileName(_config.SshPublicKeyPath);
        if (CheckSshPair(out var sshDetail))
        {
            TxtEnvSsh.Text = $"SSH:  {sshFile}";
            TxtEnvSsh.ToolTip = sshDetail;
            TxtEnvSsh.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        }
        else
        {
            TxtEnvSsh.Text = $"SSH:  {sshFile} ⚠ unverified";
            TxtEnvSsh.ToolTip = sshDetail;
            TxtEnvSsh.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        }
        TxtEnvRetry.Text = $"Retry:  {_config.MinIntervalSeconds}–{_config.MaxIntervalSeconds}s";
    }

    /// <summary>
    /// Returns true when the configured SSH public key matches a local private
    /// key. A false result is a warning only — format-only launches are still
    /// allowed — but it must be surfaced so it cannot be missed at launch time.
    /// </summary>
    private bool CheckSshPair(out string detail)
    {
        try
        {
            var verified = CryptoHelper.ValidateSshKeyPair(
                _config.SshPublicKeyPath,
                CryptoHelper.DerivePrivateKeyPath(_config.SshPublicKeyPath, _config.SshPrivateKeyPath));
            detail = verified
                ? "SSH key pair verified."
                : "SSH pairing NOT verified: no private key found. Public-key format is valid, but SSH login cannot be proven. Launch will proceed with format-only validation.";
            return verified;
        }
        catch (Exception ex)
        {
            detail = $"SSH pairing failed: {DiagnosticLog.Redact(ex.Message)}";
            return false;
        }
    }

    private static string TruncateOcid(string ocid) =>
        ocid.Length <= 40 ? ocid : ocid[..32] + "…" + ocid[^6..];

    // --- Tray icon ---
    private void InitializeTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "OracleHost",
            Visible = false,
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "OracleHost")
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open OracleHost", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { RestoreFromTray(); Close(); });
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HandleMinimizeToTray()
    {
        if (WindowState != WindowState.Minimized || _trayIcon == null) return;
        Hide();
        _trayIcon.Visible = true;
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(3000, "OracleHost is still running",
                _isHunting
                    ? "The hunt continues in the background. Double-click the tray icon to reopen."
                    : "Double-click the tray icon to reopen the dashboard.",
                WinForms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        if (_trayIcon == null) return;
        Show();
        WindowState = WindowState.Normal;
        _trayIcon.Visible = false;
        Activate();
    }

    // --- Logging ---
    private const int MaxLogChars = 200_000;

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AppendLog(message)); return; }
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        if (TxtLog.Text.Length > MaxLogChars)
        {
            var text = TxtLog.Text;
            var cut = text.IndexOf('\n', text.Length / 2);
            TxtLog.Text = "[… older entries trimmed; see the diagnostic log …]\n" +
                          (cut >= 0 ? text[(cut + 1)..] : string.Empty);
        }
        TxtLog.AppendText($"[{timestamp}] {message}\r\n");
        TxtLog.ScrollToEnd();
        DiagnosticLog.Info("Dashboard", message);
    }

    // --- First-run walkthrough ---
    private void ShowTour(int step)
    {
        _tourStep = Math.Clamp(step, 0, TourSteps.Length - 1);
        var (title, body) = TourSteps[_tourStep];
        TxtTourCount.Text = $"STEP {_tourStep + 1} OF {TourSteps.Length}";
        TxtTourTitle.Text = title;
        TxtTourBody.Text = body;
        BtnTourBack.IsEnabled = _tourStep > 0;
        BtnTourNext.Content = _tourStep == TourSteps.Length - 1 ? "Got it" : "Next";
        TourOverlay.Visibility = Visibility.Visible;
        PositionTour();
    }

    /// <summary>The real button each walkthrough step describes.</summary>
    private Button TourTarget(int step) => step switch
    {
        0 => BtnPreflight,
        1 => BtnOnce,
        _ => BtnStart
    };

    /// <summary>
    /// Cuts a rounded spotlight hole over the current step's button, draws a ring
    /// around it, and places the explanation card above it (or below when there is
    /// no room above). Called on show and whenever the window is resized.
    /// </summary>
    private void PositionTour()
    {
        if (TourOverlay.Visibility != Visibility.Visible) return;
        UpdateLayout();

        var target = TourTarget(_tourStep);
        var bounds = target.TransformToVisual(TourOverlay)
            .TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        const double pad = 8;
        var hole = new Rect(bounds.X - pad, bounds.Y - pad, bounds.Width + pad * 2, bounds.Height + pad * 2);

        var overlayW = TourOverlay.ActualWidth;
        var overlayH = TourOverlay.ActualHeight;
        TourDim.Data = Geometry.Combine(
            new RectangleGeometry(new Rect(0, 0, overlayW, overlayH)),
            new RectangleGeometry(hole, 16, 16),
            GeometryCombineMode.Exclude, null);

        TourRing.Width = hole.Width;
        TourRing.Height = hole.Height;
        TourRing.Margin = new Thickness(hole.X, hole.Y, 0, 0);

        const double edge = 16;
        var cardW = TourCard.ActualWidth;
        var cardH = TourCard.ActualHeight;
        var x = Math.Clamp(hole.X + hole.Width / 2 - cardW / 2, edge, Math.Max(edge, overlayW - cardW - edge));
        var y = hole.Y - cardH - edge;
        if (y < edge) y = hole.Bottom + edge;
        y = Math.Clamp(y, edge, Math.Max(edge, overlayH - cardH - edge));
        TourCard.Margin = new Thickness(x, y, 0, 0);
    }

    private void CloseTour()
    {
        TourOverlay.Visibility = Visibility.Collapsed;
        if (_config.HasSeenOnboarding) return;

        _config.HasSeenOnboarding = true;
        try
        {
            ConfigService.Save(_config);
        }
        catch (Exception ex)
        {
            // The tour simply reappears next launch if the flag cannot be saved.
            DiagnosticLog.Exception("Onboarding", "Save first-run flag", ex);
        }
    }

    private void BtnTour_Click(object sender, RoutedEventArgs e)
    {
        new OciGuideWindow { Owner = this }.ShowDialog();
    }

    private void BtnTourNext_Click(object sender, RoutedEventArgs e)
    {
        if (_tourStep < TourSteps.Length - 1) ShowTour(_tourStep + 1);
        else CloseTour();
    }

    private void BtnTourBack_Click(object sender, RoutedEventArgs e) => ShowTour(_tourStep - 1);

    private void BtnTourSkip_Click(object sender, RoutedEventArgs e) => CloseTour();

    // --- Buttons ---
    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticLog.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{DiagnosticLog.LogPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Dashboard", "Open diagnostic log", ex);
            MessageBox.Show(this,
                $"Could not open the diagnostic log folder.\n\nLog file:\n{DiagnosticLog.LogPath}\n\n{DiagnosticLog.Redact(ex.Message)}",
                "OracleHost Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyToClipboard(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            AppendLog($"⚠ Nothing to copy — no {what} available.");
            return;
        }
        try
        {
            Clipboard.SetText(text);
            AppendLog($"📋 Copied {what} to clipboard.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Dashboard", $"Copy {what} to clipboard", ex);
            AppendLog($"⚠ Could not copy {what}: {DiagnosticLog.Redact(ex.Message)}");
        }
    }

    private void BtnCopyLog_Click(object sender, RoutedEventArgs e) => CopyToClipboard(TxtLog.Text, "activity log");
    private void BtnCopySsh_Click(object sender, RoutedEventArgs e) => CopyToClipboard($"ssh opc@{_status.PublicIp}", "SSH command");
    private void BtnCopyIp_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_status.PublicIp ?? "", "public IP");

    private void BtnDiagnoseNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (_operationRunning)
        {
            AppendLog("⚠ Wait for the current OCI operation to finish before running network diagnostics.");
            return;
        }
        new NetworkDiagnosticWindow(_config) { Owner = this }.ShowDialog();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_operationRunning)
        {
            AppendLog("⚠ Stop the current operation before changing settings.");
            return;
        }
        var wizard = new SettingsWindow(_config) { Owner = this };
        if (wizard.ShowDialog() != true) return;
        UpdateEnvironmentStrip();
        AppendLog("⚙ Settings updated. New values apply to the next preflight or hunt.");
    }

    // --- Status display ---
    private void UpdateStatusDisplay()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(UpdateStatusDisplay); return; }

        (string text, System.Windows.Media.Brush brush, string description) = _status.State switch
        {
            HuntState.Idle => ("●  IDLE", (System.Windows.Media.Brush)FindResource("TextSecondary"), "Waiting to hunt for Always Free capacity..."),
            HuntState.Preflight => ("●  PREFLIGHT", (System.Windows.Media.Brush)FindResource("VioletBrush"), "Validating credentials and limits..."),
            HuntState.Hunting when _status.NextRetryIn > 0 => ($"●  WAITING ({_status.NextRetryIn:F0}s)", (System.Windows.Media.Brush)FindResource("WarningBrush"), "Oracle has no capacity yet — retrying soon..."),
            HuntState.Hunting => ("●  HUNTING", (System.Windows.Media.Brush)FindResource("SuccessBrush"), "Searching for Always Free capacity..."),
            HuntState.Success when _status.SshState == SshProbeResult.Unreachable || _status.SshState == SshProbeResult.Pending => ("●  SUCCESS", (System.Windows.Media.Brush)FindResource("SuccessBrush"), "Instance is running — Recovery required: SSH remains inaccessible, instance preserved."),
            HuntState.Success when _status.SshState == SshProbeResult.LoginVerified => ("●  SUCCESS", (System.Windows.Media.Brush)FindResource("SuccessBrush"), "Instance is running — SSH login verified."),
            HuntState.Success when _status.SshState == SshProbeResult.Reachable => ("●  SUCCESS", (System.Windows.Media.Brush)FindResource("SuccessBrush"), "Instance is running — SSH port reachable (login not verified)."),
            HuntState.Success => ("●  SUCCESS", (System.Windows.Media.Brush)FindResource("SuccessBrush"), "Instance is running — connect via SSH."),
            HuntState.Aborted => ("●  ERROR", (System.Windows.Media.Brush)FindResource("DangerBrush"), "Unable to complete the request — see the log."),
            HuntState.Stopped => ("●  STOPPED", (System.Windows.Media.Brush)FindResource("TextSecondary"), "Hunt stopped. Start again at any time."),
            _ => ($"●  {_status.State}", (System.Windows.Media.Brush)FindResource("TextSecondary"), string.Empty)
        };
        TxtStatus.Text = text;
        TxtStatus.Foreground = brush;
        TxtStatusDescription.Text = description;
        TxtReady.Text = _status.State switch
        {
            HuntState.Hunting => "Hunting in progress",
            HuntState.Success => "Instance ready",
            _ => "Ready to hunt"
        };

        Title = _status.State switch
        {
            HuntState.Hunting when _status.NextRetryIn > 0 =>
                $"⏳ Retry in {_status.NextRetryIn:F0}s (attempt #{_status.Attempts}) — OracleHost",
            HuntState.Hunting => $"🔍 Hunting — attempt #{_status.Attempts} — OracleHost",
            HuntState.Success => "✅ Instance ready! — OracleHost",
            _ => "OracleHost — Always Free Instance Hunter"
        };

        TxtAttempts.Text = _status.Attempts.ToString();
        TxtCapacityHits.Text = _status.CapacityHits.ToString();
        TxtCurrentAd.Text = _status.CurrentAd ?? "-";
        TxtElapsed.Text = _status.ElapsedFormatted;
        TxtNextRetry.Text = _status.NextRetryFormatted;

        // Hunt toggle button: Start when idle, Stop while hunting.
        var actionsEnabled = !_operationRunning;
        BtnOnce.IsEnabled = actionsEnabled;
        BtnPreflight.IsEnabled = actionsEnabled;
        if (_isHunting)
        {
            TxtStartLabel.Text = "Stop";
            TxtStartSub.Text = "Halt operations";
            TxtStartIcon.Text = "⏹";
            BtnStart.Background = (System.Windows.Media.Brush)FindResource("RedGrad");
            BtnStart.IsEnabled = true;
        }
        else
        {
            TxtStartLabel.Text = "Start Hunting";
            TxtStartSub.Text = "Keep trying automatically";
            TxtStartIcon.Text = "▶";
            BtnStart.Background = (System.Windows.Media.Brush)FindResource("AccentGrad");
            BtnStart.IsEnabled = actionsEnabled;
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_config.HasSeenOnboarding) ShowTour(0);
        if (!_startupPreflightPending) return;
        _startupPreflightPending = false;
        _ = RunPreflightAsync(true);
    }

    // --- Preflight ---
    private async void BtnPreflight_Click(object sender, RoutedEventArgs e) => await RunPreflightAsync(false);

    private async Task RunPreflightAsync(bool automatic)
    {
        if (_operationRunning || !IsLoaded) return;

        _operationRunning = true;
        var ct = _lifetimeCts.Token;
        _status.State = HuntState.Preflight;
        AppendLog(automatic
            ? "🔐 Authentication configured — running automatic preflight check..."
            : "Running preflight check...");
        UpdateStatusDisplay();

        bool sshPairVerified = false;
        try
        {
            using var oci = CreateOciService();
            AppendLog($"Region: {oci.Region ?? _config.Region ?? "unknown"}");
            sshPairVerified = CheckSshPair(out var sshDetail);
            AppendLog(sshPairVerified
                ? $"✓ {sshDetail}"
                : $"⚠ {sshDetail} Launch will proceed with format-only validation.");
            var readiness = await oci.DiagnoseSshReadinessAsync(_config, ct);
            foreach (var check in readiness.Checks)
                AppendLog($"[{check.State}] SSH readiness — {check.Title}: {check.Detail}");

            var allAds = await oci.ListAvailabilityDomainsAsync(ct);
            AppendLog($"Availability domains: {string.Join(", ", allAds)}");

            var capableAds = await oci.ListShapeAdsWithCapacityAsync(_config, ct);
            if (capableAds.Count == 0)
                throw new InvalidOperationException(
                    $"{_config.Shape} is not offered in any availability domain of this tenancy.");
            AppendLog($"{_config.Shape} available in: {string.Join(", ", capableAds)}");

            var images = await oci.ListImagesAsync(_config.CompartmentOcid, _config.ImageOs, _config.Shape, ct);
            if (images.Count == 0)
            {
                AppendLog($"ERROR: No {_config.ImageOs} images found for shape {_config.Shape}");
                _status.State = HuntState.Aborted;
                return;
            }
            var image = SelectImage(images);
            _status.ImageName = image.DisplayName ?? image.Id;
            AppendLog($"Image: {image.DisplayName}");

            await ValidateTenancyCapacityAsync(oci, ct);

            var limit = await oci.DetectAlwaysFreeLimitAsync(_config, ct);
            if (!limit.HasValue)
                throw new InvalidOperationException(
                    "Always Free limits could not be verified; Oracle returned no usable limit value.");

            AppendLog($"Always Free limit ({_config.ActiveLimitName}): {limit.Value}");
            AppendLog(sshPairVerified
                ? "✅ Preflight OK - everything looks ready."
                : "✅ Preflight OK - everything looks ready. (SSH pairing unverified — SSH login not proven.)");
            _status.State = HuntState.Idle;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Window closing cancels in-flight preflight calls quietly.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Preflight", "Preflight operation", ex);
            var detail = DescribeAuthenticationFailure(ex);
            AppendLog($"❌ Preflight failed: {detail}\\nDetails: {DiagnosticLog.LogPath}");
            _status.State = HuntState.Aborted;
        }
        finally
        {
            _operationRunning = false;
            UpdateStatusDisplay();
        }
    }

    /// <summary>Fails unless the complete visible-tenancy inventory leaves enough Always Free capacity.</summary>
    private async Task ValidateTenancyCapacityAsync(OciService oci, CancellationToken ct, int? additionalInstances = null)
    {
        // Inventory every visible compartment in the tenancy, not just the
        // selected compartment. Other VMs and volumes consume the same free-tier pools.
        var usage = await oci.GetTenancyUsageAsync(ct);
        var activeInstances = usage.Instances.Where(i =>
            i.LifecycleState != Instance.LifecycleStateEnum.Terminated).ToList();
        var existingSameShape = activeInstances.Where(i =>
            string.Equals(i.Shape, _config.Shape, StringComparison.OrdinalIgnoreCase)).ToList();
        var plannedCount = additionalInstances ?? _config.DesiredInstanceCount;
        var plannedBootGb = (long)(_config.BootVolumeSizeGb ?? AppConfig.AlwaysFreeBootVolumeSizeGb) * plannedCount;
        var (allowanceDescription, _) = _config.AlwaysFreeAllowance;

        AppendLog($"Tenancy inventory: {usage.Instances.Count} instance(s) across " +
                  $"{usage.Instances.Select(i => i.CompartmentId).Distinct().Count()} compartment(s); " +
                  $"{existingSameShape.Count} active {_config.Shape} instance(s).");
        AppendLog($"Storage in tenancy: {usage.BootVolumeGb} GB boot + {usage.BlockVolumeGb} GB block = " +
                  $"{usage.BootVolumeGb + usage.BlockVolumeGb}/{AppConfig.AlwaysFreeBlockStorageGb} GB. " +
                  $"Requested plan adds {plannedBootGb} GB boot storage.");

        if (_config.IsFlexibleShape)
        {
            var existingOcpus = existingSameShape.Sum(i => (double)(i.ShapeConfig?.Ocpus ?? 0));
            var existingMemoryGb = existingSameShape.Sum(i => (double)(i.ShapeConfig?.MemoryInGBs ?? 0));
            var requestedOcpus = (_config.Ocpus ?? AppConfig.AlwaysFreeA1OcpuCount) * plannedCount;
            var requestedMemoryGb = (_config.MemoryInGbs ??
                (_config.Ocpus ?? AppConfig.AlwaysFreeA1OcpuCount) * 6) * plannedCount;
            var totalOcpus = existingOcpus + requestedOcpus;
            var totalMemoryGb = existingMemoryGb + requestedMemoryGb;
            AppendLog($"A1 tenancy usage: {existingOcpus:0.#}/{AppConfig.AlwaysFreeA1OcpuCount} OCPU, " +
                      $"{existingMemoryGb:0.#}/{AppConfig.AlwaysFreeA1MemoryGb} GB RAM; requested adds " +
                      $"{requestedOcpus} OCPU / {requestedMemoryGb} GB.");
            if (existingSameShape.Count + plannedCount > AppConfig.AlwaysFreeMaxInstancesPerShape ||
                totalOcpus > AppConfig.AlwaysFreeA1OcpuCount ||
                totalMemoryGb > AppConfig.AlwaysFreeA1MemoryGb)
                throw new InvalidOperationException(
                    $"Preflight stopped: existing tenancy A1 usage plus this plan exceeds the Always Free allowance " +
                    $"({allowanceDescription}; at most {AppConfig.AlwaysFreeMaxInstancesPerShape} VMs). " +
                    "Reduce instance count/OCPU/memory or remove existing A1 instances, including in other compartments.");
        }
        else if (existingSameShape.Count + plannedCount > AppConfig.AlwaysFreeMicroInstanceCount)
        {
            throw new InvalidOperationException(
                $"Preflight stopped: the tenancy has {existingSameShape.Count} active {AppConfig.MicroShape} instance(s), " +
                $"and this plan requests {plannedCount}; the tier limit is {AppConfig.AlwaysFreeMicroInstanceCount}. " +
                "Remove an existing micro instance (including in another compartment) or reduce the target count.");
        }

        var totalStorageGb = usage.BootVolumeGb + usage.BlockVolumeGb + plannedBootGb;
        if (totalStorageGb > AppConfig.AlwaysFreeBlockStorageGb)
            throw new InvalidOperationException(
                $"Preflight stopped: tenancy storage is {usage.BootVolumeGb + usage.BlockVolumeGb} GB and this plan " +
                $"adds {plannedBootGb} GB, exceeding the {AppConfig.AlwaysFreeBlockStorageGb} GB combined boot/block-volume pool. " +
                "Delete or resize existing volumes, or reduce the target count/boot size.");

        AppendLog("✅ Existing tenancy usage leaves enough Always Free capacity for this plan.");
    }

    /// <summary>Picks the newest image, or the configured image_version match when one is set.</summary>
    private Image SelectImage(List<Image> images)
    {
        if (_config.ImageVersion != "latest")
        {
            var match = images.FirstOrDefault(i =>
                (i.DisplayName ?? "").Contains(_config.ImageVersion, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            AppendLog($"⚠ No image matched version '{_config.ImageVersion}'; using {images[0].DisplayName}.");
        }
        return images[0];
    }

    private static string DescribeAuthenticationFailure(Exception ex)
    {
        var message = DiagnosticLog.Redact(ex.Message);
        var lower = message.ToLowerInvariant();
        var localKeyProblem = lower.Contains("fingerprint") ||
                              lower.Contains("private api key") ||
                              lower.Contains("pem key");
        var oracleAuthProblem = lower.Contains("authentication") ||
                                lower.Contains("not authorized") ||
                                lower.Contains("unauthorized") ||
                                lower.Contains("401") ||
                                lower.Contains("incorrect");

        if (localKeyProblem || !oracleAuthProblem)
            return message;

        return "Oracle rejected the API request. The key file and fingerprint were verified locally, " +
               "but that does not prove Oracle has this public key registered. Verify that this public key is registered under the exact User OCID and that " +
               "the User OCID and Tenancy OCID belong to the same OCI account. " +
               $"OCI response: {message}";
    }

    // --- Single attempt ---
    private async void BtnOnce_Click(object sender, RoutedEventArgs e)
    {
        if (_operationRunning || !IsLoaded) return;

        _operationRunning = true;
        DisposeCts();
        _cts = new CancellationTokenSource();
        _status.StartTime = DateTime.UtcNow;
        _status.NextRetryIn = 0;
        UpdateStatusDisplay();

        var targetCount = _config.EffectiveInstanceCount;
        if (targetCount <= 1)
        {
            AppendLog($"Attempting single instance launch...");
            await RunSingleAttemptFlowAsync();
            return;
        }

        AppendLog($"Attempting up to {targetCount} instance(s) using the shared template...");
        await RunMultiAttemptOnceFlowAsync(targetCount);
    }

    private async Task RunSingleAttemptFlowAsync()
    {
        try
        {
            var result = await RunOneAttemptAsync(_cts?.Token ?? CancellationToken.None);
            if (result == "success")
            {
                AppendLog($"✅ Instance created! IP: {_status.PublicIp}");
                NotifyInstanceSuccess();
            }
            else if (result == "abort")
                AppendLog($"❌ Aborted: {_status.LastError}");
            else
                AppendLog($"⚠ Capacity not available: {_status.LastError}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("TryOnce", "Single launch attempt", ex);
            AppendLog($"❌ Error: {DiagnosticLog.Redact(ex.Message)}\nDetails: {DiagnosticLog.LogPath}");
        }
        finally
        {
            _operationRunning = false;
            if (_status.State == HuntState.Hunting)
                _status.State = HuntState.Idle;
            UpdateStatusDisplay();
        }
    }

    private async Task RunMultiAttemptOnceFlowAsync(int targetCount)
    {
        try
        {
            var achieved = await RunMultiAttemptCoreAsync(targetCount, oneShot: true);
            var summary = achieved == 0
                ? "No instance was created."
                : achieved < targetCount
                    ? $"Created {achieved} of {targetCount} target instance(s); the rest were not reached."
                    : $"Created {achieved} of {targetCount} target instance(s).";
            AppendLog($"✔ {summary}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("TryOnce", "Multi-instance single-run flow", ex);
            AppendLog($"❌ Error: {DiagnosticLog.Redact(ex.Message)}\nDetails: {DiagnosticLog.LogPath}");
        }
        finally
        {
            _operationRunning = false;
            if (_status.State == HuntState.Hunting)
                _status.State = HuntState.Idle;
            UpdateStatusDisplay();
        }
    }

    /// <summary>
    /// Shared-template "try once" loop: attempts up to <paramref name="targetCount"/>
    /// launches sequentially without retrying capacity failures. Returns how many
    /// instances reached RUNNING. Capacity/abort outcomes stop the pass so a
    /// single Try Once click never turns into an unbounded hunt.
    /// </summary>
    private async Task<int> RunMultiAttemptCoreAsync(int targetCount, bool oneShot)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        using var oci = CreateOciService();
        await ValidateTenancyCapacityAsync(oci, ct);
        var images = await oci.ListImagesAsync(_config.CompartmentOcid, _config.ImageOs, _config.Shape, ct);
        if (images.Count == 0)
        {
            AppendLog($"ERROR: No {_config.ImageOs} images found for shape {_config.Shape}");
            _status.State = HuntState.Aborted;
            return 0;
        }
        var image = SelectImage(images);
        _status.ImageName = image.DisplayName ?? image.Id;
        var ads = await ResolveShapeAvailabilityDomainsAsync(oci, ct);
        var achieved = 0;
        for (var i = 1; i <= targetCount && !ct.IsCancellationRequested; i++)
        {
            await ValidateTenancyCapacityAsync(oci, ct, targetCount - achieved);
            _status.State = HuntState.Hunting;
            _status.Attempts++;
            var ad = ads[(_status.Attempts - 1) % ads.Count];
            _status.CurrentAd = ad;
            AppendLog($"Attempt #{_status.Attempts} -> {ad} (instance {i} of {targetCount})");
            var outcome = await AttemptLaunchAsync(oci, ad, image, ct);
            if (outcome == AttemptOutcome.Success)
            {
                achieved++;
                AppendLog($"✅ Instance #{achieved} RUNNING - IP: {_status.PublicIp}");
                NotifyInstanceSuccess();
                continue;
            }
            if (outcome == AttemptOutcome.Abort)
            {
                AppendLog($"❌ Aborted: {_status.LastError}");
                break;
            }
            AppendLog($"⚠ Capacity not available: {_status.LastError}");
            if (oneShot)
                break;
        }
        return achieved;
    }

    // --- Continuous hunting (toggle: Start ⇄ Stop) ---
    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_isHunting)
        {
            _cts?.Cancel();
            AppendLog("Stopping...");
            return;
        }
        if (_operationRunning || !IsLoaded) return;

        _operationRunning = true;
        DisposeCts();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _isHunting = true;
        _status.Reset();
        _status.State = HuntState.Hunting;
        _status.StartTime = DateTime.UtcNow;
        Progress.Visibility = Visibility.Visible;

        var targetCount = _config.EffectiveInstanceCount;
        AppendLog(targetCount <= 1
            ? "🎯 Starting continuous hunt for one instance..."
            : $"🎯 Starting continuous hunt for up to {targetCount} instance(s) using the shared template...");


        try
        {
            using var oci = CreateOciService();
            await ValidateTenancyCapacityAsync(oci, ct);

            var images = await oci.ListImagesAsync(
                _config.CompartmentOcid, _config.ImageOs, _config.Shape, ct);
            if (images.Count == 0) { AppendLog("ERROR: No images found!"); return; }
            var image = SelectImage(images);
            _status.ImageName = image.DisplayName ?? image.Id;

            var ads = await ResolveShapeAvailabilityDomainsAsync(oci, ct);
            AppendLog($"Hunting across {ads.Count} availability domain(s): {string.Join(", ", ads)}");

            var achievedCount = 0;
            while (!ct.IsCancellationRequested && achievedCount < targetCount)
            {
                _status.NextRetryIn = 0;
                _status.Attempts++;
                var ad = ads[(_status.Attempts - 1) % ads.Count];
                _status.CurrentAd = ad;
                AppendLog($"Attempt #{_status.Attempts} -> {ad}");

                await ValidateTenancyCapacityAsync(oci, ct, targetCount - achievedCount);
                var outcome = await AttemptLaunchAsync(oci, ad, image, ct);
                if (outcome == AttemptOutcome.Success)
                {
                    achievedCount++;
                    AppendLog($"✅ Instance #{achievedCount} RUNNING - IP: {_status.PublicIp}");
                    NotifyInstanceSuccess();

                    if (achievedCount >= targetCount)
                    {
                        AppendLog($"🎉 Wanted {targetCount} instance(s); {achievedCount} are running.");
                        Progress.Visibility = Visibility.Collapsed;
                        return;
                    }

                    AppendLog($"Backing off before the next instance...");
                    var delay = Random.Shared.Next(_config.MinIntervalSeconds, _config.MaxIntervalSeconds + 1);
                    for (int i = delay; i > 0 && !ct.IsCancellationRequested; i--)
                    {
                        _status.NextRetryIn = i;
                        await Task.Delay(1000, ct);
                    }
                    continue;
                }
                if (outcome == AttemptOutcome.Abort)
                {
                    AppendLog($"❌ FATAL: {_status.LastError}");
                    Progress.Visibility = Visibility.Collapsed;
                    return;
                }
                AppendLog($"⚠ {_status.LastError}");

                if (_config.MaxAttempts > 0 && _status.Attempts >= _config.MaxAttempts)
                {
                    _status.State = HuntState.Stopped;
                    AppendLog($"Max attempts ({_config.MaxAttempts}) reached.");
                    Progress.Visibility = Visibility.Collapsed;
                    return;
                }

                var retryDelay = Random.Shared.Next(_config.MinIntervalSeconds, _config.MaxIntervalSeconds + 1);
                AppendLog($"⏳ Oracle capacity unavailable — retrying in {retryDelay}s across the availability domains.");
                for (int i = retryDelay; i > 0 && !ct.IsCancellationRequested; i--)
                {
                    _status.NextRetryIn = i;
                    await Task.Delay(1000, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Hunt stopped by user.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Hunt", "Continuous hunt", ex);
            AppendLog($"❌ Error: {DiagnosticLog.Redact(ex.Message)}\nDetails: {DiagnosticLog.LogPath}");
        }
        finally
        {
            _isHunting = false;
            _operationRunning = false;
            if (_status.State != HuntState.Success && _status.State != HuntState.Aborted)
                _status.State = HuntState.Stopped;
            _status.NextRetryIn = 0;
            Progress.Visibility = Visibility.Collapsed;
            UpdateStatusDisplay();
        }
    }

    private OciService CreateOciService()
    {
        var service = new OciService();
        service.Initialize(_config);
        return service;
    }

    private void DisposeCts()
    {
        _cts?.Dispose();
        _cts = null;
    }

    private async Task<string> RunOneAttemptAsync(CancellationToken ct)
    {
        _status.State = HuntState.Hunting;
        _status.Attempts++;

        using var oci = CreateOciService();
        await ValidateTenancyCapacityAsync(oci, ct);
        Image image;
        string ad;
        try
        {
            var images = await oci.ListImagesAsync(_config.CompartmentOcid, _config.ImageOs, _config.Shape, ct);
            if (images.Count == 0) return "abort";
            image = SelectImage(images);
            _status.ImageName = image.DisplayName ?? image.Id;

            var ads = await ResolveShapeAvailabilityDomainsAsync(oci, ct);
            ad = ads[(_status.Attempts - 1) % ads.Count];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("TryOnce", "Prepare launch attempt", ex);
            var classification = ErrorClassifier.Classify(ex, _config.StopOnLimit);
            _status.LastError = classification.Reason;
            if (classification.Kind == ErrorKind.Abort) { _status.State = HuntState.Aborted; return "abort"; }
            _status.CapacityHits++;
            return "retry";
        }
        _status.CurrentAd = ad;
        AppendLog($"Attempt #{_status.Attempts} -> {ad}");

        return await AttemptLaunchAsync(oci, ad, image, ct) switch
        {
            AttemptOutcome.Success => "success",
            AttemptOutcome.Abort => "abort",
            _ => "retry"
        };
    }

    private enum AttemptOutcome { Success, Retry, Abort }

    private async Task<AttemptOutcome> AttemptLaunchAsync(
        OciService oci, string ad, Image image, CancellationToken ct)
    {
        try
        {
            var instance = await oci.LaunchInstanceAsync(_config, ad, image, ct);
            _status.SuccessInstanceId = instance.Id;
            AppendLog($"✅ Instance created: {instance.Id}; waiting for RUNNING state...");

            var runningFor = await WaitForRunningAsync(oci, instance.Id, ct);
            if (!runningFor.HasValue)
            {
                _status.LastError = "Instance was created but did not reach RUNNING state.";
                return AttemptOutcome.Retry;
            }

            // Probe after RUNNING. A probe failure never retries/terminates:
            // the instance exists and counts toward the target.
            if (_config.EnableSshHealthCheck && _config.AssignPublicIp && !string.IsNullOrWhiteSpace(_status.PublicIp))
            {
                _status.SshState = await SshProbe.ProbeSshAsync(_status.PublicIp, runningFor.Value, ct);
                AppendLog($"Provisioned → Running → Port ({_status.SshState})");
                if (_status.SshState == SshProbeResult.Reachable)
                {
                    var privatePath = CryptoHelper.DerivePrivateKeyPath(_config.SshPublicKeyPath, _config.SshPrivateKeyPath);
                    var auth = await SshProbe.TryAuthenticateAsync(_status.PublicIp, privatePath, "opc", ct, instanceOcid: _status.SuccessInstanceId, onPrompt: PromptHostKeyApprovalAsync);
                    if (auth == SshAuthResult.Verified)
                    {
                        _status.SshState = SshProbeResult.LoginVerified;
                        AppendLog($"✅ SSH login verified — ssh opc@{_status.PublicIp} (key authentication succeeded)");
                        var cloudInitEnabled = _config.EnableCloudInit && _config.IsOracleLinuxImage;
                        var report = await SshProbe.TryRetrieveProvisionLogAsync(_status.PublicIp, privatePath, cloudInitEnabled, "opc", ct, instanceOcid: _status.SuccessInstanceId, onPrompt: PromptHostKeyApprovalAsync);
                        _status.ProvisionLogKind = report.LogKind;
                        _status.ProvisionSteps = report.Steps;
                        if (report.LogKind == ProvisionLogKind.Retrieved)
                        {
                            static string Label(ProvisionStepOutcome s) => s switch { ProvisionStepOutcome.Success => "Success", ProvisionStepOutcome.Failed => "Failed", ProvisionStepOutcome.Unknown => "Unknown", _ => "Unknown" };
                            var sshRule = ProvisionLogParser.CollapseSshFirewallRule(report.Steps);
                            AppendLog($"Provision log retrieved: SSH daemon={Label(report.Steps["sshd"])}, Firewall service={Label(report.Steps["firewalld"])}, SSH firewall rule={Label(sshRule)} (firewall_add={Label(report.Steps["firewall_add"])}, firewall_query={Label(report.Steps["firewall_query"])}, firewall_reload={Label(report.Steps["firewall_reload"])})");
                        }
                        else if (report.LogKind == ProvisionLogKind.NotApplicable)
                            AppendLog("Provision log: Not Applicable (cloud-init disabled for this launch) — not a failure.");
                        else if (report.LogKind == ProvisionLogKind.Missing)
                            AppendLog("Provision log: Missing (/var/log/oraclehost-provision.log unavailable) — login itself is verified; first-boot config cannot be confirmed.");
                        else
                            AppendLog("Provision log: Error (could not retrieve; read-only attempt failed). Login is still verified; provisioning outcome unknown.");
                    }
                    else if (auth == SshAuthResult.FailedAuth)
                        AppendLog($"⚠ SSH port reachable (login not verified) — key authentication failed. Port is open but the private key did not match.");
                    else if (auth == SshAuthResult.HostKeyUntrusted)
                        AppendLog($"⚠ Host key untrusted — SSH authentication skipped until you approve the host key for this instance OCID. Instance preserved. (Not an auth failure.)");
                    else if (auth == SshAuthResult.HostKeyChanged)
                        AppendLog($"⚠ Host key CHANGED — logged-in verification blocked until you review and approve the replacement key. Instance preserved; never silently accepted.");
                    else if (auth == SshAuthResult.NoKey || auth == SshAuthResult.NotAttempted)
                        AppendLog($"✅ SSH port reachable (TCP 22, login not verified) — try: ssh opc@{_status.PublicIp} (no private key to attempt login)");
                    else
                        AppendLog($"✅ SSH port reachable (TCP 22, login not verified) — login attempt errored. Try: ssh opc@{_status.PublicIp}");
                }
                else if (_status.SshState == SshProbeResult.Pending)
                    AppendLog("⚠ SSH initialization pending — first-boot config not yet verified. Instance preserved; open Network Diagnostic.");
                else
                    AppendLog("⚠ Recovery required: SSH remains inaccessible — instance preserved for diagnosis. Do not auto-terminate. Open Network Diagnostic. To verify firewall after authenticated login, run: sudo firewall-cmd --get-active-zones; sudo firewall-cmd --list-all; sudo firewall-cmd --permanent --query-service=ssh (verify the zone assigned to your interface, not just service definitions).");
            }

            _status.State = HuntState.Success;
            return AttemptOutcome.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Hunt", $"Launch attempt #{_status.Attempts}", ex);
            var classification = ErrorClassifier.Classify(ex, _config.StopOnLimit);
            _status.LastError = classification.Reason;
            if (classification.Kind == ErrorKind.Abort)
            {
                _status.State = HuntState.Aborted;
                return AttemptOutcome.Abort;
            }
            _status.CapacityHits++;
            return AttemptOutcome.Retry;
        }
    }

    private async Task<List<string>> ResolveShapeAvailabilityDomainsAsync(OciService oci, CancellationToken ct)
    {
        var allAds = await oci.ListAvailabilityDomainsAsync(ct);
        var configuredAds = FilterAvailabilityDomains(allAds, _config.AvailabilityDomains);
        var capableAds = await oci.ListShapeAdsWithCapacityAsync(_config, ct);
        var ads = configuredAds.Where(capableAds.Contains).ToList();
        if (ads.Count == 0)
            throw new InvalidOperationException(
                $"None of the configured availability domains offer {_config.Shape}. " +
                $"Configured: {string.Join(", ", configuredAds)}. Shape-capable: {string.Join(", ", capableAds)}.");
        return ads;
    }

    private static List<string> FilterAvailabilityDomains(List<string> allAds, string? configured)
    {
        var setting = (configured ?? "all").Trim();
        if (setting.Length == 0 || setting.Equals("all", StringComparison.OrdinalIgnoreCase))
            return allAds;

        var tokens = setting.Split(new[] { ',', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = allAds.Where(ad => tokens.Any(token =>
            ad.Equals(token, StringComparison.OrdinalIgnoreCase) ||
            ad.Contains(token, StringComparison.OrdinalIgnoreCase))).ToList();

        if (selected.Count == 0)
            throw new InvalidOperationException(
                $"None of the requested ADs exist in this region (availability_domains: \"{setting}\"). " +
                $"Available: {string.Join(", ", allAds)}. Use \"all\" or fix the setting.");
        return selected;
    }

        private Task<bool> PromptHostKeyApprovalAsync(HostKeyPrompt prompt)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            const string scanWarning = "⚠ WARNING: ssh-keyscan does not authenticate the server — a man-in-the-middle could supply a different key during discovery. Verify this fingerprint against a trusted out-of-band source (e.g. OCI console / serial output, or a known provisioning log) before approving.";
            var text = (prompt.IsChange
                ? $"⚠ SSH host key CHANGED for instance {RedactOcid(prompt.InstanceOcid)} at {prompt.Host}.\n\n{scanWarning}\n\nPrevious fingerprints:\n{string.Join("\n", prompt.PreviousKeys.Select(k => $"  {k.KeyType} {k.Fingerprint}"))}\n\nNew fingerprints:\n{string.Join("\n", prompt.Keys.Select(k => $"  {k.KeyType} {k.Fingerprint}"))}\n\nApprove the NEW key only if you verified it out-of-band? Importing it replaces the old trusted key (this instance only). Reject if you did not expect a change."
                : $"New SSH host key for instance {RedactOcid(prompt.InstanceOcid)} at {prompt.Host}.\n\n{scanWarning}\n\nFingerprint(s):\n{string.Join("\n", prompt.Keys.Select(k => $"  {k.KeyType} {k.Fingerprint}"))}\n\nTrust this server? Verify the fingerprint against a trusted source first. Once approved it is saved by OCID (valid known_hosts syntax) and verified strictly on later connections.");
            return MessageBox.Show(this, text, prompt.IsChange ? "SSH host key changed" : "Trust SSH host key?", MessageBoxButton.YesNo, prompt.IsChange ? MessageBoxImage.Exclamation : MessageBoxImage.Question) == MessageBoxResult.Yes;
        }).Task;
    }
    private static string RedactOcid(string ocid) => ocid.Length <= 18 ? ocid : ocid[..12] + "…" + ocid[^6..];

    private string ProvisionSummary()
    {
        if (_status.ProvisionLogKind == null || _status.ProvisionSteps.Count == 0) return string.Empty;
        static string Short(ProvisionStepOutcome s) => s switch { ProvisionStepOutcome.Success => "OK", ProvisionStepOutcome.Failed => "FAILED", _ => "?" };
        return _status.ProvisionLogKind switch
        {
            ProvisionLogKind.Retrieved => $" | Provision [{Short(_status.ProvisionSteps["sshd"])} sshd / {Short(_status.ProvisionSteps["firewalld"])} firewalld / {Short(ProvisionLogParser.CollapseSshFirewallRule(_status.ProvisionSteps))} ssh-rule]",
            ProvisionLogKind.NotApplicable => " | Provision: N/A (cloud-init off)",
            ProvisionLogKind.Missing => " | Provision log missing",
            _ => " | Provision log error"
        };
    }

    private void ShowSuccessPanel()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ShowSuccessPanel); return; }

        var ip = _status.PublicIp;
        var sshLine = _status.SshState switch
        {
            SshProbeResult.LoginVerified => $"✅ SSH login verified — ssh opc@{ip}" + ProvisionSummary(),
            SshProbeResult.Reachable => $"SSH port reachable (login not verified) — try: ssh opc@{ip}",
            SshProbeResult.Pending => "SSH initialization pending — instance preserved. Open Network Diagnostic.",
            SshProbeResult.Unreachable => "Recovery required: SSH remains inaccessible — instance preserved. Do not auto-terminate. Open Network Diagnostic.",
            _ => ip != null ? $"Connect with:  ssh opc@{ip}" : "No public IP was assigned."
        };
        TxtSuccess.Text =
            $"🎉 Instance is RUNNING!  {sshLine}\n" +
            $"OCID: {_status.SuccessInstanceId}";
        BtnCopySsh.IsEnabled = ip != null;
        BtnCopyIp.IsEnabled = ip != null;
        PanelSuccess.Visibility = Visibility.Visible;
    }

    private void NotifyInstanceSuccess()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(NotifyInstanceSuccess); return; }

        ShowSuccessPanel();
        AppendLog("🔔 SUCCESS ALARM — Oracle instance is ready.");
        SystemSounds.Exclamation.Play();
        SystemSounds.Asterisk.Play();

        if (_trayIcon is { Visible: true })
        {
            _trayIcon.ShowBalloonTip(10000, "🎉 Oracle instance is ready!",
                _status.PublicIp != null
                    ? $"Your Always Free instance is RUNNING.\nssh opc@{_status.PublicIp}"
                    : "Your Always Free instance is RUNNING.",
                WinForms.ToolTipIcon.Info);
            return;
        }

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private async Task<TimeSpan?> WaitForRunningAsync(OciService oci, string instanceId, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var inst = await oci.GetInstanceAsync(instanceId, ct);
                if (inst == null) return null;

                var state = inst.LifecycleState.ToString();
                if (state == "Running")
                {
                    if (_config.AssignPublicIp)
                        _status.PublicIp = await oci.GetPublicIpAsync(_config.CompartmentOcid, instanceId, ct);
                    return DateTime.UtcNow - started;
                }

                if (state == "Terminated" || state == "Failed")
                {
                    AppendLog($"Instance ended in state {state}");
                    return null;
                }

                AppendLog($"Provisioning... ({state})");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Exception("Hunt", "Polling instance state", ex);
                AppendLog($"⚠ Could not read instance state: {DiagnosticLog.Redact(ex.Message)}");
            }

            await Task.Delay(10000, ct);
        }
        AppendLog("Timed out waiting for RUNNING state.");
        return null;
    }

    private void OnWindowClosing(System.ComponentModel.CancelEventArgs e)
    {
        // An accidental close would silently end an overnight hunt.
        if (_isHunting)
        {
            var answer = MessageBox.Show(this,
                "A hunt is still running. Close OracleHost and stop hunting?",
                "OracleHost — Hunt in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _lifetimeCts.Cancel();
        _cts?.Cancel();
        DisposeCts();
        _uiTimer.Stop();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }
}
