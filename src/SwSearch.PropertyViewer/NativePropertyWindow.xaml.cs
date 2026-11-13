using System.Windows;

namespace SwSearch.PropertyViewer;

public partial class NativePropertyWindow : Window
{
    public NativePropertyWindow(NativePropertyViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }
}
