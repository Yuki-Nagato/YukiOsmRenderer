// Writes the bundled vector styles: VersaTiles styles for the Shortbread schema, with their resource URLs replaced by
// yukiosm:// placeholders that the renderer resolves at run time (see VectorStyle.AssetPlaceholder).
// Usage: node vector-styles.cjs <versatiles-style.js> <output directory> <theme>...
// Prints the font stacks the styles use, one per line.
"use strict";
const fs = require("fs");
const path = require("path");

const [library, output, ...themes] = process.argv.slice(2);
if (!library || !output || themes.length === 0) {
    console.error("usage: node vector-styles.cjs <versatiles-style.js> <output directory> <theme>...");
    process.exit(2);
}
const { osm } = require(path.resolve(library));
fs.mkdirSync(output, { recursive: true });

const fonts = new Set();
function collectFonts(value) {
    if (Array.isArray(value)) {
        value.forEach(collectFonts);
    } else if (typeof value === "string" && /^[a-z0-9_]+$/.test(value) && !["literal", "get", "case", "match", "coalesce", "step", "zoom"].includes(value)) {
        fonts.add(value);
    }
}

for (const theme of themes) {
    const style = osm({ theme, projection: "mercator", sky: false });
    for (const [id, source] of Object.entries(style.sources)) {
        if (source.type === "vector") {
            style.sources[id] = { type: "vector", url: "yukiosm://tiles" };
        }
    }
    style.glyphs = "yukiosm://glyphs/{fontstack}/{range}.pbf";
    style.sprite = (Array.isArray(style.sprite) ? style.sprite : [{ id: "base" }]).map(({ id }) => ({ id, url: "yukiosm://sprites/" + id }));
    delete style.sky;
    delete style.terrain;
    style.name = theme;
    for (const layer of style.layers) {
        collectFonts(layer.layout && layer.layout["text-font"]);
    }
    fs.writeFileSync(path.join(output, theme + ".json"), JSON.stringify(style));
}
console.log([...fonts].sort().join("\n"));
