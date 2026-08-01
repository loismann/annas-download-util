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
   * Prepares AI/Wikipedia HTML for rendering in the Learn More panel.
   *
   * Every step here exists because of real upstream output, so resist
   * "simplifying" it:
   *  - Models wrap HTML in ``` fences even when told not to.
   *  - Wikipedia emits protocol-relative image URLs (`//upload.wikimedia.org/…`),
   *    which resolve to nothing over https and must be given a scheme.
   *  - Wikipedia blocks hotlinked images unless the referrer is suppressed, so
   *    `referrerpolicy="no-referrer"` is load-bearing, not decoration.
   *  - `onerror` hides images that 404 rather than leaving a broken-image icon
   *    mid-paragraph.
   *
   * The output is passed through DomSanitizer by the caller, which is what
   * strips scripts — doing it here as well would be redundant and would also
   * remove the `onerror` this adds.
   */
  cleanModelHtml(text: string): string {
    if (!text) return '';
    // Strip common code fences (```html ... ```)
    let cleaned = text.replace(/```[\s]*html?/gi, '').replace(/```/g, '').trim();
    // Normalize double-slash image URLs to https and encode spaces/whitespace
    cleaned = cleaned.replace(/<img([^>]+)src="(\/\/|https?:\/\/)([^"]+)"/gi, (_m, pre, _proto, rest) => {
      const encoded = encodeURI(rest.trim().replace(/\s+/g, '_'));
      return `<img${pre}src="https://${encoded}"`;
    });
    // Ensure images have lazy loading, referrer policy, error hide, and basic styling
    cleaned = cleaned.replace(/<img([^>]*?)>/gi, (_match, attrs) => {
      const hasLoading = /loading\s*=/.test(attrs);
      const hasReferrer = /referrerpolicy\s*=/.test(attrs);
      const hasStyle = /style\s*=/.test(attrs);
      const hasOnError = /onerror\s*=/.test(attrs);
      const styleAppend = 'display:block;margin:6px 0;max-width:100%;border-radius:8px;';

      let finalAttrs = `${attrs}`;
      if (!hasLoading) finalAttrs += ' loading="lazy"';
      if (!hasReferrer) finalAttrs += ' referrerpolicy="no-referrer"';
      if (!hasOnError) finalAttrs += ' onerror="this.style.display=\'none\'"';
      if (!hasStyle) finalAttrs += ` style="${styleAppend}"`;

      return `<img${finalAttrs}>`;
    });
    return cleaned;
  }

  /**
   * Extracts Wikipedia article URLs from HTML content.
   *
   * Deliberately restricted to `href="…"` attributes on `en.wikipedia.org`:
   * the result feeds an image lookup for the article, and bare URLs appearing
   * in prose (or other-language wikis) are not articles we can resolve.
   */
  extractWikipediaUrls(html: string): string[] {
    const urls: string[] = [];
    // Match Wikipedia URLs in href attributes
    const regex = /href="(https?:\/\/en\.wikipedia\.org\/wiki\/[^"]+)"/gi;
    let match;
    while ((match = regex.exec(html)) !== null) {
      if (!urls.includes(match[1])) {
        urls.push(match[1]);
      }
    }
    return urls;
  }

  /**
   * Extracts the page title from a Wikipedia URL.
   *
   * Underscores are preserved — the result is used as an API path segment,
   * where `Test_Article` is correct and `Test Article` is not.
   *
   * A fragment or query string is dropped: models routinely link to a section
   * (`/wiki/Dune#Plot`), and carrying that into the title made the image lookup
   * miss, so the article rendered with no pictures.
   */
  getWikipediaTitleFromUrl(url: string): string {
    // Extract title from URL like https://en.wikipedia.org/wiki/Article_Title
    const match = url.match(/\/wiki\/([^#?]+)/);
    if (match) {
      return decodeURIComponent(match[1]);
    }
    return '';
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
   *
   * Chapter number comes from the chapter's position in the list, not its `id`
   * — EPUB ids are not contiguous and routinely start at 0 or skip values.
   * Labels that already name themselves ("Chapter 4", "IV") are abbreviated
   * rather than repeated, so the dropdown stays narrow.
   */
  formatBookmarkLabel(
    chapterId: number,
    wordOffset: number,
    pageSizeWords: number,
    chapters: Array<{ id: number; title: string; displayLabel?: string | null }>
  ): string {
    const chapter = chapters.find(ch => ch.id === chapterId);
    const chapterIndex = chapters.findIndex(ch => ch.id === chapterId);
    const chapterNumber = chapterIndex >= 0 ? chapterIndex + 1 : chapterId + 1;
    const page = Math.floor(wordOffset / Math.max(1, pageSizeWords)) + 1;
    const chapterLabel = chapter?.displayLabel || chapter?.title || `Chapter ${chapterNumber}`;
    const normalized = chapterLabel.trim();
    const chapterMatch = normalized.match(/^chapter\s+(\d+)/i);
    if (chapterMatch) {
      return `Ch. ${chapterMatch[1]} p. ${page}`;
    }

    const romanMatch = normalized.match(/^([ivxlcdm]+)\b/i);
    if (romanMatch && !/^chapter\b/i.test(normalized)) {
      return `${romanMatch[1].toLowerCase()} p. ${page}`;
    }

    return `${chapterLabel} • p. ${page}`;
  }
}
