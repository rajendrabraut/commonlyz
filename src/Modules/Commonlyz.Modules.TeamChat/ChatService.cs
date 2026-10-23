using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.TeamChat.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.TeamChat;

public sealed record MessageView(Message Message, int ReplyCount);

/// <summary>
/// In-process pub/sub that pushes "something changed in channel X" to every open Blazor circuit.
/// Blazor Server already keeps a live connection per browser, so no extra SignalR hub or JavaScript is needed.
/// Single-instance only: with several app servers, replace with a Redis/Azure SignalR backplane.
/// </summary>
public sealed class ChatNotifier
{
    public event Func<int, Task>? ChannelChanged;

    public async Task PublishAsync(int channelId)
    {
        foreach (var handler in ChannelChanged?.GetInvocationList().Cast<Func<int, Task>>() ?? [])
        {
            try
            {
                await handler(channelId);
            }
            catch (ObjectDisposedException)
            {
                // A circuit went away between subscribe and publish.
            }
        }
    }
}

/// <summary>
/// Every read path (channel list, messages, threads, search) goes through <see cref="ReadableChannels"/>,
/// so private channels can't leak through a forgotten query.
/// </summary>
public sealed class ChatService(IDbContextFactory<TeamChatDb> dbFactory, TimeProvider clock, ChatNotifier notifier)
{
    public const int PageSize = 50;

    private static IQueryable<Channel> ReadableChannels(TeamChatDb db, string userId) =>
        db.Channels.Where(c => !c.IsPrivate || db.Members.Any(m => m.ChannelId == c.Id && m.UserId == userId));

    public async Task<IReadOnlyList<Channel>> ChannelsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await ReadableChannels(db, userId).AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<Channel?> GetChannelAsync(string userId, int channelId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await ReadableChannels(db, userId).AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId);
    }

    public async Task<int> CreateChannelAsync(string userId, string name, string? topic, bool isPrivate, IEnumerable<string> memberIds)
    {
        name = Text.NormalizeSlug(name.Replace(' ', '-').TrimStart('#'));
        if (Text.ValidateSlug(name, 2, 80) is { } error)
            throw new AppException("Channel name: " + error);

        await using var db = await dbFactory.CreateDbContextAsync();
        var channel = new Channel
        {
            Name = name, Topic = Text.Optional(topic, "Topic", 250), IsPrivate = isPrivate, CreatedBy = userId, CreatedUtc = clock.UtcNowSeconds(),
        };
        db.Channels.Add(channel);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AppException($"#{name} already exists.");
        }

        if (isPrivate)
        {
            db.Members.AddRange(memberIds.Append(userId).Distinct().Select(id => new ChannelMember { ChannelId = channel.Id, UserId = id }));
            await db.SaveChangesAsync();
        }
        return channel.Id;
    }

    public async Task<IReadOnlyList<string>> MemberIdsAsync(string userId, int channelId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await ReadableChannels(db, userId).AnyAsync(c => c.Id == channelId), "Channel not found.");
        return await db.Members.Where(m => m.ChannelId == channelId).Select(m => m.UserId).ToListAsync();
    }

    /// <summary>Only existing members of a private channel can add people to it.</summary>
    public async Task AddMemberAsync(string userId, int channelId, string newMemberId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var channel = await ReadableChannels(db, userId).FirstOrDefaultAsync(c => c.Id == channelId) ?? throw new AppException("Channel not found.");
        AppException.ThrowIf(!channel.IsPrivate, "Public channels are open to everyone.");
        if (!await db.Members.AnyAsync(m => m.ChannelId == channelId && m.UserId == newMemberId))
        {
            db.Members.Add(new ChannelMember { ChannelId = channelId, UserId = newMemberId });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Newest top-level messages (returned oldest-first for display), keyset-paged by id.</summary>
    public async Task<IReadOnlyList<MessageView>> MessagesAsync(string userId, int channelId, long? beforeId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await ReadableChannels(db, userId).AnyAsync(c => c.Id == channelId), "Channel not found.");
        var page = await db.Messages.AsNoTracking()
            .Where(m => m.ChannelId == channelId && m.ParentId == null && (beforeId == null || m.Id < beforeId))
            .OrderByDescending(m => m.Id).Take(PageSize)
            .Select(m => new { Message = m, Replies = db.Messages.Count(r => r.ParentId == m.Id && r.DeletedUtc == null) })
            .ToListAsync();
        return [.. page.OrderBy(x => x.Message.Id).Select(x => new MessageView(x.Message, x.Replies))];
    }

    public async Task<(Message Root, IReadOnlyList<Message> Replies)> ThreadAsync(string userId, long rootId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var root = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == rootId && ReadableChannels(db, userId).Any(c => c.Id == m.ChannelId))
            ?? throw new AppException("Message not found.");
        var replies = await db.Messages.AsNoTracking().Where(m => m.ParentId == rootId).OrderBy(m => m.Id).ToListAsync();
        return (root, replies);
    }

    public async Task PostAsync(AppUserInfo user, int channelId, string body, long? parentId = null)
    {
        body = Text.Required(body, "Message", Message.MaxBodyLength);
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await ReadableChannels(db, user.Id).AnyAsync(c => c.Id == channelId), "Channel not found.");
        if (parentId is { } p)
            AppException.ThrowIf(!await db.Messages.AnyAsync(m => m.Id == p && m.ChannelId == channelId && m.ParentId == null), "Thread not found.");

        db.Messages.Add(new Message
        {
            ChannelId = channelId,
            ParentId = parentId,
            AuthorId = user.Id,
            AuthorName = user.DisplayName.Length > 100 ? user.DisplayName[..100] : user.DisplayName,
            Body = body,
            CreatedUtc = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync();
        await notifier.PublishAsync(channelId);
    }

    public async Task EditAsync(string userId, long messageId, string body)
    {
        body = Text.Required(body, "Message", Message.MaxBodyLength);
        await using var db = await dbFactory.CreateDbContextAsync();
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId && m.AuthorId == userId && m.DeletedUtc == null)
            ?? throw new AppException("You can only edit your own messages.");
        message.Body = body;
        message.EditedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        await notifier.PublishAsync(message.ChannelId);
    }

    /// <summary>Soft delete: the row stays so thread structure survives; the text is removed.</summary>
    public async Task DeleteAsync(string userId, long messageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId && m.AuthorId == userId)
            ?? throw new AppException("You can only delete your own messages.");
        message.Body = "";
        message.DeletedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        await notifier.PublishAsync(message.ChannelId);
    }

    public async Task<IReadOnlyList<(Message Message, string ChannelName)>> SearchAsync(string userId, string query)
    {
        if (Text.NullIfBlank(query) is not { } q)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var hits = await db.Messages.AsNoTracking()
            .Where(m => m.DeletedUtc == null && m.Body.Contains(q))
            .Join(ReadableChannels(db, userId), m => m.ChannelId, c => c.Id, (m, c) => new { m, c.Name })
            .OrderByDescending(x => x.m.Id).Take(50).ToListAsync();
        return [.. hits.Select(h => (h.m, h.Name))];
    }
}
