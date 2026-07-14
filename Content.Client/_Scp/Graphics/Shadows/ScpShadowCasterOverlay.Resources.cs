using System.Runtime.InteropServices;
using Robust.Client.Graphics;
using Robust.Shared.Graphics;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
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
        if (vertices.Count == 0)
            return;

        var handle = _drawHandle!;
        handle.SetTransform(_targetMatrix);
        handle.UseShader(_unshadedShader);
        handle.DrawPrimitives(
            DrawPrimitiveTopology.TriangleList,
            Texture.White,
            CollectionsMarshal.AsSpan(vertices));
        handle.UseShader(null);
    }

    private void DrawContribution()
    {
        var handle = _drawHandle!;
        handle.SetTransform(_targetMatrix);
        handle.UseShader(_contributionShader);
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

    #endregion

    #region Per-viewport resources

    private sealed class CachedResources : IDisposable
    {
        public IRenderTexture? CasterMask;
        public IRenderTexture? OccluderMask;
        public IRenderTexture? Contribution;
        public IRenderTexture? Blur;

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
            CasterMask?.Dispose();
            OccluderMask?.Dispose();
            Contribution?.Dispose();
            Blur?.Dispose();

            CasterMask = null;
            OccluderMask = null;
            Contribution = null;
            Blur = null;
        }
    }

    #endregion
}
