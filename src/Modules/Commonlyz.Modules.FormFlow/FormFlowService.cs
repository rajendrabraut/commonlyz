using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.FormFlow.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.FormFlow;

/// <summary>Immutable question as published. Responses are validated against this, never against the draft.</summary>
public sealed record QuestionSnapshot(string Key, QuestionType Type, string Prompt, string? Help, bool Required, IReadOnlyList<string> Options);

public sealed record PublicForm(int FormId, string Slug, string Title, string? Description, int VersionId, IReadOnlyList<QuestionSnapshot> Questions);

public sealed record ResponseRow(long Id, DateTime SubmittedUtc, int VersionNo, IReadOnlyDictionary<string, string> Answers);

public sealed class FormFlowService(IDbContextFactory<FormFlowDb> dbFactory, TimeProvider clock)
{
    public const int MaxAnswerLength = 5000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Editor ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<(Form Form, int Responses)>> ListFormsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var forms = await db.Forms.AsNoTracking().OrderByDescending(f => f.Id).ToListAsync();
        var counts = await db.Responses.GroupBy(r => r.FormId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        return [.. forms.Select(f => (f, counts.GetValueOrDefault(f.Id)))];
    }

    public async Task<int> CreateFormAsync(string title, string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var form = new Form { Title = Text.Required(title, "Title", 200), Slug = ValidSlug(slug), CreatedUtc = clock.UtcNowSeconds() };
        db.Forms.Add(form);
        await SaveUniqueAsync(db, form.Slug);
        return form.Id;
    }

