import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { StorageFooterComponent } from './storage-footer.component';
import { StorageStats, SystemStatsApiService } from '../../services/system-stats-api.service';
import { LoggerService } from '../../services/logger.service';

/**
 * Characterization tests for the storage footer.
 *
 * The unit scaling is the whole of it, and it exists for a specific reason:
 * everything was fixed at TB when the only categories were Movies, TV and Books
 * — all multi-terabyte — and Photos then read as a flat "0.00 TB" forever.
 */
describe('StorageFooterComponent (characterization)', () => {
  let fixture: ComponentFixture<StorageFooterComponent>;
  let component: StorageFooterComponent;
  let api: jasmine.SpyObj<SystemStatsApiService>;

  const MIB = 1024 ** 2;
  const GIB = 1024 ** 3;
  const TIB = 1024 ** 4;

  beforeEach(async () => {
    api = jasmine.createSpyObj<SystemStatsApiService>('SystemStatsApiService', ['getStorageStats']);
    api.getStorageStats.and.returnValue(of({ categories: [], totalBytes: 0 } as unknown as StorageStats));

    await TestBed.configureTestingModule({
      imports: [StorageFooterComponent],
      providers: [
        { provide: SystemStatsApiService, useValue: api },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(StorageFooterComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  describe('scaling the unit to the value', () => {
    it('should use TB for a multi-terabyte library', () => {
      expect(component.formatSize(2.5 * TIB)).toBe('2.50 TB');
    });

    it('should drop to GB below a terabyte', () => {
      // A photo library that reads "0.00 TB" forever tells nobody anything.
      expect(component.formatSize(450 * GIB)).toBe('450.0 GB');
    });

    it('should drop to MB below a gigabyte', () => {
      expect(component.formatSize(700 * MIB)).toBe('700 MB');
    });

    it('should never round a real size down to nothing', () => {
      // Anything present should read as at least 1 MB rather than "0 MB".
      expect(component.formatSize(1024)).toBe('1 MB');
    });

    it('should show a dash for genuinely nothing', () => {
      expect(component.formatSize(0)).toBe('—');
    });

    it('should switch units exactly at the boundary', () => {
      expect(component.formatSize(TIB)).toBe('1.00 TB');
      expect(component.formatSize(TIB - 1)).toContain('GB');
      expect(component.formatSize(GIB)).toBe('1.0 GB');
      expect(component.formatSize(GIB - 1)).toContain('MB');
    });
  });

  describe('refreshing', () => {
    it('should load once immediately rather than wait for the first interval', () => {
      // Ten minutes of an empty footer otherwise.
      component.ngOnInit();

      expect(api.getStorageStats).toHaveBeenCalledTimes(1);
      expect(component.stats).toBeTruthy();
    });

    it('should refresh on the backend\'s own cache period', () => {
      // The backend caches this for ten minutes because computing it is a full
      // directory scan plus Sonarr/Radarr calls — asking more often just
      // re-fetches the same value.
      jasmine.clock().install();
      try {
        component.ngOnInit();

        jasmine.clock().tick(10 * 60 * 1000);

        expect(api.getStorageStats).toHaveBeenCalledTimes(2);
      } finally {
        jasmine.clock().uninstall();
      }
    });

    it('should stop refreshing once it is gone', () => {
      jasmine.clock().install();
      try {
        component.ngOnInit();
        fixture.destroy();
        const after = api.getStorageStats.calls.count();

        jasmine.clock().tick(30 * 60 * 1000);

        expect(api.getStorageStats.calls.count()).toBe(after);
      } finally {
        jasmine.clock().uninstall();
      }
    });

    it('should stay quiet when the stats will not load', () => {
      // A footer is not worth interrupting anyone over.
      api.getStorageStats.and.returnValue(throwError(() => new Error('down')));

      component.ngOnInit();

      expect(component.stats).toBeNull();
    });
  });

  describe('the dark pages', () => {
    it('should default to the light treatment', () => {
      expect(component.dark).toBe(false);
    });
  });
});
