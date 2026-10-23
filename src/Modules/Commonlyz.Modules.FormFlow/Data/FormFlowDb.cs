using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.FormFlow.Data;

public enum QuestionType
{
    ShortText,
    LongText,
    Email,
    Number,
    Date,
    YesNo,
    SingleChoice,
    MultiChoice,
}

public sealed class Form
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public bool IsOpen { get; set; } = true;
    public int? PublishedVersionId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public List<FormQuestion> Questions { get; set; } = [];
}

/// <summary>The editable draft. Respondents only ever see an immutable <see cref="FormVersion"/> snapshot.</summary>
public sealed class FormQuestion
{
    public int Id { get; set; }
    public int FormId { get; set; }

    /// <summary>Stable across versions, so answers line up in exports even after edits.</summary>
    public string Key { get; set; } = Guid.NewGuid().ToString("N");

    public int Ordinal { get; set; }
    public QuestionType Type { get; set; }
    public required string Prompt { get; set; }
    public string? Help { get; set; }
    public bool IsRequired { get; set; }

    /// <summary>Choices for SingleChoice/MultiChoice, one per line.</summary>
    public string? Options { get; set; }
}

public sealed class FormVersion
{
    public int Id { get; set; }
    public int FormId { get; set; }
    public int VersionNo { get; set; }
    public required string QuestionsJson { get; set; }
    public DateTime PublishedUtc { get; set; }
}

public sealed class FormResponse
{
    public long Id { get; set; }
    public int FormId { get; set; }
    public int FormVersionId { get; set; }
    public required string AnswersJson { get; set; }
    public DateTime SubmittedUtc { get; set; }
}

public sealed class FormFlowDb(DbContextOptions<FormFlowDb> options) : DbContext(options)
{
    public DbSet<Form> Forms => Set<Form>();
    public DbSet<FormQuestion> Questions => Set<FormQuestion>();
    public DbSet<FormVersion> Versions => Set<FormVersion>();
    public DbSet<FormResponse> Responses => Set<FormResponse>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(FormFlowModule.Key);

        modelBuilder.Entity<Form>(e =>
        {
            e.Property(x => x.Slug).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.CreatedUtc).HasPrecision(0);
            e.HasMany(x => x.Questions).WithOne().HasForeignKey(x => x.FormId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FormQuestion>(e =>
        {
            e.ToTable("Question");
            e.Property(x => x.Key).HasMaxLength(32).IsUnicode(false);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Prompt).HasMaxLength(500);
            e.Property(x => x.Help).HasMaxLength(500);
            e.Property(x => x.Options).HasMaxLength(4000);
        });

        modelBuilder.Entity<FormVersion>(e =>
        {
            e.ToTable("Version", t => t.HasCheckConstraint("CK_Version_Json", "ISJSON([QuestionsJson]) = 1"));
            e.HasIndex(x => new { x.FormId, x.VersionNo }).IsUnique();
            e.Property(x => x.PublishedUtc).HasPrecision(0);
            e.HasOne<Form>().WithMany().HasForeignKey(x => x.FormId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FormResponse>(e =>
        {
            e.ToTable("Response", t => t.HasCheckConstraint("CK_Response_Json", "ISJSON([AnswersJson]) = 1"));
            e.Property(x => x.SubmittedUtc).HasPrecision(0);
            e.HasIndex(x => new { x.FormId, x.SubmittedUtc });
            e.HasOne<FormVersion>().WithMany().HasForeignKey(x => x.FormVersionId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Form>().WithMany().HasForeignKey(x => x.FormId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
