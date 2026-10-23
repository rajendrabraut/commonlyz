using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.TeamWiki.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.TeamWiki;

public sealed record TreeNode(int Id, int? ParentId, string Title, int Position);

public sealed record SearchHit(int Id, string Title, string Snippet);

public sealed class WikiService(IDbContextFactory<TeamWikiDb> dbFactory, TimeProvider clock)
{
    public async Task<IReadOnlyList<TreeNode>> TreeAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Pages.AsNoTracking().OrderBy(p => p.Position).ThenBy(p => p.Title)
            .Select(p => new TreeNode(p.Id, p.ParentId, p.Title, p.Position)).ToListAsync();
    }

    public async Task<WikiPage> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return AppException.NotFound(await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id), "Page");
    }

    public static IReadOnlyList<TreeNode> Breadcrumbs(IReadOnlyList<TreeNode> tree, int id)
    {
        var byId = tree.ToDictionary(n => n.Id);
        var trail = new List<TreeNode>();
        for (int? cursor = id; cursor is { } c && byId.TryGetValue(c, out var node) && trail.Count < 50; cursor = node.ParentId)
            trail.Insert(0, node);
        return trail;
    }

    public async Task<int> CreateAsync(int? parentId, string title, string user)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(parentId is { } p && !await db.Pages.AnyAsync(x => x.Id == p), "Parent page not found.");
        var now = clock.UtcNowSeconds();
        var page = new WikiPage
        {
            ParentId = parentId,
            Title = Text.Required(title, "Title", WikiPage.MaxTitleLength),
            Position = (await db.Pages.Where(x => x.ParentId == parentId).MaxAsync(x => (int?)x.Position) ?? 0) + 1,
            UpdatedBy = Clip(user),
            UpdatedUtc = now,
            CreatedUtc = now,
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }

    /// <summary>Saves an edit and records a revision. Throws if someone saved the page after <paramref name="rowVersion"/> was read.</summary>
    public async Task<byte[]> SaveAsync(int id, string title, string body, int? parentId, byte[] rowVersion, string user)
    {
        title = Text.Required(title, "Title", WikiPage.MaxTitleLength);
        AppException.ThrowIf(body.Length > WikiPage.MaxBodyLength, "Page is too long.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var page = AppException.NotFound(await db.Pages.FindAsync(id), "Page");
        if (parentId != page.ParentId)
            await EnsureValidParentAsync(db, id, parentId);

        // Compare against the version the editor started from, not the one we just loaded.
        db.Entry(page).Property(p => p.RowVersion).OriginalValue = rowVersion;
        page.Title = title;
        page.Body = body;
        page.ParentId = parentId;
        page.UpdatedBy = Clip(user);
        page.UpdatedUtc = clock.UtcNowSeconds();
        db.Revisions.Add(new PageRevision { PageId = id, Title = title, Body = body, SavedBy = page.UpdatedBy, SavedUtc = page.UpdatedUtc });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppException("Someone else saved this page while you were editing. Copy your changes, reload, and apply them again.");
        }
        return page.RowVersion;
    }

    /// <summary>Deletes a page; its children move up to the deleted page's parent rather than disappearing.</summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = AppException.NotFound(await db.Pages.FindAsync(id), "Page");
        await db.InTransactionAsync(async () =>
        {
            await db.Pages.Where(p => p.ParentId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ParentId, page.ParentId));
            await db.Pages.Where(p => p.Id == id).ExecuteDeleteAsync();
        });
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query)
    {
        if (Text.NullIfBlank(query) is not { } q)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var pages = await db.Pages.AsNoTracking()
            .Where(p => p.Title.Contains(q) || p.Body.Contains(q))
            .OrderByDescending(p => p.Title.Contains(q)).ThenByDescending(p => p.UpdatedUtc)
            .Take(30).Select(p => new { p.Id, p.Title, p.Body }).ToListAsync();
        return [.. pages.Select(p => new SearchHit(p.Id, p.Title, Snippet(p.Body, q)))];
    }

    public async Task<IReadOnlyList<PageRevision>> RevisionsAsync(int pageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Revisions.AsNoTracking().Where(r => r.PageId == pageId).OrderByDescending(r => r.Id).Take(100).ToListAsync();
    }

    public async Task<IReadOnlyList<WikiPage>> RecentAsync(int take = 10)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Pages.AsNoTracking().OrderByDescending(p => p.UpdatedUtc).Take(take).ToListAsync();
    }

    public async Task RestoreAsync(long revisionId, string user)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var revision = AppException.NotFound(await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == revisionId), "Revision");
        var page = AppException.NotFound(await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == revision.PageId), "Page");
        await SaveAsync(page.Id, revision.Title, revision.Body, page.ParentId, page.RowVersion, user);
    }

    /// <summary>A page can't become a child of itself or of one of its descendants.</summary>
    private static async Task EnsureValidParentAsync(TeamWikiDb db, int pageId, int? newParentId)
    {
        if (newParentId is null)
            return;
        var parents = await db.Pages.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.ParentId);
        AppException.ThrowIf(!parents.ContainsKey(newParentId.Value), "Parent page not found.");
        for (int? cursor = newParentId; cursor is { } c; cursor = parents.GetValueOrDefault(c))
            AppException.ThrowIf(c == pageId, "A page can't be moved inside itself or one of its sub-pages.");
    }

    private static string Snippet(string body, string query)
    {
        var flat = body.ReplaceLineEndings(" ");
        var index = flat.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return flat.Length <= 160 ? flat : flat[..160] + "…";
        var start = Math.Max(0, index - 60);
        var length = Math.Min(flat.Length - start, 160);
        return (start > 0 ? "…" : "") + flat.Substring(start, length) + (start + length < flat.Length ? "…" : "");
    }

    private static string Clip(string user) => user.Length > 100 ? user[..100] : user;
}
