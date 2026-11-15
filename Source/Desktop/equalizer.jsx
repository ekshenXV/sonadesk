import React, { useLayoutEffect, useMemo, useRef, useState } from 'react';
import { localizeMessage } from './locales.cjs';
import Picker from './picker.jsx';

const frequencies = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
const bandOptions = frequencies.map((_, id) => ({ id, name: String(id + 1) }));
const defaults = () => frequencies.map(Frequency => ({ Frequency, Gain: 0, Q: 1 }));
const ticks = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];
const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
const position = frequency => Math.log10(frequency / 20) / 3;
const pointFrequency = position => Math.round(20 * 10 ** (3 * position));
const top = 10;
const bottom = 140;
const width = 600;
const pointY = (gain, range) => top + (range - gain) / (2 * range) * (bottom - top);

function response(bands, rate) {
  // RBJ peaking filters, matching the audio worker. Evaluate the complete cascade.
  const filters = bands.filter(band => band.Gain !== 0 && band.Frequency < rate / 2).map(band => {
    const omega = 2 * Math.PI * band.Frequency / rate;
    const alpha = Math.sin(omega) / (2 * band.Q);
    const amplitude = 10 ** (band.Gain / 40);
    return { cosine: Math.cos(omega), numerator: alpha * amplitude, denominator: alpha / amplitude };
  });
  return Array.from({ length: 181 }, (_, index) => {
    const omega = 2 * Math.PI * Math.min(rate / 2, 20 * 10 ** (index / 60)) / rate;
    const sine = Math.sin(omega);
    const cosine = Math.cos(omega);
    let gain = 0;
    for (const filter of filters) {
      const real = (cosine - filter.cosine) ** 2;
      gain += 10 * Math.log10((real + (filter.numerator * sine) ** 2) / (real + (filter.denominator * sine) ** 2));
    }
    return gain;
  });
}

function NumberField({ label, unit, value, min, max, step, busy, editingContext, onChange }) {
  const input = useRef(null);
  useLayoutEffect(() => { input.current.value = value; }, [value, editingContext]);

  function commit(event) {
    const field = event.currentTarget;
    const next = field.valueAsNumber;
    const bounded = busy || !Number.isFinite(next) ? value : clamp(next, min, max);
    field.value = bounded;
    if (bounded !== value) onChange(bounded);
  }
  return <div className="eq-field">
    <span>{label}</span>
    <label className="eq-input">
      <input ref={input} type="number" aria-label={label} defaultValue={value} min={min} max={max} step={step} readOnly={busy}
        onBlur={commit} onKeyDown={event => {
          if (event.key === 'Escape') event.currentTarget.value = value;
          if (event.key === 'Enter' || event.key === 'Escape') event.currentTarget.blur();
        }}/>
      {unit && <span aria-hidden="true">{unit}</span>}
    </label>
  </div>;
}

