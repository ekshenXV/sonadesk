import React from 'react';

export default function DialogHeader({ id, title, closeLabel, onClose, icon, disabled, autoFocus }) {
  return <header className="dialog-header">
    <div className="dialog-heading">{icon}<h2 id={id}>{title}</h2></div>
    <button type="button" className="settings-close" aria-label={closeLabel} title={closeLabel}
      disabled={disabled} autoFocus={autoFocus} onClick={onClose}>
      <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor"
        strokeWidth="1.4" strokeLinecap="round" aria-hidden="true">
        <path d="m4 4 8 8m0-8-8 8"/>
      </svg>
    </button>
  </header>;
}
