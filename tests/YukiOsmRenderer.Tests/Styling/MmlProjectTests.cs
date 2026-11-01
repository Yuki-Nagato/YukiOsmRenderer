using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Tests.Styling {
    public class MmlProjectTests {
        // Shaped like OpenStreetMap Carto's project.mml, including anchors and merge keys.
        internal const string SampleProject = """
			_parts:
			  extents: &extents
			    extent: [-180, -85, 180, 85]
			  osm2pgsql: &osm2pgsql
			    type: "postgis"
			    dbname: "gis"
			Stylesheet:
			  - style/style.mss
			Layer:
			  - id: landcover
			    geometry: polygon
			    <<: *extents
			    Datasource:
			      <<: *osm2pgsql
			      table: |-
			        (SELECT way FROM planet_osm_polygon) AS landcover
			  - id: water-areas
			    geometry: polygon
			    Datasource:
			      <<: *osm2pgsql
			      table: (SELECT way FROM planet_osm_polygon) AS water_areas
			  - id: roads-fill
			    geometry: linestring
			    Datasource:
			      <<: *osm2pgsql
			      table: (SELECT way FROM planet_osm_line) AS roads
			  - id: amenity-points
			    geometry: point
			    Datasource:
			      <<: *osm2pgsql
			      table: (SELECT way FROM planet_osm_point) AS points
			  - id: buildings
			    geometry: polygon
			    Datasource:
			      <<: *osm2pgsql
			      table: (SELECT way FROM planet_osm_polygon) AS buildings
			""";

        internal static MmlProject Sample => MmlProject.Parse(new StringReader(SampleProject));

        [Test]
        public void ReadsLayersInOrder() {
            var project = Sample;
            Assert.Multiple(() => {
                Assert.That(project.Layers.Select(l => l.Id), Is.EqualTo(new[] { "landcover", "water-areas", "roads-fill", "amenity-points", "buildings" }));
                Assert.That(project.GeometryOf("landcover"), Is.EqualTo(LayerGeometry.Polygon));
                Assert.That(project.GeometryOf("roads-fill"), Is.EqualTo(LayerGeometry.LineString));
                Assert.That(project.GeometryOf("amenity-points"), Is.EqualTo(LayerGeometry.Point));
                Assert.That(project.GeometryOf("missing"), Is.EqualTo(LayerGeometry.Unknown));
                Assert.That(project.LayerIds, Does.Contain("buildings"));
            });
        }

        [Test]
        public void RejectsDocumentsWithoutLayers() {
            Assert.That(() => MmlProject.Parse(new StringReader("name: test\n")), Throws.TypeOf<InvalidDataException>());
        }
    }
}
