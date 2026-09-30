using Coaching.Domain.Models.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Coaching.Infrastructure.Data.Configurations;

public class TrainingPlanRunConfiguration : IEntityTypeConfiguration<TrainingPlanRun>
{
    public void Configure(EntityTypeBuilder<TrainingPlanRun> builder)
    {
        builder.ToTable("TrainingPlanRuns");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(r => r.PlanId).IsRequired();
        builder.Property(r => r.EventId).IsRequired();
        builder.Property(r => r.StartedByUserId).IsRequired();

        builder.HasIndex(r => r.PlanId).IsUnique();
        builder.HasIndex(r => r.EventId);

        // Two phones control one run. xmin is a system column, so this adds nothing to the table.
        builder.Property(r => r.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasOne(r => r.Plan)
            .WithMany()
            .HasForeignKey(r => r.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(r => r.Items)
            .WithOne(i => i.Run)
            .HasForeignKey(i => i.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
