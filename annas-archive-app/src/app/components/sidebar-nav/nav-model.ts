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
  /** Hidden from non-admins. Route guards are the real enforcement — this only
   *  keeps the menu honest about where a person can actually go. */
  adminOnly?: boolean;
}

export const NAV_ENTRIES: NavEntry[] = [
  { label: 'Book Search', shortLabel: 'Book Search', route: '/search', icon: 'search' },
  { label: 'Ebook Library', shortLabel: 'Ebooks', route: '/library', icon: 'local_library' },
  { label: 'Ebook Reader', shortLabel: 'Reader', route: '/reader', icon: 'chrome_reader_mode' },
  { label: 'Audiobooks', shortLabel: 'Audio', route: '/audiobooks', icon: 'headphones' },
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
  { label: 'Spotif-inator', shortLabel: 'Music', route: '/spotifinator', icon: 'queue_music', adminOnly: true },
  { label: 'Lucy Quiz', shortLabel: 'Quiz', route: '/quiz', icon: 'quiz', adminOnly: true }
];
