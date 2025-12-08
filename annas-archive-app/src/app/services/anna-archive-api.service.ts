import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';

import { BookDto } from '../models/book-dto.model';

/* ─────────────── existing member-download shape ──────────────── */
export interface DownloadMemberResponse {
  downloadUrl: string;
  accountFastInfo: {
    downloadsLeft: number;
    downloadsPerDay: number;
  } | null;
}

/* ─────────────── new send-to-boox shape (via Dropbox) ─────────────────────── */
export interface SendToBooxResponse {
  success: boolean;
  dropboxPath?: string;
  dropboxFileId?: string;
  message?: string;
  accountFastInfo: {
    downloadsLeft: number;
    downloadsPerDay: number;
  } | null;
}

@Injectable({ providedIn: 'root' })
export class AnnaArchiveApiService {
  private readonly baseUrl = 'https://fs01pfbooks.synology.me:5051/api/anna';
  // Use the path below for local development (HTTP)
  // private readonly baseUrl = 'http://localhost:5050/api/anna';

  constructor(private http: HttpClient) {}

  /* ══════════════════════════════════════════════════════════════
     Search – always return an array, even when the API sent 1 obj
     ══════════════════════════════════════════════════════════════ */
  searchBooks(name: string, exact: boolean): Observable<BookDto[]> {
    const params = new HttpParams()
      .set('name', name)
      .set('exact', exact.toString());

    return this.http
      .get<BookDto | BookDto[]>(`${this.baseUrl}/book`, { params })
      .pipe(map(res => (Array.isArray(res) ? res : [res])));
  }

  /* ══════════════════════════════════════════════════════════════
     Member download – returns fast-download URL + counter
     ══════════════════════════════════════════════════════════════ */
  downloadMember(md5: string): Observable<DownloadMemberResponse> {
    return this.http.get<DownloadMemberResponse>(
      `${this.baseUrl}/book/${md5}/download/member`
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Send the file to Dropbox for Boox sync
     – passes book title so backend can name it correctly
     ══════════════════════════════════════════════════════════════ */
  sendToBoox(md5: string, title: string): Observable<SendToBooxResponse> {
    const params = new HttpParams().set('title', title);
    return this.http.post<SendToBooxResponse>(
      `${this.baseUrl}/book/${md5}/send-to-boox`,
      null,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Send the file to Kindle via email
     ══════════════════════════════════════════════════════════════ */
  sendToKindle(md5: string, title: string): Observable<SendToBooxResponse> {
    const params = new HttpParams().set('title', title);
    return this.http.post<SendToBooxResponse>(
      `${this.baseUrl}/book/${md5}/send-to-kindle`,
      null,
      { params }
    );
  }
}
