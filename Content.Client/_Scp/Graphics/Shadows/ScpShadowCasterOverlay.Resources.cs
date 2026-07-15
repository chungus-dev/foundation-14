using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.Graphics;
using Robust.Shared.Graphics;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const int MaxPrimitiveVerticesPerDraw = 65_529;

    #region Render callbacks

    private void DrawShadowMask()
    {
        var handle = _drawHandle!;
        handle.SetTransform(_targetMatrix);
        var vertices = _currentShadowMaskVertices!;
        if (vertices.Count != 0)
        {
            handle.UseShader(_maskShader);
            DrawTriangleList(handle, CollectionsMarshal.AsSpan(vertices));
        }

        handle.UseShader(null);
    }

    private void ClearAndDrawShadowMask(CachedResources resources)
    {
        var renderHandle = _renderHandle!;
        var targetSize = resources.ShadowMask!.Size;
        var pixelBounds = _targetMatrix.TransformBox(_currentMaskBounds);
        var left = Math.Clamp((int) MathF.Floor(pixelBounds.Left), 0, targetSize.X);
        var top = Math.Clamp((int) MathF.Floor(pixelBounds.Bottom), 0, targetSize.Y);
        var right = Math.Clamp((int) MathF.Ceiling(pixelBounds.Right), left, targetSize.X);
        var bottom = Math.Clamp((int) MathF.Ceiling(pixelBounds.Top), top, targetSize.Y);

        renderHandle.SetScissor(new UIBox2i(left, top, right, bottom));
        try
        {
            renderHandle.RenderInRenderTarget(
                resources.ShadowMask,
                _drawShadowMask,
                Color.Black);
        }
        finally
        {
            renderHandle.SetScissor(null);
        }
    }

    private void DrawTriangleList(DrawingHandleWorld handle, ReadOnlySpan<DrawVertexUV2DColor> vertices)
    {
        while (!vertices.IsEmpty)
        {
            var count = Math.Min(vertices.Length, MaxPrimitiveVerticesPerDraw);
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, _whiteTexture, vertices[..count]);
            vertices = vertices[count..];
        }
    }

    private void DrawProtectionMask()
    {
        var handle = _drawHandle!;
        handle.UseShader(_protectionShader);

        for (var i = 0; i < _protectedSpriteLayers.Count; i++)
        {
            var layer = _protectedSpriteLayers[i];
            handle.SetTransform(Matrix3x2.Multiply(layer.WorldMatrix, _targetMatrix));
            handle.DrawTextureRectRegion(layer.Texture, layer.Quad, layer.Modulate);
        }

        handle.UseShader(null);
    }

    private void DrawContribution()
    {
        var handle = _drawHandle!;
        handle.SetTransform(_targetMatrix);
        handle.UseShader(_currentContributionShader);
        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, _currentLightMask, _lightQuad);
        handle.UseShader(null);
    }

    #endregion

    #region Per-viewport resources

    private sealed class CachedResources : IDisposable
    {
        private const int ShaderPruneInterval = 300;
        private const int ShaderLifetimeFrames = 600;
        private const int MaxCachedLightShaders = 2048;

        public IRenderTexture? ShadowMask;
        public IRenderTexture? ProtectionMask;

        private readonly Dictionary<LightShaderKey, CachedLightShader> _lightShaders = new(256);
        private readonly List<LightShaderKey> _staleLightShaders = new(64);
        private int _frame;

        public void BeginFrame()
        {
            _frame++;
            if (_frame % ShaderPruneInterval != 0)
                return;

            _staleLightShaders.Clear();
            foreach (var (key, shader) in _lightShaders)
            {
                if (_frame - shader.LastUsedFrame > ShaderLifetimeFrames)
                    _staleLightShaders.Add(key);
            }

            for (var i = 0; i < _staleLightShaders.Count; i++)
            {
                var key = _staleLightShaders[i];
                if (_lightShaders.Remove(key, out var shader))
                    shader.Dispose();
            }
        }

        public ShaderInstance GetContributionShader(
            ShaderPrototype prototype,
            EntityUid owner,
            Texture shadowMask,
            Texture protectionMask,
            Color lightColor,
            float lightRange,
            float lightPower,
            float lightFalloff,
            float lightCurveFactor,
            float lightSoftness,
            bool hasShadows,
            bool hasProtection,
            Vector2 lightCenterUv,
            bool directionalFovActive,
            Vector2 directionalFovOffset,
            Vector2 directionalViewDirection,
            Vector2 directionalRadialParameters,
            Vector2 directionalConeThresholds)
        {
            var key = new LightShaderKey(owner);
            if (!_lightShaders.TryGetValue(key, out var cached))
            {
                if (_lightShaders.Count >= MaxCachedLightShaders)
                    EvictOldestShader();

                cached = new CachedLightShader(prototype.InstanceUnique());
                _lightShaders.Add(key, cached);
            }

            cached.LastUsedFrame = _frame;
            cached.Update(
                shadowMask,
                protectionMask,
                lightColor,
                lightRange,
                lightPower,
                lightFalloff,
                lightCurveFactor,
                lightSoftness,
                hasShadows,
                hasProtection,
                lightCenterUv,
                directionalFovActive,
                directionalFovOffset,
                directionalViewDirection,
                directionalRadialParameters,
                directionalConeThresholds);
            return cached.Shader;
        }

        private void EvictOldestShader()
        {
            var hasOldest = false;
            var oldestKey = default(LightShaderKey);
            var oldestFrame = int.MaxValue;

            foreach (var (key, shader) in _lightShaders)
            {
                if (hasOldest && shader.LastUsedFrame >= oldestFrame)
                    continue;

                hasOldest = true;
                oldestKey = key;
                oldestFrame = shader.LastUsedFrame;
            }

            if (hasOldest && _lightShaders.Remove(oldestKey, out var oldest))
                oldest.Dispose();
        }

        public void EnsureSize(IClyde clyde, Vector2i size)
        {
            if (ShadowMask?.Size == size && ProtectionMask?.Size == size)
                return;

            Dispose();

            var samples = new TextureSampleParameters { Filter = true };
            ShadowMask = clyde.CreateRenderTarget(
                size,
                new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8),
                samples,
                "scp-shadow-packed-mask");
            ProtectionMask = clyde.CreateRenderTarget(
                size,
                new RenderTargetFormatParameters(RenderTargetColorFormat.R8),
                samples,
                "scp-shadow-protection-mask");
        }

        public void Dispose()
        {
            foreach (var shader in _lightShaders.Values)
                shader.Dispose();
            _lightShaders.Clear();
            _staleLightShaders.Clear();

            ShadowMask?.Dispose();
            ProtectionMask?.Dispose();
            ShadowMask = null;
            ProtectionMask = null;
        }

        private readonly record struct LightShaderKey(EntityUid Owner);

        private sealed class CachedLightShader(ShaderInstance shader) : IDisposable
        {
            public readonly ShaderInstance Shader = shader;
            public int LastUsedFrame;

            private Texture? _shadowMask;
            private Texture? _protectionMask;

            // ShaderInstance boxes individual values. Reused arrays keep the hot path allocation-free.
            private readonly Color[] _lightColor = [new(float.NaN, float.NaN, float.NaN, float.NaN)];
            private readonly float[] _lightParameters =
                [float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN];
            private readonly Vector2[] _lightCenterUv = [new(float.NaN)];
            private readonly Vector2[] _directionalFovParameters =
                [new(float.NaN), new(float.NaN), new(float.NaN), new(float.NaN)];
            private int _directionalFovMode = int.MinValue;

            public void Update(
                Texture shadowMask,
                Texture protectionMask,
                Color lightColor,
                float lightRange,
                float lightPower,
                float lightFalloff,
                float lightCurveFactor,
                float lightSoftness,
                bool hasShadows,
                bool hasProtection,
                Vector2 lightCenterUv,
                bool directionalFovActive,
                Vector2 directionalFovOffset,
                Vector2 directionalViewDirection,
                Vector2 directionalRadialParameters,
                Vector2 directionalConeThresholds)
            {
                if (!ReferenceEquals(_shadowMask, shadowMask))
                {
                    _shadowMask = shadowMask;
                    Shader.SetParameter("shadowMask", shadowMask);
                }

                if (!ReferenceEquals(_protectionMask, protectionMask))
                {
                    _protectionMask = protectionMask;
                    Shader.SetParameter("protectionMask", protectionMask);
                }

                if (_lightColor[0] != lightColor)
                {
                    _lightColor[0] = lightColor;
                    Shader.SetParameter("lightColor", _lightColor);
                }

                var parametersDirty = false;
                SetFloat(0, lightRange, ref parametersDirty);
                SetFloat(1, lightPower, ref parametersDirty);
                SetFloat(2, lightSoftness, ref parametersDirty);
                SetFloat(3, lightFalloff, ref parametersDirty);
                SetFloat(4, lightCurveFactor, ref parametersDirty);
                SetFloat(5, hasShadows ? 1f : 0f, ref parametersDirty);
                SetFloat(6, hasProtection ? 1f : 0f, ref parametersDirty);
                if (parametersDirty)
                    Shader.SetParameter("lightParameters", _lightParameters);

                if (_lightCenterUv[0] != lightCenterUv)
                {
                    _lightCenterUv[0] = lightCenterUv;
                    Shader.SetParameter("lightCenterUv", _lightCenterUv);
                }

                var fovMode = directionalFovActive ? 1 : 0;
                if (_directionalFovMode != fovMode)
                {
                    _directionalFovMode = fovMode;
                    Shader.SetParameter("directionalFovMode", fovMode);
                }

                if (!directionalFovActive)
                    return;

                var fovDirty = false;
                SetFovVector(0, directionalFovOffset, ref fovDirty);
                SetFovVector(1, directionalViewDirection, ref fovDirty);
                SetFovVector(2, directionalRadialParameters, ref fovDirty);
                SetFovVector(3, directionalConeThresholds, ref fovDirty);
                if (fovDirty)
                    Shader.SetParameter("directionalFovParameters", _directionalFovParameters);
            }

            private void SetFloat(int index, float value, ref bool dirty)
            {
                if (_lightParameters[index] == value)
                    return;

                _lightParameters[index] = value;
                dirty = true;
            }

            private void SetFovVector(int index, Vector2 value, ref bool dirty)
            {
                if (_directionalFovParameters[index] == value)
                    return;

                _directionalFovParameters[index] = value;
                dirty = true;
            }

            public void Dispose()
            {
                Shader.Dispose();
            }
        }
    }

    #endregion
}
