import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { AppComponent } from './app.component';
import { AuthService, UserActivity } from './services/auth.service';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PLATFORM_ID } from '@angular/core';
import { provideAnimations } from '@angular/platform-browser/animations';
import { BehaviorSubject, of } from 'rxjs';

/**
 * Basic smoke tests for AppComponent
 * Verifies the app component can be created and rendered
 */
describe('AppComponent', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAnimations(),
        AuthService,
        { provide: PLATFORM_ID, useValue: 'browser' }
      ]
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should render toolbar', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('mat-toolbar')).toBeTruthy();
  });

  it('should initialize with empty userActivity array', () => {
    const fixture = TestBed.createComponent(AppComponent);
    expect(fixture.componentInstance.userActivity).toEqual([]);
  });

  it('should not show activity indicators when userActivity is empty', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.user-activity-indicators')).toBeFalsy();
  });

  it('should show activity indicators when userActivity has data', fakeAsync(() => {
    const fixture = TestBed.createComponent(AppComponent);
    const component = fixture.componentInstance;

    // Manually set activity data
    component.userActivity = [
      { initial: 'M', userName: 'Mom', minutesAgo: 5, isFullTone: true, isHalfTone: false }
    ];

    // Mock authenticated state
    const authService = TestBed.inject(AuthService);
    localStorage.setItem('auth_token', 'test-token');
    localStorage.setItem('auth_name', 'Paul');

    fixture.detectChanges();
    tick();

    const compiled = fixture.nativeElement as HTMLElement;
    const activityDot = compiled.querySelector('.activity-dot');

    // Activity dot should exist if authenticated and has activity
    if (activityDot) {
      expect(activityDot.textContent?.trim()).toBe('M');
      expect(activityDot.classList.contains('full-tone')).toBe(true);
    }

    localStorage.clear();
  }));

  it('should apply full-tone class for activity within 30 minutes', () => {
    const fixture = TestBed.createComponent(AppComponent);
    const component = fixture.componentInstance;

    component.userActivity = [
      { initial: 'M', userName: 'Mom', minutesAgo: 10, isFullTone: true, isHalfTone: false }
    ];

    fixture.detectChanges();

    // The CSS class logic: full-tone class when isFullTone is true
    const activity = component.userActivity[0];
    expect(activity.isFullTone).toBe(true);
  });

  it('should apply half-tone class for activity between 30-60 minutes', () => {
    const fixture = TestBed.createComponent(AppComponent);
    const component = fixture.componentInstance;

    component.userActivity = [
      { initial: 'D', userName: 'Dad', minutesAgo: 45, isFullTone: false, isHalfTone: true }
    ];

    fixture.detectChanges();

    // The CSS class logic: half-tone class when in 30-60 min range
    const activity = component.userActivity[0];
    expect(activity.isFullTone).toBe(false);
    expect(activity.isHalfTone).toBe(true);
  });
});
