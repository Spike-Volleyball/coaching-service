using Coaching.Domain.Models.Tactics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Coaching.Infrastructure.Data.Configurations;

public class TacticsBoardConfiguration : IEntityTypeConfiguration<TacticsBoard>
{
    public void Configure(EntityTypeBuilder<TacticsBoard> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Title).IsRequired().HasMaxLength(TacticsBoard.TitleMaxLength);
        builder.Property(e => e.Category).IsRequired().HasMaxLength(TacticsBoard.CategoryMaxLength);
        builder.Property(e => e.System).IsRequired().HasMaxLength(TacticsBoard.SystemMaxLength);
        builder.Property(e => e.Scope).HasConversion<int>().IsRequired();
        builder.Property(e => e.OwnerUserId).IsRequired();

        // The scenes themselves. Opaque to the server on purpose: the editor's format changes with
        // every tool it grows, and none of those changes should need a migration.
        builder.Property(e => e.Document).HasColumnType("jsonb").IsRequired();

        // Also in the UPDATE's WHERE clause, so two saves that raced cannot both win — the second
        // one affects no rows and EF raises rather than silently overwriting the first.
        builder.Property(e => e.Version).IsConcurrencyToken().HasDefaultValue(0);

        builder.HasOne(e => e.Folder)
            .WithMany(f => f.Boards)
            .HasForeignKey(e => e.FolderId)
            .OnDelete(DeleteBehavior.SetNull);

        // ClubId and TeamId name rows in clubs-service, so they are indexed but not foreign keys.
        builder.HasIndex(e => new { e.Scope, e.OwnerUserId });
        builder.HasIndex(e => new { e.Scope, e.ClubId });
        builder.HasIndex(e => new { e.Scope, e.TeamId });
        builder.HasIndex(e => e.FolderId);
    }
}
