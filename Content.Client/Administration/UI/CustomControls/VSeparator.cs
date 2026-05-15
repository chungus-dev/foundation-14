using System.Numerics;
using Content.Client._Scp.Stylesheets.Palette;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client.Administration.UI.CustomControls;

public sealed class VSeparator : PanelContainer
{
    private static readonly Color SeparatorColor = ScpPalettes.SCPWhite; // Scp edit

    public VSeparator(Color color)
    {
        MinSize = new Vector2(2, 5);

        AddChild(new PanelContainer
        {
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = color
            }
        });
    }

    public VSeparator() : this(SeparatorColor) { }
}
