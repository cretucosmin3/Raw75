using Blossom.Core;
using Blossom.Core.Visual;
using SkiaSharp;

namespace Raw75.Views;

public sealed class WorkspaceView : View
{
    public WorkspaceView() : base("Workspace")
    {
        BackColor = new SKColor(16, 16, 18);
    }

    public override void Init()
    {
        AddElement(new VisualElement
        {
            Name = "Title",
            Text = "Raw75",
            Style = new ElementStyle
            {
                Text = new TextStyle
                {
                    Color = SKColors.White,
                    Size = 28,
                    Weight = 500,
                    Alignment = TextAlign.Center
                }
            },
            Transform = new Transform(0, 72, 0, 40)
            {
                Anchor = Anchor.Top | Anchor.Left | Anchor.Right
            }
        });

        AddElement(new VisualElement
        {
            Name = "Subtitle",
            Text = "Open a RAW photo to begin.",
            Style = new ElementStyle
            {
                Text = new TextStyle
                {
                    Color = new SKColor(160, 160, 168),
                    Size = 15,
                    Weight = 400,
                    Alignment = TextAlign.Center
                }
            },
            Transform = new Transform(0, 118, 0, 28)
            {
                Anchor = Anchor.Top | Anchor.Left | Anchor.Right
            }
        });
    }
}
