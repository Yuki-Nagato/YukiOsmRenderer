# YukiOsmRenderer

Self-contained OpenStreetMap raster renderer that reproduces the standard map layer of
[openstreetmap.org](https://www.openstreetmap.org) ([OpenStreetMap Carto](https://github.com/openstreetmap-carto/openstreetmap-carto))
from `.osm.pbf` files, fully offline. A second engine draws a different style from vector tiles, which are built
from the input in a small fraction of the time of a database import.

## Features

- **Three modes in one executable**
  - `render` – renders an area at a given zoom level into a GeoTIFF (stdout by default, or a file).
  - `tiles` – renders an area at one or more zoom levels into a directory of XYZ PNG tiles, with a configurable
    path template.
  - `serve` – serves Web Mercator XYZ tiles over HTTP, with an offline preview page.
- **Two engines**, chosen with `--engine`:
  - `carto` (default): the openstreetmap.org style, imported into PostgreSQL.
  - `vector`: [Shortbread](https://shortbread-tiles.org) vector tiles built by [Planetiler](https://github.com/onthegomap/planetiler)
    and drawn by [MapLibre Native](https://github.com/maplibre/maplibre-native) with the
    [VersaTiles](https://versatiles.org) styles; see [Vector engine](#vector-engine). The vector tiles are served as
    well.
- **Same rendering pipeline as openstreetmap.org**: osm2pgsql (flex output) → PostGIS → carto → Mapnik, using the
  original OpenStreetMap Carto style, the same Mapnik API level (`carto -a 3.0.22`), the same 8 × 8 metatiles and
  buffer size as renderd, and the same tile encoding (`png256:z=5`); full-color `png32` is available as well.
- **Multiple input files**: any number of `.osm.pbf` files can be combined.
- **CJK glyph variants**: Noto Sans CJK SC (default), TC, HK, JP or KR, chosen with `--cjk` or, for the tile
  server, per request. Like OpenStreetMap Carto's `scripts/change-fonts-cjk.sh`, the variant selects the CJK font;
  labels are also shaped with the matching language (`ja`, `ko`, `zh-hant`, `zh-hk`), because Mapnik shapes Han text
  as Simplified Chinese by default. The default `sc` therefore looks exactly like tile.openstreetmap.org.
- **Transparent rendering** for overlaying on imagery: the background and all area fills and patterns are dropped,
  while roads, lines, labels and icons are kept. Fills of selected layers can be kept, area outlines can be hidden
  and whole layers can be left out.
- **Adjustable label size**: `--text-scale` (or `textScale` per request) enlarges or shrinks labels and road shields
  by a factor from 0.5 to 3, leaving icons, lines and areas as they are.
- **Fully offline**: everything except the `.osm.pbf` input (PostgreSQL, PostGIS, osm2pgsql, Mapnik, Node.js and
  carto, Python and GDAL's ogr2ogr, the style, fonts, icons and water polygons) ships with the release.
- **Platforms**: Windows x64, Linux x64 and Linux arm64 (portable archive, or Docker image).

## Requirements

- **Windows**: Windows 10 or later, x64.
- **Linux**: x64 or arm64 with glibc ≥ 2.31 (e.g. Debian 11, Ubuntu 20.04, RHEL 9 or later) and OpenSSL (`libssl`
  1.1 or 3), which the .NET runtime uses for cryptography. Mainstream distributions include OpenSSL, but minimal
  container images such as `debian:12-slim` do not; install it there (e.g. `apt-get install libssl3`).

Everything else ships with the release.

## Usage

Download the archive for your platform from the [releases](https://github.com/Yuki-Nagato/YukiOsmRenderer/releases),
extract it and run `yukiosm` from the extracted directory.

```sh
# Render central Beijing at zoom 14 into a GeoTIFF
yukiosm render -i beijing.osm.pbf -z 14 --bbox 116.2,39.8,116.6,40.0 -o beijing-z14.tif

# Combine several extracts, use Japanese glyph shapes and write the GeoTIFF to stdout
yukiosm render -i beijing.osm.pbf -i tianjin.osm.pbf -z 10 --bbox 115.4,38.5,118.1,41.1 --cjk jp > out.tif

# Transparent overlay that keeps the fills of water areas and buildings, at twice the resolution
yukiosm render -i beijing.osm.pbf -z 15 --bbox 116.3,39.85,116.45,39.95 --scale 2 \
    --transparent --keep-fill water-areas,buildings -o overlay.tif

# PNG tiles of zoom levels 10 to 14 in tiles/z{z}/y{y}/x{x}.png
yukiosm tiles -i beijing.osm.pbf -z 10-14 --bbox 116.2,39.8,116.6,40.0 -o tiles

# Transparent high-DPI tiles in overlay/{z}/{x}-{y}.png, in full color
yukiosm tiles -z 14 --bbox 116.2,39.8,116.6,40.0 -o overlay --template "{z}/{x}-{y}.png" --scale 2 --transparent --format png32

# Labels one and a half times as large
yukiosm tiles -z 10-14 --bbox 116.2,39.8,116.6,40.0 -o large-labels --text-scale 1.5

# Tile server with a preview page at http://localhost:8080/
yukiosm serve -i beijing.osm.pbf -i tianjin.osm.pbf --port 8080

# Later runs can leave out -i to use the data imported before
yukiosm serve --port 8080

# Vector engine: build vector tiles from the extracts (seconds to minutes) and serve them, drawn in the colorful theme
yukiosm serve --engine vector -i beijing.osm.pbf -i tianjin.osm.pbf --theme colorful --port 8080

# Only build the vector tiles, into a file that any number of servers can use
yukiosm import --engine vector -i planet.osm.pbf -o planet.pmtiles
yukiosm serve --pmtiles planet.pmtiles --port 8080
```

Run `yukiosm render --help`, `yukiosm tiles --help`, `yukiosm serve --help` and `yukiosm import --help` for all
options. `import` only prepares the input (the database, or the vector tiles) without rendering anything.

### Tile formats

`--format` selects how tiles are encoded, in all three modes:

- `png256` (default): at most 256 colors per tile, exactly like tile.openstreetmap.org (`png256:z=5`). `render` gives
  the GeoTIFF the same colors, so it matches the served tiles pixel for pixel.
- `png32`: full-color RGBA, without quantization. Files are larger, but anti-aliased edges and semi-transparent
  pixels of transparent renderings stay smooth.

For `serve`, `--format` is the default for requests without the `format` parameter.

### Tile files

`tiles` writes every tile covering `--bbox` (default: the whole world) at the zoom level or range given with `-z`
(e.g. `14` or `10-14`) into the directory `-o`. `--template` sets the path of each file below that directory, with
the placeholders `{z}`, `{x}` and `{y}`; the default is `z{z}/y{y}/x{x}.png`. Existing files are replaced. The tiles
are the same as those of the tile server: 256 × 256 pixels, or 512 × 512 with `--scale 2`, encoded as set by
`--format`, and the style options `--transparent`, `--keep-fill`, `--hide-outline`, `--hide-layer`,
`--cjk` and `--text-scale` work as for `render`.

### Tile server

| URL | Content |
|-----|---------|
| `/tiles/{z}/{x}/{y}.png` | 256 × 256 tile |
| `/tiles/{z}/{x}/{y}@2x.png` | 512 × 512 tile of the same area (high DPI) |
| `/tiles/...png?transparent=1` | transparent rendering |
| `/tiles/...png?transparent=1&keepFill=water-areas,buildings&hideOutline=1` | keep some fills, hide area outlines |
| `/tiles/...png?hideLayers=buildings,amenity-points` | leave out layers |
| `/tiles/...png?cjk=jp` | CJK glyph variant (`sc`, `tc`, `hk`, `jp`, `kr`); defaults to the `--cjk` option of `serve` |
| `/tiles/...png?textScale=1.5` | label size factor (0.5–3); defaults to the `--text-scale` option of `serve` |
| `/tiles/...png?format=png32` | tile format (`png256`, `png32`); defaults to the `--format` option of `serve` |
| `/layers` | layer names usable in `keepFill` and `hideLayers` |
| `/` | preview page |

Parameters can be combined, e.g. `/tiles/{z}/{x}/{y}@2x.png?transparent=1&keepFill=water-areas&cjk=tc`. Every
combination of style parameters is a separate style variant; up to `--max-style-variants` of them are kept loaded at
the same time. The format does not count as a style variant.

With the vector engine, the server also provides:

| URL | Content |
|-----|---------|
| `/tiles/...png?theme=colorful` | theme; defaults to the `--theme` option of `serve` |
| `/themes` | available themes |
| `/vector/tiles.json` | TileJSON of the vector tiles |
| `/vector/{z}/{x}/{y}.pbf` | Shortbread vector tile (Mapbox Vector Tile) |
| `/vector/styles/{theme}.json` | MapLibre style for web or native clients, with the same parameters as the PNG tiles (e.g. `?transparent=1&textScale=1.5`) |
| `/vector/glyphs/...`, `/vector/sprites/...` | glyphs and icons the styles refer to |

The URLs in TileJSON and styles are absolute. Behind a reverse proxy, set `--public-url` (e.g.
`https://maps.example.com/osm/`) so that they point to the proxy. The glyphs do not include CJK characters; like
MapLibre GL JS does by default, clients draw them with their own fonts.

### Data directory

The first run imports the input files into a PostgreSQL database in the data directory (`--data-dir`, the
`YUKIOSM_DATA_DIR` environment variable, or a per-user directory by default). Later runs with the same input
files reuse it; a different set of input files replaces it. Without `--input`, a run uses whatever the data
directory already contains. A data directory can only be used by one process at a
time.

A city or province extract imports in minutes. Importing the full planet takes about a day and needs roughly 64 GB
of RAM and 1 TB of fast SSD storage. Every 30 seconds, the import logs its progress: how much of the input osm2pgsql
has read (with an estimate of the remaining time once it reads ways), its object counts, and the index builds and
other statements the database is running. Rendered GeoTIFFs grow quickly with the zoom level (the whole world is about
17 GB uncompressed at zoom 8), so `render` refuses images larger than `--max-pixels` (default 2³² pixels).

On Linux, run yukiosm as a regular user; PostgreSQL refuses to run as root.

### Vector engine

`--engine vector` replaces the database import with a vector tile build: Planetiler turns the input files (several
are merged first) and the bundled ocean polygons into a PMTiles archive of Shortbread vector tiles in the data
directory. Beijing and Tianjin together take under half a minute; for the planet, Planetiler's own benchmarks (with
its OpenMapTiles profile) report under an hour on 64 cores with 128 GB of memory and a few hours on 8 cores with
16 GB. The build needs free disk space of 5–10 times the input size. Like the database, the archive is reused by
later runs with the same input files and build options, and rebuilt otherwise. The build can be tuned:

- `--max-data-zoom` (default 14): the highest zoom level stored; higher zoom levels are drawn from it.
- `--schema`: a different Planetiler schema (default `assets/vector/shortbread.yml`) to change what is kept at which
  zoom level, e.g. to keep smaller features at low zoom levels.
- `--planetiler-arg key=value`: further Planetiler arguments, e.g. `--planetiler-arg min_feature_size=0.5`.
- `--import-memory`: Java heap for Planetiler in GB (default: half of the physical memory).

`import -o file.pmtiles` writes the archive to a file instead, and `--pmtiles file.pmtiles` uses such a file
(implies `--engine vector`, no input files needed).

Rendering uses the VersaTiles themes `classic` (default), `colorful`, `colorful-dark`, `gray`, `gray-dark`, `muted`,
`natural` and `toner`, selected with `--theme` or, for the server, per request. The style options work as for the
carto engine, with the layer IDs of the theme (listed by `/layers`; `*` matches any characters, e.g.
`--keep-fill "water-*"`). CJK text is shaped with the same Noto Sans CJK fonts and variants as the carto engine.

After start-up, the vector engine writes nothing: everything it caches is kept in memory, so the installation and
the data directory can be read-only. Each concurrent render uses several cores (MapLibre's tile workers and the
SwiftShader software rasterizer), so at most about a third of `--threads` renders run at the same time, and each
kept map takes 200–400 MB of memory.

A deployment that only uses the vector engine with `--pmtiles` can delete `runtime/jre` and `assets/vector/planetiler`
(only needed to build vector tiles), `runtime/pgsql`, `runtime/osm2pgsql`, `runtime/node`, `runtime/python`,
`runtime/gdal` and `assets/carto` (only needed by the carto engine) as well as `assets/external-data` (needed by both
for importing).

### Docker

Images for linux/amd64 and linux/arm64 are published to the GitHub Container Registry. The database lives in
`/data`; bind-mount a host directory there to keep it between runs and avoid importing again. The container runs as
UID 1000, so that user must be able to write to the directory.

```sh
mkdir -p /path/to/data
sudo chown 1000:1000 /path/to/data  # unless your own UID is 1000

# Tile server with a preview page at http://localhost:8080/
docker run --rm -p 8080:8080 --shm-size=1g -v /path/to/pbf:/input:ro -v /path/to/data:/data \
    ghcr.io/yuki-nagato/yukiosmrenderer serve -i /input/beijing.osm.pbf --host 0.0.0.0

# GeoTIFF on stdout, from the data imported above
docker run --rm --shm-size=1g -v /path/to/data:/data \
    ghcr.io/yuki-nagato/yukiosmrenderer render -z 12 --bbox 116.2,39.8,116.6,40.0 > beijing.tif
```

`--shm-size` gives PostgreSQL room for parallel queries and index builds; Docker's default of 64 MB is too small for
large imports.

For machines without access to the registry, every release also carries the image as an archive per architecture
(`yukiosm-<version>-docker-amd64.tar` and `yukiosm-<version>-docker-arm64.tar`). Load the one matching the
machine; it is then available as `ghcr.io/yuki-nagato/yukiosmrenderer:<version>`:

```sh
docker load -i yukiosm-<version>-docker-amd64.tar
```

## How it works

```
.osm.pbf ──► osm2pgsql (openstreetmap-carto-flex.lua) ──► PostgreSQL + PostGIS ◄── water polygons (get-external-data.py)
                                                                 │
OpenStreetMap Carto (project.mml + *.mss) ──► carto ──► Mapnik XML ──► Mapnik ──► GeoTIFF / PNG tiles
```

YukiOsmRenderer bundles all of these components and orchestrates them: it manages a private PostgreSQL
instance, imports the input files when needed (osm2pgsql, `indexes.sql`, `functions.sql`, `common-values.sql` and
the external data, which the upstream `scripts/get-external-data.py` loads with ogr2ogr from the bundled archives,
like the upstream import), compiles the style, adapts the compiled Mapnik XML to the requested
options (CJK variant, transparency) and drives Mapnik through a small native wrapper.

The vector engine:

```
.osm.pbf ──► Planetiler (shortbread.yml) ──► PMTiles ◄── water polygons
                                                │
VersaTiles style (JSON) ──► MapLibre Native + SwiftShader (Vulkan) ──► PNG tiles
                                                └──► vector tiles, styles, glyphs, sprites for clients
```

## Repository layout

| Path        | Content |
|-------------|---------|
| `src/`      | C# sources: the `yukiosm` executable and the `YukiOsmRenderer.Core` library |
| `tests/`    | NUnit tests; integration tests need a complete installation (see below) |
| `native/`   | Mapnik wrapper and build scripts of the bundled native components ([native/README.md](native/README.md)) |
| `assets/`   | Sources of the bundled assets: carto compiler package, preview page |
| `build/`    | Scripts that download the assets and package releases |
| `testdata/` | Test data (downloaded, see [testdata/README.md](testdata/README.md)) |
| `.github/`  | GitHub Actions workflows: `ci.yml` (build and unit tests), `native.yml`, `release.yml` |

## Building

Releases are built by GitHub Actions ([release.yml](.github/workflows/release.yml)) when a `v*` tag is pushed. A
local build needs the .NET SDK 10, PowerShell 7, Node.js, Docker (for the Linux runtime) and, on Windows, Visual
Studio with C++ tools:

```sh
dotnet build
dotnet test                                    # unit tests

./native/build-windows.ps1                     # or: bash native/build-linux.sh  -> artifacts/native/<rid>/runtime
(cd assets/carto-compiler && npm ci)
pwsh build/fetch-assets.ps1                    # -> artifacts/assets
pwsh build/package.ps1 -Rid win-x64            # -> artifacts/bundle/yukiosm-<version>-<rid>

# Integration tests against the bundle and the test data
YUKIOSM_HOME=artifacts/bundle/yukiosm-0.0.0-dev-win-x64 dotnet test --filter TestCategory=Integration
```

Code style is defined in [.editorconfig](.editorconfig); run `dotnet format` before committing.

## License

YukiOsmRenderer is licensed under the [GNU Affero General Public License v3.0](LICENSE). Bundled third-party
components keep their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Map data © [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), available under the
[Open Database License](https://opendatacommons.org/licenses/odbl/).
