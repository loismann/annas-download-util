import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DropboxEpubChapter } from '../../models/dropbox-epub.model';

@Component({
  selector: 'app-chapter-list',
  standalone: true,
  imports: [
    CommonModule,
    MatFormFieldModule,
    MatSelectModule,
    MatIconModule,
    MatTooltipModule
  ],
  templateUrl: './chapter-list.component.html',
  styleUrls: ['./chapter-list.component.css']
})
export class ChapterListComponent {
  @Input() chapters: DropboxEpubChapter[] = [];
  @Input() selectedChapterId: number | null = null;
  @Input() loadingChapters = false;
  @Input() loadingContent = false;
  @Input() cachedChapterIds: Set<number> = new Set();
  @Input() disabled = false;

  @Output() chapterSelected = new EventEmitter<number>();

  get isDisabled(): boolean {
    return this.disabled || !this.chapters.length || this.loadingChapters || this.loadingContent;
  }

  onSelectionChange(chapterId: number): void {
    this.chapterSelected.emit(chapterId);
  }

  isCached(chapterId: number): boolean {
    return this.cachedChapterIds.has(chapterId);
  }
}
