const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('mic', {
  state: () => ipcRenderer.invoke('get-state'),
  updateState: () => ipcRenderer.invoke('get-update-state'),
  installUpdate: () => ipcRenderer.invoke('install-update'),
  onUpdateState(callback) {
    const receive = (_, state) => callback(state);
    ipcRenderer.on('update-state', receive);
    return () => ipcRenderer.removeListener('update-state', receive);
  },
  setAppearance: (key, value) => ipcRenderer.invoke('set-appearance', key, value),
  listen(callback) {
    const receive = (_, state) => callback(state);
    ipcRenderer.on('audio-state', receive);
    return () => ipcRenderer.removeListener('audio-state', receive);
  },
  onWindowHidden(callback) {
    const receive = () => callback();
    ipcRenderer.on('window-hidden', receive);
    return () => ipcRenderer.removeListener('window-hidden', receive);
  },
  select: id => ipcRenderer.invoke('select-input', id),
  selectPlayback: id => ipcRenderer.invoke('select-playback', id),
  setInputVolume: (id, volume) => ipcRenderer.invoke('set-input-volume', id, volume),
  setPlaybackVolume: (id, volume) => ipcRenderer.invoke('set-playback-volume', id, volume),
  toggleEqualizer: (id, enabled) => ipcRenderer.invoke('toggle-equalizer', id, enabled),
  setEqualizer: (id, bands) => ipcRenderer.invoke('set-equalizer', id, bands),
  enablePlaybackEnhancements: id => ipcRenderer.invoke('enable-playback-enhancements', id),
  enableEnhancements: id => ipcRenderer.invoke('enable-enhancements', id),
  getStartup: () => ipcRenderer.invoke('get-startup'),
  setStartup: enabled => ipcRenderer.invoke('set-startup', enabled),
  monitor: enabled => ipcRenderer.invoke('monitor-input', enabled),
  recordIsolation: () => ipcRenderer.invoke('record-isolation'),
  cancelIsolation: () => ipcRenderer.invoke('cancel-isolation'),
  saveIsolation: () => ipcRenderer.invoke('save-isolation'),
  toggleIsolation: enabled => ipcRenderer.invoke('toggle-isolation', enabled),
  resetIsolation: microphone => ipcRenderer.invoke('reset-isolation', microphone),
  isolationPreview: after => ipcRenderer.invoke('isolation-preview', after),
  toggle: (id, enabled) => ipcRenderer.invoke('toggle-effect', id, enabled),
  edit: id => ipcRenderer.invoke('edit-effect', id),
  remove: id => ipcRenderer.invoke('remove-effect', id),
  add: bundle => ipcRenderer.invoke('add-effect', bundle === true),
  minimize: () => ipcRenderer.invoke('minimize'),
  close: () => ipcRenderer.invoke('close')
});
