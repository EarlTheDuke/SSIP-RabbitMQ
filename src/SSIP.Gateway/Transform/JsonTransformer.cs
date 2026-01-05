using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SSIP.Gateway.Transform;

/// <summary>
/// JSON-based data transformer implementation.
/// </summary>
public class JsonTransformer : IDataTransformer
{
    private readonly ConcurrentDictionary<string, SchemaMapping> _mappings = new();
    private readonly ISchemaMapper _schemaMapper;
    private readonly ILogger<JsonTransformer> _logger;

    public JsonTransformer(ISchemaMapper schemaMapper, ILogger<JsonTransformer> logger)
    {
        _schemaMapper = schemaMapper;
        _logger = logger;

        // Load default mappings
        LoadDefaultMappings();
    }

    public async Task<JsonDocument> TransformRequestAsync(
        JsonDocument payload,
        string sourceSchema,
        string targetSchema,
        CancellationToken ct = default)
    {
        return await TransformAsync(payload, sourceSchema, targetSchema, ct);
    }

    public async Task<JsonDocument> TransformResponseAsync(
        JsonDocument payload,
        string sourceSchema,
        string targetSchema,
        CancellationToken ct = default)
    {
        return await TransformAsync(payload, sourceSchema, targetSchema, ct);
    }

    public async Task<JsonElement> TransformElementAsync(
        JsonElement element,
        string sourceSchema,
        string targetSchema,
        CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(element.GetRawText());
        var transformed = await TransformAsync(doc, sourceSchema, targetSchema, ct);
        return transformed.RootElement.Clone();
    }

    public Task RegisterMappingAsync(SchemaMapping mapping, CancellationToken ct = default)
    {
        var key = GetMappingKey(mapping.SourceSchema, mapping.TargetSchema);
        _mappings[key] = mapping;

        _logger.LogInformation("Registered schema mapping: {Source} -> {Target}",
            mapping.SourceSchema, mapping.TargetSchema);

        return Task.CompletedTask;
    }

    public Task UnregisterMappingAsync(string sourceSchema, string targetSchema, CancellationToken ct = default)
    {
        var key = GetMappingKey(sourceSchema, targetSchema);
        _mappings.TryRemove(key, out _);

        _logger.LogInformation("Unregistered schema mapping: {Source} -> {Target}",
            sourceSchema, targetSchema);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SchemaMapping>> GetMappingsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<SchemaMapping>>(_mappings.Values.ToList());
    }

    public async Task<ValidationResult> ValidateAsync(
        JsonDocument payload,
        string schemaName,
        CancellationToken ct = default)
    {
        return await _schemaMapper.ValidateAsync(payload, schemaName, ct);
    }

    public Task<bool> HasMappingAsync(string sourceSchema, string targetSchema, CancellationToken ct = default)
    {
        var key = GetMappingKey(sourceSchema, targetSchema);
        return Task.FromResult(_mappings.ContainsKey(key));
    }

    #region Private Methods

    private async Task<JsonDocument> TransformAsync(
        JsonDocument payload,
        string sourceSchema,
        string targetSchema,
        CancellationToken ct)
    {
        var key = GetMappingKey(sourceSchema, targetSchema);

        if (!_mappings.TryGetValue(key, out var mapping))
        {
            _logger.LogDebug("No mapping found for {Source} -> {Target}, returning original",
                sourceSchema, targetSchema);
            return payload;
        }

        if (!mapping.IsActive)
        {
            return payload;
        }

        _logger.LogDebug("Transforming {Source} -> {Target}", sourceSchema, targetSchema);

        try
        {
            var sourceNode = JsonNode.Parse(payload.RootElement.GetRawText());
            var targetNode = new JsonObject();

            foreach (var (fieldName, fieldMapping) in mapping.FieldMappings)
            {
                var value = await TransformFieldAsync(sourceNode, fieldMapping, ct);
                if (value is not null)
                {
                    SetJsonValue(targetNode, fieldMapping.TargetPath, value);
                }
                else if (fieldMapping.DefaultValue is not null)
                {
                    SetJsonValue(targetNode, fieldMapping.TargetPath, 
                        JsonValue.Create(fieldMapping.DefaultValue));
                }
            }

            return JsonDocument.Parse(targetNode.ToJsonString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transforming {Source} -> {Target}",
                sourceSchema, targetSchema);
            throw;
        }
    }

    private async Task<JsonNode?> TransformFieldAsync(
        JsonNode? source,
        FieldMapping mapping,
        CancellationToken ct)
    {
        var sourceValue = GetJsonValue(source, mapping.SourcePath);
        
        if (sourceValue is null && mapping.IsRequired)
        {
            throw new InvalidOperationException(
                $"Required field missing: {mapping.SourcePath}");
        }

        if (sourceValue is null)
        {
            return null;
        }

        return mapping.Transform switch
        {
            TransformType.Direct => sourceValue.DeepClone(),
            TransformType.Constant => JsonValue.Create(mapping.TransformExpression),
            TransformType.Format => FormatValue(sourceValue, mapping.Format),
            TransformType.Map => MapValue(sourceValue, mapping.ValueMappings),
            TransformType.Lookup => await _schemaMapper.LookupValueAsync(
                sourceValue.ToString(), mapping.TransformExpression!, ct),
            TransformType.Computed => await ComputeValueAsync(source, mapping, ct),
            TransformType.Concat => ConcatValues(source, mapping.TransformExpression),
            _ => sourceValue.DeepClone()
        };
    }

