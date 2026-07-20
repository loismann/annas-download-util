export interface BookDto {
  title: string;
  md5: string;
  authors: string[];
  language: string;
  format: string;
  source: string;
  fileSize: string;
  bookType: string;
  publisher: string;
  year: number | null;
  isbn: string | null;
  coverCandidates: string[];
  description?: string | null;
  descriptionSource?: string | null;

  /* NEW – track button state in the UI */
  sendState?: 'idle' | 'sending' | 'success' | 'error';
  libraryState?: 'idle' | 'sending' | 'success' | 'error';
  dadsKindleState?: 'idle' | 'sending' | 'success' | 'error';
  momsKindleState?: 'idle' | 'sending' | 'success' | 'error';
}
