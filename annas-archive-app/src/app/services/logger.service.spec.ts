import { TestBed } from '@angular/core/testing';
import { LoggerService } from './logger.service';

describe('LoggerService', () => {
  let service: LoggerService;
  let consoleSpy: {
    log: jasmine.Spy;
    warn: jasmine.Spy;
    error: jasmine.Spy;
    debug: jasmine.Spy;
    group: jasmine.Spy;
    groupEnd: jasmine.Spy;
    table: jasmine.Spy;
  };

  beforeEach(() => {
    // Spy on console methods
    consoleSpy = {
      log: spyOn(console, 'log'),
      warn: spyOn(console, 'warn'),
      error: spyOn(console, 'error'),
      debug: spyOn(console, 'debug'),
      group: spyOn(console, 'group'),
      groupEnd: spyOn(console, 'groupEnd'),
      table: spyOn(console, 'table')
    };

    TestBed.configureTestingModule({
      providers: [LoggerService]
    });
    service = TestBed.inject(LoggerService);
  });

  // Note: Angular tests run in dev mode by default, so isEnabled will be true
  // This tests the service behavior in development mode

  describe('log', () => {
    it('should call console.log with message', () => {
      service.log('test message');
      expect(consoleSpy.log).toHaveBeenCalledWith('test message');
    });

    it('should call console.log with message and args', () => {
      service.log('test message', 'arg1', 'arg2');
      expect(consoleSpy.log).toHaveBeenCalledWith('test message', 'arg1', 'arg2');
    });

    it('should call console.log with object args', () => {
      const obj = { key: 'value' };
      service.log('test', obj);
      expect(consoleSpy.log).toHaveBeenCalledWith('test', obj);
    });
  });

  describe('info', () => {
    it('should call console.log with prefixed message', () => {
      service.info('PREFIX', 'test message');
      expect(consoleSpy.log).toHaveBeenCalledWith('[PREFIX] test message');
    });

    it('should call console.log with prefix, message and args', () => {
      service.info('API', 'request', { data: 'test' });
      expect(consoleSpy.log).toHaveBeenCalledWith('[API] request', { data: 'test' });
    });
  });

  describe('warn', () => {
    it('should call console.warn with message', () => {
      service.warn('warning message');
      expect(consoleSpy.warn).toHaveBeenCalledWith('warning message');
    });

    it('should call console.warn with message and args', () => {
      service.warn('warning', { detail: 'info' });
      expect(consoleSpy.warn).toHaveBeenCalledWith('warning', { detail: 'info' });
    });
  });

  describe('error', () => {
    it('should call console.error with message', () => {
      service.error('error message');
      expect(consoleSpy.error).toHaveBeenCalledWith('error message');
    });

    it('should call console.error with message and args', () => {
      const err = new Error('test error');
      service.error('error occurred', err);
      expect(consoleSpy.error).toHaveBeenCalledWith('error occurred', err);
    });
  });

  describe('debug', () => {
    it('should call console.debug with message', () => {
      service.debug('debug message');
      expect(consoleSpy.debug).toHaveBeenCalledWith('debug message');
    });

    it('should call console.debug with message and args', () => {
      service.debug('debug', 1, 2, 3);
      expect(consoleSpy.debug).toHaveBeenCalledWith('debug', 1, 2, 3);
    });
  });

  describe('group', () => {
    it('should call console.group with label', () => {
      service.group('Group Label');
      expect(consoleSpy.group).toHaveBeenCalledWith('Group Label');
    });
  });

  describe('groupEnd', () => {
    it('should call console.groupEnd', () => {
      service.groupEnd();
      expect(consoleSpy.groupEnd).toHaveBeenCalled();
    });
  });

  describe('table', () => {
    it('should call console.table with data', () => {
      const data = [{ name: 'Alice' }, { name: 'Bob' }];
      service.table(data);
      expect(consoleSpy.table).toHaveBeenCalledWith(data);
    });

    it('should call console.table with object', () => {
      const data = { key1: 'value1', key2: 'value2' };
      service.table(data);
      expect(consoleSpy.table).toHaveBeenCalledWith(data);
    });
  });

  describe('in production mode (simulated)', () => {
    let prodService: LoggerService;

    beforeEach(() => {
      // Create a new service and override the private isEnabled property
      prodService = new LoggerService();
      // Use Object.defineProperty to simulate production mode
      Object.defineProperty(prodService, 'isEnabled', {
        value: false,
        writable: false
      });
    });

    it('should NOT call console.log', () => {
      prodService.log('test message');
      expect(consoleSpy.log).not.toHaveBeenCalled();
    });

    it('should NOT call console.warn', () => {
      prodService.warn('warning message');
      expect(consoleSpy.warn).not.toHaveBeenCalled();
    });

    it('should STILL call console.error (always enabled)', () => {
      prodService.error('error message');
      expect(consoleSpy.error).toHaveBeenCalledWith('error message');
    });

    it('should call console.error with args in production', () => {
      const err = new Error('prod error');
      prodService.error('error', err);
      expect(consoleSpy.error).toHaveBeenCalledWith('error', err);
    });

    it('should NOT call console.debug', () => {
      prodService.debug('debug message');
      expect(consoleSpy.debug).not.toHaveBeenCalled();
    });

    it('should NOT call console.group', () => {
      prodService.group('Group Label');
      expect(consoleSpy.group).not.toHaveBeenCalled();
    });

    it('should NOT call console.groupEnd', () => {
      prodService.groupEnd();
      expect(consoleSpy.groupEnd).not.toHaveBeenCalled();
    });

    it('should NOT call console.table', () => {
      prodService.table([{ data: 'test' }]);
      expect(consoleSpy.table).not.toHaveBeenCalled();
    });

    it('should NOT call info (uses console.log)', () => {
      prodService.info('PREFIX', 'test message');
      expect(consoleSpy.log).not.toHaveBeenCalled();
    });
  });

  describe('edge cases', () => {
    it('should handle empty string message', () => {
      service.log('');
      expect(consoleSpy.log).toHaveBeenCalledWith('');
    });

    it('should handle null args', () => {
      service.log('message', null);
      expect(consoleSpy.log).toHaveBeenCalledWith('message', null);
    });

    it('should handle undefined args', () => {
      service.log('message', undefined);
      expect(consoleSpy.log).toHaveBeenCalledWith('message', undefined);
    });

    it('should handle multiple different types of args', () => {
      service.log('message', 42, 'string', { obj: true }, [1, 2, 3]);
      expect(consoleSpy.log).toHaveBeenCalledWith('message', 42, 'string', { obj: true }, [1, 2, 3]);
    });

    it('should handle error objects as args', () => {
      const error = new Error('Test error');
      service.log('caught error', error);
      expect(consoleSpy.log).toHaveBeenCalledWith('caught error', error);
    });
  });
});
