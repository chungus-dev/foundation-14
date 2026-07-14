using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.Graphics;
using Robust.Shared;
using Robust.Shared.Graphics;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const int MaxPrimitiveVerticesPerDraw = 65_529;

    #region Render callbacks

    private void DrawCasterMask()
    {
        DrawMaskVertices(_casterVertices);
    }

    private void DrawOccluderMask()
    {
        DrawMaskVertices(_occluderVertices);
    }

    private void DrawMaskVertices(List<DrawVertexUV2DColor> vertices)
    {
        var handle = _drawHandle!;
        handle.SetTransform(_targetMatrix);
        handle.UseShader(_unshadedShader);

        // ponytail: DrawingHandleWorld has no scissor API; an opaque light-AABB quad clears the same useful pixels.
        handle.DrawRect(_currentMaskBounds, Color.Black);

        if (vertices.Count == 0)
        {
            handle.UseShader(null);
            return;
        }

        DrawTriangleList(handle, CollectionsMarshal.AsSpan(vertices));
        handle.UseShader(null);
    }

    private void DrawLocalShadowStencil()
    {
        if (_localShadowVertices.Count == 0)
            return;

        var handle = _drawHandle!;
        handle.SetTransform(_targetMatrix);
        handle.UseShader(_localStencilShader);
        DrawTriangleList(handle, CollectionsMarshal.AsSpan(_localShadowVertices));
        handle.UseShader(null);
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

    private void DrawProtectedSprites()
    {
        var handle = _drawHandle!;
        handle.UseShader(_stencilShader);

        for (var i = 0; i < _protectedSpriteLayers.Count; i++)
        {
            var layer = _protectedSpriteLayers[i];
            var matrix = Matrix3x2.Multiply(layer.WorldMatrix, _targetMatrix);
            handle.SetTransform(matrix);
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

    private void DrawComposite()
    {
        var handle = _drawHandle!;
        handle.SetTransform(_targetMatrix);
        handle.UseShader(_subtractShader);
        handle.DrawTextureRect(_currentResources!.Contribution!.Texture, _worldBounds);
        handle.UseShader(null);
    }

    private void DrawLocalComposite()
    {
        var handle = _drawHandle!;
        handle.SetTransform(_targetMatrix);
        handle.UseShader(_localSubtractShader);
        handle.DrawTextureRect(_currentResources!.Contribution!.Texture, _worldBounds);
        handle.UseShader(null);
    }

    #endregion

    #region Per-viewport resources

    private sealed class CachedResources : IDisposable
    {
        private const int ShaderPruneInterval = 300;
        private const int ShaderLifetimeFrames = 600;
        private const int MaxCachedLightShaders = 2048;

        public IRenderTexture? CasterMask;
        public IRenderTexture? OccluderMask;
        public IRenderTexture? Contribution;
        public IRenderTexture? Blur;

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
            bool localContribution,
            Texture casterMask,
            Texture occluderMask,
            Color lightColor,
            float lightRange,
            float lightPower,
            float lightFalloff,
            float lightCurveFactor,
            float lightSoftness,
            Vector2 lightCenterUv)
        {
            var key = new LightShaderKey(owner, localContribution);
            if (!_lightShaders.TryGetValue(key, out var cached))
            {
                if (_lightShaders.Count >= MaxCachedLightShaders)
                    EvictOldestShader();

                cached = new CachedLightShader(prototype.InstanceUnique());
                _lightShaders.Add(key, cached);
            }

            cached.LastUsedFrame = _frame;
            cached.Update(
                casterMask,
                occluderMask,
                lightColor,
                lightRange,
                lightPower,
                lightFalloff,
                lightCurveFactor,
                lightSoftness,
                lightCenterUv);
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
            if (CasterMask?.Size == size &&
                OccluderMask?.Size == size &&
                Contribution?.Size == size &&
                Blur?.Size == size)
            {
                return;
            }

            Dispose();

            var maskFormat = new RenderTargetFormatParameters(RenderTargetColorFormat.R8);
            var maskSamples = new TextureSampleParameters { Filter = true };
            CasterMask = clyde.CreateRenderTarget(size, maskFormat, maskSamples, "scp-shadow-caster-mask");
            OccluderMask = clyde.CreateRenderTarget(size, maskFormat, maskSamples, "scp-shadow-occluder-mask");
            Contribution = clyde.CreateLightRenderTarget(size, "scp-shadow-contribution", false);
            Blur = clyde.CreateLightRenderTarget(size, "scp-shadow-contribution-blur", false);
        }

        public void Dispose()
        {
            foreach (var shader in _lightShaders.Values)
                shader.Dispose();
            _lightShaders.Clear();
            _staleLightShaders.Clear();

            CasterMask?.Dispose();
            OccluderMask?.Dispose();
            Contribution?.Dispose();
            Blur?.Dispose();

            CasterMask = null;
            OccluderMask = null;
            Contribution = null;
            Blur = null;
        }

        private readonly record struct LightShaderKey(EntityUid Owner, bool LocalContribution);

        private sealed class CachedLightShader(ShaderInstance shader) : IDisposable
        {
            public readonly ShaderInstance Shader = shader;
            public int LastUsedFrame;

            private Texture? _casterMask;
            private Texture? _occluderMask;
            // Arrays are kept for the lifetime of the shader. ShaderInstance stores
            // scalar and vector values as object, which boxes every changed value;
            // reusing reference-type uniform arrays avoids that per-frame garbage.
            private readonly Color[] _lightColor = [new(float.NaN, float.NaN, float.NaN, float.NaN)];
            private readonly float[] _lightParameters =
                [float.NaN, float.NaN, float.NaN, float.NaN, float.NaN];
            private readonly Vector2[] _lightCenterUv = [new(float.NaN)];

            public void Update(
                Texture casterMask,
                Texture occluderMask,
                Color lightColor,
                float lightRange,
                float lightPower,
                float lightFalloff,
                float lightCurveFactor,
                float lightSoftness,
                Vector2 lightCenterUv)
            {
                if (!ReferenceEquals(_casterMask, casterMask))
                {
                    _casterMask = casterMask;
                    Shader.SetParameter("casterMask", casterMask);
                }

                if (!ReferenceEquals(_occluderMask, occluderMask))
                {
                    _occluderMask = occluderMask;
                    Shader.SetParameter("occluderMask", occluderMask);
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
                if (parametersDirty)
                    Shader.SetParameter("lightParameters", _lightParameters);

                if (_lightCenterUv[0] != lightCenterUv)
                {
                    _lightCenterUv[0] = lightCenterUv;
                    Shader.SetParameter("lightCenterUv", _lightCenterUv);
                }
            }

            private void SetFloat(int index, float value, ref bool dirty)
            {
                if (_lightParameters[index] == value)
                    return;

                _lightParameters[index] = value;
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
