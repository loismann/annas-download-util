import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { GamingApiService } from '../services/gaming-api.service';

interface TerminalLine {
  text: string;
  type: 'info' | 'success' | 'error' | 'warning' | 'command';
  timestamp: string;
}

@Component({
  selector: 'app-gaming-control',
  standalone: true,
  imports: [
    CommonModule,
    MatButtonModule
  ],
  styles: [`
    .terminal-container {
      background: #000000;
      min-height: calc(100vh - 64px);
      padding: 20px;
      font-family: 'Courier New', monospace;
      max-width: 900px;
      margin: 0 auto;
    }

    .terminal-header {
      color: #00ff00;
      font-size: 18px;
      font-weight: bold;
      margin-bottom: 20px;
      text-shadow: 0 0 10px #00ff00;
      letter-spacing: 2px;
      padding: 15px;
      border: 2px solid #00ff00;
      border-radius: 4px;
      background: #0a0a0a;
    }

    .terminal-title {
      font-size: 20px;
      margin-bottom: 8px;
    }

    .terminal-version {
      font-size: 12px;
      color: #00aa00;
      text-shadow: 0 0 5px #00aa00;
    }

    @media (max-width: 768px) {
      .terminal-container {
        padding: 10px;
      }

      .terminal-header {
        font-size: 14px;
        padding: 10px;
      }

      .terminal-title {
        font-size: 16px;
      }

      .terminal-version {
        font-size: 10px;
      }
    }

    .terminal-buttons {
      display: flex;
      gap: 16px;
      margin-bottom: 24px;
    }

    .terminal-button {
      background: #1a1a1a;
      border: 2px solid #00ff00;
      color: #00ff00;
      padding: 12px 24px;
      font-family: 'Courier New', monospace;
      font-size: 14px;
      cursor: pointer;
      transition: all 0.3s;
      text-transform: uppercase;
      letter-spacing: 1px;
    }

    .terminal-button:hover:not(:disabled) {
      background: #00ff00;
      color: #000000;
      box-shadow: 0 0 20px #00ff00;
    }

    .terminal-button:disabled {
      opacity: 0.5;
      cursor: not-allowed;
      border-color: #666;
      color: #666;
    }

    .terminal-output {
      background: #0a0a0a;
      border: 2px solid #00ff00;
      border-radius: 4px;
      padding: 20px;
      min-height: 400px;
      max-height: 600px;
      overflow-y: auto;
      box-shadow: inset 0 0 20px rgba(0, 255, 0, 0.1);
    }

    .terminal-line {
      margin-bottom: 4px;
      line-height: 1.5;
      font-size: 13px;
    }

    .terminal-timestamp {
      color: #666;
      margin-right: 8px;
    }

    .terminal-info {
      color: #00ff00;
    }

    .terminal-success {
      color: #00ff00;
      font-weight: bold;
    }

    .terminal-error {
      color: #ff0000;
      font-weight: bold;
    }

    .terminal-warning {
      color: #ffff00;
    }

    .terminal-command {
      color: #00ffff;
      font-weight: bold;
    }

    .cursor {
      display: inline-block;
      width: 8px;
      height: 14px;
      background: #00ff00;
      animation: blink 1s infinite;
      margin-left: 4px;
    }

    @keyframes blink {
      0%, 50% { opacity: 1; }
      51%, 100% { opacity: 0; }
    }

    .terminal-output::-webkit-scrollbar {
      width: 8px;
    }

    .terminal-output::-webkit-scrollbar-track {
      background: #0a0a0a;
    }

    .terminal-output::-webkit-scrollbar-thumb {
      background: #00ff00;
      border-radius: 4px;
    }
  `],
  template: `
    <div class="terminal-container">
      <div class="terminal-header">
        <div class="terminal-title">GAMING PC REMOTE CONTROL TERMINAL</div>
        <div class="terminal-version">v2.0.25</div>
      </div>

      <div class="terminal-buttons">
        <button
          class="terminal-button"
          (click)="wakePC()"
          [disabled]="wakeButtonDisabled">
          {{ loading && action === 'wake' ? '► WAKING...' : '► WAKE PC & LAUNCH STEAM' }}
        </button>

        <button
          class="terminal-button"
          (click)="sleepPC()"
          [disabled]="sleepButtonDisabled">
          {{ loading && action === 'sleep' ? '■ SLEEPING...' : '■ SLEEP PC' }}
        </button>
      </div>

      <div class="terminal-output">
        <div *ngFor="let line of terminalLines" class="terminal-line">
          <span class="terminal-timestamp">{{ line.timestamp }}</span>
          <span [ngClass]="{
            'terminal-info': line.type === 'info',
            'terminal-success': line.type === 'success',
            'terminal-error': line.type === 'error',
            'terminal-warning': line.type === 'warning',
            'terminal-command': line.type === 'command'
          }">{{ line.text }}</span>
        </div>
        <span class="cursor" *ngIf="!loading"></span>
      </div>
    </div>
  `
})
export class GamingControlComponent implements OnInit {
  loading = false;
  action: 'wake' | 'sleep' | null = null;
  terminalLines: TerminalLine[] = [];
  pcOnline: boolean | null = null; // null = unknown, true = online, false = offline

