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
    private sealed record InstanceCountChoice(int Count, string Label);

    private readonly AppConfig _config;
    /// <summary>True once LoadCurrentValues has finished. Change handlers ignore
    /// events fired during XAML init and initial value assignment.</summary>
    private bool _ready;
    private bool _updatingMemoryChoices;
    private bool _updatingInstanceChoices;
    private int _instanceCountMaximum = -1;
    private OciService.TenancyUsageSnapshot? _tenancyUsage;
    private DateTime? _tenancyScannedAt;
    private const int TenancyScanMaxAgeMinutes = 10;
    private bool _scanningTenancy;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        LoadCurrentValues();
        BtnSave.IsEnabled = false;
        Loaded += async (_, _) => await ScanTenancyUsageAsync();
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

        TxtDisplayName.Text = !string.IsNullOrWhiteSpace(_config.DisplayNameBase)
            ? _config.DisplayNameBase
            : (_config.DisplayName ?? "free-tier-instance");
        TxtMinRetry.Text = _config.MinIntervalSeconds.ToString();
        TxtMaxRetry.Text = _config.MaxIntervalSeconds.ToString();

        _ready = true;
        UpdateShapeControls();
        RefreshInstanceCountChoices(_config.DesiredInstanceCount);
        UpdateAllowedOcpus();
        if (TxtBoot != null) TxtBoot.Text = $"{(int)SliderBoot.Value} GB";
        UpdateBudgetText();
        UpdateSafetyText();
    }

    private int DesiredInstanceCount => ComboInstanceCount.SelectedValue is int count ? count : 1;

    private int PlanMaximumInstanceCount
    {
        get
        {
            var plan = new AppConfig
            {
                Shape = ItemA1.IsSelected ? AppConfig.A1FlexShape : AppConfig.MicroShape,
                Ocpus = SelectedOcpusPerServer,
                MemoryInGbs = ComboMemory.SelectedItem as int?,
                BootVolumeSizeGb = (int)SliderBoot.Value
            };
            return plan.MaxInstancesForCurrentTemplate;
        }
    }

    private int SelectedOcpusPerServer => ComboOcpus.SelectedItem is ComboBoxItem item &&
        int.TryParse(item.Content.ToString()?.Split(' ')[0], out var ocpus)
            ? ocpus
            : _config.Ocpus ?? AppConfig.AlwaysFreeA1OcpuCount;

    private int CurrentMaximumInstanceCount
    {
        get
        {
            var plan = new AppConfig
            {
                Shape = ItemA1.IsSelected ? AppConfig.A1FlexShape : AppConfig.MicroShape,
                Ocpus = SelectedOcpusPerServer,
                MemoryInGbs = ComboMemory.SelectedItem as int?,
                BootVolumeSizeGb = (int)SliderBoot.Value
            };
            var configuredMaximum = plan.MaxInstancesForCurrentTemplate;
            if (_tenancyUsage == null) return configuredMaximum;

            var activeInstances = _tenancyUsage.Instances.Where(instance =>
                instance.LifecycleState != Oci.CoreService.Models.Instance.LifecycleStateEnum.Terminated).ToList();
            var selectedShapeInstances = activeInstances.Where(instance =>
                string.Equals(instance.Shape, plan.Shape, StringComparison.OrdinalIgnoreCase)).ToList();
            var remainingInstances = Math.Max(0,
                AppConfig.AlwaysFreeMaxInstancesPerShape - selectedShapeInstances.Count);
            var remainingStorageGb = Math.Max(0,
                AppConfig.AlwaysFreeBlockStorageGb - _tenancyUsage.BootVolumeGb - _tenancyUsage.BlockVolumeGb);
            var remainingFromStorage = (int)(remainingStorageGb / (long)Math.Max(1, plan.BootVolumeSizeGb ?? AppConfig.AlwaysFreeBootVolumeSizeGb));
            var max = Math.Min(configuredMaximum, Math.Min(remainingInstances, remainingFromStorage));

            if (plan.IsFlexibleShape)
            {
                var knownA1Instances = selectedShapeInstances.Where(instance => instance.ShapeConfig?.Ocpus != null &&
                    instance.ShapeConfig.MemoryInGBs != null).ToList();
                if (knownA1Instances.Count != selectedShapeInstances.Count) return 0;
                var usedOcpus = knownA1Instances.Sum(instance => (double)(instance.ShapeConfig!.Ocpus ?? 0));
                var usedMemory = knownA1Instances.Sum(instance => (double)(instance.ShapeConfig!.MemoryInGBs ?? 0));
                var perServerOcpus = plan.Ocpus ?? AppConfig.AlwaysFreeA1OcpuCount;
                var perServerMemory = plan.MemoryInGbs ?? perServerOcpus * 6;
                var remainingOcpus = Math.Max(0, AppConfig.AlwaysFreeA1OcpuCount - usedOcpus);
                var remainingMemory = Math.Max(0, AppConfig.AlwaysFreeA1MemoryGb - usedMemory);
                max = Math.Min(max, (int)Math.Floor(remainingOcpus / perServerOcpus));
                max = Math.Min(max, (int)Math.Floor(remainingMemory / perServerMemory));
            }

            return Math.Max(0, max);
        }
    }

    private void UpdateShapeControls()
    {
        if (!_ready) return;
        var flexible = ItemA1.IsSelected;
        PanelOcpus.IsEnabled = flexible;
        PanelMemory.IsEnabled = flexible;
        UpdateAllowedOcpus();
        TxtInstancePoolNote.Text = flexible
            ? "A1 total allowance: 2 OCPU / 12 GB. Per-server OCPU, memory, and boot-volume choices determine whether one or two fit."
            : "Micro allowance: up to 2 instances in this tier. Boot-volume usage is also limited by the shared 200 GB pool.";
        RefreshInstanceCountChoices(DesiredInstanceCount);
        RefreshSizingChoicesForInstanceCount();
    }

    private void RefreshSizingChoicesForInstanceCount()
    {
        if (!_ready || !ItemA1.IsSelected) return;
        UpdateAllowedOcpus();
        if (ComboOcpus.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content.ToString()?.Split(' ')[0], out var ocpus))
            ReloadMemoryChoices(ocpus);
    }

    private void RefreshInstanceCountChoices(int preferredCount)
    {
        if (!_ready) return;
        var resourceMaxCount = CurrentMaximumInstanceCount;
        var maxCount = Math.Max(1, resourceMaxCount);
        if (_instanceCountMaximum != maxCount)
        {
            _updatingInstanceChoices = true;
            try
            {
                var available = Enumerable.Range(1, maxCount)
                    .Select(count => new InstanceCountChoice(count, count == 1 ? "1 server" : $"{count} servers"))
                    .ToArray();
                ComboInstanceCount.DisplayMemberPath = nameof(InstanceCountChoice.Label);
                ComboInstanceCount.SelectedValuePath = nameof(InstanceCountChoice.Count);
                ComboInstanceCount.ItemsSource = available;
                _instanceCountMaximum = maxCount;
            }
            finally
            {
                _updatingInstanceChoices = false;
            }
        }

        ComboInstanceCount.IsEnabled = resourceMaxCount > 0;
        if (resourceMaxCount > 0)
        {
            var selectedCount = Math.Clamp(preferredCount, 1, maxCount);
            if (DesiredInstanceCount != selectedCount)
            {
                _updatingInstanceChoices = true;
                try { ComboInstanceCount.SelectedValue = selectedCount; }
                finally { _updatingInstanceChoices = false; }
            }
        }
        else
        {
            _updatingInstanceChoices = true;
            try { ComboInstanceCount.SelectedIndex = -1; }
            finally { _updatingInstanceChoices = false; }
            TxtInstancePoolNote.Text = _tenancyUsage == null
                ? "This plan exceeds the configured Always Free pools. Reduce OCPU, memory, or boot volume."
                : "The scan found no remaining capacity for this shape and size. Reduce this plan or remove existing tenancy resources.";
        }
    }

    private async void BtnScanTenancy_Click(object sender, RoutedEventArgs e) => await ScanTenancyUsageAsync();

    private async Task ScanTenancyUsageAsync()
    {
        if (_scanningTenancy) return;
        _scanningTenancy = true;
        BtnScanTenancy.IsEnabled = false;
        BtnScanTenancy.Content = "Scanning tenancy…";
        TxtStatus.Text = "Reading visible tenancy compartments, compute instances, and volumes…";
        TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        try
        {
            using var oci = new OciService();
            oci.Initialize(_config);
            var usage = await oci.GetTenancyUsageAsync();
            var activeInstances = usage.Instances.Where(instance =>
                instance.LifecycleState != Oci.CoreService.Models.Instance.LifecycleStateEnum.Terminated).ToList();
            _tenancyUsage = usage;
            _tenancyScannedAt = DateTime.UtcNow;
            RefreshInstanceCountChoices(DesiredInstanceCount);
            RefreshSizingChoicesForInstanceCount();
            UpdateBudgetText();

            TxtStatus.Text = $"Tenancy scan: {activeInstances.Count} active VM(s), " +
                $"{usage.BootVolumeGb} GB boot + {usage.BlockVolumeGb} GB block storage. " +
                $"This plan currently supports up to {CurrentMaximumInstanceCount} additional server(s).";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception("Settings", "Scan tenancy usage", ex);
            _tenancyUsage = null;
            _tenancyScannedAt = null;
            _instanceCountMaximum = -1;
            BtnSave.IsEnabled = false;
            RefreshInstanceCountChoices(DesiredInstanceCount);
            TxtStatus.Text = $"Could not scan tenancy usage: {DiagnosticLog.Redact(ex.Message)}. Current resource usage could not be checked; run Preflight before hunting.";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        }
        finally
        {
            _scanningTenancy = false;
            BtnScanTenancy.IsEnabled = true;
            BtnScanTenancy.Content = "↻  Scan tenancy usage";
            BtnSave.IsEnabled = _tenancyUsage != null;
        }
    }

    private void UpdateBudgetText()
    {
        // May be called by control events firing during XAML init or while the
        // memory list is being rebuilt; bail out until everything exists.
        if (!_ready || SliderBoot == null || ComboOcpus == null || ComboMemory == null ||
            ComboInstanceCount == null || ItemA1 == null ||
            TxtInstancePoolNote == null || TxtBudget == null)
            return;
        var bootPerInstance = (int)SliderBoot.Value;
        if (bootPerInstance <= 0 || ComboInstanceCount.SelectedIndex < 0)
            return;
        var totalBoot = bootPerInstance * DesiredInstanceCount;
        var maxInstancesFromBoot = AppConfig.AlwaysFreeMaxBootVolumePoolGb / bootPerInstance;

        var memoryGb = ComboMemory.SelectedItem is int gb ? gb : ComboOcpus.SelectedIndex + 1;
        var shapeLine = ItemA1.IsSelected
            ? $"Template: {AppConfig.A1FlexShape} " +
              $"({ComboOcpus.SelectedIndex + 1} OCPU × {DesiredInstanceCount} = " +
              $"{(ComboOcpus.SelectedIndex + 1) * DesiredInstanceCount} OCPU total), " +
              $"memory {memoryGb} GB × {DesiredInstanceCount} = " +
              $"{memoryGb * DesiredInstanceCount} GB total"
            : $"Template: {AppConfig.MicroShape} × {DesiredInstanceCount} instance(s)";

        var maxForPlan = PlanMaximumInstanceCount;
        var tenancyMaxForPlan = _tenancyUsage != null ? CurrentMaximumInstanceCount : (int?)null;
        var fitsByBoot = Math.Min(maxInstancesFromBoot, AppConfig.AlwaysFreeMaxInstancesPerShape);
        TxtInstancePoolNote.Text = ItemA1.IsSelected
            ? $"This A1 setup supports up to {maxForPlan} server(s) after OCPU, memory, and storage limits."
            : $"Micro supports up to {maxForPlan} server(s) in this tier; storage alone would fit {fitsByBoot}.";
        var tenancySuffix = tenancyMaxForPlan.HasValue
            ? $" Tenancy scan: up to {tenancyMaxForPlan.Value} additional server(s) fit current usage."
            : " Scan tenancy usage to factor in existing resources.";
        TxtBudget.Text = $"Selected plan: {shapeLine}; boot {bootPerInstance} GB × {DesiredInstanceCount} = {totalBoot} / " +
                          $"{AppConfig.AlwaysFreeMaxBootVolumePoolGb} GB block storage. Max for these choices: {maxForPlan}.{tenancySuffix}";
    }

    private void ReloadMemoryChoices(int ocpus)
    {
        var instanceCount = Math.Max(1, DesiredInstanceCount);
        var max = Math.Min(ocpus * 6, AppConfig.AlwaysFreeA1MemoryGb / instanceCount);
        var previous = ComboMemory.SelectedItem;
        _updatingMemoryChoices = true;
        try
        {
            ComboMemory.Items.Clear();
            for (var gb = ocpus; gb <= max; gb++)
                ComboMemory.Items.Add(gb);
            var previousMemory = previous is int value ? value : ocpus;
            ComboMemory.SelectedItem = Math.Clamp(previousMemory, ocpus, max);
        }
        finally
        {
            _updatingMemoryChoices = false;
        }
    }

    private void ComboMemory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingMemoryChoices || _updatingInstanceChoices) return;
        RefreshInstanceCountChoices(DesiredInstanceCount);
        UpdateBudgetText();
    }

    private void ComboOcpus_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingInstanceChoices) return;
        if (ComboOcpus.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content.ToString()?.Split(' ')[0], out var ocpus))
            ReloadMemoryChoices(ocpus);
        RefreshInstanceCountChoices(DesiredInstanceCount);
        UpdateBudgetText();
    }



    private void SliderBoot_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtBoot != null) TxtBoot.Text = $"{(int)SliderBoot.Value} GB";
        if (_ready)
        {
            RefreshInstanceCountChoices(DesiredInstanceCount);
            RefreshSizingChoicesForInstanceCount();
            UpdateBudgetText();
        }
    }

    private void ComboShape_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateShapeControls();
        UpdateBudgetText();
    }

    private void ComboInstanceCount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingInstanceChoices) return;
        UpdateAllowedOcpus();
        if (ComboOcpus.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content.ToString()?.Split(' ')[0], out var ocpus))
            ReloadMemoryChoices(ocpus);
        RefreshInstanceCountChoices(DesiredInstanceCount);
        UpdateBudgetText();
    }

    private void UpdateAllowedOcpus()
    {
        if (!_ready || !ItemA1.IsSelected) return;
        var maxOcpusPerServer = Math.Max(1, AppConfig.AlwaysFreeA1OcpuCount / DesiredInstanceCount);
        for (var i = 0; i < ComboOcpus.Items.Count; i++)
        {
            if (ComboOcpus.Items[i] is ComboBoxItem item &&
                int.TryParse(item.Content.ToString()?.Split(' ')[0], out var ocpus))
                item.IsEnabled = ocpus <= maxOcpusPerServer;
        }
        if (ComboOcpus.SelectedItem is ComboBoxItem selected && !selected.IsEnabled)
            ComboOcpus.SelectedIndex = maxOcpusPerServer - 1;
    }

    private void UpdateSafetyText()
    {
        TxtSafety.Text = $"🛡 Always Free protection is ON: only {AppConfig.MicroShape} or " +
                         $"{AppConfig.A1FlexShape} are allowed; A1 stays within {AppConfig.AlwaysFreeA1OcpuCount} OCPU / " +
                         $"{AppConfig.AlwaysFreeA1MemoryGb} GB; boot volume is capped at {AppConfig.AlwaysFreeMaxBootVolumePoolGb} GB total " +
                         $"(account-wide pool); ephemeral public IP only.";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_tenancyUsage == null || !_tenancyScannedAt.HasValue ||
            DateTime.UtcNow - _tenancyScannedAt.Value > TimeSpan.FromMinutes(TenancyScanMaxAgeMinutes))
        {
            _tenancyUsage = null;
            _tenancyScannedAt = null;
            BtnSave.IsEnabled = false;
            _instanceCountMaximum = -1;
            RefreshInstanceCountChoices(DesiredInstanceCount);
            TxtStatus.Text = "❌ Scan tenancy usage successfully before saving; scans expire after 10 minutes.";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            return;
        }
        if (CurrentMaximumInstanceCount < DesiredInstanceCount)
        {
            TxtStatus.Text = $"❌ The current tenancy usage supports at most {CurrentMaximumInstanceCount} additional server(s) for these options.";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            return;
        }

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
        result.DisplayNameBase = TxtDisplayName.Text.Trim();
        result.DesiredInstanceCount = DesiredInstanceCount;
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
