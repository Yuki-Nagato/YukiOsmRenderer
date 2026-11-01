using System.Text.Json.Serialization;
using YukiOsmRenderer.Core.Import;
using YukiOsmRenderer.Core.Runtime;

namespace YukiOsmRenderer.Core {
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AssetManifest))]
    [JsonSerializable(typeof(ImportState))]
    internal sealed partial class CoreJsonContext : JsonSerializerContext {
    }
}
