using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using OracleHost.Helpers;
using OracleHost.Models;
using OracleHost.Services;
using Oci.CoreService.Models;

namespace OracleHost.Windows;

public partial class NetworkDiagnosticWindow : Window
{
    private sealed record InstanceChoice(string Id, string Label);

    private sealed class DisplayCheck
    {
        public string Title { get; init; } = "";
        public string State { get; init; } = "";
        public string Detail { get; init; } = "";
        public string BadgeBackground => State switch
        {
            "OK" => "#183B2A",
            "ISSUE" => "#492626",
            "WARNING" => "#49391E",
            _ => "#26364A"
        };
        public string BadgeForeground => State switch
        {
            "OK" => "#70E19A",
            "ISSUE" => "#FF9292",
            "WARNING" => "#FFD276",
            _ => "#9CC8E8"
        };
    }

    private readonly AppConfig _config;
    private readonly ObservableCollection<DisplayCheck> _checks = new();
    private bool _loadedInstances;
    private bool _running;

    public NetworkDiagnosticWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        ItemsChecks.ItemsSource = _checks;
        Loaded += async (_, _) => await LoadInstancesAsync();
    }

    private async Task LoadInstancesAsync()
    {
        if (_loadedInstances) return;
        _loadedInstances = true;
        SetBusy(true, "Loading instances visible in this compartment…");
        try
        {
            using var oci = new OciService();
            oci.Initialize(_config);
            var instances = await oci.ListInstancesAsync(_config.CompartmentOcid);
            var choices = instances
                .Where(instance => instance.LifecycleState is not Instance.LifecycleStateEnum.Terminated
                    and not Instance.LifecycleStateEnum.Terminating)
                .OrderBy(instance => instance.DisplayName)
                .Select(instance => new InstanceChoice(instance.Id!,
                    $"{instance.DisplayName ?? instance.Id} · {instance.LifecycleState} · {instance.Shape}"))
                .ToArray();
            ComboInstances.ItemsSource = choices;
            if (choices.Length > 0)
            {
                ComboInstances.SelectedIndex = 0;
                TxtSummary.Text = $"Found {choices.Length} active instance(s) in the configured compartment. The check reads configuration only; it does not change OCI resources.";
            }
            else
                TxtSummary.Text = "No active instances found in the configured compartment. Instances in other compartments are not included in this picker.";
        }
        catch (Exception ex)
        {
            TxtSummary.Text = $"Could not list instances: {DiagnosticLog.Redact(ex.Message)}";
        }
        finally
        {
            SetBusy(false, TxtSummary.Text);
        }
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (ComboInstances.SelectedValue is not string instanceId)
        {
            TxtSummary.Text = "Select an active VM first.";
            return;
        }
        string? sourceIp = null;
        if (!string.IsNullOrWhiteSpace(TxtSshSourceIp.Text))
        {
            if (!IPAddress.TryParse(TxtSshSourceIp.Text.Trim(), out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                TxtSummary.Text = "Enter a valid public IPv4 address (or leave it blank to skip the SSH source check).";
                return;
            }
            sourceIp = address.ToString();
        }

        _checks.Clear();
        SetBusy(true, "Reading OCI instance, VNIC, route, and security settings…");
        try
        {
            using var oci = new OciService();
            oci.Initialize(_config);
            var report = await oci.DiagnoseInstanceNetworkAsync(_config.CompartmentOcid, instanceId, sourceIp);
            foreach (var check in report.Checks)
                _checks.Add(new DisplayCheck
                {
                    Title = check.Title,
                    State = check.State,
                    Detail = check.Detail
                });
            var issueCount = report.Checks.Count(check => check.State == "ISSUE");
            var warningCount = report.Checks.Count(check => check.State == "WARNING");
            TxtSummary.Text = issueCount == 0
                ? $"Configuration scan complete: no checked path issues found{(warningCount > 0 ? $", {warningCount} security warning(s)" : "")}. This verifies OCI control-plane settings, not live packet reachability."
                : $"Configuration scan complete: {issueCount} potential issue(s) found{(warningCount > 0 ? $" and {warningCount} security warning(s)" : "")}. Review the findings below. This does not test live internet connectivity.";
        }
        catch (Exception ex)
        {
            _checks.Add(new DisplayCheck
            {
                Title = "Diagnostic failed",
                State = "ISSUE",
                Detail = DiagnosticLog.Redact(ex.Message)
            });
            TxtSummary.Text = "OCI could not provide a complete network diagnosis. Check the detailed error and your API permissions.";
        }
        finally
        {
            SetBusy(false, TxtSummary.Text);
        }
    }

    private void SetBusy(bool busy, string summary)
    {
        _running = busy;
        BtnRun.IsEnabled = !busy;
        ComboInstances.IsEnabled = !busy;
        TxtSshSourceIp.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(summary)) TxtSummary.Text = summary;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
