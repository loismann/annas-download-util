import { TestBed } from '@angular/core/testing';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { NEVER, of, throwError } from 'rxjs';
import { PlanReviewDialogComponent } from './plan-review-dialog.component';
import { SpotifinatorApiService } from '../../services/spotifinator-api.service';
import { SpotifyPlan, SpotifyPlanPreview } from '../spotifinator.models';

/**
 * The confirmation gate.
 *
 * This is the last thing between a sentence and a real change to someone's Spotify,
 * so the properties here are about not letting a change through by accident: a
 * destructive plan needs a deliberate second act, and a plan the server refused must
 * not close as though it succeeded.
 *
 * It used to live in the chat transcript. Moving it to a modal was a usability fix —
 * a plan built from the draft panel put its decision in a pane the user was not
 * looking at — but the gate itself has to survive the move, which is what these pin.
 */
describe('PlanReviewDialogComponent', () => {
  let api: jasmine.SpyObj<SpotifinatorApiService>;
  let dialogRef: jasmine.SpyObj<MatDialogRef<PlanReviewDialogComponent>>;

  const plan = (
    over: Partial<SpotifyPlan> = {}, preview: Partial<SpotifyPlanPreview> = {}
  ): SpotifyPlan => ({
    id: 'plan-1', action: 'AddItems', safetyTier: 'Additive', status: 'AwaitingConfirmation',
    createdAtUtc: '2026-08-02T12:00:00Z', expiresAtUtc: '2026-08-02T12:30:00Z',
    targets: [], steps: [], originalRequest: null, confirmedBy: null, confirmedAtUtc: null,
    failure: null, canUndo: false, undoOfPlanId: null, recovery: null,
    preview: {
      summary: 'Add 3 tracks', confirmLabel: 'Add 3 tracks', effects: [], warnings: [],
      requiresHighImpactAcknowledgement: false, itemsAdded: 3, itemsRemoved: 0,
      itemsSkippedAsDuplicates: 0, itemsUnresolved: 0, playlistsAffected: 1,
      ...preview
    },
    ...over
  });

  const build = (subject: SpotifyPlan): PlanReviewDialogComponent => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [PlanReviewDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: SpotifinatorApiService, useValue: api },
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MAT_DIALOG_DATA, useValue: { plan: subject } }
      ]
    });
    return TestBed.createComponent(PlanReviewDialogComponent).componentInstance;
  };

  beforeEach(() => {
    api = jasmine.createSpyObj<SpotifinatorApiService>(
      'SpotifinatorApiService', ['confirmPlan', 'cancelPlan']);
    api.confirmPlan.and.returnValue(of(plan({ status: 'Completed' })));
    api.cancelPlan.and.returnValue(of(plan({ status: 'Cancelled' })));

    dialogRef = jasmine.createSpyObj<MatDialogRef<PlanReviewDialogComponent>>(
      'MatDialogRef', ['close']);
  });

  // ─── the destructive-change gate ─────────────────────────────────────────

  it('lets an ordinary additive plan be confirmed straight away', () => {
    expect(build(plan()).canConfirm).toBeTrue();
  });

  it('blocks a high-impact plan until the box is ticked', () => {
    // The second gate on a destructive change. Without it a replace could be
    // confirmed by the same single click as an ordinary add.
    const dialog = build(plan({}, { requiresHighImpactAcknowledgement: true }));

    expect(dialog.canConfirm).toBeFalse();

    dialog.acknowledged = true;
    expect(dialog.canConfirm).toBeTrue();
  });

  it('re-blocks when the acknowledgement is unticked', () => {
    const dialog = build(plan({}, { requiresHighImpactAcknowledgement: true }));

    dialog.acknowledged = true;
    dialog.acknowledged = false;

    expect(dialog.canConfirm).toBeFalse();
  });

  it('refuses to confirm a high-impact plan even if confirm is called directly', () => {
    // Disabling the button is presentation. This is the gate.
    const dialog = build(plan({}, { requiresHighImpactAcknowledgement: true }));

    dialog.confirm();

    expect(api.confirmPlan).not.toHaveBeenCalled();
  });

  it('cannot inherit an acknowledgement from a previous plan', () => {
    // Each dialog owns its own tick. The old transcript version kept a set of
    // acknowledged plan IDs on the page, which is the shape that lets a tick made
    // against one change authorize another.
    const first = build(plan({ id: 'a' }, { requiresHighImpactAcknowledgement: true }));
    first.acknowledged = true;

    const second = build(plan({ id: 'b' }, { requiresHighImpactAcknowledgement: true }));

    expect(second.canConfirm).toBeFalse();
  });

  it('tells the server whether the acknowledgement was given', () => {
    const dialog = build(plan({}, { requiresHighImpactAcknowledgement: true }));
    dialog.acknowledged = true;

    dialog.confirm();

    expect(api.confirmPlan).toHaveBeenCalledWith('plan-1', true);
  });

  // ─── closing honestly ────────────────────────────────────────────────────

  it('closes with the executed plan so the caller can report what happened', () => {
    const executed = plan({ status: 'Completed' });
    api.confirmPlan.and.returnValue(of(executed));

    build(plan()).confirm();

    expect(dialogRef.close).toHaveBeenCalledWith(executed);
  });

  it('stays open and explains itself when the server refuses', () => {
    // Closing here would look exactly like success. The 409 carries the real
    // sentence — expired, or the playlist moved under us — and it belongs beside
    // the button that failed.
    api.confirmPlan.and.returnValue(
      throwError(() => ({ error: { error: 'That playlist changed since this was planned.' } })));
    const dialog = build(plan());

    dialog.confirm();

    expect(dialogRef.close).not.toHaveBeenCalled();
    expect(dialog.error).toContain('changed since');
  });

  it('lets you try again after a refusal', () => {
    api.confirmPlan.and.returnValue(throwError(() => ({ status: 409 })));
    const dialog = build(plan());
    dialog.confirm();

    // A failed confirm must not leave the buttons dead.
    expect(dialog.busy).toBeFalse();
  });

  it('cancels server-side rather than only closing', () => {
    // Just closing leaves the plan sitting in the store until it expires, where a
    // later "resume" could still find it.
    build(plan()).cancel();

    expect(api.cancelPlan).toHaveBeenCalledWith('plan-1');
  });

  it('closes with nothing when cancelled, so the caller reports no change', () => {
    build(plan()).cancel();

    expect(dialogRef.close).toHaveBeenCalledWith(undefined);
  });

  it('still closes when the cancel call itself fails', () => {
    // The user asked to stop. Trapping them in the dialog because the tidy-up call
    // failed would be the wrong answer to that.
    api.cancelPlan.and.returnValue(throwError(() => ({ status: 500 })));

    build(plan()).cancel();

    expect(dialogRef.close).toHaveBeenCalledWith(undefined);
  });

  it('will not confirm twice while a confirm is in flight', () => {
    api.confirmPlan.and.returnValue(NEVER);
    const dialog = build(plan());

    dialog.confirm();
    dialog.confirm();

    expect(api.confirmPlan).toHaveBeenCalledTimes(1);
  });

  it('does not let a backdrop click discard the decision', () => {
    build(plan());

    expect(dialogRef.disableClose).toBeTrue();
  });
});
