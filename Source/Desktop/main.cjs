const { app, BrowserWindow, Menu, Tray, dialog, ipcMain, protocol, net, session, nativeTheme, screen } = require('electron');
const { spawn } = require('node:child_process');
const { mkdirSync, renameSync, writeFileSync } = require('node:fs');
const { readFile, stat } = require('node:fs/promises');
const { createInterface } = require('node:readline');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { strings, localizeMessage } = require('./locales.cjs');
const { createUpdater } = require('./updater.cjs');
const { repository } = require('./update-config.json');

const origin = 'mic-effects://app';
const page = origin + '/index.html';
const assetRoot = path.join(__dirname, 'ui');
const assets = new Set([
  'index.html', 'app.js', 'style.css', 'eq-cursor.svg',
  'fonts/inter-latin.woff2', 'fonts/inter-cyrillic.woff2',
  'fonts/mono-latin.woff2', 'fonts/mono-cyrillic.woff2'
]);
const portableExecutable = process.env.PORTABLE_EXECUTABLE_FILE;
const loginItem = { path: portableExecutable || process.execPath, args: ['--autostart'], name: 'SonaDesk' };
if (!app.commandLine.hasSwitch('user-data-dir')) {
  // Keep the existing profile and single-instance lock across the rename.
  const profile = path.join(app.getPath('appData'), 'Mic Effects');
  mkdirSync(profile, { recursive: true });
  app.setPath('userData', profile);
}
const appearancePath = path.join(app.getPath('userData'), 'appearance.json');
const windowColors = { light: '#edf4fc', dark: '#121b28' };
protocol.registerSchemesAsPrivileged([{ scheme: 'mic-effects', privileges: { standard: true, secure: true, supportFetchAPI: true } }]);

let window;
let tray;
let host;
let quitting = false;
let reloadingWindow = false;
let requestId = 0;
let current = { inputs: [], effects: [], level: 0, rate: 0, kind: 'idle' };
let appearance = { theme: 'light', language: 'ru' };
const pending = new Map();
let ready;
let startupError = null;
const audioBinary = app.isPackaged
  ? path.join(process.resourcesPath, 'SonaDesk.Audio.exe')
  : path.join(__dirname, 'SonaDesk.Audio.exe');
const updater = createUpdater({ repository, version: app.getVersion(), launcher: portableExecutable, audioBinary,
  fetch: (url, options) => net.fetch(url, options), notify: state => sendToWindow('update-state', state), quit });

async function loadAppearance() {
  try {
    const saved = JSON.parse(await readFile(appearancePath, 'utf8'));
    appearance = { theme: saved?.theme === 'dark' ? 'dark' : 'light', language: saved?.language === 'en' ? 'en' : 'ru' };
  } catch (error) {
    if (error.code !== 'ENOENT' && !(error instanceof SyntaxError)) console.warn('Could not read appearance settings:', error.code);
  }
  nativeTheme.themeSource = appearance.theme;
}

function updateTrayMenu() {
  const t = strings[appearance.language];
  tray.setContextMenu(Menu.buildFromTemplate([{ label: t.open, click: show }, { label: t.exit, click: quit }]));
}

function setAppearance(key, value) {
  const validTheme = key === 'theme' && (value === 'light' || value === 'dark');
  const validLanguage = key === 'language' && (value === 'ru' || value === 'en');
  if (!validTheme && !validLanguage) throw new Error('invalidAppearance');
  if (appearance[key] === value) return appearance;

  const next = { ...appearance, [key]: value };
  const temporary = appearancePath + '.tmp';
  try {
    writeFileSync(temporary, JSON.stringify(next), 'utf8');
    renameSync(temporary, appearancePath);
  } catch (error) {
    console.warn('Could not save appearance settings:', error.code);
    throw new Error('appearanceFailed');
  }
  appearance = next;
  if (validLanguage) updateTrayMenu();
  else {
    nativeTheme.themeSource = next.theme;
    window.setBackgroundColor(windowColors[next.theme]);
  }
  return appearance;
}

