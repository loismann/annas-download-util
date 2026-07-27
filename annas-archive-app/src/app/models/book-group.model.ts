import { BookDto } from './book-dto.model';

/** One "same book" cluster — everything in `books` is the same underlying
 *  work per the AI grouping call (see AiApiService.groupSearchResults),
 *  just different formats or duplicate uploads/scans. */
export interface BookGroup {
  /** Stable key for *ngFor trackBy and per-group UI state — the md5 of
   *  whichever book happened to be first in the group when it was formed. */
  key: string;
  books: BookDto[];
}
