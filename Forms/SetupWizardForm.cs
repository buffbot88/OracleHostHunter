using OracleHost.Helpers;
using OracleHost.Models;
using OracleHost.Services;
using Oci.IdentityService.Models;
using Oci.CoreService.Models;
using Compartment = Oci.IdentityService.Models.Compartment;
using Subnet = Oci.CoreService.Models.Subnet;

namespace OracleHost.Forms;

/// <summary>
/// Interactive setup wizard matching the Python --setup functionality.
/// Guides users through configuration with OCI auto-detection when possible.
/// </summary>
public class SetupWizardForm : Form
{
    private readonly TextBox _txtCompartment = new();
    private readonly TextBox _txtSubnet = new();
    private readonly TextBox _txtRegion = new();
    private readonly TextBox _txtDisplayName = new();
    private readonly NumericUpDown _numOcpus = new();
    private readonly NumericUpDown _numMemory = new();
    private readonly NumericUpDown _numMinInterval = new();
    private readonly NumericUpDown _numMaxInterval = new();
    private readonly Label _lblStatus = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnAutoDetect = new();
    private readonly CheckBox _chkEncryptSessionTokens = new();
    private readonly ComboBox _comboCompartments = new();
    private readonly ComboBox _comboSubnets = new();
    private AppConfig? _ociConfig;
    private List<Compartment>? _compartments;
    private List<Subnet>? _subnets;

    public AppConfig ResultConfig { get; private set; } = new();