function sendToWindow(channel, state) {
  const contents = window && !window.isDestroyed() ? window.webContents : null;
  if (contents && !contents.isCrashed() && contents.mainFrame?.url === page) contents.send(channel, state);
}

function publish(state) {
  current = { ...current, ...state };
  sendToWindow('audio-state', state);
}

function call(op, value, enabled, bands, volume) {
  return new Promise((resolve, reject) => {
    if (!host || !host.stdin.writable || host.exitCode !== null || host.signalCode !== null) {
      reject(new Error(startupError || 'offline'));
      return;
    }
    const id = ++requestId;
    pending.set(id, { resolve, reject });
    host.stdin.write(JSON.stringify({ Id: id, Op: op, Value: value, Enabled: enabled, Bands: bands, Volume: volume }) + '\n', error => {
      if (error && pending.delete(id)) reject(error);
    });
  });
}

function startAudio() {
  const handle = window.getNativeWindowHandle().readBigUInt64LE();
  host = spawn(audioBinary, ['--host', handle.toString()], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] });
  createInterface({ input: host.stdout }).on('line', line => {
    let message;
    try { message = JSON.parse(line); } catch { return; }
    if (message.state) publish(message.state);
    else if (typeof message.level === 'number') publish(message);
    const request = pending.get(message.id);
    if (!request) return;
    pending.delete(message.id);
    if (message.error) request.reject(new Error(message.error));
    else request.resolve(Object.hasOwn(message, 'data') ? { data: message.data } : current);
  });

  function stopped(message) {
    startupError = message;
    for (const request of pending.values()) request.reject(new Error(message));
    pending.clear();
    if (!quitting) publish({ status: message, kind: 'error', level: 0, playbackLevel: 0, rate: 0, offline: true, monitoring: false, monitorError: null, enhancementsDisabled: false });
  }
  host.on('error', () => stopped('audioLaunchFailed'));
  host.on('exit', code => stopped(code === 8 ? 'audioDuplicate' : 'audioStopped'));
  host.stdin.on('error', () => {});
  ready = call('init', undefined, process.argv.includes('--autostart')).then(({ data }) => {
    try { migrateLoginItem(data); }
    catch (error) { console.warn('Could not migrate the Windows startup entry:', error.message); }
    return current;
  }).catch(error => {
    if (!current.offline) publish({ status: error.message, kind: 'error', offline: true });
    return current;
  });
}

function startsWithWindows() {
  // Electron's Windows launchItems includes the executable separately, but may omit switches from args.
  return app.getLoginItemSettings(loginItem).launchItems.some(item =>
    item.name === loginItem.name && item.scope === 'user' && item.enabled &&
    path.normalize(item.path).toLowerCase() === path.normalize(loginItem.path).toLowerCase());
}

function migrateLoginItem(command) {
  const match = /^(?:"([^"]+)"|([^"]+?))\s+--autostart\s*$/i.exec(command || '');
  const previousPath = match?.[1] || match?.[2];
  if (!previousPath || !path.isAbsolute(previousPath) || path.basename(previousPath).toLowerCase() !== 'miceffects.exe') return;

  const previous = app.getLoginItemSettings({ path: previousPath, args: loginItem.args }).launchItems.find(item =>
    item.name === 'Mic Effects' && item.scope === 'user');
  if (!previous) return;

  if (previous.enabled && !startsWithWindows()) {
    app.setLoginItemSettings({ ...loginItem, openAtLogin: true });
    if (!startsWithWindows()) return;
  }
  app.setLoginItemSettings({ name: previous.name, path: previous.path, openAtLogin: false });
}

