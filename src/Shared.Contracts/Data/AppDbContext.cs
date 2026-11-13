using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Models;

namespace Shared.Contracts.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<CampaignEntity> Campaigns => Set<CampaignEntity>();
    public DbSet<NotificationJobEntity> NotificationJobs => Set<NotificationJobEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CampaignEntity>(entity =>
        {
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<NotificationJobEntity>(entity =>
        {
            entity.HasIndex(e => new { e.CampaignId, e.Status });
            entity.HasIndex(e => e.RecipientAddress);
            entity.HasOne(d => d.Campaign)
                  .WithMany(p => p.Deliveries)
                  .HasForeignKey(d => d.CampaignId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
