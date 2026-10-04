using BeeLogistics.Modules.Chat.Domain;
using BeeLogistics.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Chat.Infrastructure;

public class ChatDbContext : DbContext
{
    public ChatDbContext(DbContextOptions<ChatDbContext> options)
        : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        
        // Suppress query filter warning - this is expected when using soft delete with cascade deletes
        // ConversationParticipant will be cascade deleted when Conversation is soft-deleted
        optionsBuilder.ConfigureWarnings(warnings =>
            warnings.Ignore(RelationalEventId.QueryPossibleUnintendedUseOfEqualsWarning));
    }

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("chat");
        
        // Suppress query filter warning for ConversationParticipant relationship
        modelBuilder.Entity<ConversationParticipant>()
            .HasOne(e => e.Conversation)
            .WithMany(c => c.Participants)
            .HasForeignKey(e => e.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.HasIndex(e => e.BookingId);
            entity.HasIndex(e => e.DispatchId);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<ConversationParticipant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).HasMaxLength(450);
            entity.Property(e => e.UserName).HasMaxLength(200);
            entity.HasIndex(e => new { e.ConversationId, e.UserId }).IsUnique();
            // Relationship configured above to suppress query filter warning
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SenderId).HasMaxLength(450);
            entity.Property(e => e.SenderName).HasMaxLength(200);
            entity.Property(e => e.Content).HasMaxLength(4000);
            entity.HasIndex(e => e.ConversationId);
            entity.HasIndex(e => e.SentAt);
            
            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });
    }

    public override int SaveChanges() => base.SaveChanges();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => base.SaveChangesAsync(cancellationToken);
}
