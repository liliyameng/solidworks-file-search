using System.Windows;

namespace SwSearch.PropertyViewer;

public partial class NativePropertyWindow : Window
{
    public NativePropertyWindow(NativePropertyViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        // Column headers live outside the visual tree, so DataContext bindings
        // do not reach them; localize them explicitly from the view model.
        Title = PropertyViewerText.Get(viewModel.Language, "window.title");
        NameColumn.Header = PropertyViewerText.Get(viewModel.Language, "column.name");
        ValueColumn.Header = PropertyViewerText.Get(viewModel.Language, "column.value");
    }
}
