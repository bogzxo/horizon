using DotTiled.Serialization;
using Horizon.Content;

namespace Horizon.Rendering.Tiling;

/// <summary>
/// Hands DotTiled the map, its tile sets and templates out of the content source the map was found in, rather than off
/// the disk the way its own reader does, so a map packed into the game reads the same as a loose one.
/// </summary>
internal sealed class TileMapContentReader(IContentFiles source) : IResourceReader
{
    public string Read(string resourcePath) => ContentFiles.ReadAllText(source, resourcePath);
}
