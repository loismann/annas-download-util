import { Routes } from '@angular/router';
import { LoginComponent } from './login/login.component';
import { BookSearchComponent } from './book-search/book-search.component';
import { GamingControlComponent } from './gaming-control/gaming-control.component';
import { BookReaderComponent } from './book-reader/book-reader.component';
import { LibraryComponent } from './library/library.component';
import { QuizComponent } from './quiz/quiz.component';
import { SpotifinatorComponent } from './spotifinator/spotifinator.component';
import { authGuard } from './guards/auth.guard';
import { adminGuard } from './guards/admin.guard';

export const routes: Routes = [
  { path: 'login', component: LoginComponent },
  { path: 'search', component: BookSearchComponent, canActivate: [authGuard] },
  { path: 'reader', component: BookReaderComponent, canActivate: [authGuard] },
  { path: 'library', component: LibraryComponent, canActivate: [authGuard] },
  { path: 'spotifinator', component: SpotifinatorComponent, canActivate: [authGuard, adminGuard] },
  { path: 'gaming', component: GamingControlComponent, canActivate: [authGuard] },
  { path: 'quiz', component: QuizComponent, canActivate: [authGuard, adminGuard] },
  { path: '', redirectTo: '/search', pathMatch: 'full' },
  { path: '**', redirectTo: '/search' }
];
