using System.Reflection;
using System.Windows;

namespace BastionFlow.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        VersionText.Text = $"version {version}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
