using Commonlyz.Files.Domain;

namespace Commonlyz.Files.Application.Abstractions;

/// <summary>An upload as received by the API, before it is stored or trusted.</summary>
public sealed record IncomingFile(string FileName, long Length, Func<Stream> OpenRead);

/// <summary>A stored upload whose real type has been detected from its bytes.</summary>
public sealed record InputFile(string Path, string OriginalName, FileKind Kind, long Size)
{
    public string Stem => FileNaming.Stem(OriginalName);
}

/// <summary>A file an engine produced inside the job's output directory.</summary>
public sealed record OutputFile(string Path, FileKind Kind, int? Width = null, int? Height = null, InputFile? Source = null);

/// <summary>A finished, downloadable result.</summary>
public sealed record ResultFile(
    string Id, string Name, string ContentType, long Size, int? Width, int? Height, string? SourceName, long? SourceSize);

public sealed record JobResult(string JobId, IReadOnlyList<ResultFile> Files, DateTimeOffset ExpiresAt);

/// <summary>A result file looked up for download.</summary>
public sealed record StoredFile(string Path, string Name, string ContentType);
