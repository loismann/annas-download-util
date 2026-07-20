export interface VideoFormat {
  formatId: string;
  resolution: string;
  extension: string;
  fileSize: string;
  quality: string;
  isAudioOnly: boolean;
}

export interface VideoInfo {
  title: string;
  uploader: string;
  duration: string;
  thumbnail: string;
  formats: VideoFormat[];
}

export interface StartDownloadRequest {
  url: string;
  formatId: string;
  outputName?: string;
}

export interface DownloadJob {
  jobId: string;
  url: string;
  title: string;
  status: 'queued' | 'downloading' | 'processing' | 'complete' | 'failed' | 'cancelled';
  progressPercent: number;
  currentSpeed?: string;
  eta?: string;
  outputPath?: string;
  error?: string;
  statusMessage?: string;
  startedAt: string;
  completedAt?: string;
}

export interface DownloadProgressEvent {
  jobId: string;
  status: string;
  progressPercent: number;
  currentSpeed?: string;
  eta?: string;
  message?: string;
}
