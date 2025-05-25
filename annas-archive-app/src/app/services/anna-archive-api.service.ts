import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';

import { BookDto } from '../models/book-dto.model';

export interface DownloadMemberResponse {
  downloadUrl: string;
  accountFastInfo: {
    downloadsLeft: number;
    downloadsPerDay: number;
  } | null;
}

@Injectable({ providedIn: 'root' })
export class AnnaArchiveApiService {
  private readonly baseUrl = '/api/anna';

  constructor(private http: HttpClient) {}

  /**
   * Normalize the API response into an array of BookDto.
   */
  searchBooks(name: string, exact: boolean): Observable<BookDto[]> {
    const params = new HttpParams()
      .set('name', name)
      .set('exact', exact.toString());

    return this.http
      .get<BookDto | BookDto[]>(`${this.baseUrl}/book`, { params })
      .pipe(
        map(res => Array.isArray(res) ? res : [res])
      );
  }

  /**
   * Download via member endpoint, returns both URL and updated counter.
   */
  downloadMember(md5: string): Observable<DownloadMemberResponse> {
    return this.http.get<DownloadMemberResponse>(
      `${this.baseUrl}/book/${md5}/download/member`
    );
  }
}

