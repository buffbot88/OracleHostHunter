using System.Windows;
using System.Windows.Controls;
using OracleHost.Helpers;
using OracleHost.Models;
using OracleHost.Services;

namespace OracleHost.Windows;

/// <summary>
/// Settings dialog (WPF port of SetupWizardForm). Edits instance settings only;
/// compartment/subnet/region come from AccountInfo and are never editable here.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        LoadCurrentValues();
    }

    private void LoadCurrentValues()
    {
        ItemA1.IsSelected = _config.IsFlexibleShape;
        ItemMicro.IsSelected = !_config.IsFlexibleShape;

        var ocpus = _config.Ocpus ?? 2;
        ComboOcpus.SelectedIndex = Math.Clamp(ocpus, 1, 2) - 1;

        var memory = _config.MemoryInGbs ?? ocpus * 6;
        ReloadMemoryChoices(ocpus);
        ComboMemory.SelectedItem = Math.Clamp(memory, 1, AppConfig.AlwaysFreeA1MemoryGb);

        SliderBoot.Value = Math.Clamp(_config.BootVolumeSizeGb ?? AppConfig.AlwaysFreeBootVolumeSizeGb,
            AppConfig.AlwaysFreeBootVolumeSizeGb, AppConfig.AlwaysFreeBlockStorageGb);

        TxtDisplayName.Text = _config.DisplayName ?? "free-tier-micro";
        TxtMinRetry.Text = _config.MinIntervalSeconds.ToString();
        TxtMaxRetry.Text = _config.MaxIntervalSeconds.ToString();

        TxtSafety.Text = $"🛡 Always Free protection is ON: only {AppConfig.MicroShape} or " +
                         $"{AppConfig.A1FlexShape} within the {AppConfig.AlwaysFreeA1OcpuCount} OCPU / " +
                         $"{AppConfig.AlwaysFreeA1MemoryGb} GB allotment; boot volume capped at " +
                         $"{AppConfig.AlwaysFreeBlockStorageGb} GB (account-wide pool); ephemeral public IP only.";
    }

    private void ReloadMemoryChoices(int ocpus)
    {
        var max = Math.Min(ocpus * 6, AppConfig.AlwaysFreeA1MemoryGb);
        var previous = ComboMemory.SelectedItem;
        ComboMemory.Items.Clear();
        for (var gb = ocpus; gb <= max; gb++)
            ComboMemory.Items.Add(gb);
        if (previous is int value && value >= ocpus && value <= max)
            ComboMemory.SelectedItem = value;
        else
            ComboMemory.SelectedIndex = 0;
    }

    private void ComboMemory_SelectionChanged(object sender, SelectionChangedEventArgs e) { /* value read at save */ }

    private void SliderBoot_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtBoot != null) TxtBoot.Text = $"{(int)SliderBoot.Value} GB";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.CompartmentOcid) || string.IsNullOrWhiteSpace(_config.SubnetOcid))
        {
            TxtStatus.Text = "❌ Compartment and Subnet are missing — complete the AccountInfo bundle.";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            return;
        }

        if (!int.TryParse(TxtMinRetry.Text.Trim(), out var minRetry) ||
            !int.TryParse(TxtMaxRetry.Text.Trim(), out var maxRetry) ||
            minRetry is < 10 or > 600 || maxRetry is < 10 or > 600)
        {
            TxtStatus.Text = "❌ Retry intervals must be numbers between 10 and 600 seconds.";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            return;
        }
        if (minRetry > maxRetry)
        {
            TxtStatus.Text = "❌ Minimum retry interval cannot exceed the maximum.";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            return;
        }

        // Preserve authentication/OCID settings; overwrite only wizard fields.
        var result = _config;
        result.Shape = ItemA1.IsSelected ? AppConfig.A1FlexShape : AppConfig.MicroShape;
        result.Ocpus = ComboOcpus.SelectedIndex + 1;
        result.MemoryInGbs = ComboMemory.SelectedItem as int?;
        result.BootVolumeSizeGb = (int)SliderBoot.Value;
        result.DisplayName = TxtDisplayName.Text.Trim();
        result.MinIntervalSeconds = minRetry;
        result.MaxIntervalSeconds = maxRetry;
        result.AssignPublicIp = true;
        ResultConfig = result;

        try
        {
            ConfigService.Save(ResultConfig);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Setup", "Save configuration", ex);
            TxtStatus.Text = $"❌ Could not save: {DiagnosticLog.Redact(ex.Message)}\nDetails: {DiagnosticLog.LogPath}";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            return;
        }

        DialogResult = true;
        Close();
    }

    public AppConfig ResultConfig { get; private set; } = new();
}