function registerHandlers() {
  const command = (channel, handler) => ipcMain.handle(channel, async (event, ...args) => {
    if (!window || event.sender !== window.webContents || event.senderFrame?.url !== page)
      throw new Error(strings[appearance.language].unknownWindow);
    if (quitting) return { error: 'offline' };
    try { return await handler(...args); }
    catch (error) { return { error: error.message }; }
  });
  const id = value => {
    if (typeof value !== 'string' || !/^[a-f0-9-]{32,36}$/i.test(value)) throw new Error('invalidId');
    return value;
  };
  command('get-state', async () => { await ready; return current; });
  command('get-update-state', () => updater.state);
  command('install-update', () => { void updater.install(); });
  command('set-appearance', setAppearance);
  command('select-input', value => call('select', id(value)));
  command('select-playback', value => call('select-playback', id(value)));
  for (const operation of ['set-input-volume', 'set-playback-volume']) {
    command(operation, (device, level) => {
      if (!Number.isInteger(level) || level < 0 || level > 100) throw new Error('volumeChangeFailed');
      return call(operation, id(device), undefined, undefined, level);
    });
  }
  command('toggle-equalizer', (value, enabled) => call('toggle-equalizer', id(value), enabled === true));
  command('set-equalizer', (value, bands) => {
    if (!Array.isArray(bands) || bands.length !== 10) throw new Error('invalidBands');
    const values = bands.map(({ Frequency, Gain, Q }) => ({ Frequency, Gain, Q }));
    return call('set-equalizer', id(value), undefined, values);
  });
  command('enable-playback-enhancements', value => call('enable-playback-enhancements', id(value)));
  command('enable-enhancements', value => call('enable-enhancements', id(value)));
  command('get-startup', async () => { await ready; return { enabled: startsWithWindows() }; });
  command('set-startup', async enabled => {
    if (typeof enabled !== 'boolean') throw new Error('invalidStartup');
    await ready;
    app.setLoginItemSettings({ ...loginItem, openAtLogin: enabled });
    const actual = startsWithWindows();
    if (actual !== enabled) return { enabled: actual, error: 'startupFailed' };
    return { enabled: actual };
  });
  command('monitor-input', enabled => call('monitor', undefined, enabled === true));
  command('record-isolation', () => call('record-isolation'));
  command('cancel-isolation', () => call('cancel-isolation'));
  command('save-isolation', () => call('save-isolation'));
  command('toggle-isolation', enabled => call('toggle-isolation', undefined, enabled === true));
  command('reset-isolation', microphone => call('reset-isolation', id(microphone)));
  command('isolation-preview', after => call('isolation-preview', undefined, after === true));
  command('toggle-effect', (value, enabled) => call('toggle', id(value), enabled === true));
  command('edit-effect', value => call('edit', id(value)));
  command('remove-effect', value => call('remove', id(value)));
  command('add-effect', async (bundle = false) => {
    const t = strings[appearance.language];
    const result = await dialog.showOpenDialog(window, {
      title: bundle ? t.selectFolder : t.selectEffect,
      defaultPath: path.join(process.env.CommonProgramFiles || 'C:\\Program Files\\Common Files', 'VST3'),
      properties: [bundle ? 'openDirectory' : 'openFile'],
      filters: bundle ? [] : [{ name: t.vstFilter, extensions: ['dll', 'vst3'] }]
    });
    if (result.canceled) return;
    let plugin = result.filePaths[0];
    if (bundle && (!(await stat(plugin)).isDirectory() || path.extname(plugin).toLowerCase() !== '.vst3'))
      throw new Error('invalidFolder');
    if (path.extname(plugin).toLowerCase() === '.vst3' &&
        path.basename(path.dirname(plugin)).toLowerCase() === 'x86_64-win' &&
        path.basename(path.dirname(path.dirname(plugin))).toLowerCase() === 'contents') {
      const root = path.resolve(plugin, '..', '..', '..');
      if (path.basename(root).toLowerCase() === path.basename(plugin).toLowerCase()) plugin = root;
    }
    return call('add', plugin);
  });
  command('minimize', () => window.minimize());
  command('close', () => window.close());
}

