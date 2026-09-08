using Blossom.Core;
using Raw75.Views;

namespace Raw75;

public sealed class Raw75Application : Application
{
    public Raw75Application()
    {
        Title = "Raw75";

        var workspace = new WorkspaceView();
        AddView(workspace);
        SetActiveView(workspace);
    }
}
