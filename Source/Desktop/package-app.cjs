const { cpSync, copyFileSync, mkdirSync, readdirSync, writeFileSync } = require('node:fs');
const path = require('node:path');
const [stage, output, native] = process.argv.slice(2).map(value => path.resolve(value));
const options = require(path.join(stage, 'node_modules', 'esbuild'));
const version = require(path.join(__dirname, 'package.json')).version;

async function main() {
  const runtime = path.join(stage, 'node_modules', 'electron', 'dist');
  mkdirSync(output, { recursive: true });
  // Electron loads only the current UI languages. Keep its runtime and licence files together.
  for (const entry of readdirSync(runtime))
    if (!['locales', 'electron.exe', 'resources'].includes(entry))
      cpSync(path.join(runtime, entry), path.join(output, entry), { recursive: true });
  const resources = path.join(output, 'resources');
  const app = path.join(resources, 'app');
  const ui = path.join(app, 'ui');
  mkdirSync(ui, { recursive: true });
  mkdirSync(path.join(output, 'locales'), { recursive: true });
  for (const locale of ['en-US.pak', 'ru.pak'])
    copyFileSync(path.join(runtime, 'locales', locale), path.join(output, 'locales', locale));
  for (const entry of readdirSync(path.join(runtime, 'resources')))
    if (entry !== 'default_app.asar')
      cpSync(path.join(runtime, 'resources', entry), path.join(resources, entry), { recursive: true });

  for (const file of ['main.cjs', 'preload.cjs', 'locales.cjs', 'updater.cjs', 'update-config.json', 'icon.ico', 'tray.ico'])
    copyFileSync(path.join(__dirname, file), path.join(app, file));
  for (const file of ['index.html', 'style.css', 'eq-cursor.svg'])
    copyFileSync(path.join(__dirname, file), path.join(ui, file));
  cpSync(path.join(__dirname, 'fonts'), path.join(ui, 'fonts'), { recursive: true });
  for (const pkg of ['react', 'react-dom', 'scheduler'])
    copyFileSync(path.join(stage, 'node_modules', pkg, 'LICENSE'), path.join(resources, pkg + '-LICENSE.txt'));
  writeFileSync(path.join(app, 'package.json'), JSON.stringify({ name: 'sonadesk', productName: 'SonaDesk', version, main: 'main.cjs' }));
  await options.build({
    entryPoints: [path.join(__dirname, 'app.jsx')],
    outfile: path.join(ui, 'app.js'),
    nodePaths: [path.join(stage, 'node_modules')],
    bundle: true, minify: true, target: 'chrome130', legalComments: 'eof',
    define: { 'process.env.NODE_ENV': '"production"' }
  });
  copyFileSync(path.join(native, 'SonaDesk.Audio.exe'), path.join(resources, 'SonaDesk.Audio.exe'));
  copyFileSync(path.join(native, 'MicEffects.Vst3.dll'), path.join(resources, 'MicEffects.Vst3.dll'));
  copyFileSync(path.join(__dirname, '..', 'Vst3', 'LICENSE.txt'), path.join(resources, 'VST3-SDK-LICENSE.txt'));
  const executable = path.join(output, 'SonaDesk.exe');
  copyFileSync(path.join(runtime, 'electron.exe'), executable);
  console.log(executable);
}

main().catch(error => { console.error(error); process.exitCode = 1; });
