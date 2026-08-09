import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { Observable, of, throwError } from 'rxjs';

import { VpnToggleComponent } from './vpn-toggle.component';
import { BookSearchApiService, VpnSettingsResponse } from '../../services/book-search-api.service';
import { LoggerService } from '../../services/logger.service';

/**
 * VpnToggleComponent was the last component in the app without a spec, which is
 * how it ended up carrying a defect nobody had reported: a failed save left the
 * control claiming a setting the server had rejected.
 *
 * It is also the component other suites trip over. It renders three levels down
 * inside SearchFormComponent, so its `getVpnSettings()` call in `ngOnInit` is
 * what makes BookSearchComponent's TestBed need the spy at all — nothing in the
 * resulting error names this component.
 */
describe('VpnToggleComponent', () => {
  let component: VpnToggleComponent;
  let fixture: ComponentFixture<VpnToggleComponent>;
  let api: jasmine.SpyObj<BookSearchApiService>;
  let logger: jasmine.SpyObj<LoggerService>;

  const settings = (over: Partial<VpnSettingsResponse> = {}): VpnSettingsResponse => ({
    enabled: true,
    region: 'us_east',
    availableRegions: ['us_east', 'us_west', 'uk'],
    ...over
  });

  /** Builds the fixture. Call after stubbing, never before — a rebuild would
   *  discard any spy return value set against the previous instance. */
  const build = async (): Promise<void> => {
    await TestBed.configureTestingModule({
      imports: [VpnToggleComponent, NoopAnimationsModule],
      providers: [
        { provide: BookSearchApiService, useValue: api },
        { provide: LoggerService, useValue: logger }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(VpnToggleComponent);
    component = fixture.componentInstance;
  };

  beforeEach(() => {
    api = jasmine.createSpyObj('BookSearchApiService', ['getVpnSettings', 'updateVpnSettings']);
    logger = jasmine.createSpyObj('LoggerService', ['log', 'info', 'warn', 'error', 'debug']);
    api.getVpnSettings.and.returnValue(of(settings()));
    api.updateVpnSettings.and.returnValue(of(settings()));
  });

  describe('loading the current settings', () => {
    it('adopts the server state and reveals the control', async () => {
      await build();
      fixture.detectChanges();

      expect(component.enabled).toBeTrue();
      expect(component.region).toBe('us_east');
      expect(component.availableRegions).toEqual(['us_east', 'us_west', 'uk']);
      expect(component.loaded).toBeTrue();
    });

    it('still reveals the control at safe defaults when the read fails', async () => {
      api.getVpnSettings.and.returnValue(throwError(() => new Error('gluetun down')));
      await build();
      fixture.detectChanges();

      // `loaded` gates the whole template. Leaving it false on error would hide
      // the control permanently rather than degrade it.
      expect(component.loaded).toBeTrue();
      expect(component.enabled).toBeFalse();
      expect(component.region).toBe('');
      expect(logger.error).toHaveBeenCalled();
    });

    it('cancels the read when the component is destroyed', async () => {
      let aborted = false;
      api.getVpnSettings.and.returnValue(new Observable<VpnSettingsResponse>(() => () => { aborted = true; }));
      await build();
      fixture.detectChanges();

      fixture.destroy();

      expect(aborted).toBeTrue();
    });
  });

  describe('saving a change', () => {
    it('sends the new state on a toggle and on a region change', async () => {
      await build();
      fixture.detectChanges();

      component.enabled = false;
      component.onToggleChange();
      expect(api.updateVpnSettings).toHaveBeenCalledWith(false, 'us_east');

      component.region = 'uk';
      component.onRegionChange();
      expect(api.updateVpnSettings).toHaveBeenCalledWith(true, 'uk');
    });

    it('shows what the server confirmed, not what was asked for', async () => {
      // The server is allowed to answer with something else — a region it could
      // not reach, say. The control must not keep displaying the request.
      api.updateVpnSettings.and.returnValue(of(settings({ enabled: true, region: 'us_west' })));
      await build();
      fixture.detectChanges();

      component.region = 'uk';
      component.onRegionChange();

      expect(component.region).toBe('us_west');
      expect(component.saving).toBeFalse();
    });

    it('reverts to the last confirmed state when the save fails', async () => {
      await build();
      fixture.detectChanges();
      // Loaded as enabled/us_east. ngModel has already flipped both by the time
      // (change) fires, which is exactly why the revert needs a remembered value.
      api.updateVpnSettings.and.returnValue(throwError(() => new Error('refused')));

      component.enabled = false;
      component.region = 'uk';
      component.onToggleChange();

      expect(component.enabled).toBeTrue();
      expect(component.region).toBe('us_east');
      expect(logger.error).toHaveBeenCalled();
    });

    it('clears the saving flag on failure so the control is not left disabled', async () => {
      api.updateVpnSettings.and.returnValue(throwError(() => new Error('refused')));
      await build();
      fixture.detectChanges();

      component.onToggleChange();

      // `saving` disables both the toggle and the select. Leaving it true would
      // be a dead end — no way back without a reload.
      expect(component.saving).toBeFalse();
    });

    it('does not cancel the save when the component is destroyed', async () => {
      let aborted = false;
      api.updateVpnSettings.and.returnValue(new Observable(() => () => { aborted = true; }));
      await build();
      fixture.detectChanges();

      component.onToggleChange();
      fixture.destroy();

      // Writes are deliberately unguarded: unsubscribing an HttpClient call
      // aborts the request, so guarding this would mean navigating away
      // silently cancels the user's VPN change.
      expect(aborted).toBeFalse();
    });

    it('reverts to the newly confirmed state, not the state at load', async () => {
      await build();
      fixture.detectChanges();

      // A save succeeds and moves the confirmed baseline...
      api.updateVpnSettings.and.returnValue(of(settings({ enabled: true, region: 'uk' })));
      component.region = 'uk';
      component.onRegionChange();
      expect(component.region).toBe('uk');

      // ...so a later failure must come back to 'uk', not to the original.
      api.updateVpnSettings.and.returnValue(throwError(() => new Error('refused')));
      component.region = 'us_west';
      component.onRegionChange();

      expect(component.region).toBe('uk');
    });
  });
});
