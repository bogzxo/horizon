using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeKneading.Voxel;

public enum TileType : byte
{
    None,
    Ground
}

public readonly struct Tile
{
    public readonly TileType Type { get; init; }

    public static  Tile Empty = new Tile { 
        Type = TileType.None,
    };
}
