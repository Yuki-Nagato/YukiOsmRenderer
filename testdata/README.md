# Test data

Integration tests use small OpenStreetMap extracts from [Geofabrik](https://download.geofabrik.de/asia/china.html).
The `.osm.pbf` files are not committed to the repository; download them before running the tests:

```sh
curl -fL -o testdata/beijing-latest.osm.pbf https://download.geofabrik.de/asia/china/beijing-latest.osm.pbf
curl -fL -o testdata/tianjin-latest.osm.pbf https://download.geofabrik.de/asia/china/tianjin-latest.osm.pbf
```

Tests locate the files with the patterns `beijing-*.osm.pbf` and `tianjin-*.osm.pbf`, so dated file names
such as `beijing-261031.osm.pbf` work as well.

The data is © OpenStreetMap contributors and available under the
[Open Database License](https://opendatacommons.org/licenses/odbl/).
