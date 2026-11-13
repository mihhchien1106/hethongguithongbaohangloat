using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Contracts.Data;
using Shared.Contracts.Messages;
using Shared.Contracts.Models;

namespace Notification.Worker;

public class Worker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Worker> _logger;
    private IConnection? _connection;
    private IModel? _channel;
    private readonly string _workerName;
    private readonly double _simulatedFailureRate;

    public Worker(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<Worker> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _workerName = _configuration["Worker:InstanceName"] ?? Environment.MachineName;
        _simulatedFailureRate = double.TryParse(_configuration["Worker:SimulatedFailureRate"], out var rate) ? rate : 0.2;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[{WorkerName}] Đang khởi động Notification Worker...", _workerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                InitializeRabbitMq();
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[{WorkerName}] Chưa thể kết nối RabbitMQ: {Message}. Thử lại sau 5 giây...", _workerName, ex.Message);
                await Task.Delay(5000, stoppingToken);
            }
        }

        if (stoppingToken.IsCancellationRequested || _channel is null) return;

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var json = Encoding.UTF8.GetString(body);
            NotificationDispatchMessage? message = null;

            try
            {
                message = JsonSerializer.Deserialize<NotificationDispatchMessage>(json);
                if (message is null)
                {
                    _channel.BasicAck(ea.DeliveryTag, multiple: false);
                    return;
                }

                await ProcessNotificationJobAsync(message, stoppingToken);
                _channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{WorkerName}] Lỗi nghiêm trọng khi xử lý message DeliveryTag={Tag}", _workerName, ea.DeliveryTag);
                // Đẩy vào Dead-Letter Queue nếu lỗi không thể xử lý ở tầng hạ tầng
                _channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            }
        };

        _channel.BasicConsume(
            queue: RabbitMqConstants.QueueName,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation("[{WorkerName}] Đang lắng nghe hàng đợi '{Queue}' (Tỷ lệ lỗi mô phỏng: {Rate:P0})...",
            _workerName, RabbitMqConstants.QueueName, _simulatedFailureRate);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    private void InitializeRabbitMq()
    {
        var factory = new ConnectionFactory
        {
            HostName = _configuration["RabbitMQ:HostName"] ?? "localhost",
            Port = int.Parse(_configuration["RabbitMQ:Port"] ?? "5672"),
            UserName = _configuration["RabbitMQ:UserName"] ?? "guest",
            Password = _configuration["RabbitMQ:Password"] ?? "guest",
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        _channel.ExchangeDeclare(RabbitMqConstants.DeadLetterExchangeName, ExchangeType.Direct, durable: true);
        _channel.QueueDeclare(RabbitMqConstants.DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(RabbitMqConstants.DeadLetterQueueName, RabbitMqConstants.DeadLetterExchangeName, RabbitMqConstants.DeadLetterRoutingKey);

        _channel.ExchangeDeclare(RabbitMqConstants.ExchangeName, ExchangeType.Direct, durable: true);
        var args = new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", RabbitMqConstants.DeadLetterExchangeName },
            { "x-dead-letter-routing-key", RabbitMqConstants.DeadLetterRoutingKey }
        };
        _channel.QueueDeclare(RabbitMqConstants.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: args);
        _channel.QueueBind(RabbitMqConstants.QueueName, RabbitMqConstants.ExchangeName, RabbitMqConstants.RoutingKey);

        // Fair dispatch: mỗi worker nhận tối đa 1 job tại 1 thời điểm để chia tải đều giữa các worker
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
    }

    private async Task ProcessNotificationJobAsync(NotificationDispatchMessage msg, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.NotificationJobs.FirstOrDefaultAsync(j => j.Id == msg.JobId, ct);
        if (job is null)
        {
            _logger.LogWarning("[{WorkerName}] [CorrelationId: {CorrelationId}] Không tìm thấy Job {JobId} trong DB.",
                _workerName, msg.CorrelationId, msg.JobId);
            return;
        }

        // Idempotent Consumer: Nếu job đã Sent rồi thì bỏ qua, không gửi trùng
        if (job.Status == DeliveryStatus.Sent)
        {
            _logger.LogInformation("[{WorkerName}] [CorrelationId: {CorrelationId}] Job {JobId} ({Recipient}) đã ở trạng thái Sent. Bỏ qua (Idempotent).",
                _workerName, msg.CorrelationId, job.Id, job.RecipientAddress);
            return;
        }

        job.Status = DeliveryStatus.Processing;
        job.ProcessedByWorker = _workerName;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("[{WorkerName}] [CorrelationId: {CorrelationId}] Đang gửi {Channel} tới {Recipient} (Lần thử #{Attempt})...",
            _workerName, msg.CorrelationId, msg.Channel, msg.RecipientAddress, msg.AttemptNumber);

        // Mô phỏng thời gian gọi cổng gửi Email/SMS (300ms - 900ms)
        await Task.Delay(Random.Shared.Next(300, 900), ct);

        // Kiểm tra điều kiện thất bại:
        // 1. Nếu địa chỉ chứa từ khóa "fail" hoặc "error" -> lỗi cố định (trừ khi đã retry >= 1 lần thì cho qua để demo retry thành công)
        // 2. Hoặc ngẫu nhiên theo tỷ lệ SimulatedFailureRate ở lần gửi đầu tiên
        bool shouldFail = false;
        string errorReason = string.Empty;

        if (msg.RecipientAddress.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            shouldFail = true;
            errorReason = "Địa chỉ người nhận không hợp lệ (Hard Bounce).";
        }
        else if (msg.RecipientAddress.Contains("fail", StringComparison.OrdinalIgnoreCase) && job.RetryCount == 0)
        {
            shouldFail = true;
            errorReason = "SMTP Gateway Timeout (Sẽ thành công khi chạy lại / Retry).";
        }
        else if (job.RetryCount == 0 && Random.Shared.NextDouble() < _simulatedFailureRate)
        {
            shouldFail = true;
            errorReason = "Kết nối tới nhà cung cấp dịch vụ thông báo tạm thời gián đoạn (503 Service Unavailable).";
        }

        if (shouldFail)
        {
            job.Status = DeliveryStatus.Failed;
            job.RetryCount += 1;
            job.ErrorMessage = errorReason;
            job.UpdatedAt = DateTime.UtcNow;

            _logger.LogWarning("[{WorkerName}] [CorrelationId: {CorrelationId}] THẤT BẠI Job {JobId} -> {Recipient}: {Error}",
                _workerName, msg.CorrelationId, job.Id, job.RecipientAddress, errorReason);
        }
        else
        {
            job.Status = DeliveryStatus.Sent;
            if (msg.AttemptNumber > 1)
            {
                job.RetryCount = msg.AttemptNumber - 1;
            }
            job.ErrorMessage = null;
            job.SentAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation("[{WorkerName}] [CorrelationId: {CorrelationId}] THÀNH CÔNG Job {JobId} -> {Recipient}",
                _workerName, msg.CorrelationId, job.Id, job.RecipientAddress);
        }

        await db.SaveChangesAsync(ct);

        // Kiểm tra nếu toàn bộ job của chiến dịch đã xử lý xong (không còn Pending/Processing)
        var hasRemaining = await db.NotificationJobs
            .AnyAsync(j => j.CampaignId == msg.CampaignId &&
                          (j.Status == DeliveryStatus.Pending || j.Status == DeliveryStatus.Processing), ct);

        if (!hasRemaining)
        {
            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == msg.CampaignId, ct);
            if (campaign is not null && campaign.Status != CampaignStatus.Completed)
            {
                campaign.Status = CampaignStatus.Completed;
                campaign.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("[{WorkerName}] Chiến dịch {CampaignId} đã hoàn tất xử lý toàn bộ thông báo.", _workerName, campaign.Id);
            }
        }
    }

    public override void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
        base.Dispose();
    }
}
