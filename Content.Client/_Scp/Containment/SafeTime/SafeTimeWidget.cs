using Robust.Client.UserInterface.Controls;

namespace Content.Client._Scp.Containment.SafeTime;

public sealed class SafeTimeWidget : UIWidget
{
    public readonly SafeTimeProgressBar ProgressBar;

    public SafeTimeWidget()
    {
        ProgressBar = new SafeTimeProgressBar();
        AddChild(ProgressBar);
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();

        RemoveAllChildren();
        Orphan();
    }
}
