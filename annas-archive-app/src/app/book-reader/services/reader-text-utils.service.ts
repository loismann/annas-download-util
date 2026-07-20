import { Injectable } from '@angular/core';

/**
 * Service for text processing utilities used by the book reader.
 * Contains pure functions for text manipulation, escaping, and formatting.
 */
@Injectable({
  providedIn: 'root'
})
export class ReaderTextUtilsService {

  /**
   * Counts the number of words in a text string.
   * Uses the same word boundary logic as sliceByWords.
   */
  countWords(text: string): number {
    if (!text) return 0;
    const regex = /\S+/g;
    const matches = text.match(regex);
    return matches ? matches.length : 0;
  }

  /**
   * Extracts a slice of text by word offset and count.
   * @param text The source text
   * @param startWord The starting word index (0-based)
   * @param count The number of words to include
   * @returns The sliced text
   */
  sliceByWords(text: string, startWord: number, count: number): string {
    if (!text) return '';

    const regex = /\S+/g;
    let match: RegExpExecArray | null;
    let wordIndex = 0;
    let startIdx: number | null = null;
    let endIdx: number | null = null;

    while ((match = regex.exec(text)) !== null) {
      if (wordIndex === startWord) startIdx = match.index;
      if (wordIndex === startWord + count) {
        endIdx = match.index;
        break;
      }
      wordIndex++;
    }

    if (startIdx === null) return '';
    if (endIdx === null) endIdx = text.length;
    return text.slice(startIdx, endIdx);
  }

