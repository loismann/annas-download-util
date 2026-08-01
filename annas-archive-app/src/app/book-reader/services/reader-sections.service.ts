import { Injectable } from '@angular/core';

/** A section of a chapter, as word offsets into the chapter text. */
export interface SectionChunk {
  start: number;
  end: number;
}

export interface AnnotateOptions {
  chunks: SectionChunk[];
  /** Word offset of the first word on the visible page. */
  wordOffset: number;
  /** Words per page — the visible window is `wordOffset` to `wordOffset + pageSizeWords`. */
  pageSizeWords: number;
  /**
   * Section to shade, or null to shade nothing. The reader passes null unless it
   * is in section-analysis mode.
   */
  highlightSectionIndex: number | null;
}

/**
 * Section-boundary maths for the reading pane.
 *
 * Holds the two genuinely pure pieces of the section feature. Everything else
 * about sections (fetching boundaries, generating summaries, caching them) is
 * assignment to reader state and stays in the component — see the book-reader
 * notes in REFACTORING_TODO.md.
 *
 * Worth having on its own because both functions are index arithmetic over two
 * different coordinate systems — chapter-absolute word offsets and page-relative
 * ones — which is easy to get subtly wrong and impossible to eyeball in a 3,000
 * line component.
 */
@Injectable({ providedIn: 'root' })
export class ReaderSectionsService {
  /**
   * The section containing `wordOffset`.
   *
   * @returns the section index; `null` when there are no sections at all; or
   *   `undefined` when the offset falls in a gap between sections, which means
   *   "no answer — keep whatever is currently selected". That third case
   *   preserves long-standing behaviour: boundaries are contiguous in practice,
   *   so a gap indicates bad data, and silently jumping the reader to another
   *   section would be worse than leaving it be.
   */
  findSectionIndex(chunks: SectionChunk[], wordOffset: number): number | null | undefined {
    if (!chunks.length) return null;

    for (let i = 0; i < chunks.length; i++) {
      const chunk = chunks[i];
      if (wordOffset >= chunk.start && wordOffset < chunk.end) {
        return i;
      }
    }

    // Past the end of the last section — clamp to it.
    if (wordOffset >= chunks[chunks.length - 1].end) {
      return chunks.length - 1;
    }

    return undefined;
  }

  /**
   * Adds section-boundary markers and the current-section shading to a page of
   * already HTML-escaped text.
   *
   * Input must already be escaped: this inserts markup, so escaping afterwards
   * would destroy it.
   */
  annotate(escapedText: string, opts: AnnotateOptions): string {
    const { chunks, wordOffset, pageSizeWords, highlightSectionIndex } = opts;
    if (!chunks.length) return escapedText;

    const visibleStart = wordOffset;
    const visibleEnd = wordOffset + pageSizeWords;

    // Where each "end of section N" marker falls within this page, if at all.
    // The last section has no trailing marker, hence slice(0, -1).
    const boundaryMarkers = new Map<number, string>();
    if (chunks.length > 1) {
      chunks.slice(0, -1).forEach((chunk, index) => {
        const boundary = chunk.end;
        if (boundary < visibleStart || boundary > visibleEnd) return;
        boundaryMarkers.set(
          boundary - visibleStart,
          `${index + 1} <span class="section-marker-icon">&#9660;</span> ${index + 2}`
        );
      });
    }

    // The slice of this page covered by the highlighted section, clamped to the
    // page — a section usually starts before or ends after the visible window.
    let sectionStartInVisible: number | null = null;
    let sectionEndInVisible: number | null = null;
    if (highlightSectionIndex !== null) {
      const chunk = chunks[highlightSectionIndex];
      if (chunk && !(chunk.end <= visibleStart || chunk.start >= visibleEnd)) {
        sectionStartInVisible = Math.max(0, chunk.start - visibleStart);
        sectionEndInVisible = Math.min(pageSizeWords, chunk.end - visibleStart);
      }
    }

    // Split keeping the separators so whitespace is preserved verbatim.
    const words = escapedText.split(/(\s+)/);
    let wordCount = 0;
    let result = '';

    for (const word of words) {
      if (/^\s+$/.test(word)) {
        result += word;
        continue;
      }

      if (boundaryMarkers.has(wordCount)) {
        const marker = boundaryMarkers.get(wordCount);
        if (marker) {
          result += ` <span class="section-marker">${marker}</span> `;
          boundaryMarkers.delete(wordCount);
        }
      }

      if (
        sectionStartInVisible !== null &&
        sectionEndInVisible !== null &&
        wordCount >= sectionStartInVisible &&
        wordCount < sectionEndInVisible
      ) {
        result += `<span class="section-highlight">${word}</span>`;
      } else {
        result += word;
      }
      wordCount++;
    }

    // A boundary landing exactly at the end of the page has no following word to
    // anchor to, so it is emitted here instead.
    if (boundaryMarkers.has(wordCount)) {
      const marker = boundaryMarkers.get(wordCount);
      if (marker) {
        result += ` <span class="section-marker">${marker}</span> `;
      }
    }

    return result;
  }
}
