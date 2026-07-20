/**
 * Standard book genres based on BISAC classification
 * Provides a curated, finite list to prevent genre proliferation
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

/**
 * Maps Open Library subject keywords to standard genres
 */
export const GENRE_KEYWORD_MAP: Record<string, string[]> = {
  'Science Fiction': [
    'science fiction', 'sci-fi', 'scifi', 'space opera', 'cyberpunk',
    'dystopia', 'dystopian', 'time travel', 'space', 'aliens',
    'future', 'robots', 'artificial intelligence'
  ],
  'Fantasy': [
    'fantasy', 'magic', 'wizards', 'dragons', 'sword and sorcery',
    'epic fantasy', 'urban fantasy', 'paranormal', 'mythical',
    'fairy tale', 'elves', 'supernatural'
  ],
  'Mystery & Detective': [
    'mystery', 'detective', 'crime', 'murder', 'investigation',
    'whodunit', 'noir', 'police procedural', 'sleuth', 'clues'
  ],
  'Thriller': [
    'thriller', 'suspense', 'action', 'espionage', 'spy',
    'psychological thriller', 'conspiracy', 'terrorism'
  ],
  'Romance': [
    'romance', 'love story', 'romantic', 'love', 'relationships',
    'contemporary romance', 'historical romance', 'romantic comedy'
  ],
  'Historical Fiction': [
    'historical fiction', 'historical', 'period', 'world war',
    'civil war', 'victorian', 'medieval', 'ancient'
  ],
  'Literary Fiction': [
    'literary fiction', 'literary', 'contemporary fiction',
    'modern fiction', 'satire', 'allegory'
  ],
  'Horror': [
    'horror', 'terror', 'scary', 'ghost', 'vampire', 'zombie',
    'monsters', 'haunted', 'dark', 'gothic'
  ],
  'Adventure': [
    'adventure', 'quest', 'journey', 'exploration', 'expedition',
    'survival', 'treasure', 'pirates'
  ],
  'Young Adult': [
    'young adult', 'ya', 'teen', 'teenage', 'coming of age',
    'high school', 'adolescent'
  ],
  'Children\'s': [
    'children', 'kids', 'juvenile', 'picture book', 'early reader',
    'middle grade', 'bedtime story'
  ],
  'Graphic Novel': [
    'graphic novel', 'comic', 'manga', 'illustrated', 'sequential art'
  ],
  'Short Stories': [
    'short stories', 'anthology', 'collection', 'novellas', 'short fiction'
  ],
  'Classics': [
    'classic', 'classical', 'nineteenth century', '19th century',
    'eighteenth century', '18th century', 'masterpiece'
  ],
  'Biography & Memoir': [
    'biography', 'memoir', 'autobiography', 'life story', 'diaries',
    'letters', 'personal narrative', 'biographical'
  ],
  'History': [
    'history', 'historical', 'civilization', 'archaeology',
    'ancient history', 'military history', 'social history'
  ],
  'Science & Technology': [
    'science', 'technology', 'physics', 'biology', 'chemistry',
    'mathematics', 'astronomy', 'engineering', 'computers', 'nature'
  ],
  'Philosophy': [
    'philosophy', 'philosophical', 'ethics', 'logic', 'metaphysics',
    'epistemology', 'existentialism', 'phenomenology'
  ],
  'Self-Help': [
    'self-help', 'self improvement', 'personal development',
    'motivation', 'success', 'happiness', 'productivity'
  ],
  'Business & Economics': [
    'business', 'economics', 'finance', 'management', 'entrepreneurship',
    'marketing', 'investing', 'money', 'capitalism'
  ],
  'Travel': [
    'travel', 'tourism', 'guidebook', 'travelogue', 'adventure travel',
    'cultural exploration', 'geography'
  ],
  'True Crime': [
    'true crime', 'criminal', 'murder case', 'serial killer',
    'investigation', 'forensic', 'crime story'
  ],
  'Essays': [
    'essays', 'essay', 'nonfiction', 'criticism', 'commentary',
    'reflections', 'observations'
  ],
  'Politics & Current Events': [
    'politics', 'political', 'government', 'democracy', 'current events',
    'international relations', 'diplomacy', 'elections'
  ],
  'Religion & Spirituality': [
    'religion', 'religious', 'spirituality', 'faith', 'theology',
    'christianity', 'buddhism', 'islam', 'meditation', 'prayer'
  ],
  'Art & Photography': [
    'art', 'photography', 'painting', 'sculpture', 'artists',
    'visual arts', 'design', 'architecture'
  ],
  'Cooking & Food': [
    'cooking', 'food', 'recipes', 'cookbook', 'culinary', 'cuisine',
    'baking', 'gastronomy'
  ],
  'Health & Fitness': [
    'health', 'fitness', 'exercise', 'nutrition', 'diet', 'wellness',
    'medicine', 'medical', 'yoga'
  ],
  'Poetry': [
    'poetry', 'poems', 'verse', 'sonnets', 'haiku'
  ],
  'Drama': [
    'drama', 'plays', 'theater', 'theatre', 'screenplay', 'script'
  ],
  'Reference': [
    'reference', 'encyclopedia', 'dictionary', 'handbook', 'manual',
    'guide', 'textbook', 'directory'
  ]
};
