using AnnasArchive.API.Helpers.Cache;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Facade for AI content caching operations.
/// Delegates to specialized cache modules for cleaner organization.
/// </summary>
public static class AiContentCache
{
    // ─── Base utilities (delegated to AiCacheBase) ─────────────────────────────
    public static string SanitizeKey(string input) => AiCacheBase.SanitizeKey(input);
    public static HashSet<string> GetExistingSummaryKeys() => AiCacheBase.GetExistingSummaryKeys();
    public static bool HasAnySummaries(string key, ISet<string> existingKeys) => AiCacheBase.HasAnySummaries(key, existingKeys);
    public static bool DeleteAllAiCacheForBook(string dropboxPath) => AiCacheBase.DeleteAllAiCacheForBook(dropboxPath);

    // ─── Chapter summaries (delegated to ChapterSummaryCache) ──────────────────
    public static string GetChapterSummaryCachePath(string dropboxPath, int chapterId) =>
        ChapterSummaryCache.GetChapterSummaryCachePath(dropboxPath, chapterId);
    public static string GetUltraChapterSummaryCachePath(string dropboxPath, int chapterId) =>
        ChapterSummaryCache.GetUltraChapterSummaryCachePath(dropboxPath, chapterId);
    public static void SaveChapterSummary(string dropboxPath, int chapterId, object summaryData) =>
        ChapterSummaryCache.SaveChapterSummary(dropboxPath, chapterId, summaryData);
    public static void SaveUltraChapterSummary(string dropboxPath, int chapterId, object summaryData) =>
        ChapterSummaryCache.SaveUltraChapterSummary(dropboxPath, chapterId, summaryData);
    public static T? LoadChapterSummary<T>(string dropboxPath, int chapterId) where T : class =>
        ChapterSummaryCache.LoadChapterSummary<T>(dropboxPath, chapterId);
    public static T? LoadUltraChapterSummary<T>(string dropboxPath, int chapterId) where T : class =>
        ChapterSummaryCache.LoadUltraChapterSummary<T>(dropboxPath, chapterId);
    public static bool ChapterSummaryExists(string dropboxPath, int chapterId) =>
        ChapterSummaryCache.ChapterSummaryExists(dropboxPath, chapterId);
    public static bool UltraChapterSummaryExists(string dropboxPath, int chapterId) =>
        ChapterSummaryCache.UltraChapterSummaryExists(dropboxPath, chapterId);
    public static void DeleteChapterSummary(string dropboxPath, int chapterId) =>
        ChapterSummaryCache.DeleteChapterSummary(dropboxPath, chapterId);
    public static void DeleteUltraChapterSummary(string dropboxPath, int chapterId) =>
        ChapterSummaryCache.DeleteUltraChapterSummary(dropboxPath, chapterId);
    public static Dictionary<int, Dictionary<string, object>> LoadAllChapterSummaries(string dropboxPath) =>
        ChapterSummaryCache.LoadAllChapterSummaries(dropboxPath);
    public static List<string> GetAllChapterSummariesAsStrings(string dropboxPath) =>
        ChapterSummaryCache.GetAllChapterSummariesAsStrings(dropboxPath);

    // ─── Section summaries (delegated to SectionSummaryCache) ──────────────────
    public static string GetChunkBoundariesCachePath(string dropboxPath, int chapterId) =>
        SectionSummaryCache.GetChunkBoundariesCachePath(dropboxPath, chapterId);
    public static void SaveChunkBoundaries(string dropboxPath, int chapterId, List<ChunkBoundary> chunks) =>
        SectionSummaryCache.SaveChunkBoundaries(dropboxPath, chapterId, chunks);
    public static ChunkBoundariesResponse? LoadChunkBoundaries(string dropboxPath, int chapterId) =>
        SectionSummaryCache.LoadChunkBoundaries(dropboxPath, chapterId);
    public static string GetSectionSummaryCachePath(string dropboxPath, int chapterId, int sectionIndex) =>
        SectionSummaryCache.GetSectionSummaryCachePath(dropboxPath, chapterId, sectionIndex);
    public static void SaveSectionSummary(string dropboxPath, int chapterId, int sectionIndex, object summaryData) =>
        SectionSummaryCache.SaveSectionSummary(dropboxPath, chapterId, sectionIndex, summaryData);
    public static SectionSummaryResponse? LoadSectionSummary(string dropboxPath, int chapterId, int sectionIndex) =>
        SectionSummaryCache.LoadSectionSummary(dropboxPath, chapterId, sectionIndex);
    public static bool SectionSummaryExists(string dropboxPath, int chapterId, int sectionIndex) =>
        SectionSummaryCache.SectionSummaryExists(dropboxPath, chapterId, sectionIndex);
    public static string GetSectionVocabCachePath(string dropboxPath, int chapterId, int sectionIndex) =>
        SectionSummaryCache.GetSectionVocabCachePath(dropboxPath, chapterId, sectionIndex);
    public static void SaveSectionVocab(string dropboxPath, int chapterId, int sectionIndex, List<FlashcardItem> vocabCards) =>
        SectionSummaryCache.SaveSectionVocab(dropboxPath, chapterId, sectionIndex, vocabCards);
    public static List<FlashcardItem>? LoadSectionVocab(string dropboxPath, int chapterId, int sectionIndex) =>
        SectionSummaryCache.LoadSectionVocab(dropboxPath, chapterId, sectionIndex);
    public static List<string> GetAllSectionSummaries(string dropboxPath) =>
        SectionSummaryCache.GetAllSectionSummaries(dropboxPath);

    // ─── Character graphs (delegated to CharacterGraphCache) ───────────────────
    public static string GetCharacterGraphCachePath(string dropboxPath) =>
        CharacterGraphCache.GetCharacterGraphCachePath(dropboxPath);
    public static void SaveCharacterGraph(string dropboxPath, CharacterGraphResponse graph) =>
        CharacterGraphCache.SaveCharacterGraph(dropboxPath, graph);
    public static CharacterGraphResponse? LoadCharacterGraph(string dropboxPath) =>
        CharacterGraphCache.LoadCharacterGraph(dropboxPath);
    public static bool CharacterGraphExists(string dropboxPath) =>
        CharacterGraphCache.CharacterGraphExists(dropboxPath);

    // ─── Vocabulary (delegated to VocabularyCache) ─────────────────────────────
    public static string NormalizeTerm(string term) => VocabularyCache.NormalizeTerm(term);
    public static string GetKnownWordsPath() => VocabularyCache.GetKnownWordsPath();
    public static string GetStudyWordsPath() => VocabularyCache.GetStudyWordsPath();
    public static Dictionary<string, List<string>> LoadKnownWordsWithBooks() =>
        VocabularyCache.LoadKnownWordsWithBooks();
    public static void SaveKnownWordsWithBooks(Dictionary<string, List<string>> knownWords) =>
        VocabularyCache.SaveKnownWordsWithBooks(knownWords);
    public static HashSet<string> LoadKnownWords() => VocabularyCache.LoadKnownWords();
    public static Dictionary<string, (string definition, List<string> books)> LoadStudyWordsWithBooks() =>
        VocabularyCache.LoadStudyWordsWithBooks();
    public static void SaveStudyWordsWithBooks(Dictionary<string, (string definition, List<string> books)> studyWords) =>
        VocabularyCache.SaveStudyWordsWithBooks(studyWords);
}
