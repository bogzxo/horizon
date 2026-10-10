# Where the examples' art comes from

Nothing in here ships. It is what makes the files in `Assets/examples`, for when one of them wants changing.
Run a script from the `Horizon.Testing` folder, it writes over what is there.

- `make_world.py` paints the tile set the map examples share (`world/tiles_albedo.png` and its `_normal`,
  `_specular` and `_ao`), the clouds, and writes `town.tmx`, `cellar.tmx`, the tile set file and the object
  templates. A tile is a function in there and a map is a few loops, change either and run it again. The maps
  open in Tiled, but anything changed there is gone the next time the script runs, so a map that is going to be
  drawn by hand from now on wants taking out of the script first.
- `make_small_art.py` paints the white ring, disc and glow the keyboard and the scenes examples tint.

The rest was painted by code the examples used to carry about with them and has been files since, `sprites/`
(the blob and the scenery), `camera/` (the island, its minimap and its props) and everything under `checks/`.
Those are edited like any other picture. The numbers in `Checks/TileMapChecks.cs` are the numbers in the maps of
`checks/tilemap`, change one and the other wants changing too.

Needs Python with Pillow and numpy.
