

using System.Numerics;
using Content.Client.Resources;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Client.Graphics;

// ReSharper disable once CheckNamespace
namespace Content.Client.MainMenu.UI;

public sealed partial class MainMenuControl
{
    [Dependency] private readonly IResourceCache _resource = default!;

    private const string AnimationPath = "/Textures/_Scp/Lobby/Animations/deep_facility.rsi";
    private const string AnimationState = "animation";
    private static readonly Vector2 AnimationScale = new(13f, 13f);

    private RSI.State? _animationState;
    private float _animationTime;
    private int _animationFrame;

    private RSIResource? _rsiResource;

    protected override void EnteredTree()
    {
        base.EnteredTree();

        SetAnimation();

        var logoTexture = _resource.GetTexture("/Textures/_Scp/Logo/logo-hollow.png");
        Logo.Texture = logoTexture;
        Logo.TextureScale = new Vector2(0.125f, 0.125f);
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();

        _rsiResource?.Dispose();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
    }

    private void SetAnimation()
    {
    }
}
