using Content.Client.Stylesheets.Palette;

using Content.Client._Scp.UI.Stylesheets.Palette;

namespace Content.Client.Stylesheets.Stylesheets;

public partial class NanotrasenStylesheet
{
    // Scp edit start - avoid color conficts
    public override ColorPalette PrimaryPalette => ScpPalettes.Primary;
    public override ColorPalette SecondaryPalette => ScpPalettes.Secondary;
    public override ColorPalette PositivePalette => ScpPalettes.Green;
    public override ColorPalette NegativePalette => ScpPalettes.Red;
    public override ColorPalette HighlightPalette => ScpPalettes.Red;
    // Scp edit end
}
