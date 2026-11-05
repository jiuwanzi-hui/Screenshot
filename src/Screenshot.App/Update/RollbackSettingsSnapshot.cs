using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Screenshot.App.Core;

namespace Screenshot.App.Update;

// Old enum converters reject a new name (e.g. AspectRatio) and reset ALL
// settings. Numeric enum values remain readable; old Normalize methods discard
// unsupported values individually. Keep original bytes for returning to newer versions.
internal sealed record RollbackSettingsSnapshot(string SettingsPath, string CompatibleJson)
{
    internal static RollbackSettingsSnapshot? Create(string targetDirectory, Version currentVersion, Version targetVersion)
    {
        static Version Release(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
        if (Release(targetVersion) >= Release(currentVersion)) return null;
        var data = Path.Combine(targetDirectory, AppMetadata.DataDirectoryName);
        var settings = Path.Combine(data, "settings.json");
        if (!File.Exists(settings)) return null;
        var original = File.ReadAllBytes(settings);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(original).TrimStart('\uFEFF')) as JsonObject
            ?? throw new InvalidDataException("无法读取回退前的设置，已停止回退。");
        ConvertEnumNames(json, typeof(AppSettings));
        var compatible = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        // Outside the disposable update cache. Do not copy multi-GB models:
        // program replacement excludes both user-data directories entirely.
        var backup = Path.Combine(data, "VersionBackups",
            $"{currentVersion}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, "settings.json"), original);
        return new(settings, compatible);
    }

    internal void Apply()
    {
        var temporary = SettingsPath + ".rollback-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, CompatibleJson, new UTF8Encoding(false));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static JsonNode? ConvertEnumNames(JsonNode? node, Type type)
    {
        if (node is null) return null;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum && node is JsonValue value && value.TryGetValue<string>(out var name))
            return Enum.TryParse(type, name, ignoreCase: true, out var parsed)
                ? JsonValue.Create(Convert.ToInt64(parsed, CultureInfo.InvariantCulture)) : node;
        if (node is JsonArray array)
        {
            var element = type.IsArray ? type.GetElementType() :
                type.GetInterfaces().Append(type).FirstOrDefault(candidate => candidate.IsGenericType &&
                    candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
            if (element is not null)
                for (var i = 0; i < array.Count; i++)
                {
                    var converted = ConvertEnumNames(array[i], element);
                    if (!ReferenceEquals(array[i], converted)) array[i] = converted;
                }
        }
        else if (node is JsonObject obj)
        {
            var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(candidate => candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            if (dictionary is not null)
            {
                var arguments = dictionary.GetGenericArguments();
                foreach (var property in obj.ToArray())
                {
                    var key = property.Key;
                    if (arguments[0].IsEnum && Enum.TryParse(arguments[0], key, true, out var parsed))
                        key = Convert.ToInt64(parsed, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    var converted = ConvertEnumNames(property.Value, arguments[1]);
                    if (key == property.Key && ReferenceEquals(converted, property.Value)) continue;
                    obj.Remove(property.Key);
                    obj[key] = converted;
                }
            }
            else
            {
                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    var key = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                    if (!obj.TryGetPropertyValue(key, out var child)) continue;
                    var converted = ConvertEnumNames(child, property.PropertyType);
                    if (!ReferenceEquals(child, converted)) obj[key] = converted;
                }
            }
        }
        return node;
    }
}