    public SetupWizardForm(AppConfig? ociConfig = null)
    {
        _ociConfig = ociConfig;
        InitializeComponent();
        TryAutoDetect();
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        Text = "OracleHost — Setup Wizard";
        Size = new Size(600, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(24, 24, 27);
        Font = new Font("Segoe UI", 10F);

        var lblTitle = new Label
        {
            Text = "⚙ Setup Wizard",
            Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 140, 0),
            Location = new Point(20, 15),
            AutoSize = true
        };

        var lblSubtitle = new Label
        {
            Text = "Configure your Oracle Cloud instance settings. Press Enter to accept defaults.",
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(161, 161, 170),
            Location = new Point(22, 55),
            AutoSize = true
        };

        int y = 85;

        // Compartment
        AddSectionLabel("Compartment:", y);
        _comboCompartments.Location = new Point(150, y);
        _comboCompartments.Size = new Size(410, 28);
        _comboCompartments.BackColor = Color.FromArgb(32, 32, 35);
        _comboCompartments.ForeColor = Color.White;
        _comboCompartments.DropDownStyle = ComboBoxStyle.DropDown;
        _comboCompartments.SelectedIndexChanged += ComboCompartments_SelectedIndexChanged;
        _comboCompartments.Text = _ociConfig?.CompartmentOcid ?? "";
        y += 40;

        // Subnet
        AddSectionLabel("Subnet:", y);
        _comboSubnets.Location = new Point(150, y);
        _comboSubnets.Size = new Size(410, 28);
        _comboSubnets.BackColor = Color.FromArgb(32, 32, 35);
        _comboSubnets.ForeColor = Color.White;
        _comboSubnets.DropDownStyle = ComboBoxStyle.DropDown;
        _comboSubnets.Text = _ociConfig?.SubnetOcid ?? "";
        y += 40;

        // Region
        AddSectionLabel("Region:", y);
        _txtRegion.Location = new Point(150, y);
        _txtRegion.Size = new Size(200, 28);
        _txtRegion.BackColor = Color.FromArgb(32, 32, 35);
        _txtRegion.ForeColor = Color.White;
        _txtRegion.BorderStyle = BorderStyle.FixedSingle;
        _txtRegion.Text = _ociConfig?.Region ?? "us-ashburn-1";
        y += 40;

        // Display Name
        AddSectionLabel("Display Name:", y);
        _txtDisplayName.Location = new Point(150, y);
        _txtDisplayName.Size = new Size(200, 28);
        _txtDisplayName.BackColor = Color.FromArgb(32, 32, 35);
        _txtDisplayName.ForeColor = Color.White;
        _txtDisplayName.BorderStyle = BorderStyle.FixedSingle;
        _txtDisplayName.Text = _ociConfig?.DisplayName ?? "free-tier-arm";
        y += 40;

        // OCPUs
        AddSectionLabel("OCPUs:", y);
        _numOcpus.Location = new Point(150, y);
        _numOcpus.Size = new Size(100, 28);
        _numOcpus.BackColor = Color.FromArgb(32, 32, 35);
        _numOcpus.ForeColor = Color.White;
        _numOcpus.Minimum = AppConfig.HunterMaxOcpus;
        _numOcpus.Maximum = AppConfig.HunterMaxOcpus;
        _numOcpus.Value = AppConfig.HunterMaxOcpus;
        y += 40;

        // Memory
        AddSectionLabel("Memory (GB):", y);
        _numMemory.Location = new Point(150, y);
        _numMemory.Size = new Size(100, 28);
        _numMemory.BackColor = Color.FromArgb(32, 32, 35);
        _numMemory.ForeColor = Color.White;
        _numMemory.Minimum = AppConfig.HunterMaxMemoryGb;
        _numMemory.Maximum = AppConfig.HunterMaxMemoryGb;
        _numMemory.Value = AppConfig.HunterMaxMemoryGb;
        y += 40;

        // Min Interval
        AddSectionLabel("Min Retry (s):", y);
        _numMinInterval.Location = new Point(150, y);
        _numMinInterval.Size = new Size(100, 28);
        _numMinInterval.BackColor = Color.FromArgb(32, 32, 35);
        _numMinInterval.ForeColor = Color.White;
        _numMinInterval.Minimum = 10;
        _numMinInterval.Maximum = 600;
        _numMinInterval.Value = Math.Clamp(
            _ociConfig?.MinIntervalSeconds ?? AppConfig.DefaultMinRetrySeconds,
            (int)_numMinInterval.Minimum,
            (int)_numMinInterval.Maximum);
        y += 40;

        // Max Interval
        AddSectionLabel("Max Retry (s):", y);
        _numMaxInterval.Location = new Point(150, y);
        _numMaxInterval.Size = new Size(100, 28);
        _numMaxInterval.BackColor = Color.FromArgb(32, 32, 35);
        _numMaxInterval.ForeColor = Color.White;
        _numMaxInterval.Minimum = 10;
        _numMaxInterval.Maximum = 600;
        _numMaxInterval.Value = Math.Clamp(
            _ociConfig?.MaxIntervalSeconds ?? AppConfig.DefaultMaxRetrySeconds,
            (int)_numMaxInterval.Minimum,
            (int)_numMaxInterval.Maximum);
        y += 50;

        var lblSafety = new Label
        {
            Text = $"🛡 Conservative hunter protection is ON: each A1 launch fixed at {AppConfig.HunterMaxOcpus} OCPU / {AppConfig.HunterMaxMemoryGb} GB RAM; boot volume fixed at {AppConfig.AlwaysFreeBootVolumeSizeGb} GB (OCI minimum); ephemeral public IP only.",
            Location = new Point(20, y),
            Size = new Size(540, 36),
            ForeColor = Color.FromArgb(134, 239, 172),
            Font = new Font("Segoe UI", 8.5F)
        };
        Controls.Add(lblSafety);
        y += 45;

        // Auto-detect button
        _btnAutoDetect.Text = "🔍 Auto-Detect Compartments & Subnets";
        _btnAutoDetect.Location = new Point(150, y);
        _btnAutoDetect.Size = new Size(280, 35);
        _btnAutoDetect.BackColor = Color.FromArgb(79, 70, 229);
        _btnAutoDetect.ForeColor = Color.White;
        _btnAutoDetect.FlatStyle = FlatStyle.Flat;
        _btnAutoDetect.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _btnAutoDetect.Click += BtnAutoDetect_Click;
        y += 45;

        _chkEncryptSessionTokens.Text = "Protect session token files with Windows DPAPI (current user only)";
        _chkEncryptSessionTokens.Location = new Point(150, y);
        _chkEncryptSessionTokens.Size = new Size(410, 28);
        _chkEncryptSessionTokens.BackColor = Color.FromArgb(24, 24, 27);
        _chkEncryptSessionTokens.ForeColor = Color.FromArgb(212, 212, 216);
        _chkEncryptSessionTokens.FlatStyle = FlatStyle.Flat;
        _chkEncryptSessionTokens.Checked = _ociConfig?.EncryptSessionTokens ?? false;
        _chkEncryptSessionTokens.CheckedChanged += ChkEncryptSessionTokens_CheckedChanged;
        Controls.Add(_chkEncryptSessionTokens);
        y += 40;

        // Save button
        _btnSave.Text = "💾 Save Configuration";
        _btnSave.Location = new Point(150, y);
        _btnSave.Size = new Size(200, 40);
        _btnSave.BackColor = Color.FromArgb(34, 197, 94);
        _btnSave.ForeColor = Color.White;
        _btnSave.FlatStyle = FlatStyle.Flat;
        _btnSave.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        _btnSave.Click += BtnSave_Click;

        // Enter saves; hover highlights make the flat buttons feel interactive.
        AcceptButton = _btnSave;
        foreach (var button in new[] { _btnSave, _btnAutoDetect })
        {
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(button.BackColor, 0.2f);
            button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(button.BackColor, 0.1f);
        }

        // Status
        _lblStatus.Location = new Point(20, y + 50);
        _lblStatus.Size = new Size(560, 90);
        _lblStatus.ForeColor = Color.FromArgb(161, 161, 170);
        _lblStatus.Font = new Font("Segoe UI", 9F);

        Controls.Add(lblTitle);
        Controls.Add(lblSubtitle);
        Controls.Add(_comboCompartments);
        Controls.Add(_comboSubnets);
        Controls.Add(_txtRegion);
        Controls.Add(_txtDisplayName);
        Controls.Add(_numOcpus);
        Controls.Add(_numMemory);
        Controls.Add(_numMinInterval);
        Controls.Add(_numMaxInterval);
        Controls.Add(_btnAutoDetect);
        Controls.Add(_btnSave);
        Controls.Add(_lblStatus);

        ResumeLayout(false);
        PerformLayout();
    }

