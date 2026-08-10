import { TestBed } from '@angular/core/testing';
import { Router, ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { reader1Guard, reader2Guard } from './reader-split.guard';
import { AuthService } from '../services/auth.service';

/**
 * The reader split: while both readers exist, the admin lives on Reader II and
 * everyone else on Reader I. Wrong door means a redirect to the right one, not
 * a refusal — the person asked to read.
 */
describe('the reader split', () => {
  let auth: jasmine.SpyObj<AuthService>;
  let router: jasmine.SpyObj<Router>;
  const route = {} as ActivatedRouteSnapshot;
  const state = { url: '/reader' } as RouterStateSnapshot;

  beforeEach(() => {
    auth = jasmine.createSpyObj('AuthService', ['isAuthenticated', 'isAdmin']);
    router = jasmine.createSpyObj('Router', ['navigate']);

    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router }
      ]
    });

    auth.isAuthenticated.and.returnValue(true);
  });

  function run(guard: typeof reader1Guard): boolean {
    return TestBed.runInInjectionContext(() => guard(route, state)) as boolean;
  }

  describe('reader2Guard', () => {
    it('lets the admin in', () => {
      auth.isAdmin.and.returnValue(true);

      expect(run(reader2Guard)).toBe(true);
      expect(router.navigate).not.toHaveBeenCalled();
    });

    it('sends everyone else to their own reader', () => {
      auth.isAdmin.and.returnValue(false);

      expect(run(reader2Guard)).toBe(false);
      expect(router.navigate).toHaveBeenCalledWith(['/reader']);
    });

    it('sends the signed-out to the login page', () => {
      auth.isAuthenticated.and.returnValue(false);

      expect(run(reader2Guard)).toBe(false);
      expect(router.navigate).toHaveBeenCalledWith(['/login']);
    });
  });

  describe('reader1Guard', () => {
    it('lets the family in', () => {
      auth.isAdmin.and.returnValue(false);

      expect(run(reader1Guard)).toBe(true);
      expect(router.navigate).not.toHaveBeenCalled();
    });

    it('sends the admin to Reader II', () => {
      auth.isAdmin.and.returnValue(true);

      expect(run(reader1Guard)).toBe(false);
      expect(router.navigate).toHaveBeenCalledWith(['/reader2']);
    });

    it('sends the signed-out to the login page', () => {
      auth.isAuthenticated.and.returnValue(false);

      expect(run(reader1Guard)).toBe(false);
      expect(router.navigate).toHaveBeenCalledWith(['/login']);
    });
  });
});
