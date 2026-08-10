/**
 * The single source of truth for app navigation.
 *
 * Declarative on purpose: the menu outgrew a hand-written list of buttons (which
 * is what prompted the move from a dropdown to a sidebar), and every item used
 * to be duplicated in markup with its own `*ngIf="authService.isAdmin()"`.
 * Adding a destination is one entry here, rendered identically by the rail and
 * the expanded panel.
 *
 * An entry is either a direct link (`route`) or a group (`children`). Most are
 * direct links so the collapsed rail shows a useful icon for nearly everything,
 * rather than a short list of folders that all need a second click.
 */

export interface NavEntry {
  label: string;
  icon: string;
  /** Set for a direct link. Mutually exclusive with `children`. */
  route?: string;
  /** Set for a group. Mutually exclusive with `route`. */
  children?: NavEntry[];
  /** Rail caption. The rail is narrow, so long names are abbreviated the way
   *  every icon rail does it ("Spotif-inator" reads as "Music" under an icon).
   *  Two words are fine — captions wrap. Falls back to `label`.
   *
   *  Children need one too: the rail flattens groups away, so every leaf shows
   *  up there on its own. */
  shortLabel?: string;
  /** A second glyph drawn as a small badge over the first. The Material Icons
   *  font this app loads has no single "audiobook" icon, so the one place that
   *  needs one composes it from a book and a pair of headphones rather than
   *  settling for an icon that means something else. */
  overlayIcon?: string;
  /** Hidden from non-admins. Route guards are the real enforcement — this only
   *  keeps the menu honest about where a person can actually go. */
  adminOnly?: boolean;
  /** The inverse: hidden from the admin. Exists for the reader split — while
   *  both readers run, each person's menu shows only the one that is theirs. */
  nonAdminOnly?: boolean;
}

export const NAV_ENTRIES: NavEntry[] = [
  // Grouped like Audiobooks and TV & Movies below — these three were the only
  // media type still spilling its pages across the top level.
  {
    label: 'Ebooks',
    icon: 'library_books',
    children: [
      { label: 'Book Search', shortLabel: 'Book Search', route: '/search', icon: 'search' },
      // `local_library` is a person reading — the civic-library glyph. A shelf of
      // books says "collection", which is what this page actually is.
      { label: 'Ebook Library', shortLabel: 'Ebook Library', route: '/library', icon: 'library_books' },
      // One label, two destinations: each person sees a single "Ebook Reader",
      // and the split sends the admin to Reader II while it proves itself.
      { label: 'Ebook Reader', shortLabel: 'Reader', route: '/reader', icon: 'chrome_reader_mode', nonAdminOnly: true },
      { label: 'Ebook Reader', shortLabel: 'Reader', route: '/reader2', icon: 'chrome_reader_mode', adminOnly: true }
    ]
  },
  {
    label: 'Audiobooks',
    icon: 'menu_book',
    children: [
      { label: 'Audiobook Search', shortLabel: 'Audiobook Search', route: '/audiobook-search', icon: 'search' },
      { label: 'Audiobook Library', shortLabel: 'Audiobooks', route: '/audiobooks', icon: 'menu_book', overlayIcon: 'headphones' }
    ]
  },
  // Admin-only until the CVS checkout leg works — see app.routes.ts. The route
  // guard is the real enforcement; this just keeps it out of everyone's sidebar.
  // Only the child carries the flag: a group is hidden automatically once it has
  // no visible children (sidebar-nav.component.ts), and groups never consult
  // adminOnly themselves.
  {
    label: 'Photos',
    icon: 'photo_library',
    children: [
      { label: 'Photo Prints', shortLabel: 'Photo Prints', route: '/photo-prints', icon: 'print', adminOnly: true }
    ]
  },
  {
    label: 'TV & Movies',
    icon: 'live_tv',
    children: [
      // Named to mirror "Book Search" — the two are the same kind of page. This
      // used to be called just "TV & Movies", which read like a library.
      { label: 'TV & Movie Search', shortLabel: 'TV Search', route: '/media', icon: 'search' },
      // Also once labelled "Video Library", which collided with the (now
      // retired) YouTube page of the same name. Its own heading says "Media
      // Library"; this is the Sonarr/Radarr-backed collection.
      { label: 'TV & Movie Library', shortLabel: 'TV Library', route: '/media-library', icon: 'video_library' }
    ]
  },
  {
    label: 'Date Night',
    icon: 'local_movies',
    children: [
      { label: 'Date Night', shortLabel: 'Date Night', route: '/date-night', icon: 'local_movies' },
      {
        label: 'Date Night Pool',
        shortLabel: 'Pool',
        route: '/date-night/pool',
        icon: 'inventory_2',
        adminOnly: true
      }
    ]
  },
  // Not admin-only: each person connects their own Spotify account, and the
  // connection is stored per app user, so opening this up gives nobody access to
  // anybody else's library.
  { label: 'Spotify', shortLabel: 'Spotify', route: '/spotifinator', icon: 'queue_music' },
  { label: 'Lucy Quiz', shortLabel: 'Quiz', route: '/quiz', icon: 'quiz', adminOnly: true }
];
