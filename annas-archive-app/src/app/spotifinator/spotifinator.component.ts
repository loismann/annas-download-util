import { Component, OnDestroy, ViewChild, ElementRef, AfterViewChecked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatListModule } from '@angular/material/list';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Subject } from 'rxjs';
import { takeUntil } from 'rxjs/operators';

import { SpotifinatorApiService } from '../services/spotifinator-api.service';
import { LoggerService } from '../services/logger.service';
import {
  ChatMessage,
  ViewState,
  SpotifyTrack,
  SpotifyPlaylist,
  SpotifySearchResult,
  VibeGenerationResult
} from './spotifinator.models';

@Component({
  selector: 'app-spotifinator',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatInputModule,
    MatFormFieldModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatListModule,
    MatTooltipModule
  ],
  templateUrl: './spotifinator.component.html',
  styleUrl: './spotifinator.component.scss'
})
export class SpotifinatorComponent implements OnDestroy, AfterViewChecked {
  @ViewChild('chatContainer') private chatContainer!: ElementRef;
  @ViewChild('messageInput') private messageInput!: ElementRef;

  // State
  viewState: ViewState = 'idle';
  userInput = '';
  messages: ChatMessage[] = [];
  errorMessage = '';

  private destroy$ = new Subject<void>();
  private shouldScrollToBottom = false;

  constructor(
    private api: SpotifinatorApiService,
    private logger: LoggerService
  ) {
    this.addWelcomeMessage();
  }

  ngAfterViewChecked(): void {
    if (this.shouldScrollToBottom) {
      this.scrollToBottom();
      this.shouldScrollToBottom = false;
    }
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  // ─── Message Handling ──────────────────────────────────────────────────────

  onSubmit(): void {
    const message = this.userInput.trim();
    if (!message || this.viewState === 'processing') return;

    this.addUserMessage(message);
    this.userInput = '';
    this.processCommand(message);
  }

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.onSubmit();
    }
  }

  // ─── Command Processing ────────────────────────────────────────────────────

  private processCommand(message: string): void {
    this.viewState = 'processing';
    const pendingId = this.addPendingMessage();

    const context = this.getConversationContext();

    this.api.processCommand(message, context).pipe(
      takeUntil(this.destroy$)
    ).subscribe({
      next: (response) => {
        this.removePendingMessage(pendingId);
        this.addAssistantMessage(response.naturalResponse, response.data);
        this.viewState = 'idle';
      },
      error: (err) => {
        this.removePendingMessage(pendingId);
        const errorMsg = err.error?.error || err.message || 'Something went wrong';
        this.addAssistantMessage(`Sorry, I encountered an error: ${errorMsg}`, null, true);
        this.viewState = 'error';
        this.errorMessage = errorMsg;
        this.logger.error('[Spotifinator] Command failed:', err);
      }
    });
  }

  // ─── Message Management ────────────────────────────────────────────────────

  private addWelcomeMessage(): void {
    this.messages.push({
      id: this.generateId(),
      role: 'assistant',
      content: `Hey! I'm your Spotify assistant. You can ask me things like:

- "Search for songs by Taylor Swift"
- "Show me my playlists"
- "Create a playlist called Road Trip Vibes"
- "Add that song to my workout playlist"

What would you like to do?`,
      timestamp: new Date()
    });
  }

  private addUserMessage(content: string): void {
    this.messages.push({
      id: this.generateId(),
      role: 'user',
      content,
      timestamp: new Date()
    });
    this.shouldScrollToBottom = true;
  }

  private addPendingMessage(): string {
    const id = this.generateId();
    this.messages.push({
      id,
      role: 'assistant',
      content: '',
      timestamp: new Date(),
      pending: true
    });
    this.shouldScrollToBottom = true;
    return id;
  }

  private removePendingMessage(id: string): void {
    const index = this.messages.findIndex(m => m.id === id);
    if (index !== -1) {
      this.messages.splice(index, 1);
    }
  }

  private addAssistantMessage(
    content: string,
    data?: SpotifySearchResult | SpotifyPlaylist[] | SpotifyPlaylist | VibeGenerationResult | null,
    isError = false
  ): void {
    this.messages.push({
      id: this.generateId(),
      role: 'assistant',
      content,
      timestamp: new Date(),
      data,
      error: isError
    });
    this.shouldScrollToBottom = true;
  }

  private getConversationContext(): string {
    return this.messages
      .slice(-6)
      .filter(m => !m.pending)
      .map(m => `${m.role}: ${m.content}`)
      .join('\n');
  }

  // ─── Data Type Guards ──────────────────────────────────────────────────────

  isSearchResult(data: unknown): data is SpotifySearchResult {
    return !!data && typeof data === 'object' && 'tracks' in data && Array.isArray((data as SpotifySearchResult).tracks);
  }

  isPlaylistArray(data: unknown): data is SpotifyPlaylist[] {
    return Array.isArray(data) && data.length > 0 && 'trackCount' in data[0];
  }

  isPlaylist(data: unknown): data is SpotifyPlaylist {
    return !!data && typeof data === 'object' && 'trackCount' in data && !Array.isArray(data);
  }

  isVibeGenerationResult(data: unknown): data is VibeGenerationResult {
    return !!data && typeof data === 'object' && 'foundTracks' in data && Array.isArray((data as VibeGenerationResult).foundTracks);
  }

  // ─── Track Actions ─────────────────────────────────────────────────────────

  openInSpotify(url: string | null): void {
    if (url) {
      window.open(url, '_blank');
    }
  }

  formatDuration(ms: number): string {
    const minutes = Math.floor(ms / 60000);
    const seconds = Math.floor((ms % 60000) / 1000);
    return `${minutes}:${seconds.toString().padStart(2, '0')}`;
  }

  // ─── Utilities ─────────────────────────────────────────────────────────────

  private scrollToBottom(): void {
    try {
      if (this.chatContainer?.nativeElement) {
        this.chatContainer.nativeElement.scrollTop = this.chatContainer.nativeElement.scrollHeight;
      }
    } catch (err) {
      this.logger.error('[Spotifinator] Scroll error:', err);
    }
  }

  private generateId(): string {
    return `msg-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;
  }

  focusInput(): void {
    this.messageInput?.nativeElement?.focus();
  }
}
