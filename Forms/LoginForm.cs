using System.Diagnostics;
using OracleHost.Helpers;
using OracleHost.Models;
using OracleHost.Services;

namespace OracleHost.Forms;

/// <summary>
/// Login form where users sign in with their Oracle account (browser-based OAuth
/// capture) or enter API-key credentials. The browser flow is auto-launched on
/// startup when no saved credentials or session exists.
/// </summary>
public class LoginForm : Form
{
    private readonly TextBox _txtTenancyOcid = new();
    private readonly TextBox _txtUserOcid = new();
    private readonly TextBox _txtFingerprint = new();
    private readonly TextBox _txtKeyFilePath = new();
    private readonly TextBox _txtRegion = new();
    private readonly TextBox _txtIdentityDomain = new();
    private readonly TextBox _txtOauthClientId = new();
    private readonly CheckBox _chkEncryptSessionTokens = new();
    private readonly Button _btnBrowseKey = new();
    private readonly Button _btnGenerateKeys = new();
    private readonly Button _btnLogin = new();
    private readonly Button _btnOpenConsole = new();
    private readonly Button _btnBrowserLogin = new();
    private readonly Label _lblStatus = new();
    private readonly Panel _panelCredentials = new();
    private readonly Panel _panelBrowser = new();
    private AppConfig? _loadedConfig;
    private bool _autoLaunched;

    public AppConfig? ResultConfig { get; private set; }

    /// <summary>
    /// True only when this form completed a browser OAuth login during this run.
    /// Used to trigger one automatic preflight when the dashboard opens.
    /// </summary>
    public bool BrowserLoginCompleted { get; private set; }

    public LoginForm(AppConfig? initialConfig = null)
    {
        _loadedConfig = initialConfig;
        InitializeComponent();
        TryLoadExistingConfig();
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        // Form settings
        Text = "OracleHost — Oracle Cloud Login";
        Size = new Size(640, 755);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(24, 24, 27);
        Font = new Font("Segoe UI", 10F);

        // Title
        var lblTitle = new Label
        {
            Text = "☁ Oracle Cloud Login",
            Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 140, 0),
            Location = new Point(20, 15),
            AutoSize = true
        };

        var lblSubtitle = new Label
        {
            Text = "Sign in with your Oracle account, or enter OCI API credentials.",
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(161, 161, 170),
            Location = new Point(22, 55),
            AutoSize = true
        };

        // ---- API-key credentials panel ----
        _panelCredentials.Location = new Point(20, 90);
        _panelCredentials.Size = new Size(600, 245);
        _panelCredentials.BackColor = Color.FromArgb(32, 32, 35);
        _panelCredentials.Padding = new Padding(15);

        int y = 15;
        _panelCredentials.Controls.Add(CreateLabel("Tenancy OCID:", 0, y));
        _panelCredentials.Controls.Add(CreateTextBox(_txtTenancyOcid, 150, y, 430));
        y += 45;

        _panelCredentials.Controls.Add(CreateLabel("User OCID:", 0, y));
        _panelCredentials.Controls.Add(CreateTextBox(_txtUserOcid, 150, y, 430));
        y += 45;

        _panelCredentials.Controls.Add(CreateLabel("Fingerprint:", 0, y));
        _panelCredentials.Controls.Add(CreateTextBox(_txtFingerprint, 150, y, 430));
        y += 45;

        _panelCredentials.Controls.Add(CreateLabel("Key File Path:", 0, y));
        _panelCredentials.Controls.Add(CreateTextBox(_txtKeyFilePath, 150, y, 350));
        _btnBrowseKey.Text = "Browse...";
        _btnBrowseKey.Location = new Point(510, y);
        _btnBrowseKey.Size = new Size(80, 28);
        _btnBrowseKey.BackColor = Color.FromArgb(63, 63, 70);
        _btnBrowseKey.ForeColor = Color.White;
        _btnBrowseKey.FlatStyle = FlatStyle.Flat;
        _btnBrowseKey.Click += BtnBrowseKey_Click;
        _panelCredentials.Controls.Add(_btnBrowseKey);
        y += 45;

        _panelCredentials.Controls.Add(CreateLabel("Region:", 0, y));
        _panelCredentials.Controls.Add(CreateTextBox(_txtRegion, 150, y, 200));
        _txtRegion.Text = "us-ashburn-1";

