using BeeLogistics.Modules.CRM.Domain;
using BeeLogistics.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.CRM.Infrastructure;

public class CrmDbContext : DbContext
{
    public CrmDbContext(DbContextOptions<CrmDbContext> options)
        : base(options)
    {
    }

    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<CustomerNote> CustomerNotes => Set<CustomerNote>();
    public DbSet<FaqArticle> FaqArticles => Set<FaqArticle>();
    public DbSet<FaqCategory> FaqCategories => Set<FaqCategory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("crm");

        modelBuilder.Entity<CustomerProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450); // FK to ApplicationUser.Id (string)
            entity.Property(e => e.Email).HasMaxLength(254);
            entity.Property(e => e.FullName).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.CompanyName).HasMaxLength(200);
            entity.Property(e => e.Tags).HasMaxLength(500);
            entity.HasIndex(e => e.UserId).IsUnique();
            entity.HasIndex(e => e.Email);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<SupportTicket>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TicketNumber).HasMaxLength(20);
            entity.Property(e => e.Subject).HasMaxLength(200);
            entity.Property(e => e.AssignedToUserId).HasMaxLength(450);
            entity.Property(e => e.AssignedToName).HasMaxLength(200);
            entity.Property(e => e.LastZammadSyncError).HasMaxLength(500);
            entity.HasIndex(e => e.TicketNumber).IsUnique();
            entity.HasIndex(e => e.Status);
            // Partial index: the retry job only ever asks for tickets that never reached Zammad,
            // which is a small slice of the table and stays small if the job is working.
            entity.HasIndex(e => e.LastZammadSyncAttemptAt)
                .HasFilter("\"ZammadTicketId\" IS NULL");
            
            entity.HasOne(e => e.CustomerProfile)
                .WithMany(c => c.Tickets)
                .HasForeignKey(e => e.CustomerProfileId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<CustomerNote>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CreatedByUserId).HasMaxLength(450);
            entity.Property(e => e.CreatedByName).HasMaxLength(200);
            entity.Property(e => e.Content).HasMaxLength(2000);
            
            entity.HasOne(e => e.CustomerProfile)
                .WithMany(c => c.CustomerNotes)
                .HasForeignKey(e => e.CustomerProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<FaqArticle>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.Property(e => e.Tags).HasMaxLength(500);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.IsPublished);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<FaqCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Icon).HasMaxLength(50);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });
    }

    public override int SaveChanges() => base.SaveChanges();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => base.SaveChangesAsync(cancellationToken);
}
