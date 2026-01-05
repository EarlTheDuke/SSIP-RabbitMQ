using System.Text.Json;

namespace SSIP.Gateway.Transform;

/// <summary>
/// Transforms data between different system schemas.
/// Handles ERP ↔ CRM mappings, field translations, and format conversions.
/// </summary>
public interface IDataTransformer
{
    /// <summary>
    /// Transforms request payload before forwarding to backend.
    /// </summary>
    /// <param name="payload">The incoming JSON payload</param>
    /// <param name="sourceSchema">Source schema identifier</param>
    /// <param name="targetSchema">Target schema identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Transformed JSON document</returns>
    Task<JsonDocument> TransformRequestAsync(
        JsonDocument payload,
        string sourceSchema,
        string targetSchema,
        CancellationToken ct = default);

    /// <summary>
    /// Transforms response payload before returning to caller.
    /// </summary>
    /// <param name="payload">The backend response payload</param>
    /// <param name="sourceSchema">Source schema identifier</param>
    /// <param name="targetSchema">Target schema identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Transformed JSON document</returns>
    Task<JsonDocument> TransformResponseAsync(
        JsonDocument payload,
        string sourceSchema,
        string targetSchema,
        CancellationToken ct = default);

    /// <summary>
    /// Transforms a single JSON element/object.
    /// </summary>
    Task<JsonElement> TransformElementAsync(
        JsonElement element,
        string sourceSchema,
        string targetSchema,
        CancellationToken ct = default);

    /// <summary>
    /// Registers a schema mapping configuration.
    /// </summary>
    Task RegisterMappingAsync(SchemaMapping mapping, CancellationToken ct = default);

    /// <summary>
    /// Removes a schema mapping.
    /// </summary>
    Task UnregisterMappingAsync(string sourceSchema, string targetSchema, CancellationToken ct = default);

    /// <summary>
    /// Gets all registered mappings.
    /// </summary>
    Task<IReadOnlyList<SchemaMapping>> GetMappingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Validates payload against target schema.
    /// </summary>
    Task<ValidationResult> ValidateAsync(JsonDocument payload, string schemaName, CancellationToken ct = default);

    /// <summary>
    /// Checks if a mapping exists between two schemas.
    /// </summary>
    Task<bool> HasMappingAsync(string sourceSchema, string targetSchema, CancellationToken ct = default);
}

/// <summary>
/// Schema mapping configuration.
/// </summary>
public record SchemaMapping
{
    public required string MappingId { get; init; }
    public required string SourceSchema { get; init; }  // e.g., "erp.project.v1"
    public required string TargetSchema { get; init; }  // e.g., "dynamics.opportunity.v1"
    public required Dictionary<string, FieldMapping> FieldMappings { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Individual field mapping configuration.
/// </summary>
public record FieldMapping
{
    public required string SourcePath { get; init; }  // JSON path: "$.project.name"
    public required string TargetPath { get; init; }  // JSON path: "$.opportunity.title"
    public TransformType Transform { get; init; } = TransformType.Direct;
    public string? TransformExpression { get; init; }  // Expression or lookup table name
    public string? DefaultValue { get; init; }
    public bool IsRequired { get; init; }
    public string? Format { get; init; }  // Date format, number format, etc.
    public Dictionary<string, string>? ValueMappings { get; init; }  // For enum translations
}

/// <summary>
/// Type of field transformation.
/// </summary>
public enum TransformType
{
    /// <summary>Direct copy of value</summary>
    Direct,
    
    /// <summary>Computed using expression</summary>
    Computed,
    
    /// <summary>Lookup from reference table</summary>
    Lookup,
    
    /// <summary>Constant value</summary>
    Constant,
    
    /// <summary>Conditional based on source data</summary>
    Conditional,
    
    /// <summary>Concatenation of multiple fields</summary>
    Concat,
    
    /// <summary>Split single field into multiple</summary>
    Split,
    
    /// <summary>Format conversion (dates, numbers)</summary>
    Format,
    
    /// <summary>Value mapping/translation</summary>
    Map
}

/// <summary>
/// Result of schema validation.
/// </summary>
public record ValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];
    public IReadOnlyList<ValidationWarning> Warnings { get; init; } = [];

    public static ValidationResult Success() => new() { IsValid = true };
    
    public static ValidationResult Failure(params ValidationError[] errors) =>
        new() { IsValid = false, Errors = errors };
}

/// <summary>
/// Validation error detail.
/// </summary>
public record ValidationError(
    string Path,
    string Message,
    string ErrorCode,
    object? ActualValue = null
);

/// <summary>
/// Validation warning (non-blocking).
/// </summary>
public record ValidationWarning(
    string Path,
    string Message,
    string WarningCode
);

