using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace CodeForge.Studio.Views;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        InitializeComponent();
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Opens only the fixed Ollama download page in the default browser.
        if (e.Uri.AbsoluteUri == "https://ollama.com/download")
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
