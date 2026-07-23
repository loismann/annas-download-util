import { Routes } from '@angular/router';
import { LoginComponent } from './login/login.component';
import { BookSearchComponent } from './book-search/book-search.component';
import { BookReaderComponent } from './book-reader/book-reader.component';
import { LibraryComponent } from './library/library.component';
import { QuizComponent } from './quiz/quiz.component';
import { SpotifinatorComponent } from './spotifinator/spotifinator.component';
import { VideoLibraryComponent } from './video-library/video-library.component';
import { MediaSearchComponent } from './media-search/media-search.component';
import { MediaLibraryComponent } from './media-library/media-library.component';
import { SeriesDetailComponent } from './media-library/series-detail/series-detail.component';
import { authGuard } from './guards/auth.guard';
import { adminGuard } from './guards/admin.guard';

export const routes: Routes = [
  { path: 'login', component: LoginComponent },
  { path: 'search', component: BookSearchComponent, canActivate: [authGuard] },
  { path: 'reader', component: BookReaderComponent, canActivate: [authGuard] },
  { path: 'library', component: LibraryComponent, canActivate: [authGuard] },
  { path: 'spotifinator', component: SpotifinatorComponent, canActivate: [authGuard, adminGuard] },
  { path: 'quiz', component: QuizComponent, canActivate: [authGuard, adminGuard] },
  { path: 'videos', component: VideoLibraryComponent, canActivate: [authGuard, adminGuard] },
  { path: 'media', component: MediaSearchComponent, canActivate: [authGuard, adminGuard] },
  { path: 'media-library', component: MediaLibraryComponent, canActivate: [authGuard, adminGuard] },
  { path: 'media-library/series/:seriesId', component: SeriesDetailComponent, canActivate: [authGuard, adminGuard] },
  // Legacy routes redirect to main videos page
  { path: 'videos/download', redirectTo: '/videos', pathMatch: 'full' },
  { path: 'youtube', redirectTo: '/videos', pathMatch: 'full' },
  { path: '', redirectTo: '/videos', pathMatch: 'full' },
  { path: '**', redirectTo: '/videos' }
];