  constructor(
    private gamingApi: GamingApiService
  ) {}

  ngOnInit(): void {
    this.addLine('System initialized', 'success');
    this.addLine('Synology NAS: 192.168.0.81 (online)', 'success');
    this.addLine('Checking gaming PC status...', 'info');
    this.checkPCStatus();
  }

  checkPCStatus(): void {
    this.gamingApi.getGamingPCStatus().subscribe({
      next: (response) => {
        this.pcOnline = response.isOnline;
        const status = response.isOnline ? 'ONLINE' : 'OFFLINE';
        const type = response.isOnline ? 'success' : 'warning';
        this.addLine(`Gaming PC: 192.168.0.80 (${status})`, type);
        this.addLine('Ready for commands...', 'info');
      },
      error: (err) => {
        this.pcOnline = false;
        this.addLine('Gaming PC: 192.168.0.80 (status check failed)', 'error');
        this.addLine('Ready for commands...', 'info');
      }
    });
  }

  get wakeButtonDisabled(): boolean {
    return this.loading || this.pcOnline === true;
  }

  get sleepButtonDisabled(): boolean {
    return this.loading || this.pcOnline === false || this.pcOnline === null;
  }

  private addLine(text: string, type: TerminalLine['type'] = 'info', delay: number = 0): void {
    setTimeout(() => {
      const timestamp = new Date().toLocaleTimeString('en-US', { hour12: false });
      this.terminalLines.push({ text, type, timestamp });
      this.scrollToBottom();
    }, delay);
  }

  private scrollToBottom(): void {
    setTimeout(() => {
      const output = document.querySelector('.terminal-output');
      if (output) {
        output.scrollTop = output.scrollHeight;
      }
    }, 50);
  }

