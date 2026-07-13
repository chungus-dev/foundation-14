using System.Numerics;
using Content.Client.Resources;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

// ReSharper disable once CheckNamespace
namespace Content.Client.MainMenu.UI;

public sealed partial class MainMenuControl
{
    [Dependency] private IResourceCache _resource = default!;

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

        var logoTexture = _resource.GetTexture("/Textures/_Scp/Logo/logo_hollow.png");
        Logo.Texture = logoTexture;
        Logo.TextureScale = new Vector2(0.3f, 0.3f);
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();

        _rsiResource?.Dispose();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        if (_animationState == null)
            return;

        _animationTime += args.DeltaSeconds;

        var delay = _animationState.GetDelay(_animationFrame);
        if (_animationTime < delay)
            return;

        _animationTime -= delay;
        _animationFrame++;

        if (_animationFrame >= _animationState.Icons[0].Length)
            _animationFrame = 0;

        ConnectionAnimation.DisplayRect.Texture = _animationState.GetFrame(RsiDirection.South, _animationFrame);
    }

    private void SetAnimation()
    {
        if (!_resource.TryGetResource(new ResPath(AnimationPath).ToRootedPath(), out _rsiResource))
            return;

        if (!_rsiResource.RSI.TryGetState(AnimationState, out var state))
            return;

        _animationState = state;
        _animationFrame = 0;
        _animationTime = 0;

        ConnectionAnimation.DisplayRect.Texture = _animationState.GetFrame(RsiDirection.South, 0);
        ConnectionAnimation.DisplayRect.TextureScale = AnimationScale;
        ConnectionAnimation.DisplayRect.Stretch = TextureRect.StretchMode.Scale;
    }
}
