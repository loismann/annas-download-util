import { Injectable } from '@angular/core';
import { GENRE_KEYWORD_MAP, STANDARD_GENRES, StandardGenre } from '../constants/book-genres';

@Injectable({
  providedIn: 'root'
})
export class GenreMappingService {
  /**
   * Maps Open Library subjects to a standard genre
   * Returns the best matching genre or 'Uncategorized' if no match
   */
  mapToStandardGenre(openLibrarySubjects: string[]): StandardGenre {
    if (!openLibrarySubjects || openLibrarySubjects.length === 0) {
      return 'Uncategorized';
    }

    // Normalize subjects to lowercase for matching
    const normalizedSubjects = openLibrarySubjects.map(s => s.toLowerCase().trim());

    // Score each standard genre
    const scores: Record<string, number> = {};

    for (const [genre, keywords] of Object.entries(GENRE_KEYWORD_MAP)) {
      let score = 0;

      for (const subject of normalizedSubjects) {
        for (const keyword of keywords) {
          // Exact match
          if (subject === keyword) {
            score += 10;
          }
          // Subject contains keyword
          else if (subject.includes(keyword)) {
            score += 5;
          }
          // Keyword contains subject (less reliable)
          else if (keyword.includes(subject) && subject.length > 3) {
            score += 2;
          }
        }
      }

      if (score > 0) {
        scores[genre] = score;
      }
    }

    // Return the highest-scoring genre
    if (Object.keys(scores).length === 0) {
      return 'Uncategorized';
    }

    const sortedGenres = Object.entries(scores)
      .sort(([, scoreA], [, scoreB]) => scoreB - scoreA);

    return sortedGenres[0][0] as StandardGenre;
  }

  /**
   * Extracts useful tags from Open Library subjects
   * Filters out generic terms and the selected primary genre
   */
  extractTags(openLibrarySubjects: string[], primaryGenre: string, limit: number = 5): string[] {
    if (!openLibrarySubjects || openLibrarySubjects.length === 0) {
      return [];
    }

    // Terms to filter out (too generic or not useful as tags)
    const stopWords = new Set([
      'fiction', 'non-fiction', 'nonfiction', 'general', 'book', 'books',
      'literature', 'accessible book', 'protected daisy', 'in library',
      'open library staff picks', 'american', 'english', 'british'
    ]);

    // Filter and clean subjects
    const tags = openLibrarySubjects
      .map(s => s.trim())
      .filter(s => {
        const lower = s.toLowerCase();
        // Remove if it's a stop word
        if (stopWords.has(lower)) return false;
        // Remove if it matches the primary genre
        if (lower === primaryGenre.toLowerCase()) return false;
        // Remove if it's a character name (contains "fictitious character")
        if (lower.includes('fictitious character')) return false;
        // Remove if too generic (single letter, numbers only)
        if (s.length <= 1 || /^\d+$/.test(s)) return false;
        // Keep it
        return true;
      })
      .slice(0, limit);

    return tags;
  }

  /**
   * Gets all standard genres for dropdown/selection
   */
  getStandardGenres(): readonly StandardGenre[] {
    return STANDARD_GENRES;
  }
}
