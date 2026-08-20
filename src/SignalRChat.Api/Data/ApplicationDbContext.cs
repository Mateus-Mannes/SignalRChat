using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SignalRChat.Api.Domain;

namespace SignalRChat.Api.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Conversation>(conversation =>
        {
            conversation.ToTable(table => table.HasCheckConstraint(
                "CK_Conversations_Name",
                "char_length(btrim(\"Name\")) BETWEEN 1 AND 100"));

            conversation.HasKey(item => item.Id);
            conversation.Property(item => item.Name).HasMaxLength(100).IsRequired();
            conversation.Property(item => item.CreatedByUserId).IsRequired();
            conversation.Property(item => item.CreatedAtUtc).IsRequired();

            conversation
                .HasOne(item => item.CreatedByUser)
                .WithMany()
                .HasForeignKey(item => item.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            conversation
                .HasIndex(item => new { item.CreatedAtUtc, item.Id })
                .IsDescending();
        });

        builder.Entity<ConversationMember>(member =>
        {
            member.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_ConversationMembers_Role",
                    "\"Role\" IN ('Owner', 'Member')");
                table.HasCheckConstraint(
                    "CK_ConversationMembers_LeftAfterJoined",
                    "\"LeftAtUtc\" IS NULL OR \"LeftAtUtc\" >= \"JoinedAtUtc\"");
                table.HasCheckConstraint(
                    "CK_ConversationMembers_OwnerIsActive",
                    "\"Role\" <> 'Owner' OR \"LeftAtUtc\" IS NULL");
            });

            member.HasKey(item => new { item.ConversationId, item.UserId });
            member.Property(item => item.UserId).IsRequired();
            member.Property(item => item.Role)
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            member.Property(item => item.JoinedAtUtc).IsRequired();

            member
                .HasOne(item => item.Conversation)
                .WithMany(item => item.Members)
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            member
                .HasOne(item => item.User)
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            member
                .HasIndex(item => new { item.UserId, item.LeftAtUtc, item.ConversationId });

            member
                .HasIndex(item => new { item.ConversationId, item.LeftAtUtc });

            member
                .HasIndex(item => new { item.ConversationId, item.Role })
                .IsUnique()
                .HasFilter("\"Role\" = 'Owner' AND \"LeftAtUtc\" IS NULL");
        });
    }
}
