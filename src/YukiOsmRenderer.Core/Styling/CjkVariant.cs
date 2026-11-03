using System.Text.RegularExpressions;

namespace YukiOsmRenderer.Core.Styling {
    /// <summary>Regional glyph variant of Noto Sans CJK used for Han characters.</summary>
    public enum CjkVariant {
        SimplifiedChinese,
        TraditionalChinese,
        HongKong,
        Japanese,
        Korean,
    }

    public static partial class CjkVariants {
        public static readonly IReadOnlyList<string> Names = ["sc", "tc", "hk", "jp", "kr"];

        /// <summary>Region suffix used in the font family name, e.g. "SC" in "Noto Sans CJK SC".</summary>
        public static string FontSuffix(this CjkVariant variant) {
            return variant switch {
                CjkVariant.SimplifiedChinese => "SC",
                CjkVariant.TraditionalChinese => "TC",
                CjkVariant.HongKong => "HK",
                CjkVariant.Japanese => "JP",
                CjkVariant.Korean => "KR",
                _ => throw new ArgumentOutOfRangeException(nameof(variant)),
            };
        }

        public static string ShortName(this CjkVariant variant) {
            return variant.FontSuffix().ToLowerInvariant();
        }

        /// <summary>Index of the variant's face in NotoSansCJK-Regular.ttc and NotoSansCJK-Bold.ttc.</summary>
        public static int CollectionIndex(this CjkVariant variant) {
            return variant switch {
                CjkVariant.Japanese => 0,
                CjkVariant.Korean => 1,
                CjkVariant.SimplifiedChinese => 2,
                CjkVariant.TraditionalChinese => 3,
                CjkVariant.HongKong => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(variant)),
            };
        }

        /// <summary>
        /// Language used by HarfBuzz to shape labels. Noto Sans CJK selects regional glyph forms from it (OpenType
        /// 'locl'), regardless of the regional face. Null means Mapnik's default, which is zh-Hans for Han text, as
        /// on tile.openstreetmap.org.
        /// </summary>
        public static string? ShapingLanguage(this CjkVariant variant) {
            return variant switch {
                CjkVariant.SimplifiedChinese => null,
                CjkVariant.TraditionalChinese => "zh-hant",
                CjkVariant.HongKong => "zh-hk",
                CjkVariant.Japanese => "ja",
                CjkVariant.Korean => "ko",
                _ => throw new ArgumentOutOfRangeException(nameof(variant)),
            };
        }

        /// <summary>Parses a short name (sc, tc, hk, jp, kr) or a language tag (zh-Hans, zh-Hant, zh-Hant-HK, ja, ko).</summary>
        public static bool TryParse(string? text, out CjkVariant variant) {
            switch (text?.Trim().ToLowerInvariant()) {
                case "sc" or "zh-hans" or "zh-cn":
                    variant = CjkVariant.SimplifiedChinese;
                    return true;
                case "tc" or "zh-hant" or "zh-hant-tw" or "zh-tw":
                    variant = CjkVariant.TraditionalChinese;
                    return true;
                case "hk" or "zh-hant-hk" or "zh-hk":
                    variant = CjkVariant.HongKong;
                    return true;
                case "jp" or "ja":
                    variant = CjkVariant.Japanese;
                    return true;
                case "kr" or "ko":
                    variant = CjkVariant.Korean;
                    return true;
                default:
                    variant = default;
                    return false;
            }
        }

        public static CjkVariant Parse(string text) {
            return TryParse(text, out var variant)
                ? variant
                : throw new FormatException($"Unknown CJK variant '{text}'. Expected one of: {string.Join(", ", Names)}.");
        }

        /// <summary>
        /// Switches the CJK font names of OpenStreetMap Carto (e.g. "Noto Sans CJK JP Regular") to the given variant, the
        /// same way as the upstream scripts/change-fonts-cjk.sh does in style/fonts.mss.
        /// </summary>
        public static string ApplyToFontNames(string text, CjkVariant variant) {
            return CjkFontPattern().Replace(text, "Noto Sans CJK " + variant.FontSuffix());
        }

        [GeneratedRegex("Noto Sans CJK (JP|KR|SC|TC|HK)")]
        private static partial Regex CjkFontPattern();
    }
}
