const { spawn } = require('node:child_process');
const { createHash, randomUUID } = require('node:crypto');
const { createWriteStream } = require('node:fs');
const { copyFile, lstat, mkdir, readFile, rm } = require('node:fs/promises');
const path = require('node:path');
const { Readable, Transform } = require('node:stream');
const { pipeline } = require('node:stream/promises');

const archiveName = 'MicEffects.zip';
const launcherName = 'MicEffects.exe';
const stagePrefix = '.mic-effects-update-';
const stageNamePattern = /^\.mic-effects-update-[0-9a-f]{32}$/;
const releaseTag = /^v(0|[1-9]\d{0,7})\.(0|[1-9]\d{0,7})\.(0|[1-9]\d{0,7})$/;

function newerRelease(release, version) {
  if (!release || release.draft || release.prerelease) return null;
  const tag = releaseTag.exec(release.tag_name);
  const installed = releaseTag.exec('v' + version);
  if (!tag || !installed) return null;
  const difference = tag.slice(1).map((part, index) => Number(part) - Number(installed[index + 1])).find(value => value !== 0);
  if (!(difference > 0)) return null;
  const asset = release.assets?.find(item => item.name === archiveName && item.state === 'uploaded');
  const digest = /^sha256:([0-9a-f]{64})$/i.exec(asset?.digest)?.[1].toLowerCase();
  if (!digest || !Number.isSafeInteger(asset.size) || asset.size <= 0 || asset.size > 1024 ** 3) return null;
  return { tag: tag[0], version: tag[0].slice(1), size: asset.size, digest };
}

function portableRoot(executable) {
  if (!executable || !path.isAbsolute(executable) || path.basename(executable).toLowerCase() !== launcherName.toLowerCase()) return null;
  return path.dirname(executable);
}

async function downloadArchive(fetch, url, release, target, signal, progress) {
  const response = await fetch(url, { cache: 'no-store', signal });
  const final = new URL(response.url || url);
  const allowed = ['github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com'];
  if (!response.ok || !response.body || final.protocol !== 'https:' || !allowed.includes(final.host))
    throw new Error('updateDownloadFailed');

  const hash = createHash('sha256');
  let bytes = 0, percent = 0;
  const verify = new Transform({
    transform(chunk, _, done) {
      bytes += chunk.length;
      if (bytes > release.size) { done(new Error('updateInvalid')); return; }
      hash.update(chunk);
      const next = Math.floor(100 * bytes / release.size);
      if (next !== percent) { percent = next; progress(percent); }
      done(null, chunk);
    }
  });
  await pipeline(Readable.fromWeb(response.body), verify, createWriteStream(target, { flags: 'wx' }), { signal });
  if (bytes !== release.size || hash.digest('hex') !== release.digest) throw new Error('updateInvalid');
}

function prepare(executable, stage, release) {
  return new Promise((resolve, reject) => {
    const helper = spawn(executable, ['--prepare-update', release.version, release.digest], {
      cwd: stage, windowsHide: true, stdio: 'ignore'
    });
    helper.once('error', reject);
    helper.once('exit', code => code === 0 ? resolve() : reject(new Error('updateInvalid')));
  });
}

function startInstaller(executable, stage) {
  return new Promise((resolve, reject) => {
    const helper = spawn(executable, ['--install-update', String(process.pid)], {
      cwd: stage, detached: true, windowsHide: true, stdio: 'ignore'
    });
    helper.once('error', reject);
    helper.once('spawn', () => { helper.unref(); resolve(); });
  });
}

function createUpdater({ repository, version, launcher, audioBinary, fetch, notify, quit }) {
  const root = portableRoot(launcher);
  const repo = typeof repository === 'string' && /^[a-z\d][a-z\d-]{0,38}\/[a-z\d_.-]{1,100}$/i.test(repository) &&
    !['.', '..'].includes(repository.split('/')[1]) ? repository : null;
  let state = { phase: 'idle' }, release, operation;

  function publish(next) { state = next; notify(next); }

  async function check() {
    if (!repo || !root || state.phase !== 'idle') return;
    try {
      const response = await fetch('https://api.github.com/repos/' + repo + '/releases/latest', {
        headers: { Accept: 'application/vnd.github+json' }, cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(8000)
      });
      if (!response.ok) return;
      release = newerRelease(await response.json(), version);
      if (release) publish({ phase: 'available', version: release.version, size: release.size });
    } catch { /* Network errors can be retried at the next launch. */ }
  }

  async function install() {
    if (!release || operation) return;
    operation = new AbortController();
    const { signal } = operation;
    const available = { version: release.version, size: release.size };
    const stage = path.join(root, stagePrefix + randomUUID().replaceAll('-', ''));
    let staged = false, handedOff = false;
    let failure;
    try {
      publish({ ...available, phase: 'downloading', progress: 0 });
      await mkdir(stage);
      staged = true;
      const url = 'https://github.com/' + repo + '/releases/download/' + release.tag + '/' + archiveName;
      await downloadArchive(fetch, url, release, path.join(stage, archiveName),
        AbortSignal.any([signal, AbortSignal.timeout(30 * 60 * 1000)]), progress => publish({ ...available, phase: 'downloading', progress }));
      publish({ ...available, phase: 'preparing' });
      const helper = path.join(stage, 'MicEffects.Update.exe');
      await copyFile(audioBinary, helper);
      await prepare(helper, stage, release);
      signal.throwIfAborted();
      publish({ ...available, phase: 'installing' });
      await startInstaller(helper, stage);
      handedOff = true;
      quit();
    } catch (error) {
      failure = error.code === 'EACCES' || error.code === 'EPERM' ? 'updateWriteFailed'
        : error.message === 'updateInvalid' ? 'updateInvalid' : 'updateDownloadFailed';
    } finally {
      if (staged && !handedOff) await rm(stage, { recursive: true, force: true, maxRetries: 4, retryDelay: 250 }).catch(() => {});
      operation = null;
    }
    if (failure && !signal.aborted) publish({ ...available, phase: 'error', error: failure });
  }

  async function finish(args) {
    const argument = args.find(value => value.startsWith('--mic-effects-update='));
    const name = argument?.slice('--mic-effects-update='.length);
    if (!root || !name || !stageNamePattern.test(name)) return;
    const stage = path.join(root, name);
    try {
      if (!(await lstat(stage)).isDirectory()) return;
      const result = await readFile(path.join(stage, 'result'), 'utf8');
      if (result === 'restored') publish({ phase: 'restored' });
      if (result === 'installed' || result === 'restored')
        await rm(stage, { recursive: true, force: true, maxRetries: 8, retryDelay: 250 });
    } catch { /* Keep recovery files if cleanup fails. */ }
  }

  return { check, install, finish, get state() { return state; }, stop() {
    if (state.phase !== 'installing') operation?.abort();
  } };
}

module.exports = { createUpdater };
