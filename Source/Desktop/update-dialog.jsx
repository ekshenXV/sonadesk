import React, { useEffect, useRef, useState } from 'react';
import { localizeMessage } from './locales.cjs';
import DialogHeader from './dialog-header.jsx';

export default function UpdateDialog({ t, language }) {
  const panel = useRef(null);
  const [update, setUpdate] = useState({ phase: 'idle' });
  const [dismissed, setDismissed] = useState(false);
  const busy = ['downloading', 'preparing', 'installing'].includes(update.phase);
  const restored = update.phase === 'restored';

  useEffect(() => {
    if (!window.mic.onUpdateState) return;
    let received = false, active = true;
    const unsubscribe = window.mic.onUpdateState(next => { received = true; setUpdate(next); });
    window.mic.updateState().then(next => { if (active && !received) setUpdate(next); }).catch(() => {});
    return () => { active = false; unsubscribe(); };
  }, []);

  useEffect(() => {
    if (!dismissed && update.phase !== 'idle' && !panel.current.open) panel.current.showModal();
  }, [dismissed, update.phase]);

  async function install() {
    try {
      const reply = await window.mic.installUpdate();
      if (reply?.error) throw new Error(reply.error);
    } catch { setUpdate(previous => ({ ...previous, phase: 'error', error: 'updateDownloadFailed' })); }
  }

  const size = update.size && new Intl.NumberFormat(language, {
    style: 'unit', unit: 'megabyte', maximumFractionDigits: 1
  }).format(update.size / 1000000);
  const status = restored ? t.updateRestored : {
    downloading: t.updateDownloading, preparing: t.updatePreparing, installing: t.updateInstalling,
    error: localizeMessage(update.error, language)
  }[update.phase];

  return <dialog ref={panel} className="settings-panel update-panel" aria-labelledby="update-title"
    closedby={busy ? 'none' : 'any'} onClose={() => setDismissed(true)}>
    <div className="dialog-content">
      <DialogHeader id="update-title" title={restored ? t.updateFailed : t.updateAvailable} closeLabel={t.close}
        disabled={busy} onClose={() => panel.current.close()}/>
      {!restored && <p className="update-details">{t.updateVersion(update.version, size)}</p>}
      {status && <p className="update-status" role={update.phase === 'error' ? 'alert' : 'status'}>{status}</p>}
      {update.phase === 'downloading' && <progress max="100" value={update.progress} aria-label={t.updateDownloading}/>}
      <footer className="dialog-actions">
        <button type="button" autoFocus className="quiet" disabled={busy} onClick={() => panel.current.close()}>
          {restored ? t.close : t.updateLater}
        </button>
        {!restored && <button type="button" className="primary" disabled={busy} onClick={install}>{t.updateInstall}</button>}
      </footer>
    </div>
  </dialog>;
}
