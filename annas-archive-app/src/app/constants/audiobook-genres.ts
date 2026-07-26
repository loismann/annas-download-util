/**
 * Standard audiobook genres — same curated, finite philosophy as
 * STANDARD_GENRES (book-genres.ts), to prevent genre proliferation, but
 * trimmed to categories that make sense for listening rather than reading
 * (drops print-only descriptors like Graphic Novel/Reference).
 */
export const AUDIOBOOK_GENRES = [
  // Fiction
  'Science Fiction',
  'Fantasy',
  'Mystery & Detective',
  'Thriller',
  'Romance',
  'Historical Fiction',
  'Literary Fiction',
  'Horror',
  'Adventure',
  'Young Adult',
  'Children\'s',
  'Short Stories',
  'Classics',

  // Non-Fiction
  'Biography & Memoir',
  'History',
  'Science & Technology',
  'Philosophy',
  'Self-Help',
  'Business & Economics',
  'Travel',
  'True Crime',
  'Essays',
  'Politics & Current Events',
  'Religion & Spirituality',
  'Health & Fitness',

  // Other
  'Full-Cast Drama',
  'Poetry',
  'Uncategorized'
] as const;

export type AudiobookGenre = typeof AUDIOBOOK_GENRES[number];
