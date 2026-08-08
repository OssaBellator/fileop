using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class FilesView : UserControl
{
    public FilesView()
    {
        InitializeComponent();
    }

    public FilesPaneView LeftPane => LeftPaneControl;

    public FilesPaneView RightPane => RightPaneControl;

    public void SetSourceDescription(string description)
    {
        ScopeText.Text = description;
    }
}
