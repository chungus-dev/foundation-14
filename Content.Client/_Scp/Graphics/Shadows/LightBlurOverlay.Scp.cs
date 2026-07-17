using Robust.Client.Graphics;
using Robust.Shared;
using Robust.Shared.Configuration;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Content.Client.Light;

public sealed partial class LightBlurOverlay
{
    [Dependency] private IConfigurationManager _configuration = default!;

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return _configuration.GetCVar(CVars.LightBlur);
    }
}
