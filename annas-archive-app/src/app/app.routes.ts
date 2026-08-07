import { Routes } from '@angular/router';
import { authGuard } from './guards/auth.guard';
import { adminGuard } from './guards/admin.guard';

/**
 * Every page is lazy-loaded via `loadComponent`.
 *
 * These were all static `component:` imports, which meant one initial bundle
 * carrying every screen in the app — the ebook reader, the media library, the
 * quiz, Date Night, all of it — before a person could see the first page. That
 * cost is paid on Mom and Dad's iPads over Hawaii cellular, so it matters more
 * here than the raw number suggests.
 *
 * `loadComponent` (rather than `loadChildren`) is the right tool because these
 * are standalone components; each becomes its own chunk fetched on first visit.
 * Guards stay eager on purpose — they are tiny, and they must run *before* the
 * router decides whether to download the chunk at all.
 */
export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./login/login.component').then(m => m.LoginComponent)
  },
  {
    path: 'search',
    loadComponent: () => import('./book-search/book-search.component').then(m => m.BookSearchComponent),
    canActivate: [authGuard]
  },
  {
    path: 'reader',
    loadComponent: () => import('./book-reader/book-reader.component').then(m => m.BookReaderComponent),
    canActivate: [authGuard]
  },
  {
    path: 'library',
    loadComponent: () => import('./library/library.component').then(m => m.LibraryComponent),
    canActivate: [authGuard]
  },
  {
    path: 'spotifinator',
    loadComponent: () => import('./spotifinator/spotifinator.component').then(m => m.SpotifinatorComponent),
    // Signed in is enough. Everything on this page is scoped to the Spotify
    // account the person connects themselves, and the connection, drafts and plan
    // history are all keyed to the app user — so there is no shared library here
    // for one household member to reach into.
    canActivate: [authGuard]
  },
  {
    path: 'quiz',
    loadComponent: () => import('./quiz/quiz.component').then(m => m.QuizComponent),
    canActivate: [authGuard, adminGuard]
  },
  {
    path: 'media',
    loadComponent: () => import('./media-search/media-search.component').then(m => m.MediaSearchComponent),
    canActivate: [authGuard]
  },
  {
    path: 'media-library',
    loadComponent: () => import('./media-library/media-library.component').then(m => m.MediaLibraryComponent),
    canActivate: [authGuard]
  },
  {
    path: 'media-library/series/:seriesId',
    loadComponent: () =>
      import('./media-library/series-detail/series-detail.component').then(m => m.SeriesDetailComponent),
    canActivate: [authGuard]
  },
  {
    path: 'audiobooks',
    loadComponent: () => import('./audiobooks/audiobooks.component').then(m => m.AudiobooksComponent),
    canActivate: [authGuard]
  },
  {
    path: 'audiobook-search',
    loadComponent: () => import('./audiobook-search/audiobook-search.component').then(m => m.AudiobookSearchComponent),
    canActivate: [authGuard]
  },
  // Immich -> CVS pickup prints. See
  // DOCS/features/google-photos-cvs-print-automation-spec.md.
  // Admin-only while the CVS half is unfinished: the page can prepare real
  // print files but cannot yet place an order, so it would only confuse anyone
  // else in the household. Drop adminGuard once §7 is working.
  {
    path: 'photo-prints',
    loadComponent: () => import('./photo-prints/photo-prints.component').then(m => m.PhotoPrintsComponent),
    canActivate: [authGuard, adminGuard]
  },
  // The household-facing Date Night page — where Mom and Dad pick movies and
  // agree a night. See DOCS/features/DATE_NIGHT.md.
  {
    path: 'date-night',
    loadComponent: () => import('./date-night/date-night.component').then(m => m.DateNightComponent),
    canActivate: [authGuard]
  },
  // Pool administration, admin-only: CSV import, availability scanning, the
  // announcement preview, and the dry run.
  {
    path: 'date-night/pool',
    loadComponent: () => import('./date-night/date-night-pool.component').then(m => m.DateNightPoolComponent),
    canActivate: [authGuard, adminGuard]
  },
  // The retired YouTube pages (VideoLibraryComponent is still in the tree but
  // deliberately unrouted). Kept as redirects so an old bookmark lands somewhere
  // useful instead of on the wildcard. Re-enable by restoring a route here plus
  // an entry in components/sidebar-nav/nav-model.ts.
  { path: 'videos', redirectTo: '/search', pathMatch: 'full' },
  { path: 'videos/download', redirectTo: '/search', pathMatch: 'full' },
  { path: 'youtube', redirectTo: '/search', pathMatch: 'full' },
  // Home is Book Search. It used to be /videos, which was admin-only — so Mom
  // and Dad's entry experience was a guard redirect to somewhere else.
  { path: '', redirectTo: '/search', pathMatch: 'full' },
  { path: '**', redirectTo: '/search' }
];
