namespace SSIP.Gateway.EventBus.Events;

// ═══════════════════════════════════════════════════════════════
// API GATEWAY EVENTS
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Published when an API request is processed through the gateway.
/// </summary>
public record ApiRequestProcessed(
    string RequestId,
    string ServiceName,
    int StatusCode,
    TimeSpan Duration
) : IntegrationEvent
{
    public string? UserId { get; init; }
    public string? Endpoint { get; init; }
    public string? HttpMethod { get; init; }
}

/// <summary>
/// Published when a gateway error occurs.
/// </summary>
public record GatewayErrorOccurred(
    string RequestId,
    string ErrorCode,
    string ErrorMessage
) : IntegrationEvent
{
    public string? ServiceName { get; init; }
    public string? StackTrace { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// ERP INTEGRATION EVENTS
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Published when a new project is created in ERP.
/// </summary>
public record ProjectCreated(
    Guid ProjectId,
    string ProjectNumber,
    string ProjectName
) : IntegrationEvent
{
    public string? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public decimal? EstimatedValue { get; init; }
    public DateTime? TargetDate { get; init; }
    public string? ProjectManager { get; init; }
}

/// <summary>
/// Published when a project status changes.
/// </summary>
public record ProjectStatusChanged(
    Guid ProjectId,
    string ProjectNumber,
    string OldStatus,
    string NewStatus
) : IntegrationEvent
{
    public string? ChangedBy { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Published when a work order is created.
/// </summary>
public record WorkOrderCreated(
    Guid WorkOrderId,
    string WorkOrderNumber,
    string WorkOrderType,
    Guid ProjectId
) : IntegrationEvent
{
    public string? Description { get; init; }
    public int? Quantity { get; init; }
    public DateTime? DueDate { get; init; }
    public string? AssignedDepartment { get; init; }
}

/// <summary>
/// Published when work order status changes.
/// </summary>
public record WorkOrderStatusChanged(
    Guid WorkOrderId,
    string WorkOrderNumber,
    string OldStatus,
    string NewStatus
) : IntegrationEvent
{
    public string? UpdatedBy { get; init; }
    public int? CompletedQuantity { get; init; }
}

/// <summary>
/// Published when an item/component is updated.
/// </summary>
public record ItemUpdated(
    Guid ItemId,
    string ItemNumber,
    string ChangeType  // Created, Modified, Deleted
) : IntegrationEvent
{
    public string? Description { get; init; }
    public decimal? Price { get; init; }
    public int? QuantityOnHand { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// CRM INTEGRATION EVENTS
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Published when CRM contact data is synced.
/// </summary>
public record CrmContactSynced(
    Guid CrmContactId,
    string Email,
    SyncDirection Direction
) : IntegrationEvent
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Company { get; init; }
    public Guid? ErpCustomerId { get; init; }
}

/// <summary>
/// Published when an opportunity is updated in CRM.
/// </summary>
public record OpportunityUpdated(
    Guid OpportunityId,
    string OpportunityName,
    string Stage
) : IntegrationEvent
{
    public decimal? EstimatedRevenue { get; init; }
    public int? Probability { get; init; }
    public DateTime? ExpectedCloseDate { get; init; }
    public Guid? LinkedProjectId { get; init; }
}

/// <summary>
/// Published when a lead is qualified in CRM.
/// </summary>
public record LeadQualified(
    Guid LeadId,
    string CompanyName,
    string ContactEmail
) : IntegrationEvent
{
    public string? Industry { get; init; }
    public string? Source { get; init; }
    public decimal? EstimatedBudget { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// MANUFACTURING EVENTS
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Published when a manufacturing step is completed.
/// </summary>
public record ManufacturingStepCompleted(
    Guid StepId,
    Guid WorkOrderId,
    string StepType,  // CNC, EdgeBanding, Assembly, etc.
    string StepName
) : IntegrationEvent
{
    public TimeSpan Duration { get; init; }
    public int QuantityCompleted { get; init; }
    public string? CompletedBy { get; init; }
    public string? MachineId { get; init; }
}

/// <summary>
/// Published when a quality check is performed.
/// </summary>
public record QualityCheckPerformed(
    Guid CheckId,
    Guid WorkOrderId,
    string CheckType,
    bool Passed
) : IntegrationEvent
{
    public string? Inspector { get; init; }
    public string? Notes { get; init; }
    public int DefectsFound { get; init; }
}

/// <summary>
/// Published when inventory level changes.
/// </summary>
public record InventoryLevelChanged(
    Guid ItemId,
    string ItemNumber,
    int PreviousQuantity,
    int NewQuantity,
    string ChangeReason  // Received, Consumed, Adjusted, Scrapped
) : IntegrationEvent
{
    public string? Location { get; init; }
    public string? ReferenceDocument { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// AI/AUTOMATION EVENTS
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Published when TinyBox AI completes an inference.
/// </summary>
public record AiInferenceCompleted(
    Guid InferenceId,
    string ModelName,
    string InferenceType
) : IntegrationEvent
{
    public object? Result { get; init; }
    public double Confidence { get; init; }
    public TimeSpan ProcessingTime { get; init; }
}

/// <summary>
/// Published when an automated workflow is triggered.
/// </summary>
public record WorkflowTriggered(
    Guid WorkflowId,
    string WorkflowName,
    string TriggerType
) : IntegrationEvent
{
    public string? TriggerSource { get; init; }
    public Dictionary<string, object>? Parameters { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// ENUMS
// ═══════════════════════════════════════════════════════════════

public enum SyncDirection
{
    ErpToCrm,
    CrmToErp,
    Bidirectional
}

