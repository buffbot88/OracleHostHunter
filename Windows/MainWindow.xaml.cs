using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls.Primitives;
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
        TxtEnvSsh.Text = $"SSH:  {Path.GetFileName(_config.SshPublicKeyPath)}";
        TxtEnvRetry.Text = $"Retry:  {_config.MinIntervalSeconds}–{_config.MaxIntervalSeconds}s";
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
            BtnStart.Background = (System.Windows.Media.Brush)FindResource("GreenGrad");
            BtnStart.IsEnabled = actionsEnabled;
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
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

        try
        {
            using var oci = CreateOciService();
            AppendLog($"Region: {oci.Region ?? _config.Region ?? "unknown"}");

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

            var instances = await oci.ListInstancesAsync(_config.CompartmentOcid, ct);
            var existingSameShape = instances.Where(i =>
                i.Shape == _config.Shape &&
                i.LifecycleState != Instance.LifecycleStateEnum.Terminated &&
                i.LifecycleState != Instance.LifecycleStateEnum.Terminating).ToList();

            var (allowanceDescription, allowanceCount) = _config.AlwaysFreeAllowance;
            if (_config.IsFlexibleShape && existingSameShape.Count > 0)
            {
                var usedOcpus = existingSameShape
                    .Sum(i => (double)(i.ShapeConfig?.Ocpus ?? 0));
                var remaining = Math.Max(0, AppConfig.AlwaysFreeA1OcpuCount - (int)Math.Ceiling(usedOcpus));
                AppendLog($"⚠ {existingSameShape.Count} existing A1 instance(s) use {usedOcpus:0.#} OCPU(s); " +
                          $"{remaining} of the {AppConfig.AlwaysFreeA1OcpuCount} Always Free OCPU remain.");
                if (remaining < (_config.Ocpus ?? 1) && !_config.AllowExisting)
                    throw new InvalidOperationException(
                        $"Preflight stopped: the Always Free A1 allotment ({allowanceDescription}) is already used " +
                        "by existing instances. Delete one or enable AllowExisting before hunting.");
            }
            else if (existingSameShape.Count >= allowanceCount)
            {
                AppendLog($"⚠ {existingSameShape.Count} existing instance(s) of this shape found.");
                if (!_config.AllowExisting)
                    throw new InvalidOperationException(
                        $"Preflight stopped: {existingSameShape.Count} {_config.Shape} instance(s) already exist, " +
                        $"which meets the Always Free allowance ({allowanceDescription}). " +
                        "Delete one or enable AllowExisting before hunting.");
            }
            else
                AppendLog(existingSameShape.Count == 0
                    ? "No existing instances of this shape - clear to hunt."
                    : $"{existingSameShape.Count} existing instance(s); hunting for the remaining Always Free allowance.");

            var limit = await oci.DetectAlwaysFreeLimitAsync(_config, ct);
            if (!limit.HasValue)
                throw new InvalidOperationException(
                    "Always Free limits could not be verified; Oracle returned no usable limit value.");

            AppendLog($"Always Free limit ({_config.ActiveLimitName}): {limit.Value}");
            AppendLog("✅ Preflight OK - everything looks ready.");
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
            AppendLog($"❌ Preflight failed: {detail}\nDetails: {DiagnosticLog.LogPath}");
            _status.State = HuntState.Aborted;
        }
        finally
        {
            _operationRunning = false;
            UpdateStatusDisplay();
        }
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
        AppendLog("Attempting single instance launch...");

        try
        {
            var result = await RunOneAttemptAsync(_cts.Token);
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
        AppendLog("🎯 Starting continuous hunt...");

        try
        {
            using var oci = CreateOciService();

            var images = await oci.ListImagesAsync(
                _config.CompartmentOcid, _config.ImageOs, _config.Shape, ct);
            if (images.Count == 0) { AppendLog("ERROR: No images found!"); return; }
            var image = SelectImage(images);
            _status.ImageName = image.DisplayName ?? image.Id;

            var ads = await ResolveShapeAvailabilityDomainsAsync(oci, ct);
            AppendLog($"Hunting across {ads.Count} availability domain(s): {string.Join(", ", ads)}");

            while (!ct.IsCancellationRequested)
            {
                _status.NextRetryIn = 0;
                _status.Attempts++;
                var ad = ads[(_status.Attempts - 1) % ads.Count];
                _status.CurrentAd = ad;
                AppendLog($"Attempt #{_status.Attempts} -> {ad}");

                var outcome = await AttemptLaunchAsync(oci, ad, image, ct);
                if (outcome == AttemptOutcome.Success)
                {
                    AppendLog($"🎉 Instance RUNNING - IP: {_status.PublicIp}");
                    NotifyInstanceSuccess();
                    Progress.Visibility = Visibility.Collapsed;
                    return;
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

                var delay = Random.Shared.Next(_config.MinIntervalSeconds, _config.MaxIntervalSeconds + 1);
                AppendLog($"⏳ Oracle capacity unavailable — retrying in {delay}s across the availability domains.");
                for (int i = delay; i > 0 && !ct.IsCancellationRequested; i--)
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

            var running = await WaitForRunningAsync(oci, instance.Id, ct);
            if (!running)
            {
                _status.LastError = "Instance was created but did not reach RUNNING state.";
                return AttemptOutcome.Retry;
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

    private void ShowSuccessPanel()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ShowSuccessPanel); return; }

        var ip = _status.PublicIp;
        TxtSuccess.Text =
            $"🎉 Instance is RUNNING!  {(ip != null ? $"Connect with:  ssh opc@{ip}" : "No public IP was assigned.")}\n" +
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

    private async Task<bool> WaitForRunningAsync(OciService oci, string instanceId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var inst = await oci.GetInstanceAsync(instanceId, ct);
                if (inst == null) return false;

                var state = inst.LifecycleState.ToString();
                if (state == "Running")
                {
                    if (_config.AssignPublicIp)
                        _status.PublicIp = await oci.GetPublicIpAsync(_config.CompartmentOcid, instanceId, ct);
                    return true;
                }

                if (state == "Terminated" || state == "Failed")
                {
                    AppendLog($"Instance ended in state {state}");
                    return false;
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
        return false;
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
