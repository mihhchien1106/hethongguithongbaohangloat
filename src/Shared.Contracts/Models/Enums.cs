namespace Shared.Contracts.Models;

public enum CampaignStatus
{
    Draft = 0,
    Running = 1,
    Completed = 2
}

public enum DeliveryStatus
{
    Pending = 0,
    Processing = 1,
    Sent = 2,
    Failed = 3
}
