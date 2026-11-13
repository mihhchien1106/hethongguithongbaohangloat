using Campaign.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Data;
using Shared.Contracts.Messages;
using Shared.Contracts.Models;

var builder = WebApplication.CreateBuilder(args);

// Cấu hình Database PostgreSQL
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=notification_db;Username=postgres;Password=postgres";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Tự động tạo bảng Database khi khởi động (kèm cơ chế Retry chờ DB container sẵn sàng)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (int retry = 1; retry <= 10; retry++)
    {
        try
        {
            db.Database.EnsureCreated();
            logger.LogInformation("Database sẵn sàng và đã đồng bộ Schema thành công.");
            break;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Chưa thể kết nối Database (Lần thử {Retry}/10): {Message}", retry, ex.Message);
            Thread.Sleep(3000);
        }
    }
}

app.MapHealthChecks("/health");

// =========================================================================
// 1. POST /campaigns - Tạo chiến dịch thông báo mới kèm danh sách người nhận
// =========================================================================
app.MapPost("/campaigns", async (
    [FromBody] CreateCampaignRequest request,
    AppDbContext db,
    ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Recipients == null || request.Recipients.Count == 0)
    {
        return Results.BadRequest(new { Message = "Tên chiến dịch và danh sách người nhận không được để trống." });
    }

    var campaign = new CampaignEntity
    {
        Id = Guid.NewGuid(),
        Name = request.Name.Trim(),
        Subject = request.Subject.Trim(),
        Content = request.Content,
        Channel = string.IsNullOrWhiteSpace(request.Channel) ? "Email" : request.Channel.Trim(),
        Status = CampaignStatus.Draft,
        CreatedAt = DateTime.UtcNow
    };

    foreach (var recipient in request.Recipients.DistinctBy(r => r.Address.Trim().ToLowerInvariant()))
    {
        campaign.Deliveries.Add(new NotificationJobEntity
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            RecipientAddress = recipient.Address.Trim(),
            RecipientName = recipient.Name.Trim(),
            Status = DeliveryStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });
    }

    db.Campaigns.Add(campaign);
    await db.SaveChangesAsync();

    logger.LogInformation("Đã tạo chiến dịch {CampaignId} ({Name}) với {Count} người nhận.",
        campaign.Id, campaign.Name, campaign.Deliveries.Count);

    return Results.Created($"/campaigns/{campaign.Id}", new
    {
        campaign.Id,
        campaign.Name,
        campaign.Subject,
        campaign.Channel,
        Status = campaign.Status.ToString(),
        TotalRecipients = campaign.Deliveries.Count,
        campaign.CreatedAt
    });
});

// =========================================================================
// 2. GET /campaigns - Lấy danh sách chiến dịch kèm thống kê Pending/Sent/Failed
// =========================================================================
app.MapGet("/campaigns", async (AppDbContext db) =>
{
    var campaigns = await db.Campaigns
        .AsNoTracking()
        .OrderByDescending(c => c.CreatedAt)
        .Select(c => new CampaignSummaryResponse(
            c.Id,
            c.Name,
            c.Subject,
            c.Channel,
            c.Status.ToString(),
            c.CreatedAt,
            c.StartedAt,
            c.Deliveries.Count(),
            c.Deliveries.Count(d => d.Status == DeliveryStatus.Pending),
            c.Deliveries.Count(d => d.Status == DeliveryStatus.Processing),
            c.Deliveries.Count(d => d.Status == DeliveryStatus.Sent),
            c.Deliveries.Count(d => d.Status == DeliveryStatus.Failed)
        ))
        .ToListAsync();

    return Results.Ok(campaigns);
});

// =========================================================================
// 3. GET /campaigns/{id} - Chi tiết chiến dịch và thống kê trạng thái
// =========================================================================
app.MapGet("/campaigns/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var campaign = await db.Campaigns
        .AsNoTracking()
        .Where(c => c.Id == id)
        .Select(c => new
        {
            c.Id,
            c.Name,
            c.Subject,
            c.Content,
            c.Channel,
            Status = c.Status.ToString(),
            c.CreatedAt,
            c.StartedAt,
            c.CompletedAt,
            TotalRecipients = c.Deliveries.Count(),
            PendingCount = c.Deliveries.Count(d => d.Status == DeliveryStatus.Pending),
            ProcessingCount = c.Deliveries.Count(d => d.Status == DeliveryStatus.Processing),
            SentCount = c.Deliveries.Count(d => d.Status == DeliveryStatus.Sent),
            FailedCount = c.Deliveries.Count(d => d.Status == DeliveryStatus.Failed)
        })
        .FirstOrDefaultAsync();

    return campaign is not null ? Results.Ok(campaign) : Results.NotFound(new { Message = "Không tìm thấy chiến dịch." });
});

