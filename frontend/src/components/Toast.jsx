import React, { createContext, useContext, useState, useCallback, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { CheckCircle2, AlertCircle, Info, X } from 'lucide-react';

const ToastContext = createContext(null);

const ICONS = {
  success: CheckCircle2,
  error:   AlertCircle,
  info:    Info,
};

const COLORS = {
  success: 'bg-emerald-50 border-emerald-300 text-emerald-800',
  error:   'bg-red-50 border-red-300 text-red-800',
  info:    'bg-indigo-50 border-indigo-300 text-indigo-800',
};

const ICON_COLORS = {
  success: 'text-emerald-500',
  error:   'text-red-500',
  info:    'text-indigo-500',
};

let toastId = 0;

function Toast({ id, type, message, onDismiss }) {
  const [exiting, setExiting] = useState(false);
  const Icon = ICONS[type] || Info;

  useEffect(() => {
    const timer = setTimeout(() => setExiting(true), 3700);
    return () => clearTimeout(timer);
  }, []);

  useEffect(() => {
    if (exiting) {
      const t = setTimeout(() => onDismiss(id), 250);
      return () => clearTimeout(t);
    }
  }, [exiting, id, onDismiss]);

  return (
    <div
      className={`flex items-start gap-3 px-4 py-3 rounded-xl border shadow-lg backdrop-blur-sm max-w-sm w-full ${COLORS[type]} ${
        exiting ? 'animate-toast-out' : 'animate-toast-in'
      }`}
      role="alert"
    >
      <Icon className={`w-5 h-5 flex-shrink-0 mt-0.5 ${ICON_COLORS[type]}`} />
      <p className="text-sm font-medium flex-1 leading-snug">{message}</p>
      <button
        type="button"
        onClick={() => setExiting(true)}
        className="text-current opacity-40 hover:opacity-70 transition-opacity flex-shrink-0"
        aria-label="Dismiss"
      >
        <X className="w-4 h-4" />
      </button>
    </div>
  );
}

export function ToastProvider({ children }) {
  const [toasts, setToasts] = useState([]);

  const dismiss = useCallback((id) => {
    setToasts(prev => prev.filter(t => t.id !== id));
  }, []);

  const push = useCallback((type, message) => {
    const id = ++toastId;
    setToasts(prev => [...prev.slice(-4), { id, type, message }]); // max 5
  }, []);

  const toast = React.useMemo(() => ({
    success: (msg) => push('success', msg),
    error:   (msg) => push('error', msg),
    info:    (msg) => push('info', msg),
  }), [push]);

  return (
    <ToastContext.Provider value={toast}>
      {children}
      {createPortal(
        <div className="fixed bottom-4 right-4 z-[9999] flex flex-col gap-2 pointer-events-none">
          {toasts.map(t => (
            <div key={t.id} className="pointer-events-auto">
              <Toast id={t.id} type={t.type} message={t.message} onDismiss={dismiss} />
            </div>
          ))}
        </div>,
        document.body
      )}
    </ToastContext.Provider>
  );
}

export function useToast() {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error('useToast must be used within <ToastProvider>');
  return ctx;
}