    private static JsonNode? GetJsonValue(JsonNode? node, string path)
    {
        if (node is null || string.IsNullOrEmpty(path))
            return null;

        // Simple JSON path implementation: $.field.subfield
        var segments = path.TrimStart('$', '.').Split('.');
        var current = node;

        foreach (var segment in segments)
        {
            if (current is JsonObject obj)
            {
                current = obj[segment];
            }
            else if (current is JsonArray arr && int.TryParse(segment, out var index))
            {
                current = index < arr.Count ? arr[index] : null;
            }
            else
            {
                return null;
            }

            if (current is null)
                return null;
        }

        return current;
    }

    private static void SetJsonValue(JsonNode target, string path, JsonNode? value)
    {
        if (value is null)
            return;

        var segments = path.TrimStart('$', '.').Split('.');
        var current = target;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current is JsonObject obj)
            {
                if (obj[segment] is null)
                {
                    obj[segment] = new JsonObject();
                }
                current = obj[segment]!;
            }
        }

        if (current is JsonObject finalObj)
        {
            finalObj[segments[^1]] = value;
        }
    }

    private static JsonNode? FormatValue(JsonNode value, string? format)
    {
        if (string.IsNullOrEmpty(format))
            return value.DeepClone();

        var stringValue = value.ToString();

        // Date formatting
        if (DateTime.TryParse(stringValue, out var dateValue))
        {
            return JsonValue.Create(dateValue.ToString(format));
        }

        // Number formatting
        if (decimal.TryParse(stringValue, out var numValue))
        {
            return JsonValue.Create(numValue.ToString(format));
        }

        return value.DeepClone();
    }

    private static JsonNode? MapValue(JsonNode value, Dictionary<string, string>? mappings)
    {
        if (mappings is null)
            return value.DeepClone();

        var stringValue = value.ToString();
        return mappings.TryGetValue(stringValue, out var mappedValue)
            ? JsonValue.Create(mappedValue)
            : value.DeepClone();
    }

    private static JsonNode? ConcatValues(JsonNode? source, string? expression)
    {
        if (string.IsNullOrEmpty(expression))
            return null;

        // Expression format: "$.field1 + ' ' + $.field2"
        var result = expression;
        var pathPattern = @"\$\.[\w.]+";
        var matches = System.Text.RegularExpressions.Regex.Matches(expression, pathPattern);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var pathValue = GetJsonValue(source, match.Value)?.ToString() ?? "";
            result = result.Replace(match.Value, pathValue);
        }

        // Clean up expression syntax
        result = result.Replace(" + ", "").Replace("'", "");

        return JsonValue.Create(result);
    }

    private Task<JsonNode?> ComputeValueAsync(
        JsonNode? source,
        FieldMapping mapping,
        CancellationToken ct)
    {
        // Simplified expression evaluation
        // In production, use a proper expression evaluator
        if (string.IsNullOrEmpty(mapping.TransformExpression))
            return Task.FromResult<JsonNode?>(null);

        // For now, just return the expression result
        return Task.FromResult<JsonNode?>(JsonValue.Create(mapping.TransformExpression));
    }

    private static string GetMappingKey(string source, string target) => $"{source}|{target}";

    private void LoadDefaultMappings()
    {
        // ERP Project -> Dynamics Opportunity
        var erpTocrm = new SchemaMapping
        {
            MappingId = "erp-project-to-crm-opportunity",
            SourceSchema = "erp.project.v1",
            TargetSchema = "dynamics.opportunity.v1",
            Description = "Maps ERP Project to Dynamics 365 Opportunity",
            CreatedAt = DateTime.UtcNow,
            FieldMappings = new()
            {
                ["projectNumber"] = new FieldMapping
                {
                    SourcePath = "$.projectNumber",
                    TargetPath = "$.name",
                    Transform = TransformType.Direct,
                    IsRequired = true
                },
                ["description"] = new FieldMapping
                {
                    SourcePath = "$.description",
                    TargetPath = "$.description",
                    Transform = TransformType.Direct
                },
                ["estimatedValue"] = new FieldMapping
                {
                    SourcePath = "$.estimatedValue",
                    TargetPath = "$.estimatedvalue",
                    Transform = TransformType.Direct
                },
                ["status"] = new FieldMapping
                {
                    SourcePath = "$.status",
                    TargetPath = "$.statuscode",
                    Transform = TransformType.Map,
                    ValueMappings = new()
                    {
                        ["Active"] = "1",
                        ["Complete"] = "2",
                        ["OnHold"] = "3",
                        ["Cancelled"] = "4"
                    }
                },
                ["customerId"] = new FieldMapping
                {
                    SourcePath = "$.customerId",
                    TargetPath = "$.customerid",
                    Transform = TransformType.Lookup,
                    TransformExpression = "erp_customer_to_crm_account"
                }
            }
        };

        _ = RegisterMappingAsync(erpTocrm);

        _logger.LogInformation("Loaded {Count} default schema mappings", 1);
    }

    #endregion
}

