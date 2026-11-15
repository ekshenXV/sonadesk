import React, { useEffect, useId, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { strings, localizeMessage } from './locales.cjs';
import Picker from './picker.jsx';
import Equalizer from './equalizer.jsx';
import IsolationWizard from './isolation-wizard.jsx';
import UpdateDialog from './update-dialog.jsx';
import DialogHeader from './dialog-header.jsx';

const initial = { inputs: [], outputs: [], effects: [], level: 0, playbackLevel: 0, rate: 0, kind: 'idle' };

function Mark() {
  return <span className="mark" aria-hidden="true"><i/><i/><i/><i/><i/></span>;
}

function VolumeSlider({ value, label, accessibleLabel, unavailable, apply }) {
  const [draft, setDraft] = useState(null);
  const queued = useRef(null);
  const sending = useRef(false);
  const disabled = unavailable || value == null;
  const level = disabled ? value : draft ?? value;

  useEffect(() => () => { queued.current = null; }, [disabled]);

  async function change(event) {
    if (disabled) return;
    queued.current = event.currentTarget.valueAsNumber;
    setDraft(queued.current);
    if (sending.current) return;
    sending.current = true;
    try {
      while (queued.current != null) {
        const next = queued.current;
        queued.current = null;
        await apply(next);
      }
    } finally {
      sending.current = false;
      setDraft(null);
    }
  }

  return <label className="device-volume">
    <span>{label}</span>
    <input type="range" min="0" max="100" step="1" value={level ?? 0} disabled={disabled}
      aria-label={accessibleLabel} aria-valuetext={level == null ? undefined : level + '%'}
      style={{ '--volume-value': `${level ?? 0}%` }} onChange={change}/>
    <span className="device-volume-value" aria-hidden="true">{level == null ? '—' : level + '%'}</span>
  </label>;
}

function Effects({ effects, offline, run, t, language }) {
  const [working, setWorking] = useState(false);
  const [addMenu, setAddMenu] = useState(false);
  const menu = useRef(null);
  const menuId = useId();
  const add = async bundle => {
    menu.current.hidePopover();
    setWorking(true);
    try { await run(() => window.mic.add(bundle)); } finally { setWorking(false); }
  };

  return <section className="effects" aria-labelledby="effects-title">
    <div className="section-header">
      <div className="effects-title">
        <h2 id="effects-title">{t.effects}</h2><span className="format">VST2 / VST3</span>
      </div>
      <div className="add-control" onBlur={event => {
        if (!event.currentTarget.contains(event.relatedTarget)) menu.current.hidePopover();
      }}>
        <button className="primary" type="button" disabled={working || offline} aria-expanded={addMenu}
          aria-controls={menuId} popoverTarget={menuId}>
          <span aria-hidden="true" className="plus"/>{t.addVst}
        </button>
        <div id={menuId} ref={menu} className="add-menu popover-surface" popover="auto" role="group" aria-label={t.addVst}
          onToggle={event => setAddMenu(event.newState === 'open')}>
          <button type="button" onClick={() => add(false)}>{t.pickFile}</button>
          <button type="button" onClick={() => add(true)}>{t.pickFolder}</button>
        </div>
      </div>
    </div>
    <div className="effect-list">
      {effects.length === 0 && <div className="empty">
        <strong>{t.noEffects}</strong>
        <span>{t.addFirstEffect}</span>
      </div>}
      {effects.map(effect => {
        const message = localizeMessage(effect.error, language);
        return <div className="effect" key={effect.id}>
          <button className="switch" type="button" role="switch" aria-checked={effect.enabled}
            aria-label={t.effectSwitch(effect.name)} disabled={offline || !!effect.error}
            onClick={() => run(() => window.mic.toggle(effect.id, !effect.enabled))}><span/></button>
          <div className="effect-copy" title={message || effect.name}>
            <strong>{effect.name}</strong>
            {message && <small>{message}</small>}
          </div>
          {effect.editor && <button type="button" className="quiet settings" disabled={offline}
            onClick={() => run(() => window.mic.edit(effect.id))}>{t.configure}</button>}
          <button type="button" className="quiet remove" aria-label={t.removeNamed(effect.name)} title={t.remove}
            disabled={offline} onClick={() => run(() => window.mic.remove(effect.id))}>×</button>
        </div>;
      })}
    </div>
  </section>;
}

function SettingsPanel({ panel, opened, onClose, t, language }) {
  const [startup, setStartup] = useState(false);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    if (!opened) return;
    let current = true;
    setBusy(true);
    setError('');
    window.mic.getStartup().then(result => {
      if (!current) return;
      if (result.error) throw new Error(result.error);
      setStartup(result.enabled);
    }).catch(cause => { if (current) setError(cause.message); })
      .finally(() => { if (current) setBusy(false); });
    return () => { current = false; };
  }, [opened]);

  async function change(enabled) {
    if (busy) return;
    const previous = startup;
    setStartup(enabled);
    setBusy(true);
    setError('');
    try {
      const result = await window.mic.setStartup(enabled);
      setStartup(typeof result.enabled === 'boolean' ? result.enabled : previous);
      if (result.error) setError(result.error);
    } catch (cause) {
      setStartup(previous);
      setError(cause.message);
    }
    finally { setBusy(false); }
  }

  return <dialog ref={panel} className="settings-panel" aria-labelledby="settings-title" closedby="any" onClose={onClose}>
    <div className="dialog-content">
      <DialogHeader id="settings-title" title={t.settings} closeLabel={t.closeSettings} autoFocus onClose={() => panel.current.close()}/>
      <label className="check-option">
        <input type="checkbox" checked={startup} aria-disabled={busy} onChange={event => change(event.target.checked)}/>
        <span>{t.startup}</span>
      </label>
      {error && <p className="settings-error" role="alert">{localizeMessage(error, language)}</p>}
    </div>
  </dialog>;
}

