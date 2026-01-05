using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Distributed;

namespace SSIP.Gateway.Transform;

/// <summary>
/// Interface for schema validation and lookup operations.
/// </summary>
public interface ISchemaMapper
{
    /// <summary>
    /// Validates a JSON document against a schema.
    /// </summary>
    Task<ValidationResult> ValidateAsync(JsonDocument payload, string schemaName, CancellationToken ct = default);

    /// <summary>
    /// Looks up a value from a reference table.
    /// </summary>
    Task<JsonNode?> LookupValueAsync(string sourceValue, string lookupTable, CancellationToken ct = default);

    /// <summary>
    /// Registers a schema definition.
    /// </summary>
    Task RegisterSchemaAsync(string schemaName, JsonDocument schema, CancellationToken ct = default);

    /// <summary>
    /// Registers a lookup table.
    /// </summary>
    Task RegisterLookupTableAsync(string tableName, Dictionary<string, string> mappings, CancellationToken ct = default);
}

/// <summary>
/// Schema mapper implementation with caching.
/// </summary>
public class SchemaMapper : ISchemaMapper
{
    private readonly ConcurrentDictionary<string, JsonDocument> _schemas = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _lookupTables = new();
    private readonly IDistributedCache _cache;
    private readonly ILogger<SchemaMapper> _logger;

    public SchemaMapper(IDistributedCache cache, ILogger<SchemaMapper> logger)
    {
        _cache = cache;
        _logger = logger;

        // Load default lookup tables
        LoadDefaultLookupTables();
    }

