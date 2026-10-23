using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.LinkHub.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.LinkHub;

public sealed class LinkHubService(IDbContextFactory<LinkHubDb> dbFactory, TimeProvider clock)
{
    public async Task<IReadOnlyList<Profile>> ListProfilesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Profiles.AsNoTracking().Include(p => p.Links).OrderBy(p => p.Handle).ToListAsync();
    }

    public async Task<Profile> GetProfileAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var profile = AppException.NotFound(await db.Profiles.AsNoTracking().Include(p => p.Links).FirstOrDefaultAsync(p => p.Id == id), "Profile");
        profile.Links = [.. profile.Links.OrderBy(l => l.SortOrder)];
        return profile;
    }

    /// <summary>Published profile with only non-draft links, or null.</summary>
    public async Task<Profile?> GetPublicAsync(string handle)
    {
        handle = Text.NormalizeSlug(handle);
        await using var db = await dbFactory.CreateDbContextAsync();
        var profile = await db.Profiles.AsNoTracking().Include(p => p.Links.Where(l => !l.IsDraft))
            .FirstOrDefaultAsync(p => p.Handle == handle && p.IsPublished);
        if (profile is not null)
            profile.Links = [.. profile.Links.OrderBy(l => l.SortOrder)];
        return profile;
    }

    public async Task<int> CreateProfileAsync(string handle, string displayName)
    {
        var profile = new Profile
        {
            Handle = ValidHandle(handle),
            DisplayName = Text.Required(displayName, "Display name", 100),
            UpdatedUtc = clock.UtcNowSeconds(),
        };
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Profiles.Add(profile);
        await SaveUniqueAsync(db, profile.Handle);
        return profile.Id;
    }

    public async Task UpdateProfileAsync(int id, string handle, string displayName, string? bio, string? avatarUrl, string accentColor, bool isPublished)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var profile = AppException.NotFound(await db.Profiles.FindAsync(id), "Profile");
        profile.Handle = ValidHandle(handle);
        profile.DisplayName = Text.Required(displayName, "Display name", 100);
        profile.Bio = Text.Optional(bio, "Bio", 280);
        profile.AvatarUrl = Text.NullIfBlank(avatarUrl) is { } url ? Text.HttpUrl(url, "Avatar URL", 500) : null;
        AppException.ThrowIf(!Text.IsColor(accentColor), "Accent colour must look like #2563eb.");
        profile.AccentColor = accentColor;
        profile.IsPublished = isPublished;
        profile.UpdatedUtc = clock.UtcNowSeconds();
        await SaveUniqueAsync(db, profile.Handle);
    }

    public async Task DeleteProfileAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Profiles.Where(p => p.Id == id).ExecuteDeleteAsync();
    }

    public async Task AddLinkAsync(int profileId, string title, string url, bool isDraft)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await db.Profiles.AnyAsync(p => p.Id == profileId), "Profile not found.");
        var next = await db.Links.Where(l => l.ProfileId == profileId).MaxAsync(l => (int?)l.SortOrder) ?? -1;
        db.Links.Add(new ProfileLink
        {
            ProfileId = profileId,
            Title = Text.Required(title, "Title", 100),
            Url = Text.HttpUrl(url),
            IsDraft = isDraft,
            SortOrder = next + 1,
        });
        await db.SaveChangesAsync();
    }

    public async Task UpdateLinkAsync(int linkId, string title, string url, bool isDraft)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = AppException.NotFound(await db.Links.FindAsync(linkId), "Link");
        link.Title = Text.Required(title, "Title", 100);
        link.Url = Text.HttpUrl(url);
        link.IsDraft = isDraft;
        await db.SaveChangesAsync();
    }

    public async Task DeleteLinkAsync(int linkId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Links.Where(l => l.Id == linkId).ExecuteDeleteAsync();
    }

    /// <summary>Swaps a link with its neighbour (direction -1 = up, +1 = down) in one transaction.</summary>
    public async Task MoveLinkAsync(int linkId, int direction)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = AppException.NotFound(await db.Links.FindAsync(linkId), "Link");
        var siblings = await db.Links.Where(l => l.ProfileId == link.ProfileId).OrderBy(l => l.SortOrder).ToListAsync();
        var index = siblings.FindIndex(l => l.Id == linkId);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= siblings.Count)
            return;

        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++)
            siblings[i].SortOrder = i;   // renumber: also heals any gaps or duplicates
        await db.SaveChangesAsync();
    }

    /// <summary>Counts a click and returns the destination, or null if the link isn't publicly visible.</summary>
    public async Task<string?> RecordClickAsync(string handle, int linkId, CancellationToken ct)
    {
        handle = Text.NormalizeSlug(handle);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var url = await db.Links
            .Where(l => l.Id == linkId && !l.IsDraft && db.Profiles.Any(p => p.Id == l.ProfileId && p.Handle == handle && p.IsPublished))
            .Select(l => l.Url).FirstOrDefaultAsync(ct);
        if (url is not null)
            await db.Links.Where(l => l.Id == linkId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ClickCount, l => l.ClickCount + 1), ct);
        return url;
    }

    private static string ValidHandle(string handle)
    {
        var normalized = Text.NormalizeSlug(handle);
        if (Text.ValidateSlug(normalized, max: Profile.MaxHandleLength) is { } error)
            throw new AppException("Handle: " + error);
        return normalized;
    }

    private static async Task SaveUniqueAsync(LinkHubDb db, string handle)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AppException($"Handle '{handle}' is already taken.");
        }
    }
}
