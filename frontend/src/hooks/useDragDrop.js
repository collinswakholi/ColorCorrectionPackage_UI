import { useState, useRef, useCallback } from 'react';

/**
 * Custom hook for drag-and-drop image file handling.
 * @param {Function} onDropFiles - async callback receiving Array<File>
 * @param {Function} appendLog   - logger callback
 */
export function useDragDrop(onDropFiles, appendLog) {
  const [isDragging, setIsDragging] = useState(false);
  const dragCounter = useRef(0);

  const handleDragEnter = useCallback((e) => {
    e.preventDefault();
    e.stopPropagation();
    dragCounter.current++;
    if (e.dataTransfer.items && e.dataTransfer.items.length > 0) {
      setIsDragging(true);
    }
  }, []);

  const handleDragLeave = useCallback((e) => {
    e.preventDefault();
    e.stopPropagation();
    dragCounter.current--;
    if (dragCounter.current === 0) setIsDragging(false);
  }, []);

  const handleDragOver = useCallback((e) => {
    e.preventDefault();
    e.stopPropagation();
  }, []);

  const handleDrop = useCallback(async (e) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragging(false);
    dragCounter.current = 0;

    if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
      const imageFiles = Array.from(e.dataTransfer.files).filter(f => f.type.startsWith('image/'));
      if (imageFiles.length === 0) {
        appendLog('\n⚠ No image files found in dropped items');
        return;
      }
      await onDropFiles(imageFiles);
    }
  }, [onDropFiles, appendLog]);

  return { isDragging, handleDragEnter, handleDragLeave, handleDragOver, handleDrop };
}
