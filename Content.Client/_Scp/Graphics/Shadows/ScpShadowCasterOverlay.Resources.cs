using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.Graphics;
using Robust.Shared.Graphics;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const int MaxPrimitiveVerticesPerDraw = ScpLightingBatchPlanner.MaxVerticesPerDraw;

    #region Render callbacks

    private void DrawShadowMask()
    {
        var handle = _drawHandle!;
        handle.SetTransform(_targetMatrix);
        handle.UseShader(_maskShader);
        DrawTriangleList(handle, _whiteTexture, CollectionsMarshal.AsSpan(_atlasMaskVertices));
        handle.UseShader(null);
    }

    private static void DrawTriangleList(
        DrawingHandleWorld handle,
        Texture texture,
        ReadOnlySpan<DrawVertexUV2DColor> vertices)
    {
        while (!vertices.IsEmpty)
        {
            var count = Math.Min(vertices.Length, MaxPrimitiveVerticesPerDraw);
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, texture, vertices[..count]);
            vertices = vertices[count..];
        }
    }

    private void DrawProtectionMask()
    {
        var handle = _drawHandle!;
        PrepareProtectionBatches();
        handle.UseShader(_protectionShader);

        for (var batchIndex = 0; batchIndex < _activeProtectionBatches; batchIndex++)
        {
            var batch = _protectionBatches[batchIndex];
            foreach (var layer in batch.Layers)
            {
                handle.SetTransform(Matrix3x2.Multiply(layer.WorldMatrix, _targetMatrix));
                handle.DrawTextureRectRegion(layer.Texture, layer.Quad, layer.Modulate);
            }
        }

        handle.UseShader(null);
    }

    #endregion

    #region Per-viewport resources

    private sealed class CachedResources : IDisposable
    {
        public IRenderTexture? ShadowMask;
        public IRenderTexture? ProtectionMask;

        private readonly List<PooledStandardShader> _standardShaders = new(8);
        private readonly List<PooledShadowShader> _shadowShaders = new(16);
        private int _standardShaderCount;
        private int _shadowShaderCount;
        private Vector2i _targetSize;

        public void BeginFrame()
        {
            _standardShaderCount = 0;
            _shadowShaderCount = 0;
        }

        public ShaderInstance GetStandardShader(ShaderPrototype prototype, float curveFactor)
        {
            if (_standardShaderCount == _standardShaders.Count)
                _standardShaders.Add(new PooledStandardShader(prototype.InstanceUnique()));

            return _standardShaders[_standardShaderCount++].Configure(curveFactor);
        }

        public ShaderInstance GetShadowShader(
            ShaderPrototype prototype,
            Texture shadowMask,
            Texture protectionMask,
            ReadOnlySpan<Color> lightAtlasData,
            float softness,
            float falloff,
            float curveFactor,
            bool hasProtection,
            bool directionalFovActive,
            Vector2 directionalFovOffset,
            Vector2 directionalViewDirection,
            Vector2 directionalRadialParameters,
            Vector2 directionalConeThresholds)
        {
            if (_shadowShaderCount == _shadowShaders.Count)
                _shadowShaders.Add(new PooledShadowShader(prototype.InstanceUnique()));

            return _shadowShaders[_shadowShaderCount++]
                .Configure(
                shadowMask,
                protectionMask,
                lightAtlasData,
                softness,
                falloff,
                curveFactor,
                hasProtection,
                directionalFovActive,
                directionalFovOffset,
                directionalViewDirection,
                directionalRadialParameters,
                directionalConeThresholds);
        }

        public void SetSize(Vector2i size)
        {
            if (_targetSize == size)
                return;

            _targetSize = size;
            ShadowMask?.Dispose();
            ProtectionMask?.Dispose();
            ShadowMask = null;
            ProtectionMask = null;
        }

        public void EnsureShadowMask(IClyde clyde)
        {
            if (ShadowMask?.Size == _targetSize)
                return;

            var samples = new TextureSampleParameters { Filter = true };
            ShadowMask = clyde.CreateRenderTarget(
                _targetSize,
                new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8),
                samples,
                "scp-shadow-packed-mask");
        }

        public void EnsureProtectionMask(IClyde clyde)
        {
            EnsureShadowMask(clyde);
            var size = _targetSize;
            if (ProtectionMask?.Size == size)
                return;

            ProtectionMask?.Dispose();
            var samples = new TextureSampleParameters { Filter = true };
            ProtectionMask = clyde.CreateRenderTarget(
                size,
                new RenderTargetFormatParameters(RenderTargetColorFormat.R8),
                samples,
                "scp-shadow-protection-mask");
        }

        public void Dispose()
        {
            foreach (var shader in _standardShaders)
            {
                shader.Dispose();
            }

            _standardShaders.Clear();

            foreach (var shader in _shadowShaders)
            {
                shader.Dispose();
            }

            _shadowShaders.Clear();

            ShadowMask?.Dispose();
            ProtectionMask?.Dispose();
            ShadowMask = null;
            ProtectionMask = null;
        }

        private sealed class PooledStandardShader(ShaderInstance shader) : IDisposable
        {
            private float _curveFactor;

            public ShaderInstance Configure(float curveFactor)
            {
                if (!MathHelper.CloseTo(_curveFactor, curveFactor))
                {
                    _curveFactor = curveFactor;
                    shader.SetParameter("curveFactor", curveFactor);
                }

                return shader;
            }

            public void Dispose()
            {
                shader.Dispose();
            }
        }

        private sealed class PooledShadowShader(ShaderInstance shader) : IDisposable
        {
            private readonly Color[] _lightAtlasData = new Color[GeometryBatchSize];
            private readonly float[] _lightGroupParameters = new float[4];
            private readonly Vector2[] _directionalFovParameters = new Vector2[4];
            private Texture? _shadowMask;
            private Texture? _protectionMask;
            private int _directionalFovMode = -1;

            public ShaderInstance Configure(
                Texture shadowMask,
                Texture protectionMask,
                ReadOnlySpan<Color> lightAtlasData,
                float softness,
                float falloff,
                float curveFactor,
                bool hasProtection,
                bool directionalFovActive,
                Vector2 directionalFovOffset,
                Vector2 directionalViewDirection,
                Vector2 directionalRadialParameters,
                Vector2 directionalConeThresholds)
            {
                lightAtlasData.CopyTo(_lightAtlasData);
                _lightGroupParameters[0] = softness;
                _lightGroupParameters[1] = falloff;
                _lightGroupParameters[2] = curveFactor;
                _lightGroupParameters[3] = hasProtection ? 1f : 0f;
                _directionalFovParameters[0] = directionalFovOffset;
                _directionalFovParameters[1] = directionalViewDirection;
                _directionalFovParameters[2] = directionalRadialParameters;
                _directionalFovParameters[3] = directionalConeThresholds;

                if (!ReferenceEquals(_shadowMask, shadowMask))
                {
                    _shadowMask = shadowMask;
                    shader.SetParameter("shadowMask", shadowMask);
                }

                if (!ReferenceEquals(_protectionMask, protectionMask))
                {
                    _protectionMask = protectionMask;
                    shader.SetParameter("protectionMask", protectionMask);
                }

                shader.SetParameter("lightAtlasData", _lightAtlasData);
                shader.SetParameter("lightGroupParameters", _lightGroupParameters);
                var directionalFovMode = directionalFovActive ? 1 : 0;
                if (_directionalFovMode != directionalFovMode)
                {
                    _directionalFovMode = directionalFovMode;
                    shader.SetParameter("directionalFovMode", directionalFovMode);
                }

                if (directionalFovActive)
                    shader.SetParameter("directionalFovParameters", _directionalFovParameters);

                return shader;
            }

            public void Dispose()
            {
                shader.Dispose();
            }
        }
    }

    #endregion
}