        // ---- Divider ----
        var lblDivider = new Label
        {
            Text = "— or sign in with Oracle Cloud in your browser —",
            Font = new Font("Segoe UI", 9F, FontStyle.Italic),
            ForeColor = Color.FromArgb(161, 161, 170),
            Location = new Point(160, 345),
            AutoSize = true
        };

        // ---- Browser sign-in panel ----
        _panelBrowser.Location = new Point(20, 370);
        _panelBrowser.Size = new Size(600, 170);
        _panelBrowser.BackColor = Color.FromArgb(32, 32, 35);
        _panelBrowser.Padding = new Padding(15);

        _panelBrowser.Controls.Add(CreateLabel("Identity Domain URL:", 20, 15));
        _panelBrowser.Controls.Add(CreateTextBox(_txtIdentityDomain, 150, 15, 430));
        _txtIdentityDomain.PlaceholderText = "https://idcs-xxx.identity.oraclecloud.com";

        _panelBrowser.Controls.Add(CreateLabel("OAuth Client ID:", 20, 60));
        _panelBrowser.Controls.Add(CreateTextBox(_txtOauthClientId, 150, 60, 430));
        _txtOauthClientId.PlaceholderText = "ocid1.client.oc1.. or UUID from your OAuth app";

        // One-time setup pointer for first-time users.
        var lnkOauthHelp = new LinkLabel
        {
            Text = "One-time setup — how do I create the OAuth app?",
            Location = new Point(150, 135),
            AutoSize = true,
            Font = new Font("Segoe UI", 9F),
            LinkColor = Color.FromArgb(96, 165, 250),
            ActiveLinkColor = Color.FromArgb(147, 197, 253),
            BackColor = Color.FromArgb(32, 32, 35)
        };
        lnkOauthHelp.LinkClicked += (_, _) =>
        {
            _lblStatus.Text = "📖 In the OCI console: Identity & Security → Identity domains → your domain → " +
                "Applications → Add application → Confidential/Native app with Authorization Code + PKCE, " +
                "redirect URI http://localhost:8181/ — then paste the domain URL and client ID above.";
            _lblStatus.ForeColor = Color.FromArgb(96, 165, 250);
            OpenBrowser("https://docs.oracle.com/en-us/iaas/Content/Identity/applications/overview.htm");
        };
        _panelBrowser.Controls.Add(lnkOauthHelp);

        _chkEncryptSessionTokens.Text = "Protect session token files with Windows DPAPI (current user only)";
        _chkEncryptSessionTokens.Location = new Point(150, 100);
        _chkEncryptSessionTokens.Size = new Size(430, 28);
        _chkEncryptSessionTokens.BackColor = Color.FromArgb(32, 32, 35);
        _chkEncryptSessionTokens.ForeColor = Color.FromArgb(212, 212, 216);
        _chkEncryptSessionTokens.FlatStyle = FlatStyle.Flat;
        _chkEncryptSessionTokens.CheckedChanged += ChkEncryptSessionTokens_CheckedChanged;
        _panelBrowser.Controls.Add(_chkEncryptSessionTokens);

        // ---- Browser login button (primary) ----
        _btnBrowserLogin.Text = "🌐 Sign in with Oracle (Browser) — capture my login";
        _btnBrowserLogin.Location = new Point(20, 550);
        _btnBrowserLogin.Size = new Size(600, 44);
        _btnBrowserLogin.BackColor = Color.FromArgb(255, 140, 0);
        _btnBrowserLogin.ForeColor = Color.Black;
        _btnBrowserLogin.FlatStyle = FlatStyle.Flat;
        _btnBrowserLogin.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        _btnBrowserLogin.Click += BtnBrowserLogin_Click;

        // ---- API-key buttons ----
        _btnGenerateKeys.Text = "🔑 Generate API Keys";
        _btnGenerateKeys.Location = new Point(20, 610);
        _btnGenerateKeys.Size = new Size(180, 40);
        _btnGenerateKeys.BackColor = Color.FromArgb(79, 70, 229);
        _btnGenerateKeys.ForeColor = Color.White;
        _btnGenerateKeys.FlatStyle = FlatStyle.Flat;
        _btnGenerateKeys.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        _btnGenerateKeys.Click += BtnGenerateKeys_Click;

