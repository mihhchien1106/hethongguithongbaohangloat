namespace Shared.Contracts.Models;

public record RecipientDto(string Address, string Name);

public record CreateCampaignRequest(
    string Name,
    string Subject,
    string Content,
    string Channel,
    List<RecipientDto> Recipients
);

public record CampaignSummaryResponse(
    Guid Id,
    string Name,
    string Subject,
    string Channel,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    int TotalRecipients,
    int PendingCount,
    int ProcessingCount,
    int SentCount,
    int FailedCount
);

public record DeliveryDetailResponse(
    Guid JobId,
    Guid CampaignId,
    string RecipientAddress,
    string RecipientName,
    string Status,
    int RetryCount,
    string? ErrorMessage,
    string? ProcessedByWorker,
    DateTime CreatedAt,
    DateTime? SentAt
);