    public async Task<ValidationResult> ValidateAsync(
        JsonDocument payload,
        string schemaName,
        CancellationToken ct = default)
    {
        if (!_schemas.TryGetValue(schemaName, out var schema))
        {
            _logger.LogWarning("Schema not found: {SchemaName}", schemaName);
            return ValidationResult.Success(); // No schema = no validation
        }

        var errors = new List<ValidationError>();
        var warnings = new List<ValidationWarning>();

        try
        {
            var schemaRoot = schema.RootElement;
            var payloadRoot = payload.RootElement;

            // Validate required fields
            if (schemaRoot.TryGetProperty("required", out var requiredFields))
            {
                foreach (var field in requiredFields.EnumerateArray())
                {
                    var fieldName = field.GetString();
                    if (fieldName is not null && !payloadRoot.TryGetProperty(fieldName, out _))
                    {
                        errors.Add(new ValidationError(
                            $"$.{fieldName}",
                            $"Required field '{fieldName}' is missing",
                            "REQUIRED_FIELD_MISSING"
                        ));
                    }
                }
            }

            // Validate field types
            if (schemaRoot.TryGetProperty("properties", out var properties))
            {
                foreach (var prop in properties.EnumerateObject())
                {
                    if (payloadRoot.TryGetProperty(prop.Name, out var value))
                    {
                        ValidateFieldType(prop.Name, value, prop.Value, errors);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating against schema {SchemaName}", schemaName);
            errors.Add(new ValidationError("$", ex.Message, "VALIDATION_ERROR"));
        }

        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings
        };
    }

    public async Task<JsonNode?> LookupValueAsync(
        string sourceValue,
        string lookupTable,
        CancellationToken ct = default)
    {
        // Try memory cache first
        if (_lookupTables.TryGetValue(lookupTable, out var table))
        {
            if (table.TryGetValue(sourceValue, out var mappedValue))
            {
                return JsonValue.Create(mappedValue);
            }
        }

        // Try distributed cache
        var cacheKey = $"lookup:{lookupTable}:{sourceValue}";
        var cachedValue = await _cache.GetStringAsync(cacheKey, ct);
        if (!string.IsNullOrEmpty(cachedValue))
        {
            return JsonValue.Create(cachedValue);
        }

        _logger.LogWarning("Lookup not found: {Table}[{Value}]", lookupTable, sourceValue);
        return null;
    }

    public Task RegisterSchemaAsync(string schemaName, JsonDocument schema, CancellationToken ct = default)
    {
        _schemas[schemaName] = schema;
        _logger.LogInformation("Registered schema: {SchemaName}", schemaName);
        return Task.CompletedTask;
    }

    public async Task RegisterLookupTableAsync(
        string tableName,
        Dictionary<string, string> mappings,
        CancellationToken ct = default)
    {
        _lookupTables[tableName] = mappings;

        // Also cache in distributed cache for cross-instance consistency
        foreach (var (key, value) in mappings)
        {
            var cacheKey = $"lookup:{tableName}:{key}";
            await _cache.SetStringAsync(cacheKey, value, ct);
        }

        _logger.LogInformation("Registered lookup table: {TableName} with {Count} entries",
            tableName, mappings.Count);
    }

    #region Private Methods

    private void ValidateFieldType(
        string fieldName,
        JsonElement value,
        JsonElement schemaProperty,
        List<ValidationError> errors)
    {
        if (!schemaProperty.TryGetProperty("type", out var typeElement))
            return;

        var expectedType = typeElement.GetString();
        var actualType = value.ValueKind;

        var isValid = expectedType switch
        {
            "string" => actualType == JsonValueKind.String,
            "number" => actualType == JsonValueKind.Number,
            "integer" => actualType == JsonValueKind.Number && IsInteger(value),
            "boolean" => actualType == JsonValueKind.True || actualType == JsonValueKind.False,
            "array" => actualType == JsonValueKind.Array,
            "object" => actualType == JsonValueKind.Object,
            "null" => actualType == JsonValueKind.Null,
            _ => true
        };

        if (!isValid)
        {
            errors.Add(new ValidationError(
                $"$.{fieldName}",
                $"Expected type '{expectedType}' but got '{actualType}'",
                "TYPE_MISMATCH",
                value.ToString()
            ));
        }

        // Validate string constraints
        if (expectedType == "string" && actualType == JsonValueKind.String)
        {
            var stringValue = value.GetString() ?? "";

            if (schemaProperty.TryGetProperty("minLength", out var minLength) &&
                stringValue.Length < minLength.GetInt32())
            {
                errors.Add(new ValidationError(
                    $"$.{fieldName}",
                    $"String length must be at least {minLength.GetInt32()}",
                    "MIN_LENGTH",
                    stringValue.Length
                ));
            }

            if (schemaProperty.TryGetProperty("maxLength", out var maxLength) &&
                stringValue.Length > maxLength.GetInt32())
            {
                errors.Add(new ValidationError(
                    $"$.{fieldName}",
                    $"String length must not exceed {maxLength.GetInt32()}",
                    "MAX_LENGTH",
                    stringValue.Length
                ));
            }

            if (schemaProperty.TryGetProperty("pattern", out var pattern))
            {
                var regex = new System.Text.RegularExpressions.Regex(pattern.GetString()!);
                if (!regex.IsMatch(stringValue))
                {
                    errors.Add(new ValidationError(
                        $"$.{fieldName}",
                        $"Value does not match pattern '{pattern.GetString()}'",
                        "PATTERN_MISMATCH",
                        stringValue
                    ));
                }
            }
        }

        // Validate number constraints
        if (expectedType is "number" or "integer" && actualType == JsonValueKind.Number)
        {
            var numValue = value.GetDecimal();

            if (schemaProperty.TryGetProperty("minimum", out var minimum) &&
                numValue < minimum.GetDecimal())
            {
                errors.Add(new ValidationError(
                    $"$.{fieldName}",
                    $"Value must be at least {minimum.GetDecimal()}",
                    "MIN_VALUE",
                    numValue
                ));
            }

            if (schemaProperty.TryGetProperty("maximum", out var maximum) &&
                numValue > maximum.GetDecimal())
            {
                errors.Add(new ValidationError(
                    $"$.{fieldName}",
                    $"Value must not exceed {maximum.GetDecimal()}",
                    "MAX_VALUE",
                    numValue
                ));
            }
        }
    }

    private static bool IsInteger(JsonElement value)
    {
        if (value.TryGetInt64(out _))
            return true;
        
        var decimalValue = value.GetDecimal();
        return decimalValue == Math.Truncate(decimalValue);
    }

    private void LoadDefaultLookupTables()
    {
        // ERP Customer ID to CRM Account ID mapping
        var customerToAccountMapping = new Dictionary<string, string>
        {
            // These would be loaded from database in production
            ["CUST001"] = "account-guid-001",
            ["CUST002"] = "account-guid-002"
        };
        _lookupTables["erp_customer_to_crm_account"] = customerToAccountMapping;

        // ERP Status to CRM Status Code mapping
        var statusMapping = new Dictionary<string, string>
        {
            ["Active"] = "1",
            ["Inactive"] = "2",
            ["Pending"] = "0",
            ["Complete"] = "3",
            ["Cancelled"] = "4"
        };
        _lookupTables["erp_status_to_crm_statuscode"] = statusMapping;

        // Work Order Type mapping
        var workOrderTypeMapping = new Dictionary<string, string>
        {
            ["CNC"] = "CNC_ROUTING",
            ["EDGE"] = "EDGE_BANDING",
            ["SAW"] = "PANEL_SAW",
            ["METAL"] = "METAL_FABRICATION",
            ["ASSEMBLY"] = "SHOP_ASSEMBLY"
        };
        _lookupTables["work_order_type"] = workOrderTypeMapping;

        _logger.LogInformation("Loaded {Count} default lookup tables", 3);
    }

    #endregion
}

