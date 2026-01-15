import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SearchFiltersComponent } from './search-filters.component';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

describe('SearchFiltersComponent', () => {
  let component: SearchFiltersComponent;
  let fixture: ComponentFixture<SearchFiltersComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SearchFiltersComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(SearchFiltersComponent);
    component = fixture.componentInstance;
    component.availableFormats = ['EPUB', 'MOBI', 'PDF'];
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  describe('Inputs', () => {
    it('should display format dropdown', () => {
      const select = fixture.nativeElement.querySelector('mat-select');
      expect(select).toBeTruthy();
    });

    it('should be disabled when disabled input is true', () => {
      component.disabled = true;
      fixture.detectChanges();
      const select = fixture.nativeElement.querySelector('mat-select');
      expect(select.getAttribute('aria-disabled')).toBe('true');
    });

    it('should display download counter when downloadsLeft is set', () => {
      component.downloadsLeft = 50;
      component.downloadsPerDay = 100;
      fixture.detectChanges();
      const counter = fixture.nativeElement.querySelector('.download-counter');
      expect(counter).toBeTruthy();
      expect(counter.textContent).toContain('50');
      expect(counter.textContent).toContain('100');
    });

    it('should hide download counter when downloadsLeft is null', () => {
      component.downloadsLeft = null;
      fixture.detectChanges();
      const counter = fixture.nativeElement.querySelector('.download-counter');
      expect(counter).toBeFalsy();
    });
  });

  describe('Outputs', () => {
    it('should emit formatChange when format selection changes', () => {
      spyOn(component.formatChange, 'emit');
      component.onFormatChange('EPUB');
      expect(component.formatChange.emit).toHaveBeenCalledWith('EPUB');
    });
  });

  describe('Download warning levels', () => {
    it('should return none when downloadsLeft is null', () => {
      component.downloadsLeft = null;
      expect(component.downloadWarningLevel).toBe('none');
    });

    it('should return none when downloads > 30', () => {
      component.downloadsLeft = 50;
      expect(component.downloadWarningLevel).toBe('none');
    });

    it('should return yellow when downloads <= 30', () => {
      component.downloadsLeft = 30;
      expect(component.downloadWarningLevel).toBe('yellow');
    });

    it('should return orange when downloads <= 20', () => {
      component.downloadsLeft = 20;
      expect(component.downloadWarningLevel).toBe('orange');
    });

    it('should return red when downloads <= 10', () => {
      component.downloadsLeft = 10;
      expect(component.downloadWarningLevel).toBe('red');
    });

    it('should apply warning-yellow class when yellow', () => {
      component.downloadsLeft = 25;
      component.downloadsPerDay = 100;
      fixture.detectChanges();
      const counter = fixture.nativeElement.querySelector('.download-counter');
      expect(counter.classList.contains('warning-yellow')).toBe(true);
    });

    it('should apply warning-orange class when orange', () => {
      component.downloadsLeft = 15;
      component.downloadsPerDay = 100;
      fixture.detectChanges();
      const counter = fixture.nativeElement.querySelector('.download-counter');
      expect(counter.classList.contains('warning-orange')).toBe(true);
    });

    it('should apply warning-red class when red', () => {
      component.downloadsLeft = 5;
      component.downloadsPerDay = 100;
      fixture.detectChanges();
      const counter = fixture.nativeElement.querySelector('.download-counter');
      expect(counter.classList.contains('warning-red')).toBe(true);
    });
  });
});
