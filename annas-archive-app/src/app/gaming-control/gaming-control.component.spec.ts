import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { of, throwError, NEVER } from 'rxjs';
import { GamingControlComponent } from './gaming-control.component';
import { GamingApiService } from '../services/gaming-api.service';

describe('GamingControlComponent', () => {
  let component: GamingControlComponent;
  let fixture: ComponentFixture<GamingControlComponent>;
  let mockGamingApi: jasmine.SpyObj<GamingApiService>;

  beforeEach(async () => {
    mockGamingApi = jasmine.createSpyObj('GamingApiService', ['getGamingPCStatus', 'toggleGamingPC']);
    mockGamingApi.getGamingPCStatus.and.returnValue(of({ isOnline: false, ipAddress: '192.168.1.100', lastChecked: '2026-01-15T00:00:00Z' }));

    await TestBed.configureTestingModule({
      imports: [GamingControlComponent],
      providers: [
        { provide: GamingApiService, useValue: mockGamingApi }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(GamingControlComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should initialize with loading false and no action', () => {
    expect(component.loading).toBe(false);
    expect(component.action).toBeNull();
    expect(component.terminalLines).toEqual([]);
  });

  describe('ngOnInit', () => {
    it('should add initialization lines and check PC status', fakeAsync(() => {
      fixture.detectChanges();
      tick(100);

      expect(component.terminalLines.length).toBeGreaterThan(0);
      expect(mockGamingApi.getGamingPCStatus).toHaveBeenCalled();
    }));

    it('should set pcOnline based on status response', fakeAsync(() => {
      mockGamingApi.getGamingPCStatus.and.returnValue(of({ isOnline: true, ipAddress: '192.168.1.100', lastChecked: '2026-01-15T00:00:00Z' }));
      fixture.detectChanges();
      tick(100);

      expect(component.pcOnline).toBe(true);
    }));

    it('should handle status check error', fakeAsync(() => {
      mockGamingApi.getGamingPCStatus.and.returnValue(throwError(() => new Error('Network error')));
      fixture.detectChanges();
      tick(100);

      expect(component.pcOnline).toBe(false);
    }));
  });

  describe('wakeButtonDisabled', () => {
    it('should be disabled when loading', () => {
      component.loading = true;
      expect(component.wakeButtonDisabled).toBe(true);
    });

    it('should be disabled when PC is online', () => {
      component.pcOnline = true;
      expect(component.wakeButtonDisabled).toBe(true);
    });

    it('should be enabled when PC is offline and not loading', () => {
      component.loading = false;
      component.pcOnline = false;
      expect(component.wakeButtonDisabled).toBe(false);
    });
  });

  describe('sleepButtonDisabled', () => {
    it('should be disabled when loading', () => {
      component.loading = true;
      expect(component.sleepButtonDisabled).toBe(true);
    });

    it('should be disabled when PC is offline', () => {
      component.pcOnline = false;
      expect(component.sleepButtonDisabled).toBe(true);
    });

    it('should be disabled when PC status is unknown', () => {
      component.pcOnline = null;
      expect(component.sleepButtonDisabled).toBe(true);
    });

    it('should be enabled when PC is online and not loading', () => {
      component.loading = false;
      component.pcOnline = true;
      expect(component.sleepButtonDisabled).toBe(false);
    });
  });

  describe('wakePC', () => {
    beforeEach(() => {
      fixture.detectChanges();
    });

    it('should set loading and action state', () => {
      // Use NEVER so the observable doesn't emit synchronously before assertions
      mockGamingApi.toggleGamingPC.and.returnValue(NEVER);

      component.wakePC();

      expect(component.loading).toBe(true);
      expect(component.action).toBe('wake');
    });

    it('should call toggleGamingPC with action 1', fakeAsync(() => {
      mockGamingApi.toggleGamingPC.and.returnValue(of({ success: true, action: 'wake', message: 'Success' }));

      component.wakePC();
      tick(6000);

      expect(mockGamingApi.toggleGamingPC).toHaveBeenCalledWith(1);
    }));

    it('should reset loading on success', fakeAsync(() => {
      mockGamingApi.toggleGamingPC.and.returnValue(of({ success: true, action: 'wake', message: 'Success' }));

      component.wakePC();
      tick(6000);

      expect(component.loading).toBe(false);
      expect(component.action).toBeNull();
    }));

    it('should reset loading on error', fakeAsync(() => {
      mockGamingApi.toggleGamingPC.and.returnValue(throwError(() => ({ error: { message: 'Failed' } })));

      component.wakePC();
      tick(6000);

      expect(component.loading).toBe(false);
      expect(component.action).toBeNull();
    }));
  });

  describe('sleepPC', () => {
    beforeEach(() => {
      fixture.detectChanges();
    });

    it('should set loading and action state', () => {
      // Use NEVER so the observable doesn't emit synchronously before assertions
      mockGamingApi.toggleGamingPC.and.returnValue(NEVER);

      component.sleepPC();

      expect(component.loading).toBe(true);
      expect(component.action).toBe('sleep');
    });

    it('should call toggleGamingPC with action 2', fakeAsync(() => {
      mockGamingApi.toggleGamingPC.and.returnValue(of({ success: true, action: 'wake', message: 'Success' }));

      component.sleepPC();
      tick(4000);

      expect(mockGamingApi.toggleGamingPC).toHaveBeenCalledWith(2);
    }));

    it('should reset loading on success', fakeAsync(() => {
      mockGamingApi.toggleGamingPC.and.returnValue(of({ success: true, action: 'wake', message: 'Success' }));

      component.sleepPC();
      tick(4000);

      expect(component.loading).toBe(false);
      expect(component.action).toBeNull();
    }));

    it('should handle failed response', fakeAsync(() => {
      mockGamingApi.toggleGamingPC.and.returnValue(of({ success: false, action: 'sleep', message: 'Failed', error: 'PC not responding' }));

      component.sleepPC();
      tick(4000);

      expect(component.loading).toBe(false);
    }));
  });
});
