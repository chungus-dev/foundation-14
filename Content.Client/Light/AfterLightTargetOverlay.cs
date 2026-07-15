using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.Enums;

namespace Content.Client.Light;

/// <summary>
/// This exists just to copy <see cref="BeforeLightTargetOverlay"/> to the light render target
/// </summary>
public sealed partial class AfterLightTargetOverlay : Overlay
{
    public override OverlaySpace Space => OverlaySpace.BeforeLighting;

    [Dependency] private IOverlayManager _overlay = default!;

    private readonly Action _copyLightTarget;
    private DrawingHandleWorld? _drawHandle;
    private Texture? _sourceTexture;
    private Box2Rotated _destinationBounds;
    private UIBox2i _sourceRegion;
    private Matrix3x2 _targetMatrix;

    // Scp edit start - reserve LightBlurOverlay.ContentZIndex + 1 for SCP lighting.
    public const int ContentZIndex = LightBlurOverlay.ContentZIndex + 2;
    // Scp edit end

    public AfterLightTargetOverlay()
    {
        IoCManager.InjectDependencies(this);
        ZIndex = ContentZIndex;
        _copyLightTarget = CopyLightTarget;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var viewport = args.Viewport;
        var worldHandle = args.WorldHandle;

        if (viewport.Eye == null)
            return;

        var lightOverlay = _overlay.GetOverlay<BeforeLightTargetOverlay>();
        var lightRes = lightOverlay.GetCachedForViewport(args.Viewport);
        var bounds = args.WorldBounds;

        // at 1-1 render scale it's mostly fine but at 4x4 it's way too fkn big
        var lightScale = viewport.LightRenderTarget.Size / (Vector2) viewport.Size;
        var newScale = viewport.RenderScale / (Vector2.One / lightScale);

        var localMatrix =
            viewport.LightRenderTarget.GetWorldToLocalMatrix(viewport.Eye, newScale);
        var diff = (lightRes.EnlargedLightTarget.Size - viewport.LightRenderTarget.Size);
        var halfDiff = diff / 2;

        _drawHandle = worldHandle;
        _sourceTexture = lightRes.EnlargedLightTarget.Texture;
        _destinationBounds = bounds;
        _targetMatrix = localMatrix;
        _sourceRegion = new UIBox2i(
            halfDiff.X,
            halfDiff.Y,
            viewport.LightRenderTarget.Size.X + halfDiff.X,
            viewport.LightRenderTarget.Size.Y + halfDiff.Y);

        try
        {
            // Scp edit start - preserve hard-FOV stencil without allocating a frame callback.
            worldHandle.RenderInRenderTarget(
                viewport.LightRenderTarget,
                _copyLightTarget,
                null);
            // Scp edit end
        }
        finally
        {
            _drawHandle = null;
            _sourceTexture = null;
        }
    }

    // Scp added start - cached callback for the enlarged-target copy.
    private void CopyLightTarget()
    {
        _drawHandle!.SetTransform(_targetMatrix);
        _drawHandle!.DrawTextureRectRegion(
            _sourceTexture!,
            _destinationBounds,
            subRegion: _sourceRegion);
    }
    // Scp added end
}
