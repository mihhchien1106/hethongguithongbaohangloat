namespace Shared.Contracts.Messages;

public record NotificationDispatchMessage(
    Guid JobId,
    Guid CampaignId,
    string RecipientAddress,
    string RecipientName,
    string Subject,
    string Content,
    string Channel,
    string CorrelationId,
    int AttemptNumber = 1
);

public static class RabbitMqConstants
{
    public const string ExchangeName = "notification.exchange";
    public const string QueueName = "notification.dispatch.queue";
    public const string RoutingKey = "notification.dispatch";

    public const string DeadLetterExchangeName = "notification.dlx.exchange";
    public const string DeadLetterQueueName = "notification.dlq.queue";
    public const string DeadLetterRoutingKey = "notification.dlq";
}
