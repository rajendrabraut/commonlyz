using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.TeamChat.Data;

public sealed class Channel
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Topic { get; set; }
    public bool IsPrivate { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Membership only matters for private channels; public channels are readable by every signed-in user.</summary>
public sealed class ChannelMember
{
    public int ChannelId { get; set; }
    public required string UserId { get; set; }
}

public sealed class Message
{
    public const int MaxBodyLength = 4000;

    public long Id { get; set; }
    public int ChannelId { get; set; }

    /// <summary>Thread root; null for top-level messages.</summary>
    public long? ParentId { get; set; }

    public required string AuthorId { get; set; }
    public required string AuthorName { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? EditedUtc { get; set; }
    public DateTime? DeletedUtc { get; set; }
}

public sealed class TeamChatDb(DbContextOptions<TeamChatDb> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelMember> Members => Set<ChannelMember>();
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(TeamChatModule.Key);

        modelBuilder.Entity<Channel>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Topic).HasMaxLength(250);
            e.Property(x => x.CreatedBy).HasMaxLength(450);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
        });

        modelBuilder.Entity<ChannelMember>(e =>
        {
            e.HasKey(x => new { x.ChannelId, x.UserId });
            e.Property(x => x.UserId).HasMaxLength(450);
            e.HasIndex(x => x.UserId);
            e.HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.Property(x => x.AuthorId).HasMaxLength(450);
            e.Property(x => x.AuthorName).HasMaxLength(100);
            e.Property(x => x.Body).HasMaxLength(Message.MaxBodyLength);
            e.Property(x => x.CreatedUtc).HasPrecision(3);
            e.Property(x => x.EditedUtc).HasPrecision(3);
            e.Property(x => x.DeletedUtc).HasPrecision(3);
            // Keyset pagination: newest first within a channel; threads oldest first.
            e.HasIndex(x => new { x.ChannelId, x.Id }).HasFilter("[ParentId] IS NULL");
            e.HasIndex(x => new { x.ParentId, x.Id }).HasFilter("[ParentId] IS NOT NULL");
            e.HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}
