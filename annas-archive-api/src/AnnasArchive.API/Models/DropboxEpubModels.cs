namespace AnnasArchive.API.Models;

/// <summary>
/// DTOs for Dropbox EPUB file operations.
/// </summary>

/// <summary>
/// Represents a file in Dropbox.
/// </summary>
public record DropboxEpubFileDto(string Id, string Name, string Path, long Size, DateTime ServerModified);

/// <summary>
/// Response containing EPUB chapters.
/// </summary>
public record DropboxEpubChaptersResponse(string Title, List<DropboxChapterDto> Chapters);

/// <summary>
/// DTO for a chapter in an EPUB file.
/// </summary>
public record DropboxChapterDto(int Id, string Title, int Level, int WordCount, string? DisplayLabel, bool? IsMainChapter);

/// <summary>
/// DTO for chapter content.
/// </summary>
public record DropboxChapterContentDto(int Id, string Title, string Content, int CharacterCount, int WordCount);

/// <summary>
/// Represents a flattened chapter with plain text content.
/// </summary>
public record FlatChapter(int Id, string Title, int Level, string PlainText, int WordCount);

/// <summary>
/// A chapter with its display label and main content flag.
/// </summary>
public record LabeledChapter(FlatChapter Chapter, string DisplayLabel, bool IsMainChapter);

/// <summary>
/// Result of chapter labeling operation.
/// </summary>
public record ChapterLabelResult(int Id, string DisplayLabel, bool IsMainChapter);

/// <summary>
/// Metadata for a cached chapter.
/// </summary>
public record CachedChapterMeta(int Id, string Title, int Level, int CharacterCount, int WordCount, string FileName, string? DisplayLabel, bool? IsMainChapter);

/// <summary>
/// Index of cached chapters for an EPUB file.
/// </summary>
public record CachedChapterIndex(string Path, string Title, DateTime CachedAt, List<CachedChapterMeta> Chapters, string? LabelSource = null);

/// <summary>
/// Status of Dropbox EPUB caching operation.
/// </summary>
public record DropboxCacheStatusDto(bool Cached, bool InProgress, int ChaptersTotal, int ChaptersCached, double Percent, DateTime? CachedAt, string? Error);

/// <summary>
/// DTO for a search match within a Dropbox EPUB.
/// </summary>
public record DropboxSearchMatchDto(int ChapterId, string Title, int MatchCount, int Position, string Snippet);
