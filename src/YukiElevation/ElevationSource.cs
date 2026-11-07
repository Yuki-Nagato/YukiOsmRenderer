namespace YukiElevation {
    internal enum Interpolation {
        Nearest,
        Bilinear,
    }

    /// <summary>Elevations in metres from one data set.</summary>
    internal abstract class ElevationSource : IDisposable {
        public abstract string Name { get; }

        /// <summary>Whether 0 marks the sea surface, below which a lower-priority source may know the depth.</summary>
        public virtual bool ZeroMeansSea => false;

        /// <summary>The elevation at a point, or null where the source has no data.</summary>
        public abstract double? Sample(double longitude, double latitude, Interpolation interpolation);

        public abstract void Dispose();

        /// <summary>
        /// Combines the pixels (x0, y0), (x0 + 1, y0), (x0, y0 + 1), (x0 + 1, y0 + 1) around a point at the fractions
        /// <paramref name="tx"/>, <paramref name="ty"/> between them. Pixels without data (NaN) are left out of the
        /// bilinear interpolation; there is no value if the pixel containing the point has none.
        /// </summary>
        protected static double? Combine(ReadOnlySpan<double> values, double tx, double ty, Interpolation interpolation) {
            var nearest = Nearest(values, tx, ty);
            if (nearest == null || interpolation == Interpolation.Nearest) {
                return nearest;
            }
            double sum = 0, weight = 0;
            for (var i = 0; i < 4; i++) {
                if (double.IsNaN(values[i])) {
                    continue;
                }
                var w = ((i & 1) != 0 ? tx : 1 - tx) * ((i & 2) != 0 ? ty : 1 - ty);
                sum += w * values[i];
                weight += w;
            }
            return sum / weight;
        }

        /// <summary>The value of the pixel containing the point; on an edge between pixels, of one with data.</summary>
        static double? Nearest(ReadOnlySpan<double> values, double tx, double ty) {
            // Origins like 89.99999999999999 must not move a point on the outer edge out of its pixel.
            const double Half = 0.5 + 1e-6;
            double? best = null;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < 4; i++) {
                var dx = Math.Abs(tx - (i & 1));
                var dy = Math.Abs(ty - (i >> 1));
                if (double.IsNaN(values[i]) || dx > Half || dy > Half || dx * dx + dy * dy >= bestDistance) {
                    continue;
                }
                best = values[i];
                bestDistance = dx * dx + dy * dy;
            }
            return best;
        }
    }
}