export default function Equalizer({ playback, selecting, offline, run, t, language, headerAction }) {
  const [selectedBand, setSelectedBand] = useState(4);
  const [draft, setDraft] = useState(null);
  const [preview, setPreview] = useState(null);
  const [switching, setSwitching] = useState(false);
  const setupPending = useRef(false);
  const saving = useRef(null);
  const drag = useRef(null);
  const plot = useRef(null);
  const device = playback?.selected;
  const enabled = !!device && !!playback.enabled && !offline;
  const flat = useMemo(defaults, []);
  const bands = (preview?.device === device && preview?.bands) || (draft?.device === device && draft?.bands) || playback?.bands || flat;
  const band = bands[selectedBand];
  const unavailable = !device || offline;
  const busy = selecting || switching || !!playback?.configuring;
  const canEdit = enabled && !busy;
  const flatResponse = bands.every(({ Frequency, Gain, Q }, index) => Frequency === frequencies[index] && Gain === 0 && Q === 1);
  const editingContext = `${device}:${selectedBand}`;
  const values = useMemo(() => response(bands, playback?.rate || 48000), [bands, playback?.rate]);
  const range = drag.current?.range ?? Math.max(12, Math.ceil(Math.max(...values.map(Math.abs)) / 6) * 6);
  const line = values.map((gain, index) => `${index ? 'L' : 'M'}${index * width / (values.length - 1)} ${pointY(clamp(gain, -range, range), range)}`).join(' ');
  const zero = pointY(0, range);
  const error = localizeMessage(playback?.error, language);

  useLayoutEffect(() => {
    drag.current = null;
    setPreview(null);
  }, [device, canEdit]);

  async function configure(action) {
    if (busy || unavailable || setupPending.current) return;
    setupPending.current = true;
    setSwitching(true);
    try { await run(action); }
    finally { setupPending.current = false; setSwitching(false); }
  }

  async function save(next) {
    if (!canEdit) return;
    const edit = { device, bands: next };
    saving.current = edit;
    setDraft(edit);
    try { await run(() => window.mic.setEqualizer(device, next)); }
    finally {
      if (saving.current === edit) {
        saving.current = null;
        setDraft(null);
      }
    }
  }

  function change(key, value) {
    if (!canEdit || drag.current) return;
    const current = saving.current?.device === device ? saving.current.bands : bands;
    save(current.map((previous, index) => index === selectedBand ? { ...previous, [key]: value } : previous));
  }

  function move(event) {
    if (!canEdit || drag.current?.pointer !== event.pointerId) return;
    const { left, top: y, width: w, height: h } = plot.current.getBoundingClientRect();
    const Frequency = pointFrequency(clamp((event.clientX - left) / w, 0, 1));
    const gain = drag.current.range * (1 - 2 * (event.clientY - y) / h);
    const Gain = clamp(Math.round(gain * 10) / 10, -12, 12);
    const { source, index } = drag.current;
    const distance = Math.hypot(event.clientX - drag.current.x, event.clientY - drag.current.y);
    if (!drag.current.next && distance < 3) return;
    drag.current.next = source.map((previous, i) => i === index ? { ...previous, Frequency, Gain } : previous);
    setPreview({ device, bands: drag.current.next });
  }

  function finish(event, cancel = false) {
    if (drag.current?.pointer !== event.pointerId) return;
    const { next } = drag.current;
    drag.current = null;
    if (next && !cancel) save(next);
    setPreview(null);
  }

  return <section className="equalizer" aria-labelledby="equalizer-title" data-enabled={enabled}>
    <div className="eq-header">
      <div className="eq-title">
        <h2 id="equalizer-title">{t.equalizer}</h2>
        <button type="button" className="switch" role="switch" aria-label={t.eqSwitch} title={t.eqSwitch}
          aria-checked={enabled} aria-controls="eq-details" disabled={unavailable} aria-disabled={busy || undefined}
          onClick={() => configure(() => window.mic.toggleEqualizer(device, !enabled))}><span/></button>
      </div>
      {headerAction}
    </div>

    <div id="eq-details" className="eq-details" inert={!enabled}>
      <div className="eq-content">
        <div className="eq-chart" role="group" aria-label={t.eqGraph}>
      <div className="eq-y-axis" aria-hidden="true">
        {[range, range / 2, 0, -range / 2, -range].map(gain => <span key={gain}>{gain > 0 ? '+' : ''}{gain}</span>)}
      </div>
      <div className="eq-plot" ref={plot}>
        <svg viewBox={`0 ${top} ${width} ${bottom - top}`} preserveAspectRatio="none" aria-hidden="true">
          {ticks.map(tick => <line key={tick} x1={position(tick) * width} x2={position(tick) * width} y1={top} y2={bottom}/>)}
          {[range, range / 2, 0, -range / 2, -range].map(gain => <line key={gain} className={gain === 0 ? 'eq-zero' : undefined}
            x1="0" x2={width} y1={pointY(gain, range)} y2={pointY(gain, range)}/>)}
          <path className="eq-fill" d={`${line} L${width} ${zero} H0 Z`}/>
          <path className="eq-curve" d={line}/>
        </svg>
        {bands.map(({ Frequency, Gain }, index) => <button key={index} type="button" className="eq-point"
          aria-label={`${t.band} ${index + 1}, ${Frequency} ${t.hz}, ${Gain} ${t.db}`} aria-pressed={selectedBand === index}
          title={`${Frequency} ${t.hz} · ${Gain} ${t.db}`} aria-disabled={!canEdit || undefined}
          style={{ left: `${position(Frequency) * 100}%`, top: `${(range - Gain) / (2 * range) * 100}%` }}
          onClick={() => { if (canEdit) setSelectedBand(index); }} onPointerDown={event => {
            if (!canEdit || event.button !== 0 || drag.current) return;
            event.currentTarget.setPointerCapture(event.pointerId);
            setSelectedBand(index);
            drag.current = { pointer: event.pointerId, x: event.clientX, y: event.clientY, range, index, source: bands };
          }} onPointerMove={move} onPointerUp={finish} onPointerCancel={event => finish(event, true)}
          onLostPointerCapture={event => finish(event, true)}/>)}
      </div>
      <div className="eq-x-axis" aria-hidden="true">
        {ticks.map(tick => <span key={tick} style={{ left: `${position(tick) * 100}%` }}>{tick < 1000 ? tick : `${tick / 1000}k`}</span>)}
      </div>
      <span className="eq-unit" aria-hidden="true">{t.db}</span>
    </div>

    <div className="eq-fields">
      <div className="eq-field eq-band"><span>{t.band}</span>
        <Picker inputs={bandOptions} selected={selectedBand} busy={!canEdit} onChange={setSelectedBand} label={t.band} listLabel={t.bands}/>
      </div>
      <NumberField label={t.frequency} unit={t.hz} value={band.Frequency} min={20} max={20000} step={1}
        busy={!canEdit} editingContext={editingContext} onChange={value => change('Frequency', value)}/>
      <NumberField label={t.gain} unit={t.db} value={band.Gain} min={-12} max={12} step={0.1}
        busy={!canEdit} editingContext={editingContext} onChange={value => change('Gain', value)}/>
      <NumberField label="Q" value={band.Q} min={0.1} max={10} step={0.05}
        busy={!canEdit} editingContext={editingContext} onChange={value => change('Q', value)}/>
      <button type="button" className="quiet eq-reset" disabled={flatResponse}
        aria-disabled={busy || undefined} onClick={() => save(defaults())}>{t.reset}</button>
    </div>
    {playback?.configuring ? <p className="eq-status" role="status">{t.outputConfiguring}</p> :
      playback?.enabled && playback.enhancementsDisabled ? <div className="eq-status eq-warning">
        <span>{t.outputEnhancements}</span>
        <button type="button" className="quiet" aria-disabled={busy || undefined}
          onClick={() => configure(() => window.mic.enablePlaybackEnhancements(device))}>{t.enable}</button>
      </div> : error && <div className="eq-status eq-error" role="alert">
        <span>{error}</span>
        <button type="button" className="quiet" aria-disabled={busy || undefined}
          onClick={() => configure(() => window.mic.toggleEqualizer(device, true))}>{t.outputRetry}</button>
      </div>}
      </div>
    </div>
  </section>;
}
