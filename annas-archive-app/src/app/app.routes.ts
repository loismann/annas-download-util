import { Routes } from '@angular/router';
import { LoginComponent } from './login/login.component';
import { BookSearchComponent } from './book-search/book-search.component';
import { GamingControlComponent } from './gaming-control/gaming-control.component';
import { DropboxReaderComponent } from './dropbox-reader/dropbox-reader.component';
import { LibraryComponent } from './library/library.component';
import { authGuard } from './guards/auth.guard';

export const routes: Routes = [
  { path: 'login', component: LoginComponent },
  { path: 'search', component: BookSearchComponent, canActivate: [authGuard] },
  { path: 'reader', component: DropboxReaderComponent, canActivate: [authGuard] },
  { path: 'library', component: LibraryComponent, canActivate: [authGuard] },
  { path: 'gaming', component: GamingControlComponent, canActivate: [authGuard] },
  { path: '', redirectTo: '/search', pathMatch: 'full' },
  { path: '**', redirectTo: '/search' }
];
