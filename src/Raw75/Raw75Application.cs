using Blossom.Core;
using Blossom.Desktop;
using Raw75.Views;

namespace Raw75;

public sealed class Raw75Application : DesktopApplication
{
    public Raw75Application()
    {
        Title = "Raw75";
        Window.Width = 1600;
        Window.Height = 1000;
        Window.MinWidth = 1100;
        Window.MinHeight = 680;
        Window.CenterOnLoad = true;

        var workspace = new WorkspaceView();
        AddView(workspace);
        SetActiveView(workspace);
    }
}
