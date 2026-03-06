import { useState, useRef, useCallback } from 'react';

/**
 * Custom hook for batched log management.
 * Buffers rapid log messages and flushes them after 50ms of inactivity.
 * Log string is capped at ~50KB to prevent unbounded DOM growth.
 */
const MAX_LOG_LENGTH = 50000;

function capLogs(text) {
  if (text.length <= MAX_LOG_LENGTH) return text;
  // Trim from the front, keeping a newline boundary
  const trimmed = text.slice(text.length - MAX_LOG_LENGTH);
  const nlIdx = trimmed.indexOf('\n');
  return (nlIdx > 0 ? trimmed.slice(nlIdx) : trimmed);
}

export function useLogger() {
  const [logs, setLogs] = useState('');
  const logsBufferRef = useRef([]);
  const logsFlushTimerRef = useRef(null);
  const logContainerRef = useRef(null);

  const appendLog = useCallback((message) => {
    logsBufferRef.current.push(message);
    if (logsFlushTimerRef.current) clearTimeout(logsFlushTimerRef.current);
    logsFlushTimerRef.current = setTimeout(() => {
      if (logsBufferRef.current.length > 0) {
        const buffered = logsBufferRef.current.join('');
        logsBufferRef.current = [];
        setLogs((prev) => capLogs(prev + buffered));
      }
    }, 50);
  }, []);

  const flushLogs = useCallback(() => {
    if (logsFlushTimerRef.current) clearTimeout(logsFlushTimerRef.current);
    if (logsBufferRef.current.length > 0) {
      const buffered = logsBufferRef.current.join('');
      logsBufferRef.current = [];
      setLogs((prev) => capLogs(prev + buffered));
    }
  }, []);

  /** Write directly to logs (bypasses buffer). */
  const directLog = useCallback((message) => {
    setLogs((prev) => capLogs(prev + message));
  }, []);

  return { logs, setLogs, appendLog, flushLogs, directLog, logContainerRef };
}
