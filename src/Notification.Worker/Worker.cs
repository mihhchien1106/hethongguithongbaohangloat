using System.Net;
using System.Net.Mail;
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
            try
            {
                var message = JsonSerializer.Deserialize<NotificationDispatchMessage>(json);
                if (message is not null)
                {
                    await ProcessNotificationJobAsync(message, stoppingToken);
                }
                _channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{WorkerName}] Lỗi nghiêm trọng khi xử lý message DeliveryTag={Tag}", _workerName, ea.DeliveryTag);
                _channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            }
        };

        _channel.BasicConsume(queue: RabbitMqConstants.QueueName, autoAck: false, consumer: consumer);
        _logger.LogInformation("[{WorkerName}] Đang lắng nghe hàng đợi '{Queue}'...", _workerName, RabbitMqConstants.QueueName);

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
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
    }

    private async Task ProcessNotificationJobAsync(NotificationDispatchMessage msg, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.NotificationJobs.FirstOrDefaultAsync(j => j.Id == msg.JobId, ct);
        if (job is null || job.Status == DeliveryStatus.Sent) return;

        job.Status = DeliveryStatus.Processing;
        job.ProcessedByWorker = _workerName;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        bool shouldFail = false;
        string errorReason = string.Empty;

        var smtpUser = _configuration["Smtp:Username"];
        var smtpPass = _configuration["Smtp:Password"];
        bool isRealEmailTarget = !msg.RecipientAddress.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase);

        // Nếu là email thật (không phải @example.com) và đã cấu hình SMTP Gmail -> Gửi email thật!
        if (isRealEmailTarget && !string.IsNullOrWhiteSpace(smtpUser) && !string.IsNullOrWhiteSpace(smtpPass) && !smtpPass.Contains("xxxx"))
        {
            try
            {
                _logger.LogInformation("[{WorkerName}] Đang gửi EMAIL THẬT qua SMTP tới {Recipient}...", _workerName, msg.RecipientAddress);
                await SendRealEmailViaSmtpAsync(msg, smtpUser, smtpPass, ct);
            }
            catch (Exception ex)
            {
                shouldFail = true;
                errorReason = $"Lỗi SMTP thực tế: {ex.Message}";
            }
        }
        else
        {
            // Chế độ mô phỏng cho các email mẫu @example.com
            await Task.Delay(Random.Shared.Next(300, 800), ct);
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
                errorReason = "Kết nối tới nhà cung cấp dịch vụ tạm thời gián đoạn (503 Service Unavailable).";
            }
        }

        if (shouldFail)
        {
            job.Status = DeliveryStatus.Failed;
            job.RetryCount += 1;
            job.ErrorMessage = errorReason;
            job.UpdatedAt = DateTime.UtcNow;
            _logger.LogWarning("[{WorkerName}] THẤT BẠI -> {Recipient}: {Error}", _workerName, job.RecipientAddress, errorReason);
        }
        else
        {
            job.Status = DeliveryStatus.Sent;
            if (msg.AttemptNumber > 1) job.RetryCount = msg.AttemptNumber - 1;
            job.ErrorMessage = null;
            job.SentAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            _logger.LogInformation("[{WorkerName}] THÀNH CÔNG -> {Recipient}", _workerName, job.RecipientAddress);
        }

        await db.SaveChangesAsync(ct);

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
            }
        }
    }

    private async Task SendRealEmailViaSmtpAsync(NotificationDispatchMessage msg, string smtpUser, string smtpPass, CancellationToken ct)
    {
        var host = _configuration["Smtp:Host"] ?? "smtp.gmail.com";
        var port = int.Parse(_configuration["Smtp:Port"] ?? "587");

        using var client = new SmtpClient(host, port)
        {
            Credentials = new NetworkCredential(smtpUser, smtpPass.Replace(" ", "")),
            EnableSsl = true,
            Timeout = 15000
        };

        var htmlBody = $@"
            <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e2e8f0; border-radius: 10px; overflow: hidden;'>
                <div style='background: #0f3d6e; color: white; padding: 16px 24px;'>
                    <h3 style='margin: 0;'>Hệ thống Thông báo Hàng loạt (Đề tài 11)</h3>
                </div>
                <div style='padding: 24px; color: #1e293b; line-height: 1.6;'>
                    <p>Xin chào <b>{msg.RecipientName}</b>,</p>
                    <p>{msg.Content}</p>
                    <hr style='border: none; border-top: 1px solid #cbd5e1; margin: 20px 0;'/>
                    <p style='font-size: 12px; color: #64748b;'>
                        Được xử lý bất đồng bộ qua RabbitMQ bởi: <b>{_workerName}</b><br/>
                        Mã truy vết (Correlation ID): <code>{msg.CorrelationId}</code>
                    </p>
                </div>
            </div>";

        using var mail = new MailMessage
        {
            From = new MailAddress(smtpUser, "Bulk Notification System"),
            Subject = msg.Subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        mail.To.Add(new MailAddress(msg.RecipientAddress, msg.RecipientName));

        await client.SendMailAsync(mail, ct);
    }

    public override void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
        base.Dispose();
    }
}