    private void AddSectionLabel(string text, int y)
    {
        var label = new Label
        {
            Text = text,
            Location = new Point(20, y + 3),
            AutoSize = true,
            ForeColor = Color.FromArgb(212, 212, 216)
        };
        Controls.Add(label);
    }

    private async void TryAutoDetect()
    {
        if (_ociConfig == null || (!_ociConfig.HasCredentials && !_ociConfig.HasSession))
            return;

        _btnAutoDetect.Enabled = false;
        _lblStatus.Text = "🔍 Auto-detecting compartments...";
        _lblStatus.ForeColor = Color.FromArgb(59, 130, 246);

        try
        {
            using var ociService = new OciService();
            ociService.Initialize(_ociConfig);

            _compartments = await ociService.ListCompartmentsAsync();
            _comboCompartments.Items.Clear();
            foreach (var comp in _compartments)
            {
                _comboCompartments.Items.Add($"{comp.Name} ({comp.Id[..Math.Min(30, comp.Id.Length)]}...)");
            }
            if (_comboCompartments.Items.Count > 0)
                _comboCompartments.SelectedIndex = 0;

            _lblStatus.Text = $"✅ Found {_compartments.Count} compartment(s). Select one and click Save.";
            _lblStatus.ForeColor = Color.FromArgb(34, 197, 94);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Setup", "Auto-detect compartments", ex);
            var safeMessage = DiagnosticLog.Redact(ex.Message);
            var localKeyFailure = safeMessage.Contains("fingerprint", StringComparison.OrdinalIgnoreCase) ||
                                  safeMessage.Contains("private api key", StringComparison.OrdinalIgnoreCase) ||
                                  safeMessage.Contains("PEM key", StringComparison.OrdinalIgnoreCase);
            var authFailure = localKeyFailure ||
                              safeMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                              safeMessage.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
                              safeMessage.Contains("401", StringComparison.OrdinalIgnoreCase);
            var safeCredentialSummary = _ociConfig?.HasCredentials == true
                ? $"\nLocal key: {DiagnosticLog.SafeFileName(_ociConfig.KeyFilePath)}\nLocal fingerprint: {_ociConfig.Fingerprint}"
                : string.Empty;
            _lblStatus.Text = localKeyFailure
                ? $"❌ Local API-key problem: {safeMessage}\nSelect the private PEM file and matching fingerprint.{safeCredentialSummary}\nDetails: {DiagnosticLog.LogPath}"
                : authFailure
                    ? $"❌ Oracle rejected the request. The key was verified locally, but Oracle may not have this public key registered for the exact User OCID/Tenancy OCID. Verify the API key registration and account IDs.{safeCredentialSummary}\nDetails: {DiagnosticLog.LogPath}"
                    : $"⚠ Could not auto-detect: {safeMessage}. Enter OCIDs manually.\nDetails: {DiagnosticLog.LogPath}";
            _lblStatus.ForeColor = authFailure ? Color.FromArgb(239, 68, 68) : Color.FromArgb(250, 204, 21);
        }
        finally
        {
            _btnAutoDetect.Enabled = true;
        }
    }

    private async void BtnAutoDetect_Click(object? sender, EventArgs e)
    {
        await AutoDetectSubnets();
    }

