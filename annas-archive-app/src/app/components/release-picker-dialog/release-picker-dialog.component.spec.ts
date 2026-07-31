import { of } from 'rxjs';
import { ReleaseInfo } from '../../services/media-library-api.service';
import {
  ReleasePickerDialogComponent,
  ReleasePickerDialogData
} from './release-picker-dialog.component';

describe('ReleasePickerDialogComponent', () => {
  function create(fetchResult: ReleaseInfo[] = []) {
    const dialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);
    const grab = jasmine.createSpy('grab').and.returnValue(of(void 0));
    const data: ReleasePickerDialogData = {
      title: 'Find releases',
      fetch: () => of(fetchResult),
      grab
    };
    const logger = jasmine.createSpyObj('LoggerService', ['error']);
    const component = new ReleasePickerDialogComponent(dialogRef, data, logger);
    return { component, dialogRef, grab };
  }

  it('shows Radarr\'s real rejection reason', () => {
    const { component } = create();
    const release = {
      guid: 'release-1',
      indexerId: 2,
      title: 'Alternate-language title',
      size: 1024,
      rejected: true,
      rejections: ['Unknown Movie. Unable to match to correct movie using release title.']
    } satisfies ReleaseInfo;

    expect(component.rejectionSummary(release)).toBe(
      'Unknown Movie. Unable to match to correct movie using release title.'
    );
  });

  it('passes the complete selected release through to the grab callback', () => {
    const release = {
      guid: 'release-2',
      indexerId: 2,
      title: 'Obscure release',
      size: 2048,
      rejected: true,
      rejections: ['Unknown Movie.']
    } satisfies ReleaseInfo;
    const { component, dialogRef, grab } = create([release]);

    component.grab(release);

    expect(grab).toHaveBeenCalledOnceWith(release);
    expect(dialogRef.close).toHaveBeenCalledOnceWith(true);
  });
});