    public async Task<(Form Form, bool HasUnpublishedChanges, int? VersionNo)> GetEditorAsync(int formId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var form = AppException.NotFound(await db.Forms.AsNoTracking().Include(f => f.Questions).FirstOrDefaultAsync(f => f.Id == formId), "Form");
        form.Questions = [.. form.Questions.OrderBy(q => q.Ordinal)];
        var version = form.PublishedVersionId is { } vid ? await db.Versions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid) : null;
        var changed = version is null || version.QuestionsJson != Serialize(Snapshot(form.Questions));
        return (form, changed, version?.VersionNo);
    }

    public async Task UpdateFormAsync(int formId, string title, string slug, string? description, bool isOpen)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var form = AppException.NotFound(await db.Forms.FindAsync(formId), "Form");
        form.Title = Text.Required(title, "Title", 200);
        form.Slug = ValidSlug(slug);
        form.Description = Text.Optional(description, "Description", 1000);
        form.IsOpen = isOpen;
        await SaveUniqueAsync(db, form.Slug);
    }

    public async Task DeleteFormAsync(int formId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Forms.Where(f => f.Id == formId).ExecuteDeleteAsync();
    }

    public async Task SaveQuestionAsync(int formId, int? questionId, QuestionType type, string prompt, string? help, bool required, string? options)
    {
        prompt = Text.Required(prompt, "Question", 500);
        help = Text.Optional(help, "Help text", 500);
        var choices = ParseOptions(options);
        if (type is QuestionType.SingleChoice or QuestionType.MultiChoice)
            AppException.ThrowIf(choices.Count < 2, "Choice questions need at least two options (one per line).");

        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await db.Forms.AnyAsync(f => f.Id == formId), "Form not found.");
        var question = questionId is { } id
            ? AppException.NotFound(await db.Questions.FirstOrDefaultAsync(q => q.Id == id && q.FormId == formId), "Question")
            : db.Questions.Add(new FormQuestion
            {
                FormId = formId,
                Prompt = prompt,
                Ordinal = (await db.Questions.Where(q => q.FormId == formId).MaxAsync(q => (int?)q.Ordinal) ?? -1) + 1,
            }).Entity;

        question.Type = type;
        question.Prompt = prompt;
        question.Help = help;
        question.IsRequired = required;
        question.Options = choices.Count > 0 ? string.Join('\n', choices) : null;
        await db.SaveChangesAsync();
    }

    public async Task DeleteQuestionAsync(int questionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Questions.Where(q => q.Id == questionId).ExecuteDeleteAsync();
    }

    public async Task MoveQuestionAsync(int questionId, int direction)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var question = AppException.NotFound(await db.Questions.FindAsync(questionId), "Question");
        var siblings = await db.Questions.Where(q => q.FormId == question.FormId).OrderBy(q => q.Ordinal).ToListAsync();
        var index = siblings.FindIndex(q => q.Id == questionId);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= siblings.Count)
            return;
        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++)
            siblings[i].Ordinal = i;
        await db.SaveChangesAsync();
    }

    /// <summary>Snapshots the draft questions into a new immutable version that respondents will see.</summary>
    public async Task<int> PublishAsync(int formId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var form = AppException.NotFound(await db.Forms.Include(f => f.Questions).FirstOrDefaultAsync(f => f.Id == formId), "Form");
        AppException.ThrowIf(form.Questions.Count == 0, "Add at least one question before publishing.");

        var next = (await db.Versions.Where(v => v.FormId == formId).MaxAsync(v => (int?)v.VersionNo) ?? 0) + 1;
        var version = new FormVersion
        {
            FormId = formId,
            VersionNo = next,
            QuestionsJson = Serialize(Snapshot(form.Questions.OrderBy(q => q.Ordinal))),
            PublishedUtc = clock.UtcNowSeconds(),
        };
        db.Versions.Add(version);
        await db.SaveChangesAsync();
        form.PublishedVersionId = version.Id;
        await db.SaveChangesAsync();
        return next;
    }

    // ── Public ───────────────────────────────────────────────────────────────

    public async Task<PublicForm?> GetPublicAsync(string slug)
    {
        slug = Text.NormalizeSlug(slug);
        await using var db = await dbFactory.CreateDbContextAsync();
        var form = await db.Forms.AsNoTracking().FirstOrDefaultAsync(f => f.Slug == slug && f.IsOpen && f.PublishedVersionId != null);
        if (form is null)
            return null;
        var version = await db.Versions.AsNoTracking().FirstAsync(v => v.Id == form.PublishedVersionId);
        return new PublicForm(form.Id, form.Slug, form.Title, form.Description, version.Id, Deserialize(version.QuestionsJson));
    }

    /// <summary>Validates every answer server-side against the published snapshot, then stores the response.</summary>
    public async Task SubmitAsync(string slug, IReadOnlyDictionary<string, string?> answers)
    {
        var form = await GetPublicAsync(slug) ?? throw new AppException("This form is closed.");
        var clean = new Dictionary<string, string>();
        foreach (var q in form.Questions)
        {
            if (ValidateAnswer(q, answers.GetValueOrDefault(q.Key)) is { } error)
                throw new AppException($"“{q.Prompt}”: {error}");
            if (Text.NullIfBlank(answers.GetValueOrDefault(q.Key)) is { } value)
                clean[q.Key] = value;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        db.Responses.Add(new FormResponse
        {
            FormId = form.FormId,
            FormVersionId = form.VersionId,
            AnswersJson = JsonSerializer.Serialize(clean, Json),
            SubmittedUtc = clock.UtcNowSeconds(),
        });
        await db.SaveChangesAsync();
    }

    /// <returns>An error message, or null if the answer is acceptable.</returns>
    public static string? ValidateAnswer(QuestionSnapshot q, string? raw)
    {
        var value = Text.NullIfBlank(raw);
        if (value is null)
            return q.Required ? "This question is required." : null;
        if (value.Length > MaxAnswerLength)
            return $"Keep it under {MaxAnswerLength} characters.";

        return q.Type switch
        {
            QuestionType.Email when !MailAddress.TryCreate(value, out _) => "Enter a valid email address.",
            QuestionType.Number when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) => "Enter a number.",
            QuestionType.Date when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => "Enter a date.",
            QuestionType.YesNo when value is not ("Yes" or "No") => "Choose yes or no.",
            QuestionType.SingleChoice when !q.Options.Contains(value) => "Choose one of the options.",
            QuestionType.MultiChoice when value.Split('\n').Any(v => !q.Options.Contains(v)) => "Choose from the options.",
            QuestionType.ShortText when value.Contains('\n', StringComparison.Ordinal) => "Use a single line.",
            _ => null,
        };
    }

    // ── Responses ────────────────────────────────────────────────────────────

    public async Task<(IReadOnlyList<QuestionSnapshot> Columns, IReadOnlyList<ResponseRow> Rows)> GetResponsesAsync(int formId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var versions = await db.Versions.AsNoTracking().Where(v => v.FormId == formId).OrderByDescending(v => v.VersionNo).ToListAsync();
        var responses = await db.Responses.AsNoTracking().Where(r => r.FormId == formId).OrderByDescending(r => r.Id).ToListAsync();

        // Columns: latest version's order first, then questions that only exist in older versions.
        var columns = new List<QuestionSnapshot>();
        foreach (var v in versions)
            foreach (var q in Deserialize(v.QuestionsJson))
                if (columns.All(c => c.Key != q.Key))
                    columns.Add(q);

        var versionNos = versions.ToDictionary(v => v.Id, v => v.VersionNo);
        var rows = responses.Select(r => new ResponseRow(
            r.Id, r.SubmittedUtc, versionNos.GetValueOrDefault(r.FormVersionId),
            JsonSerializer.Deserialize<Dictionary<string, string>>(r.AnswersJson, Json) ?? [])).ToList();
        return (columns, rows);
    }

    public async Task<string> ExportCsvAsync(int formId)
    {
        var (columns, rows) = await GetResponsesAsync(formId);
        var csv = new StringBuilder(Csv.Row([.. new object?[] { "Submitted (UTC)", "Version" }, .. columns.Select(c => c.Prompt)]));
        foreach (var row in rows)
            csv.Append(Csv.Row([.. new object?[] { Format.DateTime(row.SubmittedUtc), row.VersionNo }, .. columns.Select(c => row.Answers.GetValueOrDefault(c.Key)?.Replace('\n', ';'))]));
        return csv.ToString();
    }

    public async Task DeleteResponseAsync(long responseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Responses.Where(r => r.Id == responseId).ExecuteDeleteAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static List<string> ParseOptions(string? options) =>
        [.. (options ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(50)];

    private static List<QuestionSnapshot> Snapshot(IEnumerable<FormQuestion> questions) =>
        [.. questions.Select(q => new QuestionSnapshot(q.Key, q.Type, q.Prompt, q.Help, q.IsRequired, ParseOptions(q.Options)))];

    private static string Serialize(List<QuestionSnapshot> questions) => JsonSerializer.Serialize(questions, Json);

    private static List<QuestionSnapshot> Deserialize(string json) => JsonSerializer.Deserialize<List<QuestionSnapshot>>(json, Json) ?? [];

    private static string ValidSlug(string slug)
    {
        var normalized = Text.NormalizeSlug(slug);
        if (Text.ValidateSlug(normalized) is { } error)
            throw new AppException("Link name: " + error);
        return normalized;
    }

    private static async Task SaveUniqueAsync(FormFlowDb db, string slug)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AppException($"Link name '{slug}' is already used by another form.");
        }
    }
}