    private async void ComboCompartments_SelectedIndexChanged(object? sender, EventArgs e)
    {
        await AutoDetectSubnets();
    }

    private async Task AutoDetectSubnets()
    {
        if (_ociConfig == null || (!_ociConfig.HasCredentials && !_ociConfig.HasSession) || _compartments == null)
            return;

        var idx = _comboCompartments.SelectedIndex;
        if (idx < 0 || idx >= _compartments.Count) return;

        var selectedComp = _compartments[idx];
        _txtCompartment.Text = selectedComp.Id;

        _lblStatus.Text = "🔍 Loading subnets...";
        _lblStatus.ForeColor = Color.FromArgb(59, 130, 246);

        try
        {
            using var ociService = new OciService();
            ociService.Initialize(_ociConfig);

            _subnets = await ociService.ListSubnetsAsync(selectedComp.Id);
            _comboSubnets.Items.Clear();
            foreach (var sub in _subnets)
            {
                _comboSubnets.Items.Add($"{sub.DisplayName} ({sub.CidrBlock})");
            }
            if (_comboSubnets.Items.Count == 0)
                throw new InvalidOperationException(
                    "No subnets were found in the selected compartment. Create or select a VCN/subnet before saving.");

            _comboSubnets.SelectedIndex = 0;
            _lblStatus.Text = $"✅ Found {_subnets.Count} subnet(s). Review and click Save.";
            _lblStatus.ForeColor = Color.FromArgb(34, 197, 94);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Setup", $"Load subnets for compartment {selectedComp.Id}", ex);
            _lblStatus.Text = $"⚠ Could not load subnets: {DiagnosticLog.Redact(ex.Message)}\nThis is usually a permissions or VCN/subnet issue.\nDetails: {DiagnosticLog.LogPath}";
            _lblStatus.ForeColor = Color.FromArgb(250, 204, 21);
        }
    }

    private void ChkEncryptSessionTokens_CheckedChanged(object? sender, EventArgs e)
    {
        if (_chkEncryptSessionTokens.Checked && !OperatingSystem.IsWindows())
        {
            _chkEncryptSessionTokens.Checked = false;
            _lblStatus.Text = "❌ Windows DPAPI session encryption is available only on Windows.";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
        }
    }

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        var compartmentOcid = _txtCompartment.Text.Trim();
        var subnetOcid = _comboSubnets.SelectedIndex >= 0 && _subnets != null
            ? _subnets[_comboSubnets.SelectedIndex].Id
            : _comboSubnets.Text.Trim();

        if (string.IsNullOrEmpty(compartmentOcid) || string.IsNullOrEmpty(subnetOcid))
        {
            _lblStatus.Text = "❌ Compartment and Subnet are required.";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            return;
        }

        if (_numMinInterval.Value > _numMaxInterval.Value)
        {
            _lblStatus.Text = "❌ Minimum retry interval cannot exceed maximum retry interval.";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            return;
        }

        // Preserve the loaded authentication and operational settings, then
        // overwrite only the wizard fields. This prevents a setup save from
        // silently dropping OCIDs, session paths, or safety settings.
        var result = _ociConfig ?? new AppConfig();
        result.CompartmentOcid = compartmentOcid;
        result.SubnetOcid = subnetOcid;
        result.Region = _txtRegion.Text.Trim();
        result.DisplayName = _txtDisplayName.Text.Trim();
        result.Ocpus = (int)_numOcpus.Value;
        result.MemoryInGb = (int)_numMemory.Value;
        result.MinIntervalSeconds = (int)_numMinInterval.Value;
        result.MaxIntervalSeconds = (int)_numMaxInterval.Value;
        result.Shape = "VM.Standard.A1.Flex";
        result.AssignPublicIp = true;
        result.BootVolumeSizeGb = AppConfig.AlwaysFreeBootVolumeSizeGb;
        result.EncryptSessionTokens = _chkEncryptSessionTokens.Checked;
        ResultConfig = result;

        try
        {
            ConfigService.Save(ResultConfig);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Setup", "Save configuration", ex);
            _lblStatus.Text = $"❌ Could not save: {DiagnosticLog.Redact(ex.Message)}\nDetails: {DiagnosticLog.LogPath}";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            return;
        }

        _lblStatus.Text = $"✅ Configuration saved to {ConfigService.DefaultConfigPath}";
        _lblStatus.ForeColor = Color.FromArgb(34, 197, 94);

        DialogResult = DialogResult.OK;
        Close();
    }
}
