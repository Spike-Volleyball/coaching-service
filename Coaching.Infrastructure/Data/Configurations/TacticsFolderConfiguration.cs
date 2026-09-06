using Coaching.Domain.Models.Tactics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Coaching.Infrastructure.Data.Configurations;

public class TacticsFolderConfiguration : IEntityTypeConfiguration<TacticsFolder>
{
    public void Configure(EntityTypeBuilder<TacticsFolder> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).IsRequired().HasMaxLength(TacticsFolder.NameMaxLength);
        builder.Property(e => e.Scope).HasConversion<int>().IsRequired();
        builder.Property(e => e.OwnerUserId).IsRequired();

        builder.HasIndex(e => new { e.Scope, e.OwnerUserId });
        builder.HasIndex(e => new { e.Scope, e.ClubId });
        builder.HasIndex(e => new { e.Scope, e.TeamId });
    }
}
