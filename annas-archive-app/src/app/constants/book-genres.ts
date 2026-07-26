/**
 * Standard book genres based on BISAC classification.
 * Provides a curated, finite list to prevent genre proliferation.
 *
 * This is the single source of the genre list for the UI. Classification
 * (subjects → genre) happens only on the backend, in
 * GenreClassificationService.cs — its keyword-map keys must stay within
 * this list. When adding a genre, update both files.
 */
export const STANDARD_GENRES = [
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
  'Graphic Novel',
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
  'Art & Photography',
  'Cooking & Food',
  'Health & Fitness',

  // Other
  'Poetry',
  'Drama',
  'Reference',
  'Uncategorized'
] as const;

export type StandardGenre = typeof STANDARD_GENRES[number];
