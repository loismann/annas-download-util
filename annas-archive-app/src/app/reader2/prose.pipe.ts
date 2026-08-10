import { Pipe, PipeTransform, SecurityContext, inject } from '@angular/core';
import { DomSanitizer } from '@angular/platform-browser';
import { marked } from 'marked';

/**
 * Model output, rendered: markdown in, sanitised HTML out.
 *
 * <p>Every summary and analysis arrives as markdown — the lenses ask for bold
 * headings and lists by name — so showing the raw text puts <code>**Who's
 * present:**</code> in front of the reader as punctuation.</p>
 *
 * <p>Sanitised through Angular's own sanitizer rather than trusted, because the
 * text comes from a model reading an arbitrary EPUB: whatever HTML survives a
 * markdown pass of untrusted input is stripped to the safe subset, never handed
 * to <code>bypassSecurityTrustHtml</code>.</p>
 */
@Pipe({ name: 'prose', standalone: true })
export class ProsePipe implements PipeTransform {
  private readonly sanitizer = inject(DomSanitizer);

  transform(markdown: string | null): string {
    if (!markdown) return '';

    return this.sanitizer.sanitize(
      SecurityContext.HTML, marked.parse(markdown, { async: false })) ?? '';
  }
}
