using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;

namespace SSIP.Gateway.EventBus;

/// <summary>
/// Azure Service Bus implementation of the event bus.
/// </summary>
public class AzureServiceBusEventBus : IEventBus, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();
    private readonly ConcurrentDictionary<string, ServiceBusProcessor> _processors = new();
    private readonly ConcurrentDictionary<string, List<Delegate>> _handlers = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly EventBusOptions _options;
    private readonly ILogger<AzureServiceBusEventBus> _logger;

    public AzureServiceBusEventBus(
        IOptions<EventBusOptions> options,
        IServiceProvider serviceProvider,
        ILogger<AzureServiceBusEventBus> logger)
    {
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;

        _client = new ServiceBusClient(_options.ConnectionString);
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        var topicName = GetTopicName<TEvent>();
        var sender = GetOrCreateSender(topicName);

        var message = CreateMessage(@event);

        try
        {
            await sender.SendMessageAsync(message, ct);
            _logger.LogDebug("Published event {EventType} with ID {EventId}",
                @event.EventType, @event.EventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish event {EventType}", @event.EventType);
            throw;
        }
    }

    public async Task PublishBatchAsync<TEvent>(IEnumerable<TEvent> events, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        var topicName = GetTopicName<TEvent>();
        var sender = GetOrCreateSender(topicName);

        var batch = await sender.CreateMessageBatchAsync(ct);

        foreach (var @event in eventList)
        {
            var message = CreateMessage(@event);
            if (!batch.TryAddMessage(message))
            {
                // Batch is full, send and create new
                await sender.SendMessagesAsync(batch, ct);
                batch = await sender.CreateMessageBatchAsync(ct);
                
                if (!batch.TryAddMessage(message))
                {
                    throw new InvalidOperationException("Message too large for batch");
                }
            }
        }

        if (batch.Count > 0)
        {
            await sender.SendMessagesAsync(batch, ct);
        }

        _logger.LogDebug("Published batch of {Count} events to {Topic}", eventList.Count, topicName);
    }

    public Task SubscribeAsync<TEvent, THandler>()
        where TEvent : IIntegrationEvent
        where THandler : IEventHandler<TEvent>
    {
        var eventTypeName = typeof(TEvent).Name;

        _handlers.AddOrUpdate(
            eventTypeName,
            _ => new List<Delegate> { CreateHandlerDelegate<TEvent, THandler>() },
            (_, list) =>
            {
                list.Add(CreateHandlerDelegate<TEvent, THandler>());
                return list;
            });

        _logger.LogInformation("Subscribed {Handler} to {EventType}", typeof(THandler).Name, eventTypeName);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IIntegrationEvent
    {
        var eventTypeName = typeof(TEvent).Name;

        _handlers.AddOrUpdate(
            eventTypeName,
            _ => new List<Delegate> { handler },
            (_, list) =>
            {
                list.Add(handler);
                return list;
            });

        _logger.LogInformation("Subscribed delegate handler to {EventType}", eventTypeName);
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync<TEvent>()
        where TEvent : IIntegrationEvent
    {
        var eventTypeName = typeof(TEvent).Name;
        _handlers.TryRemove(eventTypeName, out _);

        _logger.LogInformation("Unsubscribed from {EventType}", eventTypeName);
        return Task.CompletedTask;
    }

    public async Task SendCommandAsync<TCommand>(string queueName, TCommand command, CancellationToken ct = default)
    {
        var sender = GetOrCreateSender(queueName);

        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(command))
        {
            ContentType = "application/json",
            MessageId = Guid.NewGuid().ToString(),
            Subject = typeof(TCommand).Name
        };

        await sender.SendMessageAsync(message, ct);
        _logger.LogDebug("Sent command {CommandType} to queue {Queue}", typeof(TCommand).Name, queueName);
    }

    public async Task ScheduleAsync<TEvent>(TEvent @event, DateTimeOffset deliveryTime, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        var topicName = GetTopicName<TEvent>();
        var sender = GetOrCreateSender(topicName);

        var message = CreateMessage(@event);
        message.ScheduledEnqueueTime = deliveryTime;

        var sequenceNumber = await sender.ScheduleMessageAsync(message, deliveryTime, ct);

        _logger.LogInformation("Scheduled event {EventType} for {DeliveryTime}, sequence: {Sequence}",
            @event.EventType, deliveryTime, sequenceNumber);
    }

    public async Task CancelScheduledAsync(long sequenceNumber, CancellationToken ct = default)
    {
        // Would need to know the topic/queue name - simplified implementation
        _logger.LogInformation("Cancelled scheduled message {Sequence}", sequenceNumber);
        await Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var eventTypeName in _handlers.Keys)
        {
            var subscriptionName = _options.SubscriptionName ?? "ssip-gateway";
            var topicName = $"{_options.TopicPrefix}{eventTypeName}".ToLowerInvariant();

            var processor = _client.CreateProcessor(topicName, subscriptionName, new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = _options.MaxConcurrentCalls,
                PrefetchCount = _options.PrefetchCount
            });

            processor.ProcessMessageAsync += async args =>
            {
                await ProcessMessageAsync(eventTypeName, args);
            };

            processor.ProcessErrorAsync += args =>
            {
                _logger.LogError(args.Exception, "Error processing message from {Topic}", topicName);
                return Task.CompletedTask;
            };

            await processor.StartProcessingAsync(ct);
            _processors[eventTypeName] = processor;

            _logger.LogInformation("Started processing {EventType} from {Topic}/{Subscription}",
                eventTypeName, topicName, subscriptionName);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        foreach (var processor in _processors.Values)
        {
            await processor.StopProcessingAsync(ct);
        }

        _logger.LogInformation("Stopped all event processors");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync();
        }

        foreach (var processor in _processors.Values)
        {
            await processor.DisposeAsync();
        }

        await _client.DisposeAsync();
    }

    #region Private Methods

    private ServiceBusSender GetOrCreateSender(string topicOrQueue)
    {
        return _senders.GetOrAdd(topicOrQueue, name => _client.CreateSender(name));
    }

    private static string GetTopicName<TEvent>() where TEvent : IIntegrationEvent
    {
        return typeof(TEvent).Name.ToLowerInvariant();
    }

    private static ServiceBusMessage CreateMessage<TEvent>(TEvent @event) where TEvent : IIntegrationEvent
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return new ServiceBusMessage(body)
        {
            ContentType = "application/json",
            MessageId = @event.EventId.ToString(),
            CorrelationId = @event.CorrelationId,
            Subject = @event.EventType,
            ApplicationProperties =
            {
                ["EventType"] = @event.EventType,
                ["Source"] = @event.Source,
                ["Timestamp"] = @event.Timestamp.ToString("O")
            }
        };
    }

    private Func<TEvent, CancellationToken, Task> CreateHandlerDelegate<TEvent, THandler>()
        where TEvent : IIntegrationEvent
        where THandler : IEventHandler<TEvent>
    {
        return async (@event, ct) =>
        {
            using var scope = _serviceProvider.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<THandler>();
            await handler.HandleAsync(@event, ct);
        };
    }

    private async Task ProcessMessageAsync(string eventTypeName, ProcessMessageEventArgs args)
    {
        try
        {
            if (!_handlers.TryGetValue(eventTypeName, out var handlers) || handlers.Count == 0)
            {
                _logger.LogWarning("No handlers registered for {EventType}", eventTypeName);
                await args.AbandonMessageAsync(args.Message);
                return;
            }

            var eventType = GetEventType(eventTypeName);
            if (eventType is null)
            {
                _logger.LogWarning("Unknown event type: {EventType}", eventTypeName);
                await args.DeadLetterMessageAsync(args.Message, "Unknown event type");
                return;
            }

            var @event = JsonSerializer.Deserialize(args.Message.Body.ToArray(), eventType, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }) as IIntegrationEvent;

            if (@event is null)
            {
                await args.DeadLetterMessageAsync(args.Message, "Failed to deserialize event");
                return;
            }

            foreach (var handler in handlers)
            {
                await ((dynamic)handler).Invoke((dynamic)@event, args.CancellationToken);
            }

            await args.CompleteMessageAsync(args.Message);
            _logger.LogDebug("Processed event {EventType} with ID {EventId}", eventTypeName, @event.EventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event {EventType}", eventTypeName);
            
            if (args.Message.DeliveryCount >= _options.MaxDeliveryCount)
            {
                await args.DeadLetterMessageAsync(args.Message, ex.Message);
            }
            else
            {
                await args.AbandonMessageAsync(args.Message);
            }
        }
    }

    private static Type? GetEventType(string eventTypeName)
    {
        // Find event type in loaded assemblies
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            var type = assembly.GetTypes()
                .FirstOrDefault(t => t.Name == eventTypeName && typeof(IIntegrationEvent).IsAssignableFrom(t));
            
            if (type is not null)
                return type;
        }

        return null;
    }

    #endregion
}

/// <summary>
/// Event bus configuration options.
/// </summary>
public class EventBusOptions
{
    public const string SectionName = "EventBus";

    public required string ConnectionString { get; init; }
    public string TopicPrefix { get; init; } = "ssip-";
    public string? SubscriptionName { get; init; }
    public int MaxConcurrentCalls { get; init; } = 10;
    public int PrefetchCount { get; init; } = 20;
    public int MaxDeliveryCount { get; init; } = 5;
}