        _btnOpenConsole.Text = "🌐 Open Oracle Console";
        _btnOpenConsole.Location = new Point(210, 610);
        _btnOpenConsole.Size = new Size(180, 40);
        _btnOpenConsole.BackColor = Color.FromArgb(34, 197, 94);
        _btnOpenConsole.ForeColor = Color.White;
        _btnOpenConsole.FlatStyle = FlatStyle.Flat;
        _btnOpenConsole.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        _btnOpenConsole.Click += BtnOpenConsole_Click;

        _btnLogin.Text = "▶ Login & Continue";
        _btnLogin.Location = new Point(400, 610);
        _btnLogin.Size = new Size(200, 40);
        _btnLogin.BackColor = Color.FromArgb(59, 130, 246);
        _btnLogin.ForeColor = Color.White;
        _btnLogin.FlatStyle = FlatStyle.Flat;
        _btnLogin.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        _btnLogin.Click += BtnLogin_Click;

        // Enter submits the primary (browser) sign-in action.
        AcceptButton = _btnBrowserLogin;
        foreach (var button in new[] { _btnBrowserLogin, _btnGenerateKeys, _btnOpenConsole, _btnLogin, _btnBrowseKey })
        {
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(button.BackColor, 0.2f);
            button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(button.BackColor, 0.1f);
        }

        // Status label
        _lblStatus.Location = new Point(20, 665);
        _lblStatus.Size = new Size(600, 70);
        _lblStatus.ForeColor = Color.FromArgb(161, 161, 170);
        _lblStatus.Font = new Font("Segoe UI", 9F);
        _lblStatus.Text = "💡 Browser sign-in needs an OAuth app in your identity domain " +
                         "(Grant type: Authorization Code + PKCE, Redirect: http://localhost:8181/). " +
                         "Or paste API-key credentials below.";

        Controls.Add(lblTitle);
        Controls.Add(lblSubtitle);
        Controls.Add(_panelCredentials);
        Controls.Add(lblDivider);
        Controls.Add(_panelBrowser);
        Controls.Add(_btnBrowserLogin);
        Controls.Add(_btnGenerateKeys);
        Controls.Add(_btnOpenConsole);
        Controls.Add(_btnLogin);
        Controls.Add(_lblStatus);

