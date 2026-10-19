using System.Windows;
using System.Windows.Controls;

namespace CodeForge.Studio;

public partial class MainWindow : Window
{
    private bool _stickToBottom = true;

    public MainWindow()
    {
        InitializeComponent();
        MessagesScroll.ScrollChanged += OnScrollChanged;
    }

    private async void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ViewModels.MainViewModel vm && e.NewValue is ViewModels.TreeNodeViewModel node)
            await vm.Project.SelectNodeAsync(node);
    }

    private async void Result_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm && sender is FrameworkElement { DataContext: ViewModels.SearchResultItem r })
            await vm.Project.PreviewAsync(r.RelativePath);
    }

    // View-only behaviour: keep the newest streamed text visible unless the user scrolled up.
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            _stickToBottom = MessagesScroll.VerticalOffset >= MessagesScroll.ScrollableHeight - 8;
        else if (_stickToBottom)
            MessagesScroll.ScrollToEnd();
    }
}
