import React from 'react';
import { createPortal } from 'react-dom';
import { CheckCircle2, Monitor, RotateCcw } from 'lucide-react';

/**
 * React-managed shutdown overlay (replaces raw DOM manipulation).
 * Rendered via portal to document.body.
 */
export default function ShutdownOverlay({ onContinue, onClose }) {
  return createPortal(
    <div className="fixed inset-0 z-[10000] flex items-center justify-center p-4 animate-fade-in"
         style={{ backgroundColor: 'rgba(15,23,42,0.75)', backdropFilter: 'blur(6px)' }}>
      <div className="bg-white rounded-xl shadow-2xl max-w-md w-full text-center animate-slide-up overflow-hidden">
        {/* Top accent bar */}
        <div className="h-1 bg-gradient-to-r from-indigo-500 via-violet-500 to-indigo-500" />

        <div className="p-8">
          <div className="w-14 h-14 rounded-full bg-emerald-100 flex items-center justify-center mx-auto mb-4">
            <CheckCircle2 className="w-7 h-7 text-emerald-600" />
          </div>
          <h2 className="text-xl font-semibold text-slate-900 mb-1">Backend Shutdown Complete</h2>
          <p className="text-sm text-slate-500 mb-6">All processes terminated cleanly</p>

          <div className="bg-slate-50 rounded-lg p-4 mb-6 text-left space-y-2">
            <div className="flex items-center gap-2.5 text-sm text-slate-700">
              <CheckCircle2 className="w-4 h-4 text-emerald-500 flex-shrink-0" />
              <span>Server stopped &amp; port released</span>
            </div>
            <div className="flex items-center gap-2.5 text-sm text-slate-700">
              <CheckCircle2 className="w-4 h-4 text-emerald-500 flex-shrink-0" />
              <span>Temporary files cleaned</span>
            </div>
            <div className="flex items-center gap-2.5 text-sm text-slate-700">
              <Monitor className="w-4 h-4 text-slate-400 flex-shrink-0" />
              <span>Terminal ready for commands</span>
            </div>
          </div>

          <div className="flex gap-3">
            <button
              onClick={onContinue}
              className="flex-1 inline-flex items-center justify-center gap-2 px-4 py-2.5 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 transition-colors focus:outline-none focus:ring-2 focus:ring-indigo-500/30"
            >
              <RotateCcw className="w-4 h-4" />
              Continue Working
            </button>
            <button
              onClick={() => { window.close(); onClose?.(); }}
              className="flex-1 px-4 py-2.5 text-sm font-medium text-slate-700 bg-white border border-slate-300 rounded-lg hover:bg-slate-50 transition-colors focus:outline-none focus:ring-2 focus:ring-slate-300/50"
            >
              Close Tab
            </button>
          </div>
        </div>

        <div className="bg-slate-50 border-t border-slate-100 px-8 py-3">
          <p className="text-xs text-slate-400">Thank you for using Color Correction Studio</p>
        </div>
      </div>
    </div>,
    document.body
  );
}
