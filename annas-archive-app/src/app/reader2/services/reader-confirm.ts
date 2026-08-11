import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import {
  ConfirmDialogComponent, ConfirmDialogData
} from '../../components/confirm-dialog/confirm-dialog.component';

/**
 * Every question the reader is asked before something is spent or destroyed.
 *
 * <p>One service rather than a dialog opened wherever it is needed: these are
 * the three places the reader can lose money or work, and their wording is the
 * safeguard. Six panels each phrasing it their own way is how one of them ends
 * up not asking at all.</p>
 *
 * <p>Reuses the application's shared confirm dialog. Reader II may not touch
 * Reader I's <i>reader</i> code; ordinary shared UI is not that.</p>
 */
@Injectable({ providedIn: 'root' })
export class ReaderConfirm {
  private readonly dialog = inject(MatDialog);

  /**
   * Asks before regenerating <paramref name="what"/>.
   *
   * <p><c>force=true</c> is the only flag in the reader that bills for work the
   * household has already paid for, and it sits next to a button that looks
   * identical. An architecture test requires every caller to come through
   * here.</p>
   */
  confirmAsync(what: string): Promise<boolean> {
    return this.ask({
      title: `Generate ${what} again?`,
      message:
        `There is already ${what} saved for this book. Generating it again replaces `
        + 'it and costs another request against your allowance.',
      confirmText: 'Generate again',
      cancelText: 'Keep what I have',
      isDanger: false
    });
  }

  /**
   * The one path to anything that bills.
   *
   * <p>Every generating control needs the same three steps: resolve the book,
   * ask before replacing work already paid for, then call. Written out at each
   * call site, the fourth one eventually forgets to ask; written here, forgetting
   * is not available — and the containers do not each need their own copy.</p>
   *
   * @param force Whether this would replace something already generated.
   * @param what  Named in the question, so it says what is being replaced.
   */
  async spendAsync(
    force: boolean, what: string, work: () => Promise<void>
  ): Promise<void> {
    if (force && !(await this.confirmAsync(what))) return;

    await work();
  }

  /** Un-enrolling destroys every summary the household paid for on this book. */
  confirmRemovalAsync(): Promise<boolean> {
    return this.ask({
      title: 'Remove this book from Reader II?',
      message:
        'Every summary, analysis, and definition generated for it is deleted too, and '
        + 'they cannot be recovered without paying for them again. The book itself stays '
        + 'in your library.',
      confirmText: 'Remove it',
      cancelText: 'Keep it',
      isDanger: true
    });
  }

  /** Re-indexing is cheap, but it is still a wait the reader should choose. */
  confirmReIndexAsync(): Promise<boolean> {
    return this.ask({
      title: 'Extract this book again?',
      message:
        'The text is extracted from the file again, which takes a moment. Everything '
        + 'already generated is kept — this only rebuilds the chapters.',
      confirmText: 'Extract again',
      cancelText: 'Cancel',
      isDanger: false
    });
  }

  /**
   * Switching a book to a type that keeps a cast leaves that cast empty — the
   * earlier chapters were never ingested under it.
   *
   * <p>Offered rather than run, like everything else that spends. Named for what
   * it costs: one call per chapter already summarised, and none for the rest.</p>
   */
  confirmBackFillAsync(chapterWord: string): Promise<boolean> {
    return this.ask({
      title: 'Build the story model for this book?',
      message:
        `This book type keeps track of who is who. Building it now reads the ${chapterWord} `
        + 'you have already summarised — one request each, and nothing is summarised again. '
        + 'You can do it later instead, and chapters you summarise from now on are added as '
        + 'you go.',
      confirmText: 'Build it now',
      cancelText: 'Not now',
      isDanger: false
    });
  }

  /**
   * Discarding the record and reading every summarised chapter again.
   *
   * <p>Separate from {@link confirmBackFillAsync} because it is a different
   * question: that one offers work not yet done, this one throws away work
   * already paid for. It is for when what the record holds was gathered under
   * rules that have since changed, so the wording says that rather than implying
   * the reader did something wrong.</p>
   */
  confirmRebuildAsync(chapterWord: string): Promise<boolean> {
    return this.ask({
      title: 'Build this record again from scratch?',
      message:
        `Everything recorded about who is who is discarded, and the ${chapterWord} you have `
        + 'summarised are read again — one request each. Worth doing when the record is '
        + 'missing relationships or descriptions it should have. Answers you gave to "are '
        + 'these the same person" are not kept.',
      confirmText: 'Build it again',
      cancelText: 'Keep what I have',
      isDanger: true
    });
  }

  /**
   * @returns true only when the reader confirmed. Anything else — including
   *   dismissing with escape, which resolves to undefined — is a refusal.
   */
  private async ask(data: ConfirmDialogData): Promise<boolean> {
    return (await firstValueFrom(
      this.dialog.open(ConfirmDialogComponent, { data, width: '28rem' }).afterClosed()
    )) === true;
  }
}
