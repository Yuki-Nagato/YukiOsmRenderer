# Third-party notices

YukiOsmRenderer itself is licensed under the [GNU Affero General Public License v3.0](LICENSE). Release packages
bundle the following third-party components, which remain under their own licenses. Their license texts are
included in the packages (`runtime/licenses/`, `runtime/jre/legal/`, the `*.dist-info` directories in the
`site-packages` of `runtime/python/`, `assets/fonts/licenses/`, `assets/carto/LICENSE.txt`, `assets/vector/licenses/`,
`assets/web/leaflet/LICENSE` and the `node_modules` packages of `assets/carto-compiler/`).

| Component | License | Source |
|-----------|---------|--------|
| PostgreSQL | PostgreSQL License | https://www.postgresql.org/ |
| PostGIS | GPL-2.0-or-later | https://postgis.net/ |
| GEOS, PROJ, json-c, libxml2 and other PostGIS dependencies | LGPL-2.1 / MIT / various | see `runtime/licenses/` |
| osm2pgsql | GPL-2.0-or-later | https://osm2pgsql.org/ |
| Mapnik | LGPL-2.1-or-later | https://mapnik.org/ |
| Boost, ICU, HarfBuzz, FreeType, libpng, zlib and other Mapnik dependencies | various permissive licenses | see `runtime/licenses/` |
| Node.js | MIT | https://nodejs.org/ |
| GDAL (ogr2ogr) | MIT | https://gdal.org/ |
| PROJ, libtiff, libgeotiff, libpq, OpenSSL and other GDAL dependencies | various permissive licenses | see `runtime/licenses/` |
| Python (python-build-standalone) and the libraries it bundles | PSF-2.0 / various | https://github.com/astral-sh/python-build-standalone |
| psycopg2 (psycopg2-binary, with libpq and OpenSSL) | LGPL-3.0-or-later | https://www.psycopg.org/ |
| PyYAML, requests, urllib3, idna, certifi, charset-normalizer | MIT / Apache-2.0 / BSD-3-Clause / MPL-2.0 | https://pypi.org/ |
| MapLibre Native (patched) and the libraries it bundles | BSD-2-Clause / various permissive licenses | https://github.com/maplibre/maplibre-native |
| curl, libuv, libjpeg-turbo, libwebp and other MapLibre Native dependencies | various permissive licenses | see `runtime/licenses/` |
| SwiftShader, with LLVM, marl, SPIRV-Tools and SPIRV-Headers | Apache-2.0 / Apache-2.0 WITH LLVM-exception / MIT | https://github.com/google/swiftshader |
| Eclipse Temurin (OpenJDK) Java runtime | GPL-2.0 WITH Classpath-exception-2.0 and others, see `runtime/jre/legal/` | https://adoptium.net/ |
| Planetiler | Apache-2.0 | https://github.com/onthegomap/planetiler |
| Shortbread schema for Planetiler | Apache-2.0 | https://github.com/onthegomap/planetiler |
| VersaTiles styles, icons and Noto Sans glyphs | MIT / SIL Open Font License 1.1 | https://github.com/versatiles-org |
| carto (CartoCSS compiler) and its npm dependencies | Apache-2.0 / MIT / various | https://github.com/cartocss/carto |
| OpenStreetMap Carto | CC0-1.0 | https://github.com/openstreetmap-carto/openstreetmap-carto |
| Noto fonts, Noto Sans CJK, Noto Emoji | SIL Open Font License 1.1 | https://notofonts.github.io/ |
| Hanazono fonts | SIL Open Font License 1.1 / Hanazono Font License | https://fonts.jp/hanazono/ |
| Leaflet | BSD-2-Clause | https://leafletjs.com/ |
| .NET runtime and ASP.NET Core | MIT | https://dotnet.microsoft.com/ |
| Npgsql | PostgreSQL License | https://www.npgsql.org/ |
| YamlDotNet | MIT | https://github.com/aaubry/YamlDotNet |
| System.CommandLine | MIT | https://github.com/dotnet/command-line-api |

## Data

The bundled water polygons and Antarctic ice sheet data are derived from OpenStreetMap by
[osmdata.openstreetmap.de](https://osmdata.openstreetmap.de/) and, like all map data rendered by YukiOsmRenderer, are
© OpenStreetMap contributors and available under the [Open Database License](https://opendatacommons.org/licenses/odbl/).
Maps rendered from OpenStreetMap data must carry the attribution "© OpenStreetMap contributors".

## Source code of GPL and LGPL components

The bundled GPL and LGPL components are unmodified releases built by the scripts in [`native/`](native/), which
document the exact versions and download locations of their source code. The Java runtime is the unmodified
Eclipse Temurin binary release, psycopg2 the unmodified psycopg2-binary wheel from PyPI (listed with the other
Python packages in [`native/python-wheels.txt`](native/python-wheels.txt)). MapLibre Native is built with the
patches in [`native/yukimaplibre/patches`](native/yukimaplibre/patches).
