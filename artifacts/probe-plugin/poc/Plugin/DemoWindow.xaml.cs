using System.Windows;

namespace Poc.Plugin;

public partial class DemoWindow : Window
{
    public DemoWindow(string hostInfo)
    {
        InitializeComponent();
        HostInfo.Text = hostInfo;
        CloseBtn.Click += (_, _) => Close();
    }
}
