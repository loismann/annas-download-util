import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

import { SeasonPickerModalComponent, SeasonPickerModalData } from './season-picker-modal.component';
import { MediaSeasonInfo } from '../services/media-search-api.service';

/**
 * Characterization tests for the season picker.
 *
 * It opens two different ways and the difference matters: adding a show fresh
 * defaults to everything but Specials, while re-opening one already in Sonarr
 * has to mirror what is currently monitored — because confirming there submits
 * the *whole* new monitored set, so a wrong default would unmonitor seasons the
 * user never touched.
 */
describe('SeasonPickerModalComponent (characterization)', () => {
  let fixture: ComponentFixture<SeasonPickerModalComponent>;
  let component: SeasonPickerModalComponent;
  let dialogRef: jasmine.SpyObj<MatDialogRef<SeasonPickerModalComponent>>;

  function season(seasonNumber: number, totalEpisodeCount?: number): MediaSeasonInfo {
    return { seasonNumber, statistics: { totalEpisodeCount } };
  }

  async function build(over: Partial<SeasonPickerModalData> = {}): Promise<void> {
    dialogRef = jasmine.createSpyObj<MatDialogRef<SeasonPickerModalComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [SeasonPickerModalComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            title: 'The Outer Limits',
            seasons: [season(0, 2), season(1, 32), season(2, 17)],
            ...over
          } as SeasonPickerModalData
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SeasonPickerModalComponent);
    component = fixture.componentInstance;
  }

  beforeEach(async () => build());

  afterEach(() => fixture.destroy());

  describe('adding a show fresh', () => {
    it('should check everything except Specials', () => {
      // Specials are usually not what someone means by "get this show".
      expect(component.choices.filter(c => c.selected).map(c => c.seasonNumber)).toEqual([1, 2]);
    });

    it('should show Specials as an option all the same', () => {
      expect(component.choices.map(c => c.seasonNumber)).toEqual([0, 1, 2]);
      expect(component.choices[0].label).toBe('Specials');
    });

    it('should mark nothing as already added', () => {
      expect(component.choices.every(c => !c.alreadyAdded)).toBe(true);
    });
  });

  describe('re-opening a show already in Sonarr', () => {
    /**
     * Confirming submits the whole new monitored set, not a delta — so anything
     * left unchecked gets unmonitored. Starting from "everything but Specials"
     * here would silently unmonitor a season the user had deliberately skipped,
     * or re-request one they had dropped.
     */
    it('should mirror what is currently monitored', async () => {
      await build({ alreadyAddedSeasons: [2] });

      expect(component.choices.filter(c => c.selected).map(c => c.seasonNumber)).toEqual([2]);
    });

    it('should mark which seasons are already there', async () => {
      await build({ alreadyAddedSeasons: [2] });

      expect(component.choices.map(c => c.alreadyAdded)).toEqual([false, false, true]);
    });

    it('should treat an empty monitored list as nothing checked', async () => {
      // Not the same as a fresh add: this show is in Sonarr with no seasons
      // monitored, and defaulting to all would re-request the lot.
      await build({ alreadyAddedSeasons: [] });

      expect(component.choices.every(c => !c.selected)).toBe(true);
    });
  });

  describe('the list', () => {
    it('should order seasons however they arrived', async () => {
      await build({ seasons: [season(2), season(0), season(1)] });

      expect(component.choices.map(c => c.seasonNumber)).toEqual([0, 1, 2]);
    });

    it('should carry the episode count for each season', () => {
      expect(component.choices.map(c => c.episodeCount)).toEqual([2, 32, 17]);
    });

    it('should cope with a season Sonarr has no counts for', async () => {
      await build({ seasons: [{ seasonNumber: 1 }] });

      expect(component.choices[0].episodeCount).toBeUndefined();
    });

    it('should name every other season by number', () => {
      expect(component.choices.map(c => c.label)).toEqual(['Specials', 'Season 1', 'Season 2']);
    });
  });

  describe('confirming', () => {
    it('should hand back the checked seasons', () => {
      component.confirm();

      expect(dialogRef.close).toHaveBeenCalledWith([1, 2]);
    });

    it('should check and uncheck everything at once', () => {
      component.selectNone();
      expect(component.choices.every(c => !c.selected)).toBe(true);

      component.selectAll();
      expect(component.choices.every(c => c.selected)).toBe(true);
    });

    it('should hand back an empty list as a deliberate choice', () => {
      // Distinct from cancelling: this means "monitor none of them".
      component.selectNone();

      component.confirm();

      expect(dialogRef.close).toHaveBeenCalledWith([]);
    });

    it('should hand back nothing at all on cancel', () => {
      // undefined is what tells the caller to do nothing.
      component.cancel();

      expect(dialogRef.close).toHaveBeenCalledWith(undefined);
    });
  });
});
