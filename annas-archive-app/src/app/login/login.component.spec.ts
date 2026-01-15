import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { LoginComponent } from './login.component';
import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

describe('LoginComponent', () => {
  let component: LoginComponent;
  let fixture: ComponentFixture<LoginComponent>;
  let mockAuthService: jasmine.SpyObj<AuthService>;
  let mockRouter: jasmine.SpyObj<Router>;
  let mockLogger: jasmine.SpyObj<LoggerService>;

  beforeEach(async () => {
    mockAuthService = jasmine.createSpyObj('AuthService', ['login']);
    mockRouter = jasmine.createSpyObj('Router', ['navigate']);
    mockLogger = jasmine.createSpyObj('LoggerService', ['error']);

    await TestBed.configureTestingModule({
      imports: [LoginComponent, NoopAnimationsModule],
      providers: [
        { provide: AuthService, useValue: mockAuthService },
        { provide: Router, useValue: mockRouter },
        { provide: LoggerService, useValue: mockLogger }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(LoginComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should initialize with empty state', () => {
    expect(component.code).toBe('');
    expect(component.loading).toBe(false);
    expect(component.error).toBeNull();
  });

  describe('onLogin', () => {
    it('should show error when code is empty', () => {
      component.code = '';
      component.onLogin();

      expect(component.error).toBe('Please enter your access code.');
      expect(mockAuthService.login).not.toHaveBeenCalled();
    });

    it('should show error when code is only whitespace', () => {
      component.code = '   ';
      component.onLogin();

      expect(component.error).toBe('Please enter your access code.');
      expect(mockAuthService.login).not.toHaveBeenCalled();
    });

    it('should trim code before sending to auth service', () => {
      mockAuthService.login.and.returnValue(of({ token: 'test-token', name: 'Test User', isAdmin: false, expiresAt: '2026-01-16T00:00:00Z' }));
      component.code = '  my-code  ';
      component.onLogin();

      expect(mockAuthService.login).toHaveBeenCalledWith('my-code');
    });

    it('should set loading to true while logging in', () => {
      mockAuthService.login.and.returnValue(of({ token: 'test-token', name: 'Test User', isAdmin: false, expiresAt: '2026-01-16T00:00:00Z' }));
      component.code = 'test-code';

      component.onLogin();

      // After successful login, loading should be false
      expect(component.loading).toBe(false);
    });

    it('should navigate to search on successful login', () => {
      mockAuthService.login.and.returnValue(of({ token: 'test-token', name: 'Test User', isAdmin: false, expiresAt: '2026-01-16T00:00:00Z' }));
      component.code = 'valid-code';

      component.onLogin();

      expect(mockRouter.navigate).toHaveBeenCalledWith(['/search']);
      expect(component.loading).toBe(false);
      expect(component.error).toBeNull();
    });

    it('should show invalid code error on 401 response', () => {
      mockAuthService.login.and.returnValue(throwError(() => ({ status: 401 })));
      component.code = 'invalid-code';

      component.onLogin();

      expect(component.error).toBe('Invalid access code.');
      expect(component.loading).toBe(false);
      expect(mockLogger.error).toHaveBeenCalled();
    });

    it('should show generic error on other errors', () => {
      mockAuthService.login.and.returnValue(throwError(() => ({ status: 500 })));
      component.code = 'test-code';

      component.onLogin();

      expect(component.error).toBe('Login failed. Please try again.');
      expect(component.loading).toBe(false);
      expect(mockLogger.error).toHaveBeenCalled();
    });

    it('should clear previous error when attempting new login', () => {
      component.error = 'Previous error';
      mockAuthService.login.and.returnValue(of({ token: 'test-token', name: 'Test User', isAdmin: false, expiresAt: '2026-01-16T00:00:00Z' }));
      component.code = 'test-code';

      component.onLogin();

      expect(component.error).toBeNull();
    });
  });
});
