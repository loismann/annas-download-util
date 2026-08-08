import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { HideBrokenImagesDirective } from './hide-broken-images.directive';

@Component({
  standalone: true,
  imports: [HideBrokenImagesDirective],
  template: `
    <div appHideBrokenImages class="host">
      <img id="inner" src="about:blank" />
      <span><img id="nested" src="about:blank" /></span>
    </div>
    <img id="outside" src="about:blank" />
  `
})
class HostComponent {}

/**
 * This behaviour used to be an `onerror` attribute string-injected into every
 * model-written `<img>`. It only worked because that HTML bypassed
 * sanitisation; the point of the directive is to keep the behaviour once the
 * HTML is sanitised properly.
 */
describe('HideBrokenImagesDirective', () => {
  let fixture: ComponentFixture<HostComponent>;

  function img(id: string): HTMLImageElement {
    return fixture.nativeElement.querySelector(`#${id}`)
      ?? fixture.debugElement.parent?.nativeElement.querySelector(`#${id}`);
  }

  function fail(el: HTMLElement): void {
    // `error` does not bubble, which is the whole reason the directive listens
    // in the capture phase. Dispatching without bubbling is therefore the
    // honest simulation — a bubbling event would pass even if the directive
    // were wrong.
    el.dispatchEvent(new Event('error', { bubbles: false }));
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
  });

  it('should hide a direct child image that fails to load', () => {
    const el = img('inner');

    fail(el);

    expect(el.style.display).toBe('none');
  });

  it('should hide a nested image that fails to load', () => {
    const el = img('nested');

    fail(el);

    expect(el.style.display).toBe('none');
  });

  it('should leave images that load alone', () => {
    expect(img('inner').style.display).toBe('');
    expect(img('nested').style.display).toBe('');
  });

  it('should ignore an error from something that is not an image', () => {
    const host: HTMLElement = fixture.nativeElement.querySelector('.host');
    const span = host.querySelector('span') as HTMLElement;

    fail(span);

    expect(span.style.display).toBe('');
  });

  it('should stop listening once destroyed', () => {
    const el = img('inner');
    const host: HTMLElement = fixture.nativeElement.querySelector('.host');
    const removeSpy = spyOn(host, 'removeEventListener').and.callThrough();

    fixture.destroy();

    expect(removeSpy).toHaveBeenCalledWith('error', jasmine.any(Function), true);
    expect(el.style.display).toBe('');
  });
});
