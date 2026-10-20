/* Dressmaker Vortex game extension
 * Steam app 4019220. Unity Mono, DX12.
 * BepInEx plugins install to BepInEx/plugins.
 * BepInEx 5.4.23.5 is copied in on setup because Unity 6 needs that Doorstop build.
 */

const path = require('path');
const fs = require('fs');
const { util } = require('vortex-api');

const GAME_ID = 'dressmaker';
const STEAMAPP_ID = '4019220';
const GAME_NAME = 'Dressmaker';
const EXEC = 'Dressmaker.exe';
const PLUGIN_DLL = 'dressmakerautosew.dll';

function findGame() {
  return util.GameStoreHelper.findByAppId([STEAMAPP_ID])
    .then(game => game.gamePath);
}

function packRoot() {
  return path.join(__dirname, 'bepinex-pack');
}

function copyFile(src, dest) {
  return fs.promises.mkdir(path.dirname(dest), { recursive: true })
    .then(() => fs.promises.copyFile(src, dest));
}

function copyTree(src, dest) {
  return fs.promises.mkdir(dest, { recursive: true }).then(() => fs.promises.readdir(src, { withFileTypes: true }))
    .then(entries => Promise.all(entries.map(entry => {
      const from = path.join(src, entry.name);
      const to = path.join(dest, entry.name);
      if (entry.isDirectory())
        return copyTree(from, to);
      return copyFile(from, to);
    })));
}

function installBepInEx(gamePath) {
  if (!gamePath)
    return Promise.resolve();
  const doorstop = path.join(gamePath, 'doorstop_config.ini');
  const hook = path.join(gamePath, 'winhttp.dll');
  const pack = packRoot();
  if ((fs.existsSync(doorstop) && fs.existsSync(hook)) || !fs.existsSync(pack))
    return Promise.resolve();
  return copyTree(pack, gamePath);
}

function prepareForModding(discovery) {
  if (!discovery || !discovery.path)
    return Promise.reject(new Error('Dressmaker discovery path is undefined.'));
  return fs.promises.mkdir(path.join(discovery.path, 'BepInEx', 'plugins'), { recursive: true })
    .then(() => installBepInEx(discovery.path));
}

function discoveredPath(api) {
  const state = api.getState();
  const discovered = state.settings && state.settings.gameMode && state.settings.gameMode.discovered
    ? state.settings.gameMode.discovered[GAME_ID]
    : undefined;
  return discovered && discovered.path;
}

function testAutoSew(files, gameId) {
  const supported = gameId === GAME_ID && files.some(file => path.basename(file).toLowerCase() === PLUGIN_DLL);
  return Promise.resolve({ supported, requiredFiles: [] });
}

function installAutoSew(files) {
  const source = files.find(file => path.basename(file).toLowerCase() === PLUGIN_DLL);
  return Promise.resolve({
    instructions: [
      { type: 'setmodtype', value: 'bepinex-plugin' },
      { type: 'copy', source, destination: 'DressmakerAutoSew.dll' },
    ],
  });
}

function main(context) {
  context.requireExtension('modtype-bepinex');

  context.registerGame({
    id: GAME_ID,
    name: GAME_NAME,
    mergeMods: true,
    queryPath: findGame,
    queryModPath: () => path.join('BepInEx', 'plugins'),
    logo: 'gameart.jpg',
    executable: () => EXEC,
    requiredFiles: [
      EXEC,
      path.join('Dressmaker_Data', 'Managed', 'Assembly-CSharp.dll'),
    ],
    setup: prepareForModding,
    environment: {
      SteamAPPId: STEAMAPP_ID,
    },
    details: {
      steamAppId: Number(STEAMAPP_ID),
    },
  });

  context.registerInstaller('dressmaker-autosew', 20, testAutoSew, installAutoSew);

  context.once(() => {
    if (context.api.ext.bepinexAddGame !== undefined) {
      context.api.ext.bepinexAddGame({
        gameId: GAME_ID,
        autoDownloadBepInEx: false,
        architecture: 'x64',
        bepinexVersion: '5.4.23.5',
        doorstopConfig: {
          doorstopType: 'default',
        },
      });
    }

    context.api.onAsync('did-deploy', (profileId) => {
      const state = context.api.getState();
      const profile = state.persistent && state.persistent.profiles
        ? state.persistent.profiles[profileId]
        : undefined;
      if (!profile || profile.gameId !== GAME_ID)
        return Promise.resolve();
      return installBepInEx(discoveredPath(context.api));
    });
  });

  return true;
}

module.exports = { default: main };