  /**
   * Escapes HTML special characters for safe rendering.
   */
  escapeHtml(value: string): string {
    if (!value) return '';
    return value
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  /**
   * Escapes special characters for use in a regular expression.
   */
  escapeRegExp(value: string): string {
    if (!value) return '';
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }

  /**
   * Collapses multiple blank lines into a maximum of two newlines.
   * Also trims leading and trailing whitespace.
   */
  collapseBlankLines(text: string): string {
    if (!text) return '';
    return text
      .replace(/\n[ \t]*\n[ \t]*\n+/g, '\n\n')
      .replace(/\n{3,}/g, '\n\n')
      .replace(/^\s*\n+/, '')
      .replace(/\n+\s*$/, '');
  }

  /**
   * Capitalizes the first letter of each word.
   */
  capitalizeWords(text: string): string {
    if (!text) return '';
    return text
      .toLowerCase()
      .split(' ')
      .map(word => {
        if (word.length === 0) return word;
        return word.charAt(0).toUpperCase() + word.slice(1);
      })
      .join(' ');
  }

  /**
   * Formats markdown text as HTML for display.
   * Handles bold, italic, bullet points, and numbered lists.
   */
  formatAsHtml(text: string): string {
    if (!text) return '';

    let formatted = text;

    // Convert **bold** to <strong>bold</strong>
    formatted = formatted.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');

    // Convert *italic* to <em>italic</em>
    formatted = formatted.replace(/\*([^*]+)\*/g, '<em>$1</em>');

    // Convert bullet points (- item) to HTML list items
    const lines = formatted.split('\n');
    const processedLines: string[] = [];
    let inList = false;

    for (const line of lines) {
      const trimmed = line.trim();

      if (trimmed.startsWith('- ')) {
        if (!inList) {
          processedLines.push('<ul>');
          inList = true;
        }
        processedLines.push(`<li>${trimmed.slice(2)}</li>`);
      } else if (/^\d+\.\s/.test(trimmed)) {
        // Numbered list
        if (!inList) {
          processedLines.push('<ol>');
          inList = true;
        }
        processedLines.push(`<li>${trimmed.replace(/^\d+\.\s/, '')}</li>`);
      } else {
        if (inList) {
          // Check if we were in unordered or ordered list
          const lastListTag = [...processedLines].reverse().find((l: string) => l === '<ul>' || l === '<ol>');
          processedLines.push(lastListTag === '<ol>' ? '</ol>' : '</ul>');
          inList = false;
        }
        processedLines.push(line);
      }
    }

    if (inList) {
      const lastListTag = [...processedLines].reverse().find((l: string) => l === '<ul>' || l === '<ol>');
      processedLines.push(lastListTag === '<ol>' ? '</ol>' : '</ul>');
    }

    return processedLines.join('\n');
  }

  /**
   * Cleans HTML from AI model output for safe display.
   * Removes script tags and limits link attributes.
   */
  cleanModelHtml(text: string): string {
    if (!text) return '';

    let cleaned = text;

    // Remove script tags and their content
    cleaned = cleaned.replace(/<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>/gi, '');

    // Remove event handlers
    cleaned = cleaned.replace(/\s*on\w+\s*=\s*"[^"]*"/gi, '');
    cleaned = cleaned.replace(/\s*on\w+\s*=\s*'[^']*'/gi, '');

    // Ensure links open in new tab and have noopener
    cleaned = cleaned.replace(/<a\s+([^>]*href="[^"]*"[^>]*)>/gi, (match, attrs) => {
      // Check if target and rel already exist
      let finalAttrs = attrs;
      if (!/target\s*=/i.test(attrs)) {
        finalAttrs += ' target="_blank"';
      }
      if (!/rel\s*=/i.test(attrs)) {
        finalAttrs += ' rel="noopener noreferrer"';
      }
      return `<a ${finalAttrs}>`;
    });

    // Limit img tags to safe attributes
    cleaned = cleaned.replace(/<img\s+([^>]*)>/gi, (match, attrs) => {
      const srcMatch = attrs.match(/src\s*=\s*"([^"]*)"/i);
      const altMatch = attrs.match(/alt\s*=\s*"([^"]*)"/i);

      let finalAttrs = '';
      if (srcMatch) finalAttrs += ` src="${srcMatch[1]}"`;
      if (altMatch) finalAttrs += ` alt="${altMatch[1]}"`;
      finalAttrs += ' style="max-width: 100%; height: auto;"';

      return `<img${finalAttrs}>`;
    });

    return cleaned;
  }

  /**
   * Extracts Wikipedia URLs from HTML content.
   */
  extractWikipediaUrls(html: string): string[] {
    if (!html) return [];
    const regex = /https?:\/\/[a-z]{2,3}\.wikipedia\.org\/wiki\/[^\s"'<>]+/gi;
    const matches = html.match(regex);
    return matches ? [...new Set(matches)] : [];
  }

  /**
   * Extracts the page title from a Wikipedia URL.
   */
  getWikipediaTitleFromUrl(url: string): string {
    if (!url) return '';
    const match = url.match(/\/wiki\/([^#?]+)/);
    return match ? decodeURIComponent(match[1].replace(/_/g, ' ')) : '';
  }

  /**
   * Formats a chapter label for display, truncating if necessary.
   */
  truncateChapterLabel(label: string, maxLength: number = 20): string {
    if (!label) return '';
    if (label.length <= maxLength) return label;
    return label.slice(0, maxLength) + '...';
  }

  /**
   * Formats a bookmark label showing chapter and page information.
   */
  formatBookmarkLabel(
    chapterId: number,
    wordOffset: number,
    pageSizeWords: number,
    chapters: Array<{ chapterId: number; title: string }>
  ): string {
    const chapter = chapters.find(c => c.chapterId === chapterId);
    const chapterTitle = chapter?.title ?? `Chapter ${chapterId}`;

    // Try to extract chapter number from title
    const numMatch = chapterTitle.match(/(?:Chapter|Ch\.?)\s*(\d+|[IVXLCDM]+)/i);
    const chapterNum = numMatch ? numMatch[1] : String(chapterId);

    const page = Math.floor(wordOffset / pageSizeWords) + 1;
    return `Ch ${chapterNum} - Page ${page}`;
  }
}
