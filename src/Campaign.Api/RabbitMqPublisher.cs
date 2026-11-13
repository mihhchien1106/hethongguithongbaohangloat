using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using Shared.Contracts.Messages;

namespace Campaign.Api;

public interface IMessagePublisher
{
    void PublishJobs(IEnumerable<NotificationDispatchMessage> messages);
}

public class RabbitMqPublisher : IMessagePublisher, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private IConnection? _connection;
    private IModel? _channel;
    private readonly object _lock = new();

    public RabbitMqPublisher(IConfiguration configuration, ILogger<RabbitMqPublisher> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private void EnsureChannel()
    {
        if (_channel is { IsOpen: true }) return;

        lock (_lock)
        {
            if (_channel is { IsOpen: true }) return;

            var factory = new ConnectionFactory
            {
                HostName = _configuration["RabbitMQ:HostName"] ?? "localhost",
                Port = int.Parse(_configuration["RabbitMQ:Port"] ?? "5672"),
                UserName = _configuration["RabbitMQ:UserName"] ?? "guest",
                Password = _configuration["RabbitMQ:Password"] ?? "guest",
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            // Khai báo Dead Letter Exchange & Queue (Mở rộng)
            _channel.ExchangeDeclare(RabbitMqConstants.DeadLetterExchangeName, ExchangeType.Direct, durable: true);
            _channel.QueueDeclare(RabbitMqConstants.DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.QueueBind(RabbitMqConstants.DeadLetterQueueName, RabbitMqConstants.DeadLetterExchangeName, RabbitMqConstants.DeadLetterRoutingKey);

            // Khai báo Main Exchange & Queue
            _channel.ExchangeDeclare(RabbitMqConstants.ExchangeName, ExchangeType.Direct, durable: true);
            var args = new Dictionary<string, object>
            {
                { "x-dead-letter-exchange", RabbitMqConstants.DeadLetterExchangeName },
                { "x-dead-letter-routing-key", RabbitMqConstants.DeadLetterRoutingKey }
            };
            _channel.QueueDeclare(RabbitMqConstants.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: args);
            _channel.QueueBind(RabbitMqConstants.QueueName, RabbitMqConstants.ExchangeName, RabbitMqConstants.RoutingKey);

            _logger.LogInformation("[RabbitMQ Publisher] Đã kết nối và khởi tạo Exchange/Queue thành công tại {Host}", factory.HostName);
        }
    }

    public void PublishJobs(IEnumerable<NotificationDispatchMessage> messages)
    {
        EnsureChannel();

        var batch = _channel!.CreateBasicPublishBatch();
        int count = 0;

        foreach (var msg in messages)
        {
            var json = JsonSerializer.Serialize(msg);
            var body = Encoding.UTF8.GetBytes(json);

            var props = _channel.CreateBasicProperties();
            props.Persistent = true;
            props.CorrelationId = msg.CorrelationId;
            props.MessageId = msg.JobId.ToString();

            batch.Add(
                exchange: RabbitMqConstants.ExchangeName,
                routingKey: RabbitMqConstants.RoutingKey,
                mandatory: false,
                properties: props,
                body: new ReadOnlyMemory<byte>(body)
            );
            count++;
        }

        batch.Publish();
        _logger.LogInformation("[RabbitMQ Publisher] Đã đẩy hàng loạt {Count} jobs vào hàng đợi {Queue}", count, RabbitMqConstants.QueueName);
    }

    public void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
    }
}
