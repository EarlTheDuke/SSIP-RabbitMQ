using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SSIP.Gateway.EventBus;

/// <summary>
/// RabbitMQ implementation of the event bus for local/on-premises deployment.
/// Uses exchanges and queues to implement pub/sub messaging patterns.
/// </summary>
public class RabbitMqEventBus : IEventBus, IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _publishChannel;
    private readonly ConcurrentDictionary<string, IModel> _consumerChannels = new();
    private readonly ConcurrentDictionary<string, List<Delegate>> _handlers = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventBus> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public RabbitMqEventBus(
        IOptions<RabbitMqOptions> options,
        IServiceProvider serviceProvider,
        ILogger<RabbitMqEventBus> logger)
    {
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection(_options.ClientName ?? "SSIP.Gateway");
        _publishChannel = _connection.CreateModel();

        // Enable publisher confirms for reliable publishing
        _publishChannel.ConfirmSelect();

        _logger.LogInformation(
            "RabbitMQ EventBus connected to {Host}:{Port}/{VHost}",
            _options.HostName, _options.Port, _options.VirtualHost);
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        var exchangeName = GetExchangeName<TEvent>();
        var routingKey = @event.EventType.ToLowerInvariant();

        EnsureExchangeExists(exchangeName);

        var body = SerializeEvent(@event);
        var properties = CreateBasicProperties(@event);

        try
        {
            _publishChannel.BasicPublish(
                exchange: exchangeName,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: properties,
                body: body);

            // Wait for publisher confirm
            _publishChannel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));

            _logger.LogDebug(
                "Published event {EventType} with ID {EventId} to exchange {Exchange}",
                @event.EventType, @event.EventId, exchangeName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish event {EventType}", @event.EventType);
            throw;
        }

        await Task.CompletedTask;
    }

    public async Task PublishBatchAsync<TEvent>(IEnumerable<TEvent> events, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        var exchangeName = GetExchangeName<TEvent>();
        EnsureExchangeExists(exchangeName);

        var batch = _publishChannel.CreateBasicPublishBatch();

        foreach (var @event in eventList)
        {
            var routingKey = @event.EventType.ToLowerInvariant();
            var body = SerializeEvent(@event);
            var properties = CreateBasicProperties(@event);

            batch.Add(
                exchange: exchangeName,
                routingKey: routingKey,
                mandatory: false,
                properties: properties,
                body: new ReadOnlyMemory<byte>(body));
        }

        try
        {
            batch.Publish();
            _publishChannel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));

            _logger.LogDebug(
                "Published batch of {Count} events to exchange {Exchange}",
                eventList.Count, exchangeName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish batch of {Count} events", eventList.Count);
            throw;
        }

        await Task.CompletedTask;
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

        _logger.LogInformation(
            "Subscribed {Handler} to {EventType}",
            typeof(THandler).Name, eventTypeName);

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

        // Stop and dispose the consumer channel if exists
        if (_consumerChannels.TryRemove(eventTypeName, out var channel))
        {
            channel.Close();
            channel.Dispose();
        }

        _logger.LogInformation("Unsubscribed from {EventType}", eventTypeName);

        return Task.CompletedTask;
    }

    public async Task SendCommandAsync<TCommand>(string queueName, TCommand command, CancellationToken ct = default)
    {
        EnsureQueueExists(queueName);

        var body = JsonSerializer.SerializeToUtf8Bytes(command, _jsonOptions);

        var properties = _publishChannel.CreateBasicProperties();
        properties.ContentType = "application/json";
        properties.DeliveryMode = 2; // Persistent
        properties.MessageId = Guid.NewGuid().ToString();
        properties.Type = typeof(TCommand).Name;
        properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        _publishChannel.BasicPublish(
            exchange: "",
            routingKey: queueName,
            mandatory: true,
            basicProperties: properties,
            body: body);

        _publishChannel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));

        _logger.LogDebug("Sent command {CommandType} to queue {Queue}", typeof(TCommand).Name, queueName);

        await Task.CompletedTask;
    }

    public async Task ScheduleAsync<TEvent>(TEvent @event, DateTimeOffset deliveryTime, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        // RabbitMQ doesn't have native scheduled messages, but we can use:
        // 1. x-delayed-message plugin (if installed)
        // 2. Message TTL with dead-letter exchange
        // Here we use TTL approach which works out of the box

        var delay = deliveryTime - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            // Deliver immediately if delay is in the past
            await PublishAsync(@event, ct);
            return;
        }

        var exchangeName = GetExchangeName<TEvent>();
        var delayQueueName = $"{_options.QueuePrefix}delay.{@event.EventType.ToLowerInvariant()}";
        var targetRoutingKey = @event.EventType.ToLowerInvariant();

        // Ensure the delay queue exists with TTL and dead-letter to actual exchange
        EnsureDelayQueueExists(delayQueueName, exchangeName, targetRoutingKey, (long)delay.TotalMilliseconds);

        var body = SerializeEvent(@event);
        var properties = CreateBasicProperties(@event);
        properties.Expiration = ((long)delay.TotalMilliseconds).ToString();

        _publishChannel.BasicPublish(
            exchange: "",
            routingKey: delayQueueName,
            mandatory: false,
            basicProperties: properties,
            body: body);

        _publishChannel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));

        _logger.LogInformation(
            "Scheduled event {EventType} for {DeliveryTime} (delay: {Delay}ms)",
            @event.EventType, deliveryTime, delay.TotalMilliseconds);

        await Task.CompletedTask;
    }

    public Task CancelScheduledAsync(long sequenceNumber, CancellationToken ct = default)
    {
        // RabbitMQ doesn't support canceling scheduled messages directly
        // This would require tracking message IDs and purging specific messages
        _logger.LogWarning(
            "CancelScheduledAsync is not fully supported with RabbitMQ. Sequence: {Sequence}",
            sequenceNumber);

        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var eventTypeName in _handlers.Keys)
        {
            var exchangeName = $"{_options.ExchangePrefix}{eventTypeName}".ToLowerInvariant();
            var queueName = $"{_options.QueuePrefix}{_options.SubscriptionName}.{eventTypeName}".ToLowerInvariant();
            var routingKey = eventTypeName.ToLowerInvariant();

            var channel = _connection.CreateModel();

            // Set QoS (prefetch count)
            channel.BasicQos(0, (ushort)_options.PrefetchCount, false);

            // Declare exchange
            channel.ExchangeDeclare(
                exchange: exchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false);

            // Declare queue with dead-letter exchange
            var deadLetterExchange = $"{_options.ExchangePrefix}deadletter";
            var deadLetterQueue = $"{_options.QueuePrefix}deadletter.{eventTypeName}".ToLowerInvariant();

            channel.ExchangeDeclare(deadLetterExchange, ExchangeType.Direct, durable: true);
            channel.QueueDeclare(deadLetterQueue, durable: true, exclusive: false, autoDelete: false);
            channel.QueueBind(deadLetterQueue, deadLetterExchange, eventTypeName.ToLowerInvariant());

            var queueArgs = new Dictionary<string, object>
            {
                { "x-dead-letter-exchange", deadLetterExchange },
                { "x-dead-letter-routing-key", eventTypeName.ToLowerInvariant() }
            };

            channel.QueueDeclare(
                queue: queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: queueArgs);

            channel.QueueBind(queueName, exchangeName, routingKey);

            // Create async consumer
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.Received += async (sender, args) =>
            {
                await ProcessMessageAsync(eventTypeName, channel, args);
            };

            channel.BasicConsume(
                queue: queueName,
                autoAck: false,
                consumer: consumer);

            _consumerChannels[eventTypeName] = channel;

            _logger.LogInformation(
                "Started consuming {EventType} from queue {Queue}",
                eventTypeName, queueName);
        }

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        foreach (var kvp in _consumerChannels)
        {
            kvp.Value.Close();
            kvp.Value.Dispose();
        }

        _consumerChannels.Clear();
        _logger.LogInformation("Stopped all event consumers");

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var channel in _consumerChannels.Values)
        {
            channel.Close();
            channel.Dispose();
        }

        _publishChannel.Close();
        _publishChannel.Dispose();

        _connection.Close();
        _connection.Dispose();

        _disposed = true;
    }

    #region Private Methods

    private string GetExchangeName<TEvent>() where TEvent : IIntegrationEvent
    {
        return $"{_options.ExchangePrefix}{typeof(TEvent).Name}".ToLowerInvariant();
    }

    private void EnsureExchangeExists(string exchangeName)
    {
        _publishChannel.ExchangeDeclare(
            exchange: exchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false);
    }

    private void EnsureQueueExists(string queueName)
    {
        _publishChannel.QueueDeclare(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false);
    }

    private void EnsureDelayQueueExists(string queueName, string targetExchange, string targetRoutingKey, long ttlMs)
    {
        var args = new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", targetExchange },
            { "x-dead-letter-routing-key", targetRoutingKey },
            { "x-message-ttl", ttlMs }
        };

        _publishChannel.QueueDeclare(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: args);
    }

    private byte[] SerializeEvent<TEvent>(TEvent @event) where TEvent : IIntegrationEvent
    {
        return JsonSerializer.SerializeToUtf8Bytes(@event, _jsonOptions);
    }

    private IBasicProperties CreateBasicProperties<TEvent>(TEvent @event) where TEvent : IIntegrationEvent
    {
        var properties = _publishChannel.CreateBasicProperties();
        properties.ContentType = "application/json";
        properties.DeliveryMode = 2; // Persistent
        properties.MessageId = @event.EventId.ToString();
        properties.CorrelationId = @event.CorrelationId;
        properties.Type = @event.EventType;
        properties.Timestamp = new AmqpTimestamp(@event.Timestamp.ToUnixTimeSeconds());
        properties.Headers = new Dictionary<string, object>
        {
            { "EventType", @event.EventType },
            { "Source", @event.Source },
            { "Timestamp", @event.Timestamp.ToString("O") }
        };

        return properties;
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

    private async Task ProcessMessageAsync(string eventTypeName, IModel channel, BasicDeliverEventArgs args)
    {
        var deliveryCount = GetDeliveryCount(args);

        try
        {
            if (!_handlers.TryGetValue(eventTypeName, out var handlers) || handlers.Count == 0)
            {
                _logger.LogWarning("No handlers registered for {EventType}", eventTypeName);
                channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            var eventType = GetEventType(eventTypeName);
            if (eventType is null)
            {
                _logger.LogWarning("Unknown event type: {EventType}", eventTypeName);
                channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            var @event = JsonSerializer.Deserialize(args.Body.Span, eventType, _jsonOptions) as IIntegrationEvent;

            if (@event is null)
            {
                _logger.LogWarning("Failed to deserialize event {EventType}", eventTypeName);
                channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            foreach (var handler in handlers)
            {
                await ((dynamic)handler).Invoke((dynamic)@event, CancellationToken.None);
            }

            channel.BasicAck(args.DeliveryTag, multiple: false);

            _logger.LogDebug(
                "Processed event {EventType} with ID {EventId}",
                eventTypeName, @event.EventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event {EventType}", eventTypeName);

            if (deliveryCount >= _options.MaxDeliveryCount)
            {
                // Send to dead-letter queue
                channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
                _logger.LogWarning(
                    "Event {EventType} exceeded max delivery count ({Count}), sent to DLQ",
                    eventTypeName, _options.MaxDeliveryCount);
            }
            else
            {
                // Requeue for retry
                channel.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
            }
        }
    }

    private static int GetDeliveryCount(BasicDeliverEventArgs args)
    {
        if (args.BasicProperties.Headers?.TryGetValue("x-delivery-count", out var count) == true)
        {
            return Convert.ToInt32(count);
        }

        // RabbitMQ doesn't track delivery count natively in older versions
        // We use redelivered flag as a simple indicator
        return args.Redelivered ? 2 : 1;
    }

    private static Type? GetEventType(string eventTypeName)
    {
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
/// RabbitMQ configuration options.
/// </summary>
public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>RabbitMQ server hostname.</summary>
    public string HostName { get; init; } = "localhost";

    /// <summary>RabbitMQ server port (default: 5672).</summary>
    public int Port { get; init; } = 5672;

    /// <summary>Username for authentication.</summary>
    public string UserName { get; init; } = "guest";

    /// <summary>Password for authentication.</summary>
    public string Password { get; init; } = "guest";

    /// <summary>Virtual host to use.</summary>
    public string VirtualHost { get; init; } = "/";

    /// <summary>Client connection name for identification.</summary>
    public string? ClientName { get; init; } = "SSIP.Gateway";

    /// <summary>Prefix for exchange names.</summary>
    public string ExchangePrefix { get; init; } = "ssip.";

    /// <summary>Prefix for queue names.</summary>
    public string QueuePrefix { get; init; } = "ssip.";

    /// <summary>Subscription/consumer name for queue naming.</summary>
    public string SubscriptionName { get; init; } = "gateway";

    /// <summary>Number of messages to prefetch.</summary>
    public int PrefetchCount { get; init; } = 20;

    /// <summary>Maximum delivery attempts before dead-lettering.</summary>
    public int MaxDeliveryCount { get; init; } = 5;
}
