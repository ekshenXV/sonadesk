import React, { memo, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { localizeMessage } from './locales.cjs';
import DialogHeader from './dialog-header.jsx';

function WaveIcon() {
  return <svg viewBox="0 0 20 20" width="16" height="16" fill="none" stroke="currentColor"
    strokeWidth="1.45" strokeLinecap="round" aria-hidden="true">
    <path d="M2.5 9v2m3.75-4.25v6.5M10 4v12m3.75-9.25v6.5M17.5 9v2"/>
  </svg>;
}

const emptyWave = [0, 0];
const easeOut = time => 1 - (1 - time) ** 2;
const toPercent = value => `${Math.max(0, Math.min(1, value)) * 100}%`;

function wavePath(values) {
  const step = 480 / values.length;
  const points = values.map(value => Math.round(56 * Math.sqrt(Math.min(100, value) / 100)) / 2);
  let path = `M0 ${32 - points[0]}`;
  for (let i = 0; i < points.length; i++) {
    const next = points[i + 1] ?? points[i];
    path += `Q${(i + .5) * step} ${32 - points[i]} ${(i + 1) * step} ${32 - (points[i] + next) / 2}`;
  }
  path += `V${32 + points[points.length - 1]}`;
  for (let i = points.length - 1; i >= 0; i--) {
    const next = points[i - 1] ?? points[i];
    path += `Q${(i + .5) * step} ${32 + points[i]} ${i * step} ${32 + (points[i] + next) / 2}`;
  }
  return path + 'Z';
}

const Waveform = memo(function Waveform({ values, progress }) {
  const gradient = useId();
  const shape = useRef(null);
  const marker = useRef(null);
  const rendered = useRef({ values: emptyWave, progress: 0 });
  const live = progress !== undefined;

  useEffect(() => {
    if (!live) return;
    const target = values?.length ? values : emptyWave;
    const from = rendered.current;
    const started = performance.now();
    const duration = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 105;
    let frame;
    function draw(now) {
      const t = duration ? Math.max(0, Math.min(1, (now - started) / duration)) : 1;
      const mix = easeOut(t);
      const points = target.map((value, index) => {
        const initial = from.values[index] ?? 0;
        return initial + (value - initial) * mix;
      });
      const cursor = from.progress + (progress / 100 - from.progress) * t;
      shape.current.setAttribute('d', wavePath(points));
      marker.current.style.transform = `translateX(${toPercent(cursor)})`;
      rendered.current = { values: points, progress: cursor };
      if (t < 1) frame = requestAnimationFrame(draw);
    }
    if (duration) frame = requestAnimationFrame(draw);
    else draw(started);
    return () => cancelAnimationFrame(frame);
  }, [values, progress, live]);

  return <span className="isolation-wave-window" aria-hidden="true">
    <svg className="isolation-wave" viewBox="0 0 480 64" preserveAspectRatio="none">
      <defs><linearGradient id={gradient} x1="0" x2="0" y1="0" y2="1" gradientUnits="objectBoundingBox">
        <stop stopColor="var(--level)" stopOpacity=".95"/>
        <stop offset=".5" stopColor="var(--level)" stopOpacity=".55"/>
        <stop offset="1" stopColor="var(--level)" stopOpacity=".95"/>
      </linearGradient></defs>
      <path className="isolation-wave-grid" d="M0 16H480M0 32H480M0 48H480M96 0V64M192 0V64M288 0V64M384 0V64"/>
      <path ref={shape} className="isolation-wave-signal" fill={`url(#${gradient})`}
        d={live ? wavePath(emptyWave) : wavePath(values?.length ? values : emptyWave)}/>
    </svg>
    {live && <span className="isolation-cursor" ref={marker}/>}
  </span>;
});

function AudioComparison({ recording, opened, load, onError, t }) {
  const [preview, setPreview] = useState(null);
  const clip = useRef(null);
  const beforeCursor = useRef(null);
  const afterCursor = useRef(null);

  function releasePreview() {
    const current = clip.current;
    clip.current = null;
    if (!current) return;
    cancelAnimationFrame(current.frame);
    if (current.cursor.current) current.cursor.current.style.transform = 'translateX(0)';
    if (current?.player) {
      current.player.pause();
      URL.revokeObjectURL(current.url);
    }
  }

  function stopPreview() {
    releasePreview();
    setPreview(null);
  }


  useEffect(() => {
    if (!opened) stopPreview();
    return releasePreview;
  }, [opened]);

  async function playPreview(after) {
    const same = clip.current?.after === after;
    stopPreview();
    if (same) return;
    const request = { after, cursor: after ? afterCursor : beforeCursor };
    clip.current = request;
    onError('');
    setPreview(after ? 'after' : 'before');
    try {
      const result = await load(after);
      if (clip.current !== request) return;
      if (result.error || !result.data) throw Error(result.error || 'recordingPreviewUnavailable');
      const bytes = Uint8Array.fromBase64(result.data);
      request.url = URL.createObjectURL(new Blob([bytes], { type: 'audio/wav' }));
      request.player = new Audio(request.url);
      request.player.onended = () => { if (clip.current === request) stopPreview(); };
      request.player.onerror = () => {
        if (clip.current !== request) return;
        stopPreview();
        onError('recordingPreviewUnavailable');
      };
      await request.player.play();
      if (clip.current !== request) return;
      function draw() {
        if (clip.current !== request) return;
        const cursor = request.cursor.current;
        const player = request.player;
        if (cursor) cursor.style.transform = `translateX(${toPercent(player.currentTime / player.duration || 0)})`;
        request.frame = requestAnimationFrame(draw);
      }
      request.frame = requestAnimationFrame(draw);
    } catch (cause) {
      if (clip.current !== request) return;
      stopPreview();
      onError(cause.message);
    }
  }


  return <div className="isolation-comparison">
    {[['before', t.recordingBefore], ['after', t.recordingAfter]].map(([key, label]) => <button type="button" key={key}
      className="isolation-track" aria-pressed={preview === key} onClick={() => playPreview(key === 'after')}>
      <span className="isolation-track-icon" aria-hidden="true">
        <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
          {preview === key ? <>
            <rect x="4" y="3" width="2.5" height="10" rx=".65"/>
            <rect x="9.5" y="3" width="2.5" height="10" rx=".65"/>
          </> : <path d="M5 3.6a.6.6 0 0 1 .92-.51l7 4.4a.6.6 0 0 1 0 1.02l-7 4.4a.6.6 0 0 1-.92-.51Z"/>}
        </svg>
      </span>
      <span className="isolation-track-label">{label}</span>
      <span className="isolation-track-wave"><Waveform values={recording[key]}/>
        <span className="isolation-cursor" ref={key === 'after' ? afterCursor : beforeCursor} aria-hidden="true"/>
      </span>
    </button>)}
  </div>;
}

export default function IsolationWizard({ panel, opened, onClose, audio, t, language }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [closingView, setClosingView] = useState(null);
  const test = closingView ?? audio.isolation ?? {};
  const stage = test.stage;
  const reviewing = stage === 'review';
  const recording = stage === 'recording';
  const processing = stage === 'processing';
  const ready = audio.isolationReady && !audio.offline;

  useLayoutEffect(() => {
    if (opened) {
      setError('');
      setClosingView(null);
    }
  }, [opened]);

  async function run(action, close = false) {
    if (busy) return;
    setBusy(true);
    setError('');
    if (close) setClosingView(test);
    try {
      const result = await action();
      if (result?.error) throw new Error(result.error);
      if (close) panel.current.close();
    } catch (cause) { setClosingView(null); setError(cause.message); }
    finally { setBusy(false); }
  }

  const title = reviewing ? t.isolationCompare : processing ? t.isolationProcessing :
    recording ? t.isolationRecording : t.isolationTest;
  const description = reviewing || processing ? '' : t.isolationTestBody;
  const message = localizeMessage(error || test.error || (!ready ? 'isolationNeedsMicrophone' : ''), language);

  return <dialog ref={panel} className="settings-panel isolation-panel" aria-labelledby="isolation-title"
    closedby="any" onClose={() => { setClosingView(test); onClose(); }}>
    <div className="dialog-content">
      <DialogHeader id="isolation-title" title={t.isolationTitle} closeLabel={t.close}
        icon={<span className="isolation-mark"><WaveIcon/></span>} onClose={() => panel.current.close()}/>
      <div className="isolation-body">
        <h3 aria-live="polite">{title}</h3>
        {description && <p>{description}</p>}
        {recording && <div className="isolation-recording" role="progressbar" aria-valuenow={test.progress || 0} aria-label={title}>
          <Waveform values={test.wave} progress={test.progress || 0}/>
        </div>}
        {reviewing && <AudioComparison recording={test} opened={opened} load={window.mic.isolationPreview} onError={setError} t={t}/>}
      </div>
      {message && <p className="isolation-error" role="alert">{message}</p>}
      <footer className="dialog-actions">
        {!reviewing && <button type="button" className="quiet" onClick={() => panel.current.close()}>{t.cancel}</button>}
        {reviewing && <button type="button" className="quiet" disabled={busy || !ready}
          onClick={() => run(window.mic.recordIsolation)}>{t.isolationRetake}</button>}
        {reviewing ? <button type="button" className="primary" disabled={busy || !ready}
          onClick={() => run(window.mic.saveIsolation, true)}>{t.isolationSave}</button> :
          !recording && !processing && <button type="button" className="primary" disabled={busy || !ready}
            onClick={() => run(window.mic.recordIsolation)}>{t.isolationRecord}</button>}
      </footer>
    </div>
  </dialog>;
}
