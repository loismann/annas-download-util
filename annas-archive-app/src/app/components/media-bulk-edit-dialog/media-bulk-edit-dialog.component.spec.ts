import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

import { MediaBulkEditDialogComponent } from './media-bulk-edit-dialog.component';

/**
 * Characterization tests for the media bulk edit dialog.
 *
 * The append/replace choice is what these are about. The selected items may
 * already carry different owners and genres, so the dialog starts blank and
 * makes the caller say which it means — a default of replace would silently
 * wipe whatever each item already had.
 */
describe('MediaBulkEditDialogComponent (characterization)', () => {
  let fixture: ComponentFixture<MediaBulkEditDialogComponent>;
  let component: MediaBulkEditDialogComponent;
  let dialogRef: jasmine.SpyObj<MatDialogRef<MediaBulkEditDialogComponent>>;

  async function build(count = 3): Promise<void> {
    dialogRef = jasmine.createSpyObj<MatDialogRef<MediaBulkEditDialogComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [MediaBulkEditDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MAT_DIALOG_DATA, useValue: { count, availableGenres: ['Sci-Fi', 'Horror'] } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(MediaBulkEditDialogComponent);
    component = fixture.componentInstance;
  }

  beforeEach(async () => build());

  afterEach(() => fixture.destroy());

  it('should start blank', () => {
    // The selected items may already differ from one another, so pre-filling
    // from any one of them would be wrong for the rest.
    expect(component.genres).toEqual([]);
    expect(component.selectedOwners).toEqual([]);
  });

  it('should default to appending', () => {
    // The safe direction: replace throws away what each item already had.
    expect(component.appendMode).toBe(true);
  });

  it('should report append when the toggle is on', () => {
    component.genres = ['Horror'];
    component.selectedOwners = ['Dad'];

    component.onSave();

    expect(dialogRef.close).toHaveBeenCalledWith({
      genres: ['Horror'], owners: ['Dad'], mode: 'append'
    });
  });

  it('should report replace when the toggle is off', () => {
    component.appendMode = false;

    component.onSave();

    expect(dialogRef.close.calls.mostRecent().args[0]!.mode).toBe('replace');
  });

  it('should close with nothing on cancel', () => {
    component.onCancel();

    expect(dialogRef.close).toHaveBeenCalledWith();
  });

  it('should count the items in the heading', async () => {
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('3 selected items');

    await build(1);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('1 selected item');
  });
});
