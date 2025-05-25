// src/main.ts

import { bootstrapApplication } from '@angular/platform-browser';
import { importProvidersFrom }  from '@angular/core';
import { provideAnimations }    from '@angular/platform-browser/animations';
import {
  provideHttpClient,
  withInterceptorsFromDi
} from '@angular/common/http';

import { AppComponent } from './app/app.component';

// Angular Material & animations modules:
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { MatToolbarModule }   from '@angular/material/toolbar';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule }     from '@angular/material/input';
import { MatCheckboxModule }  from '@angular/material/checkbox';
import { MatButtonModule }    from '@angular/material/button';
import { MatCardModule }      from '@angular/material/card';

bootstrapApplication(AppComponent, {
  providers: [
    // HttpClient support
    provideHttpClient(withInterceptorsFromDi()),

    // Enable Material animations
    provideAnimations(),

    // Import BrowserAnimationsModule and all Material modules
    importProvidersFrom(
      BrowserAnimationsModule,
      MatToolbarModule,
      MatFormFieldModule,
      MatInputModule,
      MatCheckboxModule,
      MatButtonModule,
      MatCardModule
    )
  ]
})
.catch(err => console.error(err));
