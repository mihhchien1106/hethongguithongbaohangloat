using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Shared.Contracts.Models;

public class NotificationJobEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid CampaignId { get; set; }

    [ForeignKey(nameof(CampaignId))]
    [JsonIgnore]
    public CampaignEntity? Campaign { get; set; }

    [Required]
    [MaxLength(255)]
    public string RecipientAddress { get; set; } = string.Empty; // Email hoặc SĐT

    [MaxLength(150)]
    public string RecipientName { get; set; } = string.Empty;

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    public int RetryCount { get; set; } = 0;

    public string? ErrorMessage { get; set; }

    public string? ProcessedByWorker { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
