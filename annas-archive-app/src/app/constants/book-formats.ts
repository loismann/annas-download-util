/** The only formats the app surfaces as pickable — search results come back
 *  in all sorts of formats (AZW3, FB2, LIT, TXT, ...), but these three cover
 *  what every household device can actually open, so they're the only ones
 *  shown as format badges/filter options. Order matters: it's also the
 *  preference order for which format a newly-grouped book defaults to
 *  displaying (see BookSearchComponent.activeBookFor). */
export const DISPLAYABLE_BOOK_FORMATS = ['EPUB', 'PDF', 'MOBI'] as const;
