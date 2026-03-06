import React, { memo, useEffect, useRef, useState, useCallback } from 'react';
import { X } from 'lucide-react';

/**
 * Accessible modal with entrance/exit animation, Escape key, and focus trap.
 */
const Modal = memo(({ isOpen, onClose, title, children, maxWidth = 'max-w-2xl' }) => {
  const [visible, setVisible] = useState(false);
  const [closing, setClosing] = useState(false);
  const dialogRef = useRef(null);

  // Sync visibility with isOpen prop (drive enter / exit animation)
  useEffect(() => {
    if (isOpen) {
      setVisible(true);
      setClosing(false);
    } else if (visible) {
      // External close (e.g. "Done" / "Close" button set parent isOpen=false)
      setClosing(true);
      const timer = setTimeout(() => {
        setClosing(false);
        setVisible(false);
      }, 200);
      return () => clearTimeout(timer);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen]); // deliberately omit `visible` to avoid infinite loops

  // Call onClose immediately — the isOpen→false useEffect drives exit animation
  const handleClose = useCallback(() => {
    onClose();
  }, [onClose]);

  // Escape key
  useEffect(() => {
    if (!visible) return;
    const handler = (e) => { if (e.key === 'Escape') handleClose(); };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [visible, handleClose]);

  // Focus trap & initial focus
  useEffect(() => {
    if (!visible || closing) return;
    const el = dialogRef.current;
    if (!el) return;
    const focusable = el.querySelectorAll('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
    if (focusable.length) focusable[0].focus();

    const trap = (e) => {
      if (e.key !== 'Tab' || !focusable.length) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (e.shiftKey) {
        if (document.activeElement === first) { e.preventDefault(); last.focus(); }
      } else {
        if (document.activeElement === last) { e.preventDefault(); first.focus(); }
      }
    };
    el.addEventListener('keydown', trap);
    return () => el.removeEventListener('keydown', trap);
  }, [visible, closing]);

  if (!visible) return null;

  return (
    <div
      className={`fixed inset-0 z-50 flex items-center justify-center p-4 ${
        closing ? 'animate-fade-out' : 'animate-fade-in'
      }`}
      style={{ backgroundColor: 'rgba(15,23,42,0.6)' }}
      onClick={handleClose}
      role="dialog"
      aria-modal="true"
      aria-labelledby="modal-title"
    >
      <div
        ref={dialogRef}
        className={`bg-white rounded-xl shadow-xl border border-slate-200 ${maxWidth} w-full max-h-[85vh] overflow-y-auto ${
          closing ? 'animate-slide-down' : 'animate-slide-up'
        }`}
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="sticky top-0 bg-white border-b border-slate-100 px-6 py-4 flex items-center justify-between z-10 rounded-t-xl">
          <h2 id="modal-title" className="text-lg font-semibold text-slate-900 leading-tight truncate pr-4">
            {title}
          </h2>
          <button
            type="button"
            onClick={handleClose}
            className="text-slate-400 hover:text-slate-600 hover:bg-slate-100 rounded-lg p-1.5 transition-colors focus:outline-none focus:ring-2 focus:ring-indigo-500/30"
            aria-label="Close"
          >
            <X className="w-5 h-5" />
          </button>
        </div>
        {/* Body */}
        <div className="px-6 py-5">{children}</div>
      </div>
    </div>
  );
});

Modal.displayName = 'Modal';
export default Modal;
