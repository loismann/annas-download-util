import { TestBed } from '@angular/core/testing';
import { Router, ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { adminGuard } from './admin.guard';
import { AuthService } from '../services/auth.service';

describe('adminGuard', () => {
  let mockAuthService: jasmine.SpyObj<AuthService>;
  let mockRouter: jasmine.SpyObj<Router>;
  let mockRoute: ActivatedRouteSnapshot;
  let mockState: RouterStateSnapshot;

  beforeEach(() => {
    mockAuthService = jasmine.createSpyObj('AuthService', ['isAuthenticated', 'isAdmin']);
    mockRouter = jasmine.createSpyObj('Router', ['navigate']);
    mockRoute = {} as ActivatedRouteSnapshot;
    mockState = { url: '/admin' } as RouterStateSnapshot;

    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: mockAuthService },
        { provide: Router, useValue: mockRouter }
      ]
    });
  });

  it('should allow access when user is authenticated and admin', () => {
    mockAuthService.isAuthenticated.and.returnValue(true);
    mockAuthService.isAdmin.and.returnValue(true);

    const result = TestBed.runInInjectionContext(() => adminGuard(mockRoute, mockState));

    expect(result).toBe(true);
    expect(mockRouter.navigate).not.toHaveBeenCalled();
  });

  it('should redirect to login when user is not authenticated', () => {
    mockAuthService.isAuthenticated.and.returnValue(false);

    const result = TestBed.runInInjectionContext(() => adminGuard(mockRoute, mockState));

    expect(result).toBe(false);
    expect(mockRouter.navigate).toHaveBeenCalledWith(['/login']);
  });

  it('should redirect to search when user is authenticated but not admin', () => {
    mockAuthService.isAuthenticated.and.returnValue(true);
    mockAuthService.isAdmin.and.returnValue(false);

    const result = TestBed.runInInjectionContext(() => adminGuard(mockRoute, mockState));

    expect(result).toBe(false);
    expect(mockRouter.navigate).toHaveBeenCalledWith(['/search']);
  });
});
