namespace SSIP.Gateway.EventBus;

/// <summary>
/// Event bus for async communication between SSIP components.
/// Backed by Azure Service Bus for reliable message delivery.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Publishes an integration event to all subscribers.
    /// </summary>
    /// <typeparam name="TEvent">Event type</typeparam>
    /// <param name="event">The event to publish</param>
    /// <param name="ct">Cancellation token</param>
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent;

    /// <summary>
    /// Publishes multiple events in a batch.
    /// </summary>
    Task PublishBatchAsync<TEvent>(IEnumerable<TEvent> events, CancellationToken ct = default)
        where TEvent : IIntegrationEvent;

    /// <summary>
    /// Subscribes handler to specific event type.
    /// </summary>
    Task SubscribeAsync<TEvent, THandler>()
        where TEvent : IIntegrationEvent
        where THandler : IEventHandler<TEvent>;

    /// <summary>
    /// Subscribes with a delegate handler.
    /// </summary>
    Task SubscribeAsync<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IIntegrationEvent;

    /// <summary>
    /// Unsubscribes from an event type.
    /// </summary>
    Task UnsubscribeAsync<TEvent>()
        where TEvent : IIntegrationEvent;

    /// <summary>
    /// Sends a command to a specific queue.
    /// </summary>
    Task SendCommandAsync<TCommand>(string queueName, TCommand command, CancellationToken ct = default);

    /// <summary>
    /// Schedules an event for future delivery.
    /// </summary>
    Task ScheduleAsync<TEvent>(TEvent @event, DateTimeOffset deliveryTime, CancellationToken ct = default)
        where TEvent : IIntegrationEvent;

    /// <summary>
    /// Cancels a scheduled event.
    /// </summary>
    Task CancelScheduledAsync(long sequenceNumber, CancellationToken ct = default);

    /// <summary>
    /// Starts processing messages.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops processing messages.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Base interface for all integration events.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Unique event identifier</summary>
    Guid EventId { get; }
    
    /// <summary>When the event occurred</summary>
    DateTimeOffset Timestamp { get; }
    
    /// <summary>Correlation ID for tracing</summary>
    string CorrelationId { get; }
    
    /// <summary>Source system/service</summary>
    string Source { get; }
    
    /// <summary>Event type name</summary>
    string EventType { get; }
}

/// <summary>
/// Base record for integration events.
/// </summary>
public abstract record IntegrationEvent : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString();
    public string Source { get; init; } = "SSIP.Gateway";
    public virtual string EventType => GetType().Name;
}

/// <summary>
/// Handler interface for processing events.
/// </summary>
/// <typeparam name="TEvent">Event type to handle</typeparam>
public interface IEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    /// <summary>
    /// Handles the event.
    /// </summary>
    Task HandleAsync(TEvent @event, CancellationToken ct);
}

/// <summary>
/// Marker interface for dynamic handler resolution.
/// </summary>
public interface IEventHandler
{
    Task HandleAsync(IIntegrationEvent @event, CancellationToken ct);
}

/// <summary>
/// Event subscription info.
/// </summary>
public record EventSubscription
{
    public required string EventTypeName { get; init; }
    public required Type EventType { get; init; }
    public required Type HandlerType { get; init; }
    public DateTime SubscribedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Result of event publishing.
/// </summary>
public record PublishResult
{
    public bool Success { get; init; }
    public string? MessageId { get; init; }
    public long? SequenceNumber { get; init; }
    public string? Error { get; init; }

    public static PublishResult Succeeded(string messageId, long? sequenceNumber = null) =>
        new() { Success = true, MessageId = messageId, SequenceNumber = sequenceNumber };

    public static PublishResult Failed(string error) =>
        new() { Success = false, Error = error };
}

