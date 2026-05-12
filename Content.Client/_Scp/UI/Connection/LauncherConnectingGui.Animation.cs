using System.Numerics;
using Content.Client.Resources;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

// ReSharper disable once CheckNamespace
namespace Content.Client.Launcher;

public sealed partial class LauncherConnectingGui
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
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();

        _rsiResource?.Dispose();
    }

    private void UpdateAnimation(FrameEventArgs args)
    {
    }

    private void SetAnimation()
    {
    }
}
