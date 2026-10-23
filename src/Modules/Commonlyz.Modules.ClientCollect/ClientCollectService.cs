using System.Net.Mail;
using Commonlyz.Core;
using Commonlyz.Modules.ClientCollect.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.ClientCollect;

public sealed record NewItem(ItemKind Kind, string Prompt);

public sealed record FileInfoView(int Id, string FileName, long SizeBytes, DateTime UploadedUtc);

public sealed class ClientCollectService(IDbContextFactory<ClientCollectDb> dbFactory, TimeProvider clock, INotificationSender notifications)
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxFilesPerItem = 10;
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromDays(3);

    // ── Admin ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Client>> ListClientsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Clients.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<int> CreateClientAsync(string name, string email)
    {
        email = Text.Required(email, "Email", 256);
        AppException.ThrowIf(!MailAddress.TryCreate(email, out _), "Enter a valid email address.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var client = new Client { Name = Text.Required(name, "Client name", 200), Email = email };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    public async Task<IReadOnlyList<CollectRequest>> ListRequestsAsync(bool includeArchived)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Requests.AsNoTracking().Include(r => r.Client).Include(r => r.Items)
            .Where(r => includeArchived || r.Status != RequestStatus.Archived)
            .OrderByDescending(r => r.Id).ToListAsync();
    }

    /// <returns>The request id and the raw magic-link token (shown once; only its hash is stored).</returns>
    public async Task<(int Id, string Token)> CreateRequestAsync(int clientId, string title, string? message, DateOnly? dueDate, IReadOnlyList<NewItem> items)
    {
        var clean = items.Where(i => !string.IsNullOrWhiteSpace(i.Prompt)).ToList();
        AppException.ThrowIf(clean.Count == 0, "Add at least one item to collect.");
        var token = Text.RandomToken();

        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await db.Clients.AnyAsync(c => c.Id == clientId), "Choose a client.");
        var request = new CollectRequest
        {
            ClientId = clientId,
            Title = Text.Required(title, "Title", 200),
            Message = Text.Optional(message, "Message", 2000),
            DueDate = dueDate,
            TokenHash = Text.Sha256(token),
            CreatedUtc = clock.UtcNowSeconds(),
            Items = [.. clean.Select((i, n) => new RequestItem { Ordinal = n, Kind = i.Kind, Prompt = Text.Required(i.Prompt, "Item", 500) })],
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return (request.Id, token);
    }

    /// <summary>Issues a new link; the old one stops working immediately.</summary>
    public async Task<string> RegenerateLinkAsync(int requestId)
    {
        var token = Text.RandomToken();
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db.Requests.Where(r => r.Id == requestId).ExecuteUpdateAsync(s => s.SetProperty(r => r.TokenHash, Text.Sha256(token)));
        AppException.ThrowIf(updated == 0, "Request not found.");
        return token;
    }

    public async Task<(CollectRequest Request, IReadOnlyDictionary<int, IReadOnlyList<FileInfoView>> Files)> GetRequestAsync(int requestId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var request = AppException.NotFound(
            await db.Requests.AsNoTracking().Include(r => r.Client).Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == requestId), "Request");
        request.Items = [.. request.Items.OrderBy(i => i.Ordinal)];
        return (request, await FilesForAsync(db, request.Items.Select(i => i.Id)));
    }

    public async Task ReviewAsync(int itemId, bool accept, string? comment)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = AppException.NotFound(await db.Items.FindAsync(itemId), "Item");
        AppException.ThrowIf(item.Status == ItemStatus.Pending, "Nothing has been submitted for this item yet.");
        AppException.ThrowIf(!accept && string.IsNullOrWhiteSpace(comment), "Tell the client what needs changing.");
        item.Status = accept ? ItemStatus.Accepted : ItemStatus.ChangesRequested;
        item.ReviewComment = accept ? null : Text.Optional(comment, "Comment", 1000);
        await db.SaveChangesAsync();
        await UpdateCompletionAsync(db, item.RequestId);
    }

    public async Task SetArchivedAsync(int requestId, bool archived)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var request = AppException.NotFound(await db.Requests.FindAsync(requestId), "Request");
        request.Status = archived ? RequestStatus.Archived : RequestStatus.Open;
        await db.SaveChangesAsync();
        if (!archived)
            await UpdateCompletionAsync(db, requestId);
    }

    public async Task<ItemFile?> GetFileAsync(int fileId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId);
    }

    // ── Client portal (token-authenticated) ──────────────────────────────────

    public async Task<(CollectRequest Request, IReadOnlyDictionary<int, IReadOnlyList<FileInfoView>> Files)?> GetPortalAsync(string token)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var request = await FindByTokenAsync(db, token, tracking: false);
        if (request is null)
            return null;
        request.Items = [.. request.Items.OrderBy(i => i.Ordinal)];
        return (request, await FilesForAsync(db, request.Items.Select(i => i.Id)));
    }

    public async Task SubmitTextAsync(string token, int itemId, string? answer)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await PortalItemAsync(db, token, itemId, ItemKind.Text);
        item.TextAnswer = Text.Required(answer, "Answer", 10_000);
        MarkSubmitted(item);
        await db.SaveChangesAsync();
    }

    public async Task UploadAsync(string token, int itemId, string fileName, string contentType, Stream content, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await PortalItemAsync(db, token, itemId, ItemKind.File);
        AppException.ThrowIf(await db.Files.CountAsync(f => f.ItemId == itemId, ct) >= MaxFilesPerItem, $"At most {MaxFilesPerItem} files per item.");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        AppException.ThrowIf(buffer.Length == 0, "The file is empty.");
        AppException.ThrowIf(buffer.Length > MaxFileBytes, $"Files must be at most {Format.Bytes(MaxFileBytes)}.");

        var safeName = Path.GetFileName(fileName);
        db.Files.Add(new ItemFile
        {
            ItemId = itemId,
            FileName = safeName.Length > 260 ? safeName[^260..] : safeName,
            ContentType = string.IsNullOrWhiteSpace(contentType) || contentType.Length > 100 ? "application/octet-stream" : contentType,
            SizeBytes = buffer.Length,
            Content = buffer.ToArray(),
            UploadedUtc = clock.UtcNowSeconds(),
        });
        MarkSubmitted(item);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteFileAsync(string token, int itemId, int fileId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await PortalItemAsync(db, token, itemId, ItemKind.File);
        await db.Files.Where(f => f.Id == fileId && f.ItemId == item.Id).ExecuteDeleteAsync();
        if (!await db.Files.AnyAsync(f => f.ItemId == item.Id))
        {
            item.Status = ItemStatus.Pending;
            await db.SaveChangesAsync();
        }
    }

    // ── Reminders ────────────────────────────────────────────────────────────

    /// <summary>
    /// Emails clients with outstanding items whose last reminder is older than <see cref="ReminderInterval"/>.
    /// State is re-read at send time, so completed or archived requests never get a reminder.
    /// </summary>
    public async Task<int> SendDueRemindersAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNowSeconds();
        var cutoff = now - ReminderInterval;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = await db.Requests.Include(r => r.Client).Include(r => r.Items)
            .Where(r => r.Status == RequestStatus.Open
                && (r.LastReminderUtc ?? r.CreatedUtc) <= cutoff
                && r.Items.Any(i => i.Status == ItemStatus.Pending || i.Status == ItemStatus.ChangesRequested))
            .ToListAsync(ct);

        foreach (var request in due)
        {
            var outstanding = request.Items.Count(i => i.Status is ItemStatus.Pending or ItemStatus.ChangesRequested);
            await notifications.SendAsync(request.Client!.Email, $"Reminder: {request.Title}",
                $"Hi {request.Client.Name}, {outstanding} item(s) are still needed for \"{request.Title}\". Use the link we sent you to continue.", ct);
            request.LastReminderUtc = now;
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<CollectRequest?> FindByTokenAsync(ClientCollectDb db, string token, bool tracking)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
            return null;
        var hash = Text.Sha256(token);
        var query = db.Requests.Include(r => r.Client).Include(r => r.Items).Where(r => r.TokenHash == hash && r.Status != RequestStatus.Archived);
        return await (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync();
    }

    private static async Task<RequestItem> PortalItemAsync(ClientCollectDb db, string token, int itemId, ItemKind kind)
    {
        var request = await FindByTokenAsync(db, token, tracking: true) ?? throw new AppException("This link is no longer valid.");
        var item = request.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new AppException("Item not found.");
        AppException.ThrowIf(item.Kind != kind, "Wrong kind of answer for this item.");
        AppException.ThrowIf(item.Status == ItemStatus.Accepted, "This item has already been accepted.");
        return item;
    }

    private void MarkSubmitted(RequestItem item)
    {
        item.Status = ItemStatus.Submitted;
        item.SubmittedUtc = clock.UtcNowSeconds();
    }

    private async Task UpdateCompletionAsync(ClientCollectDb db, int requestId)
    {
        var request = await db.Requests.Include(r => r.Items).FirstAsync(r => r.Id == requestId);
        if (request.Status == RequestStatus.Archived)
            return;
        var complete = request.Items.All(i => i.Status == ItemStatus.Accepted);
        request.Status = complete ? RequestStatus.Complete : RequestStatus.Open;
        request.CompletedUtc = complete ? request.CompletedUtc ?? clock.UtcNowSeconds() : null;
        await db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyDictionary<int, IReadOnlyList<FileInfoView>>> FilesForAsync(ClientCollectDb db, IEnumerable<int> itemIds)
    {
        var ids = itemIds.ToList();
        // Project metadata only; never load file bytes for listings.
        var files = await db.Files.AsNoTracking().Where(f => ids.Contains(f.ItemId))
            .Select(f => new { f.ItemId, View = new FileInfoView(f.Id, f.FileName, f.SizeBytes, f.UploadedUtc) }).ToListAsync();
        return files.GroupBy(f => f.ItemId).ToDictionary(g => g.Key, g => (IReadOnlyList<FileInfoView>)[.. g.Select(x => x.View)]);
    }
}
