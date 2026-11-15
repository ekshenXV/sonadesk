import React, { useEffect, useId, useRef, useState } from 'react';

export default function Picker({ inputs, selected, disabled, busy = false, onChange, label, listLabel, placeholder, empty }) {
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const field = useRef(null);
  const list = useRef(null);
  const id = useId();
  const anchor = '--picker-' + id.replace(/[^a-z\d-]/gi, '');
  const selectedIndex = inputs.findIndex(input => input.id === selected);
  const shown = open && !disabled && !busy && inputs.length > 0;

  useEffect(() => {
    if (disabled || busy || inputs.length === 0) list.current.hidePopover();
  }, [disabled, busy, inputs.length]);

  useEffect(() => {
    if (!shown) return;
    const menu = list.current;
    const item = menu.children[active];
    if (!item) return;
    const start = item.offsetTop - menu.clientTop;
    const end = start + item.offsetHeight;
    if (start < menu.scrollTop) menu.scrollTop = start;
    else if (end > menu.scrollTop + menu.clientHeight) menu.scrollTop = end - menu.clientHeight;
  }, [active, shown]);

  function reveal(index) {
    setActive(Math.max(0, index));
    list.current.showPopover({ source: field.current });
  }

  function select(index) {
    list.current.hidePopover();
    const input = inputs[index];
    if (input && input.id !== selected) onChange(input.id);
  }

  function onKeyDown(event) {
    if (busy) return;
    if (event.key === 'Escape' && shown) {
      event.preventDefault();
      list.current.hidePopover();
    } else if (event.key === 'Tab') {
      list.current.hidePopover();
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      if (!shown) reveal(selectedIndex < 0 && event.key === 'ArrowUp' ? inputs.length - 1 : selectedIndex);
      else setActive(index => Math.min(inputs.length - 1, Math.max(0, index + (event.key === 'ArrowDown' ? 1 : -1))));
    } else if (shown && (event.key === 'Home' || event.key === 'End')) {
      event.preventDefault();
      setActive(event.key === 'Home' ? 0 : inputs.length - 1);
    } else if (shown && (event.key === 'Enter' || event.key === ' ')) {
      event.preventDefault();
      select(active);
    }
  }

  return <div className="picker" style={{ '--picker-anchor': anchor }}>
    <button ref={field} type="button" className="picker-field" role="combobox" aria-label={label}
      aria-controls={id} aria-expanded={shown} popoverTarget={id} aria-activedescendant={shown ? id + '-' + active : undefined}
      disabled={disabled || inputs.length === 0} aria-disabled={busy || undefined} onKeyDown={onKeyDown} title={inputs[selectedIndex]?.name}
      onClick={event => {
        if (busy) event.preventDefault();
        else if (!list.current.matches(':popover-open')) setActive(Math.max(0, selectedIndex));
      }}>
      <span>{inputs[selectedIndex]?.name || (inputs.length ? placeholder : empty)}</span>
      <i className="chevron" aria-hidden="true"/>
    </button>
    <div id={id} ref={list} className="picker-popover popover-surface" popover="auto" role="listbox" aria-label={listLabel}
      onToggle={event => setOpen(event.newState === 'open')}>
      {inputs.map((input, index) => <button key={input.id} id={id + '-' + index} role="option" tabIndex={-1}
        aria-selected={selected === input.id} type="button" title={input.name}
        className={active === index ? 'highlighted' : ''} onPointerEnter={() => setActive(index)}
        onMouseDown={event => event.preventDefault()} onClick={() => select(index)}>
        <span className="option-check" aria-hidden="true">{selected === input.id ? '✓' : ''}</span>
        <span className="option-name">{input.name}</span>
      </button>)}
    </div>
  </div>;
}
