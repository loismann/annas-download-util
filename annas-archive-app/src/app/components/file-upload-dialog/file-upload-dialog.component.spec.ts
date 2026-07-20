import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';
import { FileUploadDialogComponent, UploadResult } from './file-upload-dialog.component';
import { LibraryApiService, LibrarySupportedFormatsResponse, LibraryUploadResponse } from '../../services/library-api.service';

describe('FileUploadDialogComponent', () => {
  let component: FileUploadDialogComponent;
  let fixture: ComponentFixture<FileUploadDialogComponent>;
  let mockDialogRef: jasmine.SpyObj<MatDialogRef<FileUploadDialogComponent>>;
  let mockLibraryApi: jasmine.SpyObj<LibraryApiService>;

  const mockFormatsResponse: LibrarySupportedFormatsResponse = {
    formats: ['.epub', '.pdf', '.mobi'],
    maxFileSizeMb: 500
  };

  const createComponent = () => {
    mockDialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);
    mockLibraryApi = jasmine.createSpyObj('LibraryApiService', ['getSupportedFormats', 'uploadBook']);
    mockLibraryApi.getSupportedFormats.and.returnValue(of(mockFormatsResponse));

    TestBed.configureTestingModule({
      imports: [FileUploadDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: LibraryApiService, useValue: mockLibraryApi }
      ]
    });

    fixture = TestBed.createComponent(FileUploadDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  };

  beforeEach(() => {
    createComponent();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should load supported formats on init', () => {
    expect(mockLibraryApi.getSupportedFormats).toHaveBeenCalled();
    expect(component.supportedFormats).toEqual(['.epub', '.pdf', '.mobi']);
    expect(component.maxFileSizeMb).toBe(500);
  });

  it('should use defaults if API fails', fakeAsync(() => {
    mockLibraryApi.getSupportedFormats.and.returnValue(throwError(() => new Error('API Error')));

    fixture = TestBed.createComponent(FileUploadDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    tick();

    expect(component.supportedFormats.length).toBeGreaterThan(0);
    expect(component.maxFileSizeMb).toBe(500);
  }));

  describe('file validation', () => {
    it('should reject unsupported file extensions', () => {
      const file = new File(['content'], 'document.exe', { type: 'application/octet-stream' });

      // Simulate file selection
      const input = { target: { files: [file] } } as unknown as Event;
      (input.target as HTMLInputElement).value = '';
      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);

      expect(component.uploadQueue.length).toBe(1);
      expect(component.uploadQueue[0].status).toBe('error');
      expect(component.uploadQueue[0].error).toContain('Unsupported format');
    });

    it('should accept supported file extensions', () => {
      const file = new File(['content'], 'book.epub', { type: 'application/epub+zip' });

      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);

      expect(component.uploadQueue.length).toBe(1);
      expect(component.uploadQueue[0].status).toBe('pending');
    });

    it('should reject files over size limit', () => {
      // Create a mock file that reports large size
      const file = new File([''], 'bigbook.epub', { type: 'application/epub+zip' });
      Object.defineProperty(file, 'size', { value: 600 * 1024 * 1024 });

      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);

      expect(component.uploadQueue.length).toBe(1);
      expect(component.uploadQueue[0].status).toBe('error');
      expect(component.uploadQueue[0].error).toContain('too large');
    });

    it('should not add duplicate files to queue', () => {
      const file = new File(['content'], 'book.epub', { type: 'application/epub+zip' });

      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);
      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);

      expect(component.uploadQueue.length).toBe(1);
    });
  });

  describe('removeFromQueue', () => {
    it('should remove pending files', () => {
      const file = new File(['content'], 'book.epub', { type: 'application/epub+zip' });
      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);
      expect(component.uploadQueue.length).toBe(1);

      component.removeFromQueue(0);

      expect(component.uploadQueue.length).toBe(0);
    });

    it('should not remove uploading files', () => {
      const file = new File(['content'], 'book.epub', { type: 'application/epub+zip' });
      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);
      component.uploadQueue[0].status = 'uploading';

      component.removeFromQueue(0);

      expect(component.uploadQueue.length).toBe(1);
    });
  });

  describe('upload process', () => {
    it('should upload files sequentially', fakeAsync(() => {
      const file1 = new File(['content1'], 'book1.epub', { type: 'application/epub+zip' });
      const file2 = new File(['content2'], 'book2.pdf', { type: 'application/pdf' });

      const uploadResponse: LibraryUploadResponse = {
        success: true,
        fileName: 'book1.epub',
        fileSize: '1KB',
        message: 'Uploaded'
      };
      mockLibraryApi.uploadBook.and.returnValue(of(uploadResponse));

      component.onFileSelected({ target: { files: [file1, file2], value: '' } } as unknown as Event);
      expect(component.uploadQueue.length).toBe(2);

      component.startUpload();
      tick();

      expect(mockLibraryApi.uploadBook).toHaveBeenCalledTimes(2);
      expect(component.uploadQueue[0].status).toBe('success');
      expect(component.uploadQueue[1].status).toBe('success');
    }));

    it('should handle upload errors', fakeAsync(() => {
      const file = new File(['content'], 'book.epub', { type: 'application/epub+zip' });
      mockLibraryApi.uploadBook.and.returnValue(throwError(() => ({ error: { error: 'Duplicate file' } })));

      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);
      component.startUpload();
      tick();

      expect(component.uploadQueue[0].status).toBe('error');
      expect(component.uploadQueue[0].error).toBe('Duplicate file');
    }));
  });

  describe('computed properties', () => {
    it('hasPendingFiles should return true when pending files exist', () => {
      const file = new File(['content'], 'book.epub', { type: 'application/epub+zip' });
      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);

      expect(component.hasPendingFiles).toBeTrue();
    });

    it('hasPendingFiles should return false when no pending files', () => {
      expect(component.hasPendingFiles).toBeFalse();
    });

    it('hasSuccessFiles should return true after successful upload', fakeAsync(() => {
      const file = new File(['content'], 'book.epub', { type: 'application/epub+zip' });
      mockLibraryApi.uploadBook.and.returnValue(of({
        success: true,
        fileName: 'book.epub',
        fileSize: '1KB',
        message: 'Uploaded'
      }));

      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);
      component.startUpload();
      tick();

      expect(component.hasSuccessFiles).toBeTrue();
    }));

    it('allComplete should return true when all files processed', fakeAsync(() => {
      const file = new File(['content'], 'book.epub', { type: 'application/epub+zip' });
      mockLibraryApi.uploadBook.and.returnValue(of({
        success: true,
        fileName: 'book.epub',
        fileSize: '1KB',
        message: 'Uploaded'
      }));

      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);
      component.startUpload();
      tick();

      expect(component.allComplete).toBeTrue();
    }));
  });

  describe('formatFileSize', () => {
    it('should format bytes correctly', () => {
      expect(component.formatFileSize(0)).toBe('0 B');
      expect(component.formatFileSize(512)).toBe('512 B');
      expect(component.formatFileSize(1024)).toBe('1 KB');
      expect(component.formatFileSize(1536)).toBe('1.5 KB');
      expect(component.formatFileSize(1024 * 1024)).toBe('1 MB');
      expect(component.formatFileSize(1024 * 1024 * 1024)).toBe('1 GB');
    });
  });

  describe('drag and drop', () => {
    it('should set isDragOver on dragover', () => {
      const event = new DragEvent('dragover');
      spyOn(event, 'preventDefault');
      spyOn(event, 'stopPropagation');

      component.onDragOver(event);

      expect(event.preventDefault).toHaveBeenCalled();
      expect(component.isDragOver).toBeTrue();
    });

    it('should clear isDragOver on dragleave', () => {
      component.isDragOver = true;
      const event = new DragEvent('dragleave');
      spyOn(event, 'preventDefault');

      component.onDragLeave(event);

      expect(component.isDragOver).toBeFalse();
    });
  });

  describe('dialog close', () => {
    it('should close dialog with false when no uploads', () => {
      component.onClose();
      expect(mockDialogRef.close).toHaveBeenCalledWith(false);
    });

    it('should close dialog with true when successful uploads exist', fakeAsync(() => {
      const file = new File(['content'], 'book.epub', { type: 'application/epub+zip' });
      mockLibraryApi.uploadBook.and.returnValue(of({
        success: true,
        fileName: 'book.epub',
        fileSize: '1KB',
        message: 'Uploaded'
      }));

      component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);
      component.startUpload();
      tick();

      component.onClose();
      expect(mockDialogRef.close).toHaveBeenCalledWith(true);
    }));
  });
});
