import React, { memo, useRef } from 'react';
import { ChevronDown } from 'lucide-react';

/**
 * Animated collapsible card section.
 * Uses CSS grid-template-rows for smooth height transitions.
 * Lazily renders children: content is not rendered until the section is first opened,
 * then stays rendered for smooth close animations.
 */
const CollapsibleSection = memo(({ icon: Icon, title, isOpen, onToggle, badge, children }) => {
  const hasOpenedRef = useRef(isOpen);
  if (isOpen) hasOpenedRef.current = true;

  return (
    <div className="bg-white rounded-xl border border-slate-200 shadow-sm overflow-hidden">
      <button
        type="button"
        onClick={onToggle}
        className="w-full flex items-center gap-2.5 px-4 py-3 text-left hover:bg-slate-50/80 transition-colors"
      >
        {Icon && <Icon className="w-4 h-4 text-indigo-500 flex-shrink-0" />}
        <span className="text-sm font-semibold text-slate-800 truncate flex-1">{title}</span>
        {badge != null && (
          <span className="text-[10px] font-bold bg-indigo-100 text-indigo-700 px-1.5 py-0.5 rounded-full min-w-[20px] text-center">
            {badge}
          </span>
        )}
        <ChevronDown
          className={`w-4 h-4 text-slate-400 flex-shrink-0 transition-transform duration-200 ${
            isOpen ? 'rotate-180' : ''
          }`}
        />
      </button>
      <div className="collapsible-grid" data-open={isOpen}>
        <div>
          <div className="px-4 pb-4 pt-1">{hasOpenedRef.current ? children : null}</div>
        </div>
      </div>
    </div>
  );
});

CollapsibleSection.displayName = 'CollapsibleSection';
export default CollapsibleSection;