function hide() {
  window.hide();
  call('hide').catch(() => {});
  const contents = window.webContents;
  if (!contents.isCrashed() && contents.mainFrame?.url === page) contents.send('window-hidden');
}

async function show() {
  if (quitting || !window || window.isDestroyed() || reloadingWindow) return;
  if (window.webContents.isCrashed()) {
    reloadingWindow = true;
    try { await window.loadURL(page); }
    catch (error) {
      if (!quitting) dialog.showErrorBox('SonaDesk', localizeMessage(error.message, appearance.language));
      return;
    }
    finally { reloadingWindow = false; }
    if (quitting || window.isDestroyed() || window.webContents.isCrashed()) return;
  }
  if (window.isMinimized()) window.restore();
  window.show();
  window.focus();
}

function quit() {
  if (quitting) return;
  quitting = true;
  updater.stop();
  if (!host?.pid || host.exitCode !== null || host.signalCode !== null) { app.quit(); return; }
  const audio = host;
  const timeout = setTimeout(() => { audio.kill(); app.quit(); }, 4000);
  audio.once('exit', () => { clearTimeout(timeout); app.quit(); });
  call('quit').catch(() => audio.stdin.end());
}

if (!app.requestSingleInstanceLock()) {
  app.exit(0);
} else {
  app.on('second-instance', () => { if (window) show(); });
  app.whenReady().then(async () => {
    await loadAppearance();
    const index = await readFile(path.join(assetRoot, 'index.html'), 'utf8');
    protocol.handle('mic-effects', request => {
      const url = new URL(request.url);
      const filename = url.pathname.slice(1);
      if (url.hostname !== 'app' || !assets.has(filename)) return new Response(null, { status: 404 });
      if (filename === 'index.html') {
        const html = index.replace('<html lang="ru">', `<html lang="${appearance.language}" data-theme="${appearance.theme}">`);
        return new Response(html, { headers: { 'Content-Type': 'text/html; charset=utf-8' } });
      }
      return net.fetch(pathToFileURL(path.join(assetRoot, filename)).href);
    });
    session.defaultSession.setPermissionRequestHandler((_, __, callback) => callback(false));
    session.defaultSession.setPermissionCheckHandler(() => false);
    Menu.setApplicationMenu(null);
    const icon = path.join(__dirname, 'icon.ico');
    const workArea = screen.getPrimaryDisplay().workAreaSize;
    const width = Math.min(930, workArea.width);
    window = new BrowserWindow({
      title: 'SonaDesk', width, minWidth: width, maxWidth: width,
      height: Math.min(680, workArea.height), minHeight: Math.min(470, workArea.height), maximizable: false,
      backgroundColor: windowColors[appearance.theme], frame: false, show: false, icon,
      webPreferences: { sandbox: true, contextIsolation: true, nodeIntegration: false, preload: path.join(__dirname, 'preload.cjs') }
    });
    window.webContents.on('will-navigate', event => event.preventDefault());
    window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    window.webContents.on('render-process-gone', () => { if (!quitting) hide(); });
    window.on('close', event => {
      if (quitting) return;
      event.preventDefault();
      hide();
    });
    window.on('session-end', quit);
    tray = new Tray(path.join(__dirname, 'tray.ico'));
    tray.setToolTip('SonaDesk');
    updateTrayMenu();
    tray.on('double-click', show);
    registerHandlers();
    startAudio();
    window.once('ready-to-show', show);
    await window.loadURL(page);
    await updater.finish(process.argv);
    void updater.check();
  }).catch(error => {
    dialog.showErrorBox('SonaDesk', localizeMessage(error.message, appearance.language));
    quit();
  });
  app.on('before-quit', event => {
    if (quitting) return;
    event.preventDefault();
    quit();
  });
}
