using System.Text;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.Utility;
using Robust.Shared.Graphics;
using Robust.Shared.Graphics.RSI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client.Clickable
{
    internal sealed partial class ClickMapManager : IClickMapManager, IPostInjectInit
    {
        private static readonly string[] IgnoreTexturePaths =
        {
            // These will probably never need click maps so skip em.
            "/Textures/Interface",
            "/Textures/LobbyScreens",
            "/Textures/Parallaxes",
            "/Textures/Logo",
        };

        private const float Threshold = 0.1f;
        private const int ClickRadius = 2;

        [Dependency] private IResourceCache _resourceCache = default!;

        [ViewVariables]
        private readonly Dictionary<Texture, ClickMap> _textureMaps = new();

        [ViewVariables] private readonly Dictionary<RSI, RsiClickMapData> _rsiMaps =
            new();

        public void PostInject()
        {
            _resourceCache.OnRawTextureLoaded += OnRawTextureLoaded;
            _resourceCache.OnRsiLoaded += OnOnRsiLoaded;
        }

        private void OnOnRsiLoaded(RsiLoadedEventArgs obj)
        {
            if (obj.Atlas is Image<Rgba32> rgba)
            {
                var clickMap = ClickMap.FromImage(rgba, Threshold);

                var rsiData = new RsiClickMapData(clickMap, obj.AtlasOffsets);
                _rsiMaps[obj.Resource.RSI] = rsiData;
            }
        }

        private void OnRawTextureLoaded(TextureLoadedEventArgs obj)
        {
            if (obj.Image is Image<Rgba32> rgba)
            {
                var pathStr = obj.Path.ToString();
                foreach (var path in IgnoreTexturePaths)
                {
                    if (pathStr.StartsWith(path, StringComparison.Ordinal))
                        return;
                }

                _textureMaps[obj.Resource] = ClickMap.FromImage(rgba, Threshold);
            }
        }

        public bool IsOccluding(Texture texture, Vector2i pos)
        {
            if (!_textureMaps.TryGetValue(texture, out var clickMap))
            {
                return false;
            }

            return SampleClickMap(clickMap, pos, clickMap.Size, Vector2i.Zero);
        }

        // Scp added start - expose cached alpha maps for content shadow contours
        public bool TryGetRegion(Texture texture, out ClickMapRegion region)
        {
            if (!_textureMaps.TryGetValue(texture, out var clickMap))
            {
                region = default;
                return false;
            }

            region = clickMap.GetRegion(clickMap.Size, Vector2i.Zero);
            return true;
        }
        // Scp added end - expose cached alpha maps for content shadow contours

        public bool IsOccluding(RSI rsi, RSI.StateId state, RsiDirection dir, int frame, Vector2i pos)
        {
            if (!_rsiMaps.TryGetValue(rsi, out var rsiData))
            {
                return false;
            }

            if (!rsiData.Offsets.TryGetValue(state, out var stateDat) || stateDat.Length <= (int) dir)
            {
                return false;
            }

            var dirDat = stateDat[(int) dir];
            if (dirDat.Length <= frame)
            {
                return false;
            }

            var offset = dirDat[frame];
            return SampleClickMap(rsiData.ClickMap, pos, rsi.Size, offset);
        }

        // Scp added start - expose cached alpha maps for content shadow contours
        public bool TryGetRegion(
            RSI rsi,
            RSI.StateId state,
            RsiDirection dir,
            int frame,
            out ClickMapRegion region)
        {
            if (!_rsiMaps.TryGetValue(rsi, out var rsiData) ||
                !rsiData.Offsets.TryGetValue(state, out var stateData) ||
                stateData.Length <= (int) dir ||
                stateData[(int) dir].Length <= frame)
            {
                region = default;
                return false;
            }

            region = rsiData.ClickMap.GetRegion(rsi.Size, stateData[(int) dir][frame]);
            return true;
        }
        // Scp added end - expose cached alpha maps for content shadow contours

        private static bool SampleClickMap(ClickMap map, Vector2i pos, Vector2i bounds, Vector2i offset)
        {
            var (width, height) = bounds;
            var (px, py) = pos;

            for (var x = -ClickRadius; x <= ClickRadius; x++)
            {
                var ox = px + x;
                if (ox < 0 || ox >= width)
                {
                    continue;
                }

                for (var y = -ClickRadius; y <= ClickRadius; y++)
                {
                    var oy = py + y;

                    if (oy < 0 || oy >= height)
                    {
                        continue;
                    }

                    if (map.IsOccluded((ox, oy) + offset))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private sealed class RsiClickMapData
        {
            public readonly ClickMap ClickMap;
            public readonly Dictionary<RSI.StateId, Vector2i[][]> Offsets;

            public RsiClickMapData(ClickMap clickMap, Dictionary<RSI.StateId, Vector2i[][]> offsets)
            {
                ClickMap = clickMap;
                Offsets = offsets;
            }
        }

        internal sealed class ClickMap
        {
            [ViewVariables] private readonly byte[] _data;

            public int Width { get; }
            public int Height { get; }
            [ViewVariables] public Vector2i Size => (Width, Height);

            public bool IsOccluded(int x, int y)
            {
                var i = y * Width + x;
                return (_data[i / 8] & (1 << (i % 8))) != 0;
            }

            public bool IsOccluded(Vector2i vector)
            {
                var (x, y) = vector;
                return IsOccluded(x, y);
            }

            private ClickMap(byte[] data, int width, int height)
            {
                Width = width;
                Height = height;
                _data = data;
            }

            // Scp added - expose a read-only slice without copying the bit map
            public ClickMapRegion GetRegion(Vector2i size, Vector2i offset)
            {
                return new ClickMapRegion(_data, Width, size, offset);
            }

            public static ClickMap FromImage<T>(Image<T> image, float threshold) where T : unmanaged, IPixel<T>
            {
                var threshByte = (byte) (threshold * 255);
                var width = image.Width;
                var height = image.Height;

                var dataSize = (int) Math.Ceiling(width * height / 8f);
                var data = new byte[dataSize];

                var pixelSpan = image.GetPixelSpan();

                for (var i = 0; i < pixelSpan.Length; i++)
                {
                    Rgba32 rgba = default;
                    pixelSpan[i].ToRgba32(ref rgba);
                    if (rgba.A >= threshByte)
                    {
                        data[i / 8] |= (byte) (1 << (i % 8));
                    }
                }

                return new ClickMap(data, width, height);
            }

            public string DumpText()
            {
                var sb = new StringBuilder();
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        sb.Append(IsOccluded(x, y) ? "1" : "0");
                    }

                    sb.AppendLine();
                }

                return sb.ToString();
            }
        }
    }

    public interface IClickMapManager
    {
        public bool IsOccluding(Texture texture, Vector2i pos);

        public bool IsOccluding(RSI rsi, RSI.StateId state, RsiDirection dir, int frame, Vector2i pos);

        // Scp added start - expose cached alpha maps for content shadow contours
        public bool TryGetRegion(Texture texture, out ClickMapRegion region);

        public bool TryGetRegion(
            RSI rsi,
            RSI.StateId state,
            RsiDirection dir,
            int frame,
            out ClickMapRegion region);
        // Scp added end - expose cached alpha maps for content shadow contours
    }

    // Scp added start - expose cached alpha maps for content shadow contours
    /// <summary>
    /// Provides read-only access to a rectangular slice of a cached alpha click map.
    /// </summary>
    public readonly struct ClickMapRegion
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly int _atlasWidth;
        private readonly Vector2i _offset;

        public Vector2i Size { get; }

        internal ClickMapRegion(ReadOnlyMemory<byte> data, int atlasWidth, Vector2i size, Vector2i offset)
        {
            _data = data;
            _atlasWidth = atlasWidth;
            _offset = offset;
            Size = size;
        }

        /// <summary>
        /// Returns whether the pixel is opaque according to the click-map alpha threshold.
        /// </summary>
        public bool IsOccluded(int x, int y)
        {
            if ((uint) x >= (uint) Size.X || (uint) y >= (uint) Size.Y)
                return false;

            var index = (y + _offset.Y) * _atlasWidth + x + _offset.X;
            return (_data.Span[index / 8] & 1 << (index % 8)) != 0;
        }
    }
    // Scp added end - expose cached alpha maps for content shadow contours
}
