using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace OracleHost.Windows;

public partial class OciGuideWindow : Window
{
    public OciGuideWindow()
    {
        InitializeComponent();
    }

    private void GuideLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme is not ("http" or "https")) return;
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
