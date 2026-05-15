using System.Windows;

namespace WorkshopSentinel;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"v{Program.Version}";
    }
}
