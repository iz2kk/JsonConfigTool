using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConfigTool.Models;

public sealed class JsonEditorNode
{
    public string ClientId { get; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = string.Empty;
    public string Kind { get; set; } = "string";
    public string? Value { get; set; } = string.Empty;
    public bool UseTextarea { get; set; }
    public bool IsCollapsed { get; set; }
    public int BulkCount { get; set; } = 1;
    public int InsertIndex { get; set; } = -1;
    public int DesiredArrayCount { get; set; }
    public string QuickInput { get; set; } = string.Empty;
    public string QuickInputKind { get; set; } = "string";
    public List<JsonEditorNode> Children { get; } = [];
}

public static class JsonEditorNodeFactory
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonEditorNode CreateRootFromEditText(string? key, string? expectedKind, string? editText)
    {
        var kind = NormalizeKind(expectedKind);
        if (kind is "object" or "array" or "json")
        {
            var text = editText?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    var node = JsonNode.Parse(text);
                    if (kind == "object" && node is not JsonObject)
                    {
                        return CreateDefaultNode(key, "object");
                    }

                    if (kind == "array" && node is not JsonArray)
                    {
                        return CreateDefaultNode(key, "array");
                    }

                    return FromJsonNode(key, node);
                }
                catch
                {
                    return CreateDefaultNode(key, kind == "json" ? "object" : kind);
                }
            }
        }

        return CreateDefaultNode(key, kind);
    }

    public static JsonEditorNode FromJsonNode(string? key, JsonNode? node)
    {
        if (node is null)
        {
            return new JsonEditorNode
            {
                Key = key ?? string.Empty,
                Kind = "null",
                Value = null
            };
        }

        if (node is JsonObject obj)
        {
            var editor = new JsonEditorNode
            {
                Key = key ?? string.Empty,
                Kind = "object"
            };

            foreach (var property in obj)
            {
                editor.Children.Add(FromJsonNode(property.Key, property.Value));
            }

            return editor;
        }

        if (node is JsonArray array)
        {
            var editor = new JsonEditorNode
            {
                Key = key ?? string.Empty,
                Kind = "array"
            };

            foreach (var item in array)
            {
                editor.Children.Add(FromJsonNode(string.Empty, item));
            }

            return editor;
        }

        if (node is JsonValue value)
        {
            var text = ValueToEditText(value);
            return new JsonEditorNode
            {
                Key = key ?? string.Empty,
                Kind = GetValueKind(value),
                Value = text,
                UseTextarea = ShouldUseTextarea(text)
            };
        }

        return new JsonEditorNode
        {
            Key = key ?? string.Empty,
            Kind = "json",
            Value = node.ToJsonString(PrettyJsonOptions),
            UseTextarea = true
        };
    }

    public static JsonEditorNode CreateDefaultNode(string? key, string? kind)
    {
        var normalized = NormalizeKind(kind);
        return new JsonEditorNode
        {
            Key = key ?? string.Empty,
            Kind = normalized,
            Value = DefaultValue(normalized),
            UseTextarea = normalized is "json" or "object" or "array"
        };
    }

    public static JsonEditorNode CreateDefaultChild(bool parentIsArray, IReadOnlyCollection<JsonEditorNode>? siblings = null)
    {
        return new JsonEditorNode
        {
            Key = parentIsArray ? string.Empty : MakeUniqueKey(siblings ?? [], "newKey"),
            Kind = "string",
            Value = string.Empty
        };
    }

    public static string BuildJsonText(JsonEditorNode node)
    {
        var jsonNode = BuildJsonNode(node, isRoot: true);
        return jsonNode?.ToJsonString(PrettyJsonOptions) ?? "null";
    }

    public static JsonNode? BuildJsonNode(JsonEditorNode node, bool isRoot = false)
    {
        var kind = NormalizeKind(node.Kind);
        return kind switch
        {
            "object" => BuildObjectNode(node),
            "array" => BuildArrayNode(node),
            "json" => string.IsNullOrWhiteSpace(node.Value) ? null : JsonNode.Parse(node.Value),
            "null" => null,
            "bool" => ParseBool(node.Value, node.Key),
            "number" => ParseNumber(node.Value, node.Key),
            _ => JsonValue.Create(node.Value ?? string.Empty)
        };
    }

    public static string NormalizeKind(string? kind)
        => kind?.Trim().ToLowerInvariant() switch
        {
            "boolean" => "bool",
            "bool" => "bool",
            "number" => "number",
            "object" => "object",
            "array" => "array",
            "json" => "json",
            "null" => "null",
            _ => "string"
        };

    public static string DefaultValue(string? kind)
        => NormalizeKind(kind) switch
        {
            "bool" => "false",
            "number" => "0",
            "object" => "{}",
            "array" => "[]",
            "json" => "{}",
            "null" => null!,
            _ => string.Empty
        };

    public static bool IsContainer(string? kind)
    {
        var normalized = NormalizeKind(kind);
        return normalized is "object" or "array";
    }

    public static bool ShouldUseTextarea(string? value)
        => !string.IsNullOrEmpty(value) && (value.Length > 96 || value.Contains('\n') || value.Contains('\r'));

    private static JsonObject BuildObjectNode(JsonEditorNode node)
    {
        var obj = new JsonObject();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in node.Children)
        {
            var key = child.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Object đang có field/key rỗng. Hãy nhập tên key trước khi lưu.");
            }

            if (!keys.Add(key))
            {
                throw new InvalidOperationException($"Object đang bị trùng key '{key}'. Mỗi key trong cùng một block cha chỉ được xuất hiện một lần.");
            }

            obj[key] = BuildJsonNode(child);
        }

        return obj;
    }

    private static JsonArray BuildArrayNode(JsonEditorNode node)
    {
        var array = new JsonArray();
        foreach (var child in node.Children)
        {
            array.Add(BuildJsonNode(child));
        }

        return array;
    }

    private static JsonNode ParseBool(string? value, string key)
    {
        if (bool.TryParse(value, out var boolValue))
        {
            return JsonValue.Create(boolValue);
        }

        throw new FormatException($"Key '{key}' phải là true/false.");
    }

    private static JsonNode ParseNumber(string? value, string key)
    {
        value ??= string.Empty;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return JsonValue.Create(longValue);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return JsonValue.Create(doubleValue);
        }

        throw new FormatException($"Key '{key}' phải là số hợp lệ, dùng dấu chấm cho phần thập phân.");
    }

    private static string MakeUniqueKey(IReadOnlyCollection<JsonEditorNode> siblings, string baseName)
    {
        if (!siblings.Any(x => string.Equals(x.Key, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        var index = 1;
        string candidate;
        do
        {
            candidate = baseName + index.ToString(CultureInfo.InvariantCulture);
            index++;
        } while (siblings.Any(x => string.Equals(x.Key, candidate, StringComparison.OrdinalIgnoreCase)));

        return candidate;
    }

    private static string GetValueKind(JsonValue value)
    {
        if (value.TryGetValue<bool>(out _))
        {
            return "bool";
        }

        if (value.TryGetValue<int>(out _)
            || value.TryGetValue<long>(out _)
            || value.TryGetValue<float>(out _)
            || value.TryGetValue<double>(out _)
            || value.TryGetValue<decimal>(out _))
        {
            return "number";
        }

        return "string";
    }

    private static string ValueToEditText(JsonValue value)
    {
        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue ? "true" : "false";
        }

        return value.ToJsonString();
    }
}