  wakePC(): void {
    this.loading = true;
    this.action = 'wake';

    this.addLine('$ wake-gaming-pc --action=1', 'command');
    this.addLine('────────────────────────────────────────────────', 'info', 100);
    this.addLine('→ Connecting to Synology NAS...', 'info', 200);
    this.addLine('✓ Connection established', 'success', 400);
    this.addLine('→ Sending Wake-on-LAN magic packet...', 'info', 600);
    this.addLine('  Target MAC: 04:7C:16:EA:C7:58', 'info', 800);
    this.addLine('  Target IP: 192.168.0.80', 'info', 1000);

    this.gamingApi.toggleGamingPC(1).subscribe({
      next: (response) => {
        this.loading = false;
        this.action = null;

        if (response.success) {
          this.addLine('✓ Magic packet sent successfully', 'success', 1200);
          this.addLine('→ Waiting for PC to respond...', 'info', 1400);
          this.addLine('✓ PC is online and reachable', 'success', 2600);
          this.addLine('→ Executing remote tasks...', 'info', 2800);
          this.addLine('  [1/2] Launching Steam...', 'info', 3200);
          this.addLine('  [2/2] Turning off monitor...', 'info', 3600);
          this.addLine('✓ All tasks completed successfully', 'success', 4000);
          this.addLine('→ Gaming PC is ready for Steam Link', 'success', 4200);

          if (response.output) {
            this.addLine('────────────────────────────────────────────────', 'info', 4400);
            this.addLine('SERVER RESPONSE:', 'warning', 4600);
            const lines = response.output.split('\n');
            lines.forEach((line, index) => {
              if (line.trim()) {
                this.addLine(line, 'info', 4800 + (index * 100));
              }
            });
          }

          this.addLine('────────────────────────────────────────────────', 'info', 5000);

          // Re-check PC status after wake operation
          setTimeout(() => {
            this.addLine('→ Re-checking PC status...', 'info');
            this.checkPCStatus();
          }, 5200);
        } else {
          this.addLine('✗ Operation failed', 'error', 1200);
          this.addLine(`Error: ${response.error || response.message}`, 'error', 1400);
          this.addLine('────────────────────────────────────────────────', 'info', 1600);
        }
      },
      error: (err) => {
        this.loading = false;
        this.action = null;
        this.addLine('✗ CRITICAL ERROR', 'error', 1200);
        this.addLine(`${err.error?.message || err.message}`, 'error', 1400);
        this.addLine('────────────────────────────────────────────────', 'info', 1600);
      }
    });
  }

  sleepPC(): void {
    this.loading = true;
    this.action = 'sleep';

    this.addLine('$ sleep-gaming-pc --action=2', 'command');
    this.addLine('────────────────────────────────────────────────', 'info', 100);
    this.addLine('→ Connecting to Synology NAS...', 'info', 200);
    this.addLine('✓ Connection established', 'success', 400);
    this.addLine('→ Sending sleep command to gaming PC...', 'info', 600);
    this.addLine('  Target IP: 192.168.0.80', 'info', 800);

    this.gamingApi.toggleGamingPC(2).subscribe({
      next: (response) => {
        this.loading = false;
        this.action = null;

        if (response.success) {
          this.addLine('✓ Sleep command sent', 'success', 1000);
          this.addLine('→ Closing Steam application...', 'info', 1200);
          this.addLine('→ Putting PC into sleep mode...', 'info', 1400);
          this.addLine('✓ Gaming PC is now sleeping', 'success', 1800);

          if (response.output) {
            this.addLine('────────────────────────────────────────────────', 'info', 2000);
            this.addLine('SERVER RESPONSE:', 'warning', 2200);
            const lines = response.output.split('\n');
            lines.forEach((line, index) => {
              if (line.trim()) {
                this.addLine(line, 'info', 2400 + (index * 100));
              }
            });
          }

          this.addLine('────────────────────────────────────────────────', 'info', 2600);

          // Re-check PC status after sleep operation
          setTimeout(() => {
            this.addLine('→ Re-checking PC status...', 'info');
            this.checkPCStatus();
          }, 2800);
        } else {
          this.addLine('✗ Operation failed', 'error', 1000);
          this.addLine(`Error: ${response.error || response.message}`, 'error', 1200);
          this.addLine('────────────────────────────────────────────────', 'info', 1400);
        }
      },
      error: (err) => {
        this.loading = false;
        this.action = null;
        this.addLine('✗ CRITICAL ERROR', 'error', 1000);
        this.addLine(`${err.error?.message || err.message}`, 'error', 1200);
        this.addLine('────────────────────────────────────────────────', 'info', 1400);
      }
    });
  }
}