const sun = <svg viewBox="0 0 16 16" aria-hidden="true">
  <circle cx="8" cy="8" r="2.5"/><path d="M8 1.5v1.25m0 10.5v1.25m6.5-6.5h-1.25m-10.5 0H1.5m11.1-4.6-.88.88m-7.44 7.44-.88.88m9.2 0-.88-.88M4.28 4.28l-.88-.88"/>
</svg>;
const moon = <svg viewBox="0 0 16 16" aria-hidden="true">
  <path d="M13.85 10a5.8 5.8 0 0 1-7.86-7.85A6 6 0 1 0 13.85 10Z"/>
</svg>;

function Segments({ label, value, options, onChange }) {
  return <div className="segments" role="group" aria-label={label} data-second={value === options[1].value}>
    {options.map(option => <button type="button" key={option.value} aria-label={option.label} title={option.label} lang={option.lang}
      aria-pressed={value === option.value} onClick={() => onChange(option.value)}>{option.content}</button>)}
  </div>;
}

function App() {
  const [appearance, setAppearance] = useState(() => ({
    theme: document.documentElement.dataset.theme || 'light', language: document.documentElement.lang
  }));
  const [audio, setAudio] = useState(initial);
  const [starting, setStarting] = useState(true);
  const [microphoneChange, setMicrophoneChange] = useState(null);
  const [selectingPlayback, setSelectingPlayback] = useState(false);
  const [playbackCommandError, setPlaybackCommandError] = useState('');
  const [monitorPending, setMonitorPending] = useState(null);
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [isolationOpened, setIsolationOpened] = useState(false);
  const [isolationChanging, setIsolationChanging] = useState(false);
  const [error, setError] = useState('');
  const panel = useRef(null);
  const isolationPanel = useRef(null);
  const isolationSwitch = useRef(null);
  const resetPanel = useRef(null);
  const resetMicrophone = useRef(null);
  const { theme, language } = appearance;
  const t = strings[language];
  const offline = starting || audio.offline;
  function sync(update) {
    setAudio(state => {
      const next = { ...state, ...update };
      if (!next.offline && next.playback?.configuring) next.monitoring = state.monitoring;
      return next;
    });
  }

  useEffect(() => {
    const unsubscribe = window.mic.listen(sync);
    const unsubscribeHidden = window.mic.onWindowHidden(() => {
      panel.current.close();
      isolationPanel.current.close();
      resetPanel.current.close('cancel');
      for (const popover of document.querySelectorAll('.popover-surface:popover-open')) popover.hidePopover();
    });
    window.mic.state().then(sync).catch(cause => setError(cause.message)).finally(() => setStarting(false));
    return () => { unsubscribe(); unsubscribeHidden(); };
  }, []);


  async function run(action, showError = setError) {
    try {
      const result = await action();
      showError(result?.error || '');
    } catch (cause) { showError(cause.message); }
  }

  async function select(id) {
    if (microphoneChange) return;
    setMicrophoneChange({ monitoring: !!audio.monitoring });
    try { await run(() => window.mic.select(id)); } finally { setMicrophoneChange(null); }
  }

  const runPlayback = action => run(action, setPlaybackCommandError);

  async function selectPlayback(id) {
    if (selectingPlayback) return;
    setSelectingPlayback(true);
    try { await runPlayback(() => window.mic.selectPlayback(id)); } finally { setSelectingPlayback(false); }
  }

  async function listen(enabled) {
    if (monitorPending !== null || microphoneChange || audio.playback?.configuring) return;
    setMonitorPending(enabled);
    try { await run(() => window.mic.monitor(enabled)); }
    finally { setMonitorPending(null); }
  }

  async function enableEnhancements() {
    if (microphoneChange) return;
    setMicrophoneChange({ monitoring: !!audio.monitoring });
    try { await run(() => window.mic.enableEnhancements(audio.selected)); }
    finally { setMicrophoneChange(null); }
  }

  function openIsolationSetup() {
    isolationPanel.current.showModal();
    setIsolationOpened(true);
  }

  function closeIsolationSetup() {
    setIsolationOpened(false);
    window.mic.cancelIsolation().catch(() => {});
  }

  async function updateIsolation(action) {
    if (isolationChanging) return;
    setIsolationChanging(true);
    try { await run(action); }
    finally { setIsolationChanging(false); }
  }

  function changeIsolation() {
    if (isolationChanging) return;
    if (!audio.isolation?.configured) { openIsolationSetup(); return; }
    updateIsolation(() => window.mic.toggleIsolation(!audio.isolation.requested));
  }

  function openIsolationReset() {
    if (isolationChanging || !audio.selected) return;
    resetMicrophone.current = audio.selected;
    resetPanel.current.returnValue = '';
    resetPanel.current.showModal();
  }

  function closeIsolationReset(event) {
    const microphone = resetMicrophone.current;
    resetMicrophone.current = null;
    if (event.currentTarget.returnValue !== 'reset' || !microphone) return;
    isolationSwitch.current.focus();
    updateIsolation(() => window.mic.resetIsolation(microphone));
  }

  async function changeAppearance(key, value) {
    await run(async () => {
      const next = await window.mic.setAppearance(key, value);
      if (!next.error) {
        document.documentElement.dataset.theme = next.theme;
        document.documentElement.lang = next.language;
        setAppearance(next);
      }
      return next;
    });
  }

  const message = localizeMessage(error || audio.monitorError || audio.inputRoutingError || (audio.kind === 'error' ? audio.status : ''), language);

  return <>
    <header className="titlebar">
      <div className="brand"><Mark/><span>SonaDesk</span></div>
      <div className="appearance-controls">
        <Segments label={t.theme} value={theme} onChange={value => changeAppearance('theme', value)} options={[
          { value: 'light', label: t.light, content: sun }, { value: 'dark', label: t.dark, content: moon }
        ]}/>
        <Segments label={t.language} value={language} onChange={value => changeAppearance('language', value)} options={[
          { value: 'ru', label: 'Русский', lang: 'ru', content: 'RU' }, { value: 'en', label: 'English', lang: 'en', content: 'EN' }
        ]}/>
      </div>
      <div className="caption-actions">
        <button type="button" aria-label={t.settings} title={t.settings} aria-haspopup="dialog" onClick={() => {
          panel.current.showModal();
          setSettingsOpened(true);
        }}>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6"
            strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="m10 2-.6 2.8-2 .9-2.4-1.5-2.8 2.8 1.5 2.4-.9 2L0 12v3.8l2.8.6.9 2-1.5 2.4L5 23.6l2.4-1.5 2 .9.6 2.8h3.8l.6-2.8 2-.9 2.4 1.5 2.8-2.8-1.5-2.4.9-2 2.8-.6V12l-2.8-.6-.9-2 1.5-2.4-2.8-2.8-2.4 1.5-2-.9-.6-2.8Z" transform="translate(2.4 .9) scale(.8)"/>
            <circle cx="12" cy="12" r="2.8"/>
          </svg>
        </button>
        <button type="button" aria-label={t.minimize} title={t.minimize} onClick={() => window.mic.minimize()}><i className="minimize"/></button>
        <button className="close" type="button" aria-label={t.close} title={t.close} onClick={() => window.mic.close()}><i className="cross"/></button>
      </div>
    </header>
    <main>
      <div className="devices">
        <section className="speakers" aria-labelledby="speakers-title">
          <h2 id="speakers-title">{t.speakers}</h2>
          <Picker inputs={audio.outputs} selected={audio.playback?.selected} label={t.output} listLabel={t.speakers}
            placeholder={t.chooseOutput} empty={t.noOutputs} disabled={offline}
            busy={selectingPlayback || audio.playback?.configuring} onChange={selectPlayback}/>
          <section className="monitor" aria-labelledby="playback-meter-title">
            <div className="monitor-head"><h2 id="playback-meter-title">{t.signal}</h2></div>
            <meter aria-label={t.playbackLevel} min="0" max="100" value={audio.playbackLevel}/>
            <VolumeSlider key={audio.playback?.selected} value={audio.playbackVolume} label={t.volume} accessibleLabel={t.speakerVolume}
              unavailable={offline || selectingPlayback || audio.playback?.configuring || !audio.playback?.selected}
              apply={value => runPlayback(() => window.mic.setPlaybackVolume(audio.playback.selected, value))}/>
          </section>
          {playbackCommandError && <div className="error-notice" role="alert">{localizeMessage(playbackCommandError, language)}</div>}
        </section>
        <section className="microphone" aria-labelledby="microphone-title">
          <h1 id="microphone-title">{t.microphone}</h1>
          <Picker inputs={audio.inputs} selected={audio.selected} label={t.input} listLabel={t.microphones} placeholder={t.chooseInput} empty={t.noInputs}
            disabled={offline} busy={!!microphoneChange} onChange={select}/>
          {audio.enhancementsDisabled && <div className="enhancements-notice">
            <p>{t.enhancements}</p>
            <button type="button" className="quiet" disabled={offline} aria-disabled={!!microphoneChange}
              onClick={enableEnhancements}>{t.enable}</button>
          </div>}
          <section className="monitor" aria-labelledby="meter-title">
            <div className="monitor-head">
              <h2 id="meter-title">{t.signal}</h2>
            </div>
            <meter aria-label={t.microphoneLevel} min="0" max="100" value={audio.level}/>
            <VolumeSlider key={audio.selected} value={audio.inputVolume} label={t.gainLabel} accessibleLabel={t.microphoneGain}
              unavailable={offline || !!microphoneChange || !audio.selected}
              apply={value => run(() => window.mic.setInputVolume(audio.selected, value))}/>
            <label className="check-option listen">
              <input type="checkbox" checked={monitorPending ?? microphoneChange?.monitoring ?? !!audio.monitoring}
                aria-disabled={monitorPending !== null || !!microphoneChange || !!audio.playback?.configuring}
                disabled={offline || (!audio.connected && !microphoneChange && !audio.playback?.configuring)}
                onChange={event => listen(event.target.checked)}/>
              <span>{t.listen}</span>
            </label>
          </section>
          {message && <div className="error-notice" role="alert">{message}</div>}
        </section>
      </div>
      <div className="processing">
        <Equalizer playback={audio.playback} selecting={selectingPlayback}
          offline={offline} run={runPlayback} t={t} language={language} headerAction={
            <div className="eq-title isolation-controls">
              <h2 id="isolation-heading">{t.isolationTitle}</h2>
              <button ref={isolationSwitch} type="button" className="switch" role="switch" aria-labelledby="isolation-heading"
                aria-checked={!!audio.isolation?.enabled} disabled={offline || !audio.selected} aria-disabled={isolationChanging || undefined}
                aria-haspopup={audio.isolation?.configured ? undefined : 'dialog'} onClick={changeIsolation}><span/></button>
              <span className="isolation-reset-slot">
                {audio.isolation?.configured && <button type="button" className="isolation-reset" title={t.isolationReset}
                  aria-label={t.isolationReset} aria-haspopup="dialog" disabled={offline || isolationChanging} onClick={openIsolationReset}>
                  <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5"
                    strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                    <path d="M2.25 2.75v4h4M2.6 6.4a5.25 5.25 0 1 1 .9 4.8"/>
                  </svg>
                </button>}
              </span>
            </div>}/>
        {audio.isolation?.failure && <p className="isolation-error" role="status">{t.isolationUnavailable}</p>}
        <Effects effects={audio.effects} offline={starting || audio.offline} run={run} t={t} language={language}/>
      </div>
    </main>
    <SettingsPanel panel={panel} opened={settingsOpened} onClose={() => setSettingsOpened(false)} t={t} language={language}/>
    <UpdateDialog t={t} language={language}/>
    <IsolationWizard panel={isolationPanel} opened={isolationOpened} onClose={closeIsolationSetup} audio={audio} t={t} language={language}/>
    <dialog ref={resetPanel} className="settings-panel isolation-reset-panel" aria-labelledby="isolation-reset-title"
      aria-describedby="isolation-reset-description" closedby="any" onClose={closeIsolationReset}>
      <form method="dialog" className="dialog-content">
        <DialogHeader id="isolation-reset-title" title={t.isolationResetTitle} closeLabel={t.close}
          onClose={() => resetPanel.current.close('cancel')}/>
        <p id="isolation-reset-description">{t.isolationResetDescription}</p>
        <div className="dialog-actions">
          <button type="submit" className="quiet" value="cancel" autoFocus>{t.cancel}</button>
          <button type="submit" className="primary danger" value="reset">{t.reset}</button>
        </div>
      </form>
    </dialog>
  </>;
}

createRoot(document.getElementById('root')).render(<App/>);