// =========================================================================
// 4. POST /campaigns/{id}/start - Sinh các job gửi thông báo & đẩy vào RabbitMQ
// =========================================================================
app.MapPost("/campaigns/{id:guid}/start", async (
    Guid id,
    AppDbContext db,
    IMessagePublisher publisher,
    ILogger<Program> logger) =>
{
    var campaign = await db.Campaigns
        .Include(c => c.Deliveries)
        .FirstOrDefaultAsync(c => c.Id == id);

    if (campaign is null)
        return Results.NotFound(new { Message = "Không tìm thấy chiến dịch." });

    var pendingJobs = campaign.Deliveries
        .Where(d => d.Status == DeliveryStatus.Pending)
        .ToList();

    if (pendingJobs.Count == 0)
    {
        return Results.BadRequest(new { Message = "Chiến dịch không có thông báo nào ở trạng thái Pending để gửi." });
    }

    var correlationId = $"CAMP-{campaign.Id.ToString()[..8]}-{DateTime.UtcNow:HHmmss}";
    campaign.Status = CampaignStatus.Running;
    campaign.StartedAt ??= DateTime.UtcNow;

    var messages = pendingJobs.Select(job => new NotificationDispatchMessage(
        JobId: job.Id,
        CampaignId: campaign.Id,
        RecipientAddress: job.RecipientAddress,
        RecipientName: job.RecipientName,
        Subject: campaign.Subject,
        Content: campaign.Content,
        Channel: campaign.Channel,
        CorrelationId: correlationId,
        AttemptNumber: job.RetryCount + 1
    )).ToList();

    await db.SaveChangesAsync();

    // Đẩy hàng loạt vào RabbitMQ
    publisher.PublishJobs(messages);

    logger.LogInformation("[CorrelationId: {CorrelationId}] Đã kích hoạt chiến dịch {CampaignId}, đẩy {Count} jobs vào RabbitMQ.",
        correlationId, campaign.Id, messages.Count);

    return Results.Ok(new
    {
        Message = "Đã kích hoạt chiến dịch và đẩy các job vào RabbitMQ thành công.",
        CampaignId = campaign.Id,
        CorrelationId = correlationId,
        DispatchedJobsCount = messages.Count
    });
});

// =========================================================================
// 5. GET /campaigns/{id}/deliveries - Tra cứu chi tiết kết quả gửi của chiến dịch
// =========================================================================
app.MapGet("/campaigns/{id:guid}/deliveries", async (
    Guid id,
    [FromQuery] string? status,
    AppDbContext db) =>
{
    var exists = await db.Campaigns.AnyAsync(c => c.Id == id);
    if (!exists)
        return Results.NotFound(new { Message = "Không tìm thấy chiến dịch." });

    var query = db.NotificationJobs
        .AsNoTracking()
        .Where(d => d.CampaignId == id);

    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<DeliveryStatus>(status, true, out var parsedStatus))
    {
        query = query.Where(d => d.Status == parsedStatus);
    }

    var deliveries = await query
        .OrderBy(d => d.RecipientAddress)
        .Select(d => new DeliveryDetailResponse(
            d.Id,
            d.CampaignId,
            d.RecipientAddress,
            d.RecipientName,
            d.Status.ToString(),
            d.RetryCount,
            d.ErrorMessage,
            d.ProcessedByWorker,
            d.CreatedAt,
            d.SentAt
        ))
        .ToListAsync();

    return Results.Ok(deliveries);
});

// =========================================================================
// 6. POST /campaigns/{id}/retry-failed - Chức năng số 8: Chạy lại các job thất bại
// =========================================================================
app.MapPost("/campaigns/{id:guid}/retry-failed", async (
    Guid id,
    AppDbContext db,
    IMessagePublisher publisher,
    ILogger<Program> logger) =>
{
    var campaign = await db.Campaigns
        .Include(c => c.Deliveries)
        .FirstOrDefaultAsync(c => c.Id == id);

    if (campaign is null)
        return Results.NotFound(new { Message = "Không tìm thấy chiến dịch." });

    var failedJobs = campaign.Deliveries
        .Where(d => d.Status == DeliveryStatus.Failed)
        .ToList();

    if (failedJobs.Count == 0)
    {
        return Results.Ok(new { Message = "Không có job nào bị Failed cần chạy lại.", RetriedCount = 0 });
    }

    var correlationId = $"RETRY-{campaign.Id.ToString()[..8]}-{DateTime.UtcNow:HHmmss}";
    campaign.Status = CampaignStatus.Running;
    campaign.CompletedAt = null;

    foreach (var job in failedJobs)
    {
        job.Status = DeliveryStatus.Pending;
        job.ErrorMessage = null;
        job.UpdatedAt = DateTime.UtcNow;
    }

    await db.SaveChangesAsync();

    var messages = failedJobs.Select(job => new NotificationDispatchMessage(
        JobId: job.Id,
        CampaignId: campaign.Id,
        RecipientAddress: job.RecipientAddress,
        RecipientName: job.RecipientName,
        Subject: campaign.Subject,
        Content: campaign.Content,
        Channel: campaign.Channel,
        CorrelationId: correlationId,
        AttemptNumber: job.RetryCount + 1
    )).ToList();

    publisher.PublishJobs(messages);

    logger.LogInformation("[CorrelationId: {CorrelationId}] Đã đặt lại trạng thái và đẩy {Count} jobs thất bại của chiến dịch {CampaignId} vào RabbitMQ.",
        correlationId, messages.Count, campaign.Id);

    return Results.Ok(new
    {
        Message = $"Đã đưa {messages.Count} job thất bại vào hàng đợi để xử lý lại.",
        CampaignId = campaign.Id,
        CorrelationId = correlationId,
        RetriedCount = messages.Count
    });
});

app.Run();
