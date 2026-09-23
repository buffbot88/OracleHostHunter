using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OracleHost.Services;

namespace OracleHost.Windows;

/// <summary>
/// Startup report (WPF port of AccountInfoReportForm): lists every checked
/// AccountInfo field with found/missing state so the user can fix the bundle.
/// </summary>
public partial class AccountInfoReportWindow : Window
{
    public AccountInfoReportWindow(AccountInfoReport report)
    {
        InitializeComponent();
        Build(report);
    }

    private void Build(AccountInfoReport report)
    {
        AddRow($"AccountInfo folder: {report.Folder}", report.MetadataFileExists);
        AddRow("information.md", report.MetadataFileExists);

        foreach (var field in report.Fields)
            AddRow($"{field.Label} ({(field.Required ? "required" : "optional")})", field.Present, field.Value);

        AddRow("Private key (.pem)", report.HasExactlyOnePrivateKey,
            report.PrivateKeyName ?? $"found {report.PrivateKeyCount}, expected exactly 1");
        AddRow("Public key (.pem, optional)", report.PublicKeyCount <= 1,
            report.PublicKeyName ?? (report.PublicKeyCount == 0 ? "not present" : $"found {report.PublicKeyCount}, expected at most 1"));
    }

    private void AddRow(string label, bool ok, string? detail = null)
    {
        var panel = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (detail != null)
            lbl.Text += $"   —  {detail}";

        var state = new TextBlock
        {
            Text = ok ? "● found" : "● MISSING",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush")
        };

        grid.Children.Add(lbl);
        Grid.SetColumn(state, 1);
        grid.Children.Add(state);
        panel.Child = grid;
        PanelFields.Children.Add(panel);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
