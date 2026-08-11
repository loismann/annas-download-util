import { TestBed } from '@angular/core/testing';
import { Router, ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { reader1Guard, reader2Guard } from './reader-split.guard';
import { AuthService } from '../services/auth.service';

/**
 * There is one reader now, and it is Reader II.
 *
 * <p>These tests used to pin the split — the admin on Reader II, everyone else
 * on Reader I — and what is left is the part that still matters: whichever door
 * somebody arrives at, they end up reading. A redirect rather than a refusal,
 * because the person asked to read and there is exactly one place to do it.</p>
 */
describe('the reader route', () => {
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
    it('lets anybody signed in read, admin or not', () => {
      for (const admin of [true, false]) {
        auth.isAdmin.and.returnValue(admin);

        expect(run(reader2Guard)).withContext(`isAdmin=${admin}`).toBe(true);
      }

      expect(router.navigate).not.toHaveBeenCalled();
    });

    it('sends the signed-out to the login page', () => {
      auth.isAuthenticated.and.returnValue(false);

      expect(run(reader2Guard)).toBe(false);
      expect(router.navigate).toHaveBeenCalledWith(['/login']);
    });
  });

  describe('reader1Guard', () => {
    /**
     * Reader I still routes and still builds; nothing here deletes it. What has
     * gone is anybody arriving at it — a stale bookmark, a link in an old
     * message — and the answer is to take them to the reader that exists rather
     * than to the one being retired.
     */
    it('forwards a stale /reader link to Reader II, whoever follows it', () => {
      for (const admin of [true, false]) {
        auth.isAdmin.and.returnValue(admin);

        expect(run(reader1Guard)).withContext(`isAdmin=${admin}`).toBe(false);
        expect(router.navigate).toHaveBeenCalledWith(['/reader2']);
      }
    });

    it('sends the signed-out to the login page', () => {
      auth.isAuthenticated.and.returnValue(false);

      expect(run(reader1Guard)).toBe(false);
      expect(router.navigate).toHaveBeenCalledWith(['/login']);
    });
  });
});
