# YukiOsmRenderer

Self-contained OpenStreetMap raster renderer that reproduces the standard map layer of
[openstreetmap.org](https://www.openstreetmap.org) ([OpenStreetMap Carto](https://github.com/openstreetmap-carto/openstreetmap-carto))
from `.osm.pbf` files, fully offline.

> **Status:** work in progress.

## Features

- **Two modes in one executable**
  - `render` – renders an area at a given zoom level into a GeoTIFF (stdout by default, or a file).
  - `serve` – serves Web Mercator XYZ tiles over HTTP, with an offline preview page.
- **Same rendering pipeline as openstreetmap.org**: osm2pgsql (flex output) → PostGIS → carto → Mapnik, using the
  original OpenStreetMap Carto style, the same Mapnik API level (`carto -a 3.0.22`), the same 8 × 8 metatiles and
  buffer size as renderd, and the same tile encoding (`png256:z=5`).
- **Multiple input files**: any number of `.osm.pbf` files can be combined.
- **CJK glyph variants**: Noto Sans CJK SC (default), TC, HK, JP or KR, chosen with `--cjk` or, for the tile
  server, per request. Like OpenStreetMap Carto's `scripts/change-fonts-cjk.sh`, the variant selects the CJK font;
  labels are also shaped with the matching language (`ja`, `ko`, `zh-hant`, `zh-hk`), because Mapnik shapes Han text
  as Simplified Chinese by default. The default `sc` therefore looks exactly like tile.openstreetmap.org.
- **Transparent rendering** for overlaying on imagery: the background and all area fills and patterns are dropped,
  while roads, lines, labels and icons are kept. Fills of selected layers can be kept, area outlines can be hidden
  and whole layers can be left out.
- **Fully offline**: everything except the `.osm.pbf` input (PostgreSQL, PostGIS, osm2pgsql, Mapnik, Node.js and
  carto, the style, fonts, icons and water polygons) ships with the release.
- **Platforms**: Windows x64, Linux x64 and Linux arm64 (portable archive requiring only glibc ≥ 2.31, or Docker image).

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

# Tile server with a preview page at http://localhost:8080/
yukiosm serve -i beijing.osm.pbf -i tianjin.osm.pbf --port 8080
```

Run `yukiosm render --help` and `yukiosm serve --help` for all options.

### Tile server

| URL | Content |
|-----|---------|
| `/tiles/{z}/{x}/{y}.png` | 256 × 256 tile |
| `/tiles/{z}/{x}/{y}@2x.png` | 512 × 512 tile of the same area (high DPI) |
| `/tiles/...png?transparent=1` | transparent rendering |
| `/tiles/...png?transparent=1&keepFill=water-areas,buildings&hideOutline=1` | keep some fills, hide area outlines |
| `/tiles/...png?hideLayers=buildings,amenity-points` | leave out layers |
| `/tiles/...png?cjk=jp` | CJK glyph variant (`sc`, `tc`, `hk`, `jp`, `kr`); defaults to the `--cjk` option of `serve` |
| `/layers` | layer names usable in `keepFill` and `hideLayers` |
| `/` | preview page |

Parameters can be combined, e.g. `/tiles/{z}/{x}/{y}@2x.png?transparent=1&keepFill=water-areas&cjk=tc`. Every
combination is a separate style variant; up to `--max-style-variants` of them are kept loaded at the same time.

### Data directory

The first run imports the input files into a PostgreSQL database in the data directory (`--data-dir`, the
`YUKIOSM_DATA_DIR` environment variable, or a per-user directory by default). Later runs with the same input
files reuse it; a different set of input files replaces it. A data directory can only be used by one process at a
time.

A city or province extract imports in minutes. Importing the full planet takes about a day and needs roughly 64 GB
of RAM and 1 TB of fast SSD storage. Rendered GeoTIFFs grow quickly with the zoom level (the whole world is about
17 GB uncompressed at zoom 8), so `render` refuses images larger than `--max-pixels` (default 2³² pixels).

On Linux, run yukiosm as a regular user; PostgreSQL refuses to run as root.

### Docker

```sh
docker run --rm -p 8080:8080 -v /path/to/pbf:/input:ro -v yukiosm-data:/data \
    ghcr.io/yuki-nagato/yukiosmrenderer serve -i /input/beijing.osm.pbf --host 0.0.0.0
```

## How it works

```
.osm.pbf ──► osm2pgsql (openstreetmap-carto-flex.lua) ──► PostgreSQL + PostGIS ◄── water polygons (pg_dump)
                                                                 │
OpenStreetMap Carto (project.mml + *.mss) ──► carto ──► Mapnik XML ──► Mapnik ──► GeoTIFF / PNG tiles
```

YukiOsmRenderer bundles all of these components and orchestrates them: it manages a private PostgreSQL
instance, imports the input files when needed (osm2pgsql, `indexes.sql`, `functions.sql`, `common-values.sql` and
the external data, like the upstream import), compiles the style, adapts the compiled Mapnik XML to the requested
options (CJK variant, transparency) and drives Mapnik through a small native wrapper.

## Repository layout

| Path        | Content |
|-------------|---------|
| `src/`      | C# sources: the `yukiosm` executable and the `YukiOsmRenderer.Core` library |
| `tests/`    | NUnit tests; integration tests need a complete installation (see below) |
| `native/`   | Mapnik wrapper and build scripts of the bundled native components ([native/README.md](native/README.md)) |
| `assets/`   | Sources of the bundled assets: carto compiler package, preview page |
| `build/`    | Scripts that download the assets, build the external data and package releases |
| `testdata/` | Test data (downloaded, see [testdata/README.md](testdata/README.md)) |
| `.github/`  | GitHub Actions workflows: `ci.yml` (build and unit tests), `native.yml`, `release.yml` |

## Building

Releases are built by GitHub Actions ([release.yml](.github/workflows/release.yml)) when a `v*` tag is pushed. A
local build needs the .NET SDK 10, PowerShell 7, Node.js, Docker and, on Windows, Visual Studio with C++ tools:

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
