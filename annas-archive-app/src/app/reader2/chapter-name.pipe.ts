import { Pipe, PipeTransform } from '@angular/core';
import { ChapterInfo } from './reader2.models';

/**
 * What to call a chapter, given its index.
 *
 * <p><b>The book's own title, not a count.</b> Every chapter index in the story
 * model is a position in the spine — front matter, copyright page and all — so a
 * novel whose first chapter is the twelfth file was reporting "Ch 12" for what
 * the contents list, three inches to the left, called "Chapter One". Two numbers
 * for one chapter is worse than no number: the reader cannot tell which is
 * wrong, and the model's is the one that looks authoritative.</p>
 *
 * <p>Falls back to counting only when the list is not loaded or the index is off
 * the end, because a label that says something is better than a blank.</p>
 */
export function chapterName(chapters: ChapterInfo[], index: number | null | undefined): string {
  if (index === null || index === undefined) return '';

  return chapters[index]?.title ?? `Chapter ${index + 1}`;
}

/** Pure: both the index and the list are arguments, so it recomputes when either changes. */
@Pipe({ name: 'chapterName', standalone: true })
export class ChapterNamePipe implements PipeTransform {
  transform(index: number | null | undefined, chapters: ChapterInfo[]): string {
    return chapterName(chapters, index);
  }
}
