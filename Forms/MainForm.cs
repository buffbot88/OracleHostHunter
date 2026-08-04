using OracleHost.Helpers;
using OracleHost.Models;
using OracleHost.Services;
using System.Diagnostics;
using System.Media;
using Oci.CoreService.Models;
using Image = Oci.CoreService.Models.Image;

namespace OracleHost.Forms;

/// <summary>
/// Main hunting dashboard - matches the Python rich live dashboard.
/// Shows status, attempts, capacity hits, elapsed time, and live activity log.
/// </summary>
public class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly Label _lblStatus = new();
    private readonly Label _lblAttempts = new();
    private readonly Label _lblCapacityHits = new();
    private readonly Label _lblCurrentAd = new();
    private readonly Label _lblElapsed = new();
    private readonly Label _lblNextRetry = new();
    private readonly Label _lblImage = new();
    private readonly TextBox _txtLog = new();
    private readonly Button _btnStart = new();
    private readonly Button _btnStop = new();
    private readonly Button _btnOnce = new();
    private readonly Button _btnPreflight = new();
    private readonly Button _btnOpenLogs = new();
    private readonly Button _btnCopyLog = new();
    private readonly Button _btnSettings = new();
    private readonly Panel _panelSuccess = new();
    private readonly Label _lblSuccessDetails = new();
    private readonly Button _btnCopySsh = new();
    private readonly Button _btnCopyIp = new();
    private readonly ToolTip _toolTip = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly ProgressBar _progressBar = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private bool _trayHintShown;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _cts;
    private HuntStatus _status = new();
    private bool _isHunting;
    private bool _operationRunning;
    private bool _startupPreflightPending;

    public MainForm(AppConfig config, bool runStartupPreflight = false)
    {
        _config = config;
        _startupPreflightPending = runStartupPreflight;
        InitializeComponent();
        InitializeTrayIcon();
        AppendLog("👋 How this works: 1) ✓ Preflight verifies your setup → 2) ▶ Start Hunting retries");
        AppendLog("    until Oracle has capacity → 3) leave it running — an alarm sounds on success.");
        _uiTimer.Interval = 250;
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        Text = "OracleHost — Always Free ARM Instance Hunter";
        Size = new Size(700, 620);
        MinimumSize = new Size(700, 620);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 24, 27);
        Font = new Font("Segoe UI", 10F);

        var lblTitle = new Label
        {
            Text = "☁ OracleHost",
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 140, 0),
            Location = new Point(15, 10),
            AutoSize = true
        };

        var lblShape = new Label
        {
            Text = $"{_config.Shape} · {_config.Ocpus} OCPU / {_config.MemoryInGb} GB · {_config.Region ?? "default"}",
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(161, 161, 170),
            Location = new Point(17, 42),
            AutoSize = true
        };

        var panelStatus = new Panel
        {
            Location = new Point(15, 70),
            Size = new Size(320, 200),
            BackColor = Color.FromArgb(32, 32, 35),
            Padding = new Padding(10)
        };

        int y = 10;
        _lblStatus.Text = "IDLE";
        _lblStatus.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
        _lblStatus.ForeColor = Color.FromArgb(161, 161, 170);
        _lblStatus.Location = new Point(10, y);
        _lblStatus.AutoSize = true;
        panelStatus.Controls.Add(_lblStatus);
        y += 35;

        SetupStatusRow(panelStatus, "Attempts:", _lblAttempts, "0", y); y += 28;
        SetupStatusRow(panelStatus, "Capacity hits:", _lblCapacityHits, "0", y); y += 28;
        SetupStatusRow(panelStatus, "Current AD:", _lblCurrentAd, "-", y); y += 28;
        SetupStatusRow(panelStatus, "Elapsed:", _lblElapsed, "00:00", y); y += 28;
        SetupStatusRow(panelStatus, "Next retry:", _lblNextRetry, "-", y); y += 28;
        SetupStatusRow(panelStatus, "Image:", _lblImage, "-", y);

        var lblActivity = new Label
        {
            Text = "Activity",
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(161, 161, 170),
            Location = new Point(345, 70),
            AutoSize = true
        };

        _txtLog.Location = new Point(345, 92);
        _txtLog.Size = new Size(320, 178);
        _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.BackColor = Color.FromArgb(32, 32, 35);
        _txtLog.ForeColor = Color.FromArgb(161, 161, 170);
        _txtLog.Font = new Font("Consolas", 9F);
        _txtLog.WordWrap = true;

        _progressBar.Location = new Point(15, 280);
        _progressBar.Size = new Size(650, 6);
        _progressBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.ForeColor = Color.FromArgb(255, 140, 0);
        _progressBar.BackColor = Color.FromArgb(32, 32, 35);
        _progressBar.Visible = false;

        // ---- Success panel (hidden until an instance is RUNNING) ----
        _panelSuccess.Location = new Point(15, 280);
        _panelSuccess.Size = new Size(650, 56);
        _panelSuccess.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _panelSuccess.BackColor = Color.FromArgb(20, 45, 30);
        _panelSuccess.Visible = false;

        _lblSuccessDetails.Location = new Point(10, 6);
        _lblSuccessDetails.Size = new Size(420, 44);
        _lblSuccessDetails.ForeColor = Color.FromArgb(134, 239, 172);
        _lblSuccessDetails.Font = new Font("Segoe UI", 9F);
        _panelSuccess.Controls.Add(_lblSuccessDetails);

        _btnCopySsh.Text = "📋 Copy SSH";
        _btnCopySsh.Location = new Point(435, 12);
        _btnCopySsh.Size = new Size(105, 32);
        StyleButton(_btnCopySsh, Color.FromArgb(34, 197, 94));
        _btnCopySsh.Click += (_, _) => CopyToClipboard($"ssh opc@{_status.PublicIp}", "SSH command");
        _panelSuccess.Controls.Add(_btnCopySsh);

        _btnCopyIp.Text = "📋 Copy IP";
        _btnCopyIp.Location = new Point(548, 12);
        _btnCopyIp.Size = new Size(92, 32);
        StyleButton(_btnCopyIp, Color.FromArgb(63, 63, 70));
        _btnCopyIp.Click += (_, _) => CopyToClipboard(_status.PublicIp ?? "", "public IP");
        _panelSuccess.Controls.Add(_btnCopyIp);

        int btnY = 344;
        _btnPreflight.Text = "✓ Preflight Check";
        _btnPreflight.Location = new Point(15, btnY);
        _btnPreflight.Size = new Size(150, 40);
        _btnPreflight.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        StyleButton(_btnPreflight, Color.FromArgb(79, 70, 229));
        _btnPreflight.Click += BtnPreflight_Click;
        _toolTip.SetToolTip(_btnPreflight,
            "Validates credentials, detects your Always Free limits,\nand checks for existing instances. Run this first.");

        _btnOnce.Text = "⚡ Try Once";
        _btnOnce.Location = new Point(175, btnY);
        _btnOnce.Size = new Size(150, 40);
        _btnOnce.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        StyleButton(_btnOnce, Color.FromArgb(59, 130, 246));
        _btnOnce.Click += BtnOnce_Click;
        _toolTip.SetToolTip(_btnOnce, "Makes a single launch attempt, then stops.");

        _btnStart.Text = "▶ Start Hunting";
        _btnStart.Location = new Point(335, btnY);
        _btnStart.Size = new Size(160, 40);
        _btnStart.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        StyleButton(_btnStart, Color.FromArgb(34, 197, 94));
        _btnStart.Click += BtnStart_Click;
        _toolTip.SetToolTip(_btnStart,
            "Retries automatically every " +
            $"{_config.MinIntervalSeconds}–{_config.MaxIntervalSeconds}s until capacity frees up.\nLeave it running — you'll hear an alarm on success.");

        _btnStop.Text = "⏹ Stop";
        _btnStop.Location = new Point(505, btnY);
        _btnStop.Size = new Size(160, 40);
        _btnStop.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        StyleButton(_btnStop, Color.FromArgb(239, 68, 68));
        _btnStop.Enabled = false;
        _btnStop.Click += BtnStop_Click;
        _toolTip.SetToolTip(_btnStop, "Cancels the current hunt. Nothing is deleted.");

        _btnOpenLogs.Text = "📄 Open Diagnostic Log";
        _btnOpenLogs.Location = new Point(15, 454);
        _btnOpenLogs.Size = new Size(210, 35);
        _btnOpenLogs.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        StyleButton(_btnOpenLogs, Color.FromArgb(63, 63, 70));
        _btnOpenLogs.Click += BtnOpenLogs_Click;
        _toolTip.SetToolTip(_btnOpenLogs, "Opens the folder containing the full diagnostic log file.");

        _btnCopyLog.Text = "📋 Copy Activity Log";
        _btnCopyLog.Location = new Point(235, 454);
        _btnCopyLog.Size = new Size(190, 35);
        _btnCopyLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        StyleButton(_btnCopyLog, Color.FromArgb(63, 63, 70));
        _btnCopyLog.Click += (_, _) => CopyToClipboard(_txtLog.Text, "activity log");
        _toolTip.SetToolTip(_btnCopyLog, "Copies the activity log to the clipboard for sharing.");

        _btnSettings.Text = "⚙ Settings";
        _btnSettings.Location = new Point(435, 454);
        _btnSettings.Size = new Size(120, 35);
        _btnSettings.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        StyleButton(_btnSettings, Color.FromArgb(63, 63, 70));
        _btnSettings.Click += BtnSettings_Click;
        _toolTip.SetToolTip(_btnSettings, "Reopens the Setup Wizard to change region, subnet,\nretry intervals, or other settings.");

        var lblConfig = new Label
        {
            Text = $"Compartment: {_config.CompartmentOcid[..Math.Min(30, _config.CompartmentOcid.Length)]}... | " +
                   $"SSH: {Path.GetFileName(_config.SshPublicKeyPath)} | " +
                   $"Retry: {_config.MinIntervalSeconds}-{_config.MaxIntervalSeconds}s",
            Font = new Font("Segoe UI", 8F),
            ForeColor = Color.FromArgb(113, 113, 122),
            Location = new Point(15, 399),
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };

        Controls.Add(lblTitle);
        Controls.Add(lblShape);
        Controls.Add(panelStatus);
        Controls.Add(lblActivity);
        Controls.Add(_txtLog);
        Controls.Add(_progressBar);
        Controls.Add(_panelSuccess);
        Controls.Add(_btnPreflight);
        Controls.Add(_btnOnce);
        Controls.Add(_btnStart);
        Controls.Add(_btnStop);
        Controls.Add(_btnOpenLogs);
        Controls.Add(_btnCopyLog);
        Controls.Add(_btnSettings);
        Controls.Add(lblConfig);

        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>
    /// Configures the tray icon: minimizing hides the window to the tray during
    /// long hunts, and success pops a balloon notification even when hidden.
    /// </summary>
    private void InitializeTrayIcon()
    {
        // Reuse the executable's own icon so no new asset is required.
        _trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        _trayIcon.Text = "OracleHost";
        _trayIcon.Visible = false;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open OracleHost", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { RestoreFromTray(); Close(); });
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.BalloonTipClicked += (_, _) => RestoreFromTray();

        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized) return;
            Hide();
            _trayIcon.Visible = true;
            UpdateTrayText();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloonTip(3000, "OracleHost is still running",
                    _isHunting
                        ? "The hunt continues in the background. Double-click the tray icon to reopen."
                        : "Double-click the tray icon to reopen the dashboard.",
                    ToolTipIcon.Info);
            }
        };
    }

    private void RestoreFromTray()
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { BeginInvoke(RestoreFromTray); return; }

        Show();
        WindowState = FormWindowState.Normal;
        _trayIcon.Visible = false;
        Activate();
        BringToFront();
    }

    /// <summary>Keeps the tray hover text in sync with the hunt (max 63 chars for NotifyIcon).</summary>
    private void UpdateTrayText()
    {
        if (!_trayIcon.Visible) return;
        var text = _status.State switch
        {
            HuntState.Hunting when _status.NextRetryIn > 0 =>
                $"OracleHost — retry in {_status.NextRetryIn:F0}s (attempt #{_status.Attempts})",
            HuntState.Hunting => $"OracleHost — hunting, attempt #{_status.Attempts}",
            HuntState.Success => "OracleHost — instance ready!",
            _ => "OracleHost"
        };
        _trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    /// <summary>Applies the shared flat dark style plus a hover highlight.</summary>
    private static void StyleButton(Button button, Color backColor)
    {
        button.BackColor = backColor;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(backColor, 0.2f);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor, 0.1f);
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

    private void BtnSettings_Click(object? sender, EventArgs e)
    {
        if (_operationRunning)
        {
            AppendLog("⚠ Stop the current operation before changing settings.");
            return;
        }

        using var wizard = new SetupWizardForm(_config);
        if (wizard.ShowDialog(this) != DialogResult.OK) return;

        // The wizard mutates and saves the same AppConfig instance the
        // dashboard holds, so new values apply to the next operation.
        AppendLog("⚙ Settings updated. New values apply to the next preflight or hunt.");
        _toolTip.SetToolTip(_btnStart,
            "Retries automatically every " +
            $"{_config.MinIntervalSeconds}–{_config.MaxIntervalSeconds}s until capacity frees up.\nLeave it running — you'll hear an alarm on success.");
    }

    private void ShowSuccessPanel()
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { BeginInvoke(ShowSuccessPanel); return; }

        var ip = _status.PublicIp;
        _lblSuccessDetails.Text =
            $"🎉 Instance is RUNNING!  {(ip != null ? $"Connect with:  ssh opc@{ip}" : "No public IP was assigned.")}\n" +
            $"OCID: {_status.SuccessInstanceId}";
        _btnCopySsh.Enabled = ip != null;
        _btnCopyIp.Enabled = ip != null;
        _panelSuccess.Visible = true;
    }

    private static void SetupStatusRow(Control parent, string labelText, Label valueLabel, string defaultValue, int yPos)
    {
        var lbl = new Label
        {
            Text = labelText,
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(161, 161, 170),
            Location = new Point(10, yPos + 2),
            AutoSize = true
        };
        valueLabel.Text = defaultValue;
        valueLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        valueLabel.ForeColor = Color.White;
        valueLabel.Location = new Point(130, yPos + 2);
        valueLabel.AutoSize = true;
        parent.Controls.Add(lbl);
        parent.Controls.Add(valueLabel);
    }

    // Keeps day-long hunts from growing the activity TextBox without bound;
    // the full history is always in the diagnostic log file.
    private const int MaxLogChars = 200_000;

    private void AppendLog(string message)
    {
        if (IsDisposed || Disposing) return;
        if (_txtLog.InvokeRequired)
        {
            _txtLog.Invoke(() => AppendLog(message));
            return;
        }
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        if (_txtLog.TextLength > MaxLogChars)
        {
            var text = _txtLog.Text;
            var cut = text.IndexOf("\r\n", text.Length / 2, StringComparison.Ordinal);
            _txtLog.Text = "[… older entries trimmed; see the diagnostic log …]\r\n" +
                           (cut >= 0 ? text[(cut + 2)..] : string.Empty);
        }
        _txtLog.AppendText($"[{timestamp}] {message}\r\n");
        DiagnosticLog.Info("Dashboard", message);
    }

    private void BtnOpenLogs_Click(object? sender, EventArgs e)
    {
        try
        {
            DiagnosticLog.Info("Dashboard", "Opening the diagnostic log location.");
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
            MessageBox.Show(
                $"Could not open the diagnostic log folder.\n\nLog file:\n{DiagnosticLog.LogPath}\n\n{DiagnosticLog.Redact(ex.Message)}",
                "OracleHost Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateStatusDisplay()
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(UpdateStatusDisplay); } catch (InvalidOperationException) { }
            return;
        }

        _lblStatus.Text = _status.State switch
        {
            HuntState.Idle => "IDLE",
            HuntState.Preflight => "PREFLIGHT",
            HuntState.Hunting when _status.NextRetryIn > 0 => $"⏳ WAITING ({_status.NextRetryIn:F0}s)",
            HuntState.Hunting => "🔍 HUNTING...",
            HuntState.Success => "✅ SUCCESS",
            HuntState.Aborted => "❌ ABORTED",
            HuntState.Stopped => "⏹ STOPPED",
            _ => _status.State.ToString()
        };

        _lblStatus.ForeColor = _status.State switch
        {
            HuntState.Success => Color.FromArgb(34, 197, 94),
            HuntState.Aborted => Color.FromArgb(239, 68, 68),
            HuntState.Hunting when _status.NextRetryIn > 0 => Color.FromArgb(249, 115, 22),
            HuntState.Hunting => Color.FromArgb(250, 204, 21),
            _ => Color.FromArgb(161, 161, 170)
        };

        // Title-bar progress keeps the hunt state visible from the taskbar.
        Text = _status.State switch
        {
            HuntState.Hunting when _status.NextRetryIn > 0 =>
                $"⏳ Retry in {_status.NextRetryIn:F0}s (attempt #{_status.Attempts}) — OracleHost",
            HuntState.Hunting => $"🔍 Hunting — attempt #{_status.Attempts} — OracleHost",
            HuntState.Success => "✅ Instance ready! — OracleHost",
            _ => "OracleHost — Always Free ARM Instance Hunter"
        };

        _lblAttempts.Text = _status.Attempts.ToString();
        _lblCapacityHits.Text = _status.CapacityHits.ToString();
        _lblCurrentAd.Text = _status.CurrentAd ?? "-";
        _lblElapsed.Text = _status.ElapsedFormatted;
        _lblNextRetry.Text = _status.NextRetryFormatted;
        _lblImage.Text = _status.ImageName ?? "-";

        var actionsEnabled = !_operationRunning;
        _btnStart.Enabled = actionsEnabled;
        _btnOnce.Enabled = actionsEnabled;
        _btnPreflight.Enabled = actionsEnabled;
        _btnStop.Enabled = _isHunting;
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        UpdateStatusDisplay();
        UpdateTrayText();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_startupPreflightPending) return;

        _startupPreflightPending = false;
        _ = RunPreflightAsync(true);
    }

    // --- Preflight ---
    private async void BtnPreflight_Click(object? sender, EventArgs e)
    {
        await RunPreflightAsync(false);
    }

    private async Task RunPreflightAsync(bool automatic)
    {
        if (_operationRunning || IsDisposed || Disposing) return;

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
            DiagnosticLog.Info("Preflight", "OCI service initialized; listing availability domains.");

            var allAds = await oci.ListAvailabilityDomainsAsync(ct);
            AppendLog($"Availability domains: {string.Join(", ", allAds)}");
            var configuredAds = FilterAvailabilityDomains(allAds, _config.AvailabilityDomains);
            if (configuredAds.Count < allAds.Count)
                AppendLog($"Configured to hunt in: {string.Join(", ", configuredAds)}");

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
            var existingA1 = instances.Where(i =>
                i.Shape == _config.Shape &&
                i.LifecycleState != Oci.CoreService.Models.Instance.LifecycleStateEnum.Terminated &&
                i.LifecycleState != Oci.CoreService.Models.Instance.LifecycleStateEnum.Terminating).ToList();

            if (_config.Ocpus != AppConfig.HunterMaxOcpus ||
                _config.MemoryInGb != AppConfig.HunterMaxMemoryGb)
            {
                throw new InvalidOperationException(
                    $"Hunter safety protection stopped preflight: each launch must use exactly " +
                    $"{AppConfig.HunterMaxOcpus} OCPU / {AppConfig.HunterMaxMemoryGb} GB RAM.");
            }

            if (existingA1.Count > 0)
            {
                AppendLog($"⚠ {existingA1.Count} existing A1 instance(s) found.");
                if (!_config.AllowExisting)
                    throw new InvalidOperationException(
                        $"Preflight stopped: {existingA1.Count} existing {_config.Shape} instance(s) already exist. " +
                        "Delete them or enable AllowExisting before hunting.");

                var existingUsage = GetExistingA1Usage(existingA1);
                if (existingUsage.Ocpus + _config.Ocpus > AppConfig.AlwaysFreeA1Ocpus ||
                    existingUsage.MemoryGb + _config.MemoryInGb > AppConfig.AlwaysFreeA1MemoryGb)
                {
                    throw new InvalidOperationException(
                        $"Always Free protection stopped preflight: existing A1 usage is " +
                        $"{existingUsage.Ocpus:0.##} OCPU / {existingUsage.MemoryGb:0.##} GB, " +
                        $"so this launch would exceed the {AppConfig.AlwaysFreeA1Ocpus} OCPU / " +
                        $"{AppConfig.AlwaysFreeA1MemoryGb} GB account allowance.");
                }
            }
            else
                AppendLog("No existing A1 instances - clear to hunt.");

            var limits = await oci.DetectFreeLimitsAsync(ct);
            if (!limits.Ocpus.HasValue || !limits.MemoryGb.HasValue)
                throw new InvalidOperationException(
                    "Always Free limits could not be verified; Oracle returned no usable A1 limit values.");

            AppendLog($"Always Free A1 limits: {limits.Ocpus} OCPUs / {limits.MemoryGb} GB");
            if (_config.Ocpus > limits.Ocpus.Value)
                throw new InvalidOperationException(
                    $"Preflight stopped: configuration requests {_config.Ocpus} OCPUs, but Oracle reports only {limits.Ocpus} available.");
            if (_config.MemoryInGb > limits.MemoryGb.Value)
                throw new InvalidOperationException(
                    $"Preflight stopped: configuration requests {_config.MemoryInGb} GB RAM, but Oracle reports only {limits.MemoryGb} GB available.");

            AppendLog("✅ Preflight OK - everything looks ready.");
            _status.State = HuntState.Idle;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closing the dashboard cancels in-flight preflight calls quietly.
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

    private static (double Ocpus, double MemoryGb) GetExistingA1Usage(
        IEnumerable<Oci.CoreService.Models.Instance> instances)
    {
        double totalOcpus = 0;
        double totalMemoryGb = 0;

        foreach (var instance in instances)
        {
            var shapeConfig = instance.ShapeConfig;
            if (shapeConfig?.Ocpus == null || shapeConfig.MemoryInGBs == null)
            {
                throw new InvalidOperationException(
                    "Always Free protection could not verify the resource usage of an existing A1 instance. " +
                    "Remove it or disable AllowExisting before launching another instance.");
            }

            totalOcpus += shapeConfig.Ocpus.Value;
            totalMemoryGb += shapeConfig.MemoryInGBs.Value;
        }

        return (totalOcpus, totalMemoryGb);
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
    private async void BtnOnce_Click(object? sender, EventArgs e)
    {
        if (_operationRunning || IsDisposed || Disposing) return;

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
            // A single attempt is finished after one capacity miss; do not leave
            // the dashboard displaying HUNTING... when no retry loop is running.
            if (_status.State == HuntState.Hunting)
                _status.State = HuntState.Idle;
            UpdateStatusDisplay();
        }
    }

    // --- Continuous hunting ---
    private async void BtnStart_Click(object? sender, EventArgs e)
    {
        if (_operationRunning || IsDisposed || Disposing) return;

        _operationRunning = true;
        DisposeCts();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _isHunting = true;
        _status.Reset();
        _status.State = HuntState.Hunting;
        _status.StartTime = DateTime.UtcNow;
        _progressBar.Visible = true;
        AppendLog("🎯 Starting continuous hunt...");

        try
        {
            using var oci = CreateOciService();

            var images = await oci.ListImagesAsync(
                _config.CompartmentOcid, _config.ImageOs, _config.Shape, ct);
            if (images.Count == 0) { AppendLog("ERROR: No images found!"); return; }
            var image = SelectImage(images);
            _status.ImageName = image.DisplayName ?? image.Id;

            // Resolve the AD rotation once per hunt: ADs do not change mid-hunt,
            // and re-listing them on every attempt was a needless API call.
            var ads = FilterAvailabilityDomains(
                await oci.ListAvailabilityDomainsAsync(ct), _config.AvailabilityDomains);
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
                    _progressBar.Visible = false;
                    return;
                }
                if (outcome == AttemptOutcome.Abort)
                {
                    AppendLog($"❌ FATAL: {_status.LastError}");
                    _progressBar.Visible = false;
                    return;
                }
                AppendLog($"⚠ {_status.LastError}");

                if (_config.MaxAttempts > 0 && _status.Attempts >= _config.MaxAttempts)
                {
                    _status.State = HuntState.Stopped;
                    AppendLog($"Max attempts ({_config.MaxAttempts}) reached.");
                    _progressBar.Visible = false;
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
            _progressBar.Visible = false;
            UpdateStatusDisplay();
        }
    }

    private void BtnStop_Click(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        AppendLog("Stopping...");
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

            var ads = FilterAvailabilityDomains(
                await oci.ListAvailabilityDomainsAsync(ct), _config.AvailabilityDomains);
            ad = ads[(_status.Attempts - 1) % ads.Count];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Classify setup failures exactly like launch failures so a
            // capacity blip during image/AD discovery stays retryable.
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

    /// <summary>
    /// Runs one launch attempt and waits for RUNNING, classifying any failure.
    /// Shared by the continuous hunt and Try Once so both behave identically.
    /// </summary>
    private async Task<AttemptOutcome> AttemptLaunchAsync(
        OciService oci, string ad, Image image, CancellationToken ct)
    {
        try
        {
            var instance = await oci.LaunchInstanceAsync(_config, ad, image, ct);
            _status.SuccessInstanceId = instance.Id;
            AppendLog($"✅ Instance created: {instance.Id}; waiting for RUNNING state...");

            // Do not sound the success alarm while OCI is still provisioning.
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

    /// <summary>
    /// Applies the availability_domains config setting ("all", one AD, or a
    /// comma-separated list) to the ADs Oracle reports for this region.
    /// </summary>
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

    private void NotifyInstanceSuccess()
    {
        if (IsDisposed || Disposing) return;

        ShowSuccessPanel();
        AppendLog("🔔 SUCCESS ALARM — Oracle instance is ready.");
        SystemSounds.Exclamation.Play();
        SystemSounds.Asterisk.Play();

        // When hidden in the tray, a balloon is the only visible signal;
        // clicking it (or the icon) restores the dashboard.
        if (_trayIcon.Visible)
        {
            _trayIcon.ShowBalloonTip(10000, "🎉 Oracle instance is ready!",
                _status.PublicIp != null
                    ? $"Your Always Free ARM instance is RUNNING.\nssh opc@{_status.PublicIp}"
                    : "Your Always Free ARM instance is RUNNING.",
                ToolTipIcon.Info);
            UpdateTrayText();
            return;
        }

        // Bring the dashboard forward if it was minimized or behind another
        // window, while leaving the alarm non-blocking and safe for long hunts.
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // An accidental close would silently end an overnight hunt.
        if (_isHunting && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(
                "A hunt is still running. Close OracleHost and stop hunting?",
                "OracleHost — Hunt in progress", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _lifetimeCts.Cancel();
        _cts?.Cancel();
        DisposeCts();
        _uiTimer.Stop();
        // Remove the tray icon immediately; otherwise a ghost icon lingers in
        // the notification area until the user hovers over it.
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        // Keep the lifetime token source alive until in-flight async continuations
        // observe cancellation; the process exits with the form.
        base.OnFormClosing(e);
    }
}
