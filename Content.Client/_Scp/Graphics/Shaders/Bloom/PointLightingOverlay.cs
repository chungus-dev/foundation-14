using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Profiling;
using Robust.Shared.Prototypes;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;

namespace Content.Client._Scp.Graphics.Shaders.Bloom;

public sealed class PointLightingOverlay : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpaceEntities;
    public override bool RequestScreenTexture => true;

    private readonly ShaderInstance _shader;
    private readonly ProfManager _prof;

    private readonly Texture _pointTexture;
    private readonly Box2 _pointQuad;
    private readonly List<WorldTextureRect> _drawRects = [];
    private readonly List<BloomOverlayEntry> _entities;
    private Texture? _screenTexture;
    private float _strength = 1f;

    public bool Enabled;
    public float Strength
    {
        get => _strength;
        set
        {
            if (_strength.Equals(value))
                return;

            _strength = value;
            _shader.SetParameter(
                "hueta_divisor",
                BloomOverlayVisualsComponent.DefaultPointHuetaDivisor / value);
        }
    }

    internal PointLightingOverlay(
        IPrototypeManager prototypeManager,
        SpriteSystem spriteSystem,
        ProfManager prof,
        ProtoId<ShaderPrototype> shader,
        List<BloomOverlayEntry> entities)
    {
        _shader = prototypeManager.Index(shader).InstanceUnique();
        _prof = prof;
        _entities = entities;
        ZIndex = (int) DrawDepth.Effects + 1;

        _pointTexture = spriteSystem.Frame0(BloomOverlayVisualsComponent.Point);

        var xOffset = BloomOverlayVisualsComponent.PointOffset.X - (_pointTexture.Width / 2f) / EyeManager.PixelsPerMeter;
        var yOffset = BloomOverlayVisualsComponent.PointOffset.Y - (_pointTexture.Height / 2f) / EyeManager.PixelsPerMeter;
        var pointOffset = new Vector2(xOffset, yOffset);
        _pointQuad = Box2.FromDimensions(pointOffset, _pointTexture.Size / (float) EyeManager.PixelsPerMeter);
        _shader.SetParameter("base_haze", BloomOverlayVisualsComponent.DefaultPointBaseHaze);
        _shader.SetParameter(
            "hueta_divisor",
            BloomOverlayVisualsComponent.DefaultPointHuetaDivisor / _strength);
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        base.BeforeDraw(in args);

        return Enabled && _entities.Count > 0;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (ScreenTexture == null)
            return;

        using var pointGroup = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpLightBloom.PointDraw")
            : (ProfManager.GroupGuard?) null;

        var handle = args.WorldHandle;
        var bounds = args.WorldAABB.Enlarged(5f);
        _drawRects.Clear();

        foreach (var entity in _entities)
        {
            if (entity.Transform.MapID != args.MapId)
                continue;

            if (!bounds.Contains(entity.WorldPosition))
                continue;

            var quad = new Box2Rotated(
                _pointQuad.Translated(entity.WorldPosition),
                entity.WorldRotation,
                entity.WorldPosition);
            _drawRects.Add(new WorldTextureRect(quad, entity.Color));
        }

        if (!ReferenceEquals(_screenTexture, ScreenTexture))
        {
            _screenTexture = ScreenTexture;
            _shader.SetParameter("SCREEN_TEXTURE", ScreenTexture);
        }
        handle.UseShader(_shader);
        handle.SetTransform(Matrix3x2.Identity);
        handle.DrawTextureRects(_pointTexture, CollectionsMarshal.AsSpan(_drawRects));
        handle.UseShader(null);
    }

    protected override void DisposeBehavior()
    {
        base.DisposeBehavior();

        _shader.Dispose();
    }
}
