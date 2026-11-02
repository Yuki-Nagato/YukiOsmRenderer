using System.Globalization;
using System.Text;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>Relative path of a tile file with the placeholders {z}, {x} and {y}, e.g. "z{z}/y{y}/x{x}.png".</summary>
    public sealed class TilePathTemplate {
        // Literal text, or a placeholder when Placeholder is 'z', 'x' or 'y'.
        readonly (string Literal, char Placeholder)[] parts;

        TilePathTemplate(string text, (string, char)[] parts) {
            Text = text;
            this.parts = parts;
        }

        public static TilePathTemplate Default { get; } = Parse("z{z}/y{y}/x{x}.png");

        public string Text { get; }

        public bool HasZoom => parts.Any(part => part.Placeholder == 'z');

        /// <exception cref="FormatException">The template is not a relative file path with {x} and {y}.</exception>
        public static TilePathTemplate Parse(string text) {
            ArgumentNullException.ThrowIfNull(text);
            if (Path.IsPathRooted(text)) {
                throw new FormatException("The template must be a path relative to the output directory.");
            }
            if (text.Split('/', '\\').Contains("..")) {
                throw new FormatException("The template must not leave the output directory with '..'.");
            }
            if (Path.GetFileName(text).Length == 0) {
                throw new FormatException("The template must end with a file name.");
            }
            var parts = new List<(string, char)>();
            var literal = new StringBuilder();
            for (var i = 0; i < text.Length; i++) {
                if (text[i] == '{') {
                    var end = text.IndexOf('}', i + 1);
                    if (end < 0) {
                        throw new FormatException("Unclosed '{'.");
                    }
                    var name = text[(i + 1)..end];
                    if (name is not ("z" or "x" or "y")) {
                        throw new FormatException($"Unknown placeholder '{{{name}}}'; use {{z}}, {{x}} and {{y}}.");
                    }
                    if (literal.Length > 0) {
                        parts.Add((literal.ToString(), '\0'));
                        literal.Clear();
                    }
                    parts.Add(("", name[0]));
                    i = end;
                }
                else if (text[i] == '}') {
                    throw new FormatException("Unmatched '}'.");
                }
                else {
                    literal.Append(text[i]);
                }
            }
            if (literal.Length > 0) {
                parts.Add((literal.ToString(), '\0'));
            }
            if (!parts.Any(part => part.Item2 == 'x') || !parts.Any(part => part.Item2 == 'y')) {
                throw new FormatException("The template must contain {x} and {y}.");
            }
            return new TilePathTemplate(text, [.. parts]);
        }

        /// <summary>Relative path of a tile.</summary>
        public string Format(int zoom, long x, long y) {
            var builder = new StringBuilder(Text.Length + 16);
            foreach (var (literal, placeholder) in parts) {
                builder.Append(placeholder switch {
                    'z' => zoom.ToString(CultureInfo.InvariantCulture),
                    'x' => x.ToString(CultureInfo.InvariantCulture),
                    'y' => y.ToString(CultureInfo.InvariantCulture),
                    _ => literal,
                });
            }
            return builder.ToString();
        }

        public override string ToString() {
            return Text;
        }
    }
}
