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
    publicationYear: number | null;
    baseScore: number | null;
    finalScore: number | null;
  
    /** newly added */
    isbn?: string;
  
    coverCandidates: string[];
  }
  