        ResumeLayout(false);
        PerformLayout();
    }

    private static Label CreateLabel(string text, int x, int y)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y + 3),
            AutoSize = true,
            ForeColor = Color.FromArgb(212, 212, 216),
            Font = new Font("Segoe UI", 10F)
        };
    }

    private static TextBox CreateTextBox(TextBox txt, int x, int y, int width)
    {
        txt.Location = new Point(x, y);
        txt.Size = new Size(width, 27);
        txt.BackColor = Color.FromArgb(24, 24, 27);
        txt.ForeColor = Color.White;
        txt.BorderStyle = BorderStyle.FixedSingle;
        txt.Font = new Font("Segoe UI", 10F);
        return txt;
    }

    private void TryLoadExistingConfig()
    {
        // Use the startup snapshot when supplied so stale session paths that
        // Program.cs rejected cannot be reloaded from disk accidentally.
        var existing = _loadedConfig ?? ConfigService.Load();
        _loadedConfig = existing;

        if (existing.HasCredentials)
        {
            _txtTenancyOcid.Text = existing.TenancyOcid ?? "";
            _txtUserOcid.Text = existing.UserOcid ?? "";
            _txtFingerprint.Text = existing.Fingerprint ?? "";
            _txtKeyFilePath.Text = existing.KeyFilePath ?? "";
            _txtRegion.Text = existing.Region ?? "us-ashburn-1";
        }
        else
        {
            // Try ~/.oci/config
            var ociConfig = ConfigService.ReadOciConfig();
            if (ociConfig.HasValue)
            {
                _txtTenancyOcid.Text = ociConfig.Value.TenancyOcid;
                _txtUserOcid.Text = ociConfig.Value.UserOcid;
                _txtFingerprint.Text = ociConfig.Value.Fingerprint;
                _txtKeyFilePath.Text = ociConfig.Value.KeyFilePath;
                _txtRegion.Text = ociConfig.Value.Region;
                _lblStatus.Text = "✅ Loaded from ~/.oci/config — click Login to continue.";
                _lblStatus.ForeColor = Color.FromArgb(34, 197, 94);
            }
        }

        // Pre-fill browser sign-in settings from a previous run
        if (!string.IsNullOrWhiteSpace(existing.OciIdentityDomain))
            _txtIdentityDomain.Text = existing.OciIdentityDomain;
        if (!string.IsNullOrWhiteSpace(existing.OciOauthClientId))
            _txtOauthClientId.Text = existing.OciOauthClientId;
        _chkEncryptSessionTokens.Checked = existing.EncryptSessionTokens;
        if (!string.IsNullOrWhiteSpace(existing.Region))
            _txtRegion.Text = existing.Region;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_autoLaunched) return;
        _autoLaunched = true;

        // No saved browser session → auto-launch the browser sign-in flow so
        // the user can log in to Oracle and have their login captured. Existing
        // API-key fields are still preserved as a manual fallback.
        if (!_loadedConfig!.HasSession)
            AutoStartBrowserLogin();
    }

    private void AutoStartBrowserLogin()
    {
        bool hasDomain = !string.IsNullOrWhiteSpace(_txtIdentityDomain.Text);
        bool hasClientId = !string.IsNullOrWhiteSpace(_txtOauthClientId.Text);

        if (hasDomain && hasClientId)
        {
            _lblStatus.Text = "🔐 Signing you in… opening Oracle Cloud in your browser.";
            _lblStatus.ForeColor = Color.FromArgb(59, 130, 246);
            _ = RunBrowserLoginAsync();
        }
        else
        {
            _lblStatus.Text = "🌐 Opening Oracle Cloud in your browser…\n" +
                "Create or open your OAuth app, then paste its Identity Domain URL " +
                "and Client ID above and click Sign in with Oracle.";
            _lblStatus.ForeColor = Color.FromArgb(59, 130, 246);
            OpenBrowser("https://cloud.oracle.com");
        }
    }

    private async void BtnBrowserLogin_Click(object? sender, EventArgs e)
    {
        await RunBrowserLoginAsync();
    }

    private async Task RunBrowserLoginAsync()
    {
        var domain = _txtIdentityDomain.Text.Trim();
        var clientId = _txtOauthClientId.Text.Trim();
        var region = string.IsNullOrWhiteSpace(_txtRegion.Text.Trim())
            ? "us-ashburn-1"
            : _txtRegion.Text.Trim();

        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(clientId))
        {
            _lblStatus.Text = "❌ Enter your Identity Domain URL and OAuth Client ID first.";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            OpenBrowser("https://cloud.oracle.com");
            return;
        }

        _btnBrowserLogin.Enabled = false;
        _btnLogin.Enabled = false;
        try
        {
            var progress = new Progress<string>(msg =>
            {
                _lblStatus.Text = msg;
                _lblStatus.ForeColor = Color.FromArgb(59, 130, 246);
            });

            var session = await OciBrowserLogin.LoginAsync(
                domain, clientId, region, progress,
                encryptSessionTokens: _chkEncryptSessionTokens.Checked);

            var cfg = _loadedConfig ?? new AppConfig();
            cfg.Region = region;
            cfg.SessionConfigPath = session.SessionConfigPath;
            cfg.OciIdentityDomain = domain;
            cfg.OciOauthClientId = clientId;
            cfg.EncryptSessionTokens = _chkEncryptSessionTokens.Checked;
            cfg.UserOcid = session.UserOcid;
            cfg.TenancyOcid = session.TenancyOcid;

            try { ConfigService.Save(cfg); } catch { /* non-fatal */ }

            ResultConfig = cfg;
            BrowserLoginCompleted = true;
            _lblStatus.Text = "✅ Signed in! Session captured. Continuing…";
            _lblStatus.ForeColor = Color.FromArgb(34, 197, 94);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Login", "Browser login", ex);
            _lblStatus.Text = $"❌ {DiagnosticLog.Redact(ex.Message)}\nDetailed diagnostics: {DiagnosticLog.LogPath}";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
        }
        finally
        {
            _btnBrowserLogin.Enabled = true;
            _btnLogin.Enabled = true;
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

    private void BtnBrowseKey_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select OCI API Private Key",
            Filter = "PEM files (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oci")
        };

        if (dialog.ShowDialog() == DialogResult.OK)
            _txtKeyFilePath.Text = dialog.FileName;
    }

    private void BtnGenerateKeys_Click(object? sender, EventArgs e)
    {
        try
        {
            var (privateKeyPem, publicKeyPem, fingerprint) = CryptoHelper.GenerateKeyPair();

            // Save to ~/.oci/
            var ociDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oci");
            Directory.CreateDirectory(ociDir);

            var privateKeyPath = Path.Combine(ociDir, "oci_api_key.pem");
            var publicKeyPath = Path.Combine(ociDir, "oci_api_key_public.pem");

            File.WriteAllText(privateKeyPath, privateKeyPem);
            File.WriteAllText(publicKeyPath, publicKeyPem);

            _txtKeyFilePath.Text = privateKeyPath;
            _txtFingerprint.Text = fingerprint;

            _lblStatus.Text = $"✅ Keys generated!\n" +
                $"Fingerprint: {fingerprint}\n" +
                $"Keep the private key on this computer. Register the public key in your OCI user API-key settings if Oracle asks for it:\n" +
                $"  {publicKeyPath}";
            _lblStatus.ForeColor = Color.FromArgb(34, 197, 94);
            _lblStatus.Size = new Size(600, 80);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Login", "Generate API key pair", ex);
            _lblStatus.Text = $"❌ Key generation failed: {DiagnosticLog.Redact(ex.Message)}\nDetailed diagnostics: {DiagnosticLog.LogPath}";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
        }
    }

    private void BtnOpenConsole_Click(object? sender, EventArgs e)
    {
        OpenBrowser("https://cloud.oracle.com");
        _lblStatus.Text = "🌐 Oracle Cloud Console opened in your browser.\n" +
            "Open User settings → API keys. Use the OCI user API-key page, not Analytics & AI API keys.";
        _lblStatus.ForeColor = Color.FromArgb(59, 130, 246);
    }

    private void BtnLogin_Click(object? sender, EventArgs e)
    {
        // Validate inputs
        var tenancy = _txtTenancyOcid.Text.Trim();
        var user = _txtUserOcid.Text.Trim();
        var fingerprint = _txtFingerprint.Text.Trim();
        var keyFile = _txtKeyFilePath.Text.Trim();
        var region = _txtRegion.Text.Trim();

        DiagnosticLog.Info("Login", $"Validating API-key login fields. Fingerprint supplied: {!string.IsNullOrWhiteSpace(fingerprint)}; key file supplied: {!string.IsNullOrWhiteSpace(keyFile)}.");
        if (string.IsNullOrEmpty(tenancy) || string.IsNullOrEmpty(user) ||
            string.IsNullOrEmpty(fingerprint) || string.IsNullOrEmpty(keyFile))
        {
            DiagnosticLog.Warn("Login", "API-key login was rejected because one or more credential fields were empty.");
            _lblStatus.Text = $"❌ Please fill in all credential fields.\nDetailed diagnostics: {DiagnosticLog.LogPath}";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            return;
        }

        if (!File.Exists(Environment.ExpandEnvironmentVariables(keyFile)))
        {
            DiagnosticLog.Warn("Login", $"API private key file was not found at {keyFile}.");
            _lblStatus.Text = $"❌ Key file not found: {keyFile}\nDetailed diagnostics: {DiagnosticLog.LogPath}";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            return;
        }

        if (!tenancy.StartsWith("ocid1.tenancy."))
        {
            _lblStatus.Text = "❌ Tenancy OCID must start with 'ocid1.tenancy.'";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            return;
        }

        if (!user.StartsWith("ocid1.user."))
        {
            _lblStatus.Text = "❌ User OCID must start with 'ocid1.user.'";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            return;
        }

        // Build config
        var cfg = _loadedConfig ?? new AppConfig();
        // Choosing API-key login explicitly replaces any stale browser-session
        // metadata, so the selected credential type is authoritative.
        cfg.SessionConfigPath = null;
        cfg.OciIdentityDomain = null;
        cfg.OciOauthClientId = null;
        cfg.TenancyOcid = tenancy;
        cfg.UserOcid = user;
        cfg.Fingerprint = fingerprint;
        cfg.KeyFilePath = keyFile;
        cfg.EncryptSessionTokens = _chkEncryptSessionTokens.Checked;
        cfg.Region = string.IsNullOrEmpty(region) ? "us-ashburn-1" : region;

        // Save for next time
        try
        {
            ConfigService.Save(cfg);
        }
        catch { /* non-fatal */ }

        DiagnosticLog.Info("Login", "API-key credentials accepted and saved by path; private key contents were not logged.");
        ResultConfig = cfg;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open browser: {DiagnosticLog.Redact(ex.Message)}", "OracleHost",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
