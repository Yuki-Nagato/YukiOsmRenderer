using System.Text.Json.Serialization;
using YukiOsmRenderer.Core.Import;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Core {
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AssetManifest))]
    [JsonSerializable(typeof(ImportState))]
    [JsonSerializable(typeof(VectorBuildState))]
    internal sealed partial class CoreJsonContext : JsonSerializerContext {
    }
}
