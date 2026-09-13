import assert from 'node:assert/strict';
import path from 'node:path';
import { writeFile } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';
import { Sidecar } from './sidecar.mjs';
import { checkDesktopUi } from './smoke-ui.mjs';

// Executed only with --smoke against an explicit scratch root. This exercises
// the real Chromium renderer/preload, sidecar, and synthetic codec fixture.
export async function runSmoke(window, { dataRoot, packageEvents, media, backend, executable }) {
  const contents = window.webContents;
  const boundary = await contents.executeJavaScript(`(async () => {
    const bridge = window.revuDesktop;
    if (!bridge) throw new Error('Missing preload bridge');
    const status = await bridge.invoke('get_config');
    const recording = await bridge.invoke('get_recording_status');
    const background = await bridge.invoke('get_background_settings');
    if (recording.available || recording.settings.enabled || background.startupAvailable)
      throw new Error('Scratch recording/background boundaries failed');
    const commands = [];
    for (const command of ['get_dashboard', 'get_objectives', 'get_games', 'get_rules', 'get_patterns']) {
      const value = await bridge.invoke(command);
      if (!value || typeof value !== 'object') throw new Error('Invalid snapshot ' + command);
      commands.push(command);
    }
    let rejected = false;
    try { await bridge.invoke('arbitrary_fetch', {url:'http://127.0.0.1'}); } catch { rejected = true; }
    return { version: bridge.version, kind: bridge.kind, node: typeof window.require,
      process: typeof window.process, noCredentials: !('token' in bridge) && !('port' in bridge),
      recorder: bridge.capabilities.recorder, recording, background, commands, rejected, hasConfig: !!status };
  })()`);
  assert.equal(boundary.version, 1); assert.equal(boundary.kind, 'electron');
  assert.equal(boundary.node, 'undefined'); assert.equal(boundary.process, 'undefined');
  assert.equal(boundary.noCredentials, true); assert.equal(boundary.rejected, true);
  const owner = await backend.request('/api/host');
  const competitor = new Sidecar({ dataRoot, executable, isolated: true });
  await assert.rejects(() => competitor.start(), /exited before becoming ready/);
  assert.equal((await backend.request('/api/host')).launchId, owner.launchId);
  const deadline = Date.now() + 15000;
  let child;
  while (Date.now() < deadline) {
    child = contents.mainFrame.frames.find(frame => frame.url.startsWith('revu-app://ui/'));
    if (child) break;
    await delay(100);
  }
  assert.ok(child, 'Trusted UI iframe must load');
  const iframe = await child.executeJavaScript(`(async () => {
    const { getInvoke } = await import('./platform/index.mjs');
    const invoke = await getInvoke();
    const result = await invoke('get_objectives');
    return { directBridge: typeof window.revuDesktop, node: typeof window.require,
      origin: location.origin, hasSnapshot: !!result };
  })()`);
  assert.equal(iframe.directBridge, 'undefined'); assert.equal(iframe.node, 'undefined');
  assert.equal(iframe.origin, 'revu-app://ui'); assert.equal(iframe.hasSnapshot, true);
  const dashboard = await child.executeJavaScript(`(async () => {
    const deadline = Date.now() + 15000;
    while (Date.now() < deadline) {
      const error = document.querySelector('#errpanel');
      if (error && !error.hidden) throw new Error(document.querySelector('#err-detail')?.textContent || 'Dashboard failed');
      const status = document.querySelector('#statusline b')?.textContent;
      const statCount = document.querySelector('#strip')?.children.length;
      if (status && !status.includes('Loading') && statCount === 5) return { status, statCount };
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('Dashboard did not render its backend data');
  })()`);
  const rendered = await contents.executeJavaScript(`({frame:document.querySelector('iframe').getBoundingClientRect().toJSON(),
    textLength:document.querySelector('iframe').contentDocument.body.innerText.length,
    title:document.querySelector('iframe').contentDocument.title,
    appVersion:document.querySelector('#appbar-ver').textContent})`);
  assert.ok(rendered.textLength > 100, 'The real page must render content');

  const fixture = path.join(dataRoot, 'recordings', 'synthetic ü space.mp4');
  const grants = await media.replace([fixture]);
  assert.equal(grants.length, 1, 'Generate the synthetic codec fixture before --smoke');
  const mediaUrl = grants[0][1];
  const playback = await contents.executeJavaScript(`(async () => {
    const video = document.createElement('video'); video.id = 'host-smoke-video'; video.muted = true;
    video.style.cssText = 'position:fixed;right:20px;bottom:20px;width:320px;z-index:99999';
    document.body.append(video);
    const wait = (event) => new Promise((resolve,reject) => {
      const timeout = setTimeout(() => reject(new Error('Video timeout: '+event)), 10000);
      video.addEventListener(event, () => { clearTimeout(timeout); resolve(); }, {once:true});
      video.addEventListener('error', () => {clearTimeout(timeout);reject(new Error('Video error '+video.error?.code));}, {once:true});
    });
    const loaded = wait('loadedmetadata'); video.src = ${JSON.stringify(mediaUrl)}; await loaded;
    const seeked = wait('seeked'); video.currentTime = 1.5; await seeked;
    const played = wait('timeupdate'); await video.play(); await played; video.pause();
    return { duration: video.duration, width: video.videoWidth, height: video.videoHeight, time: video.currentTime,
      h264: video.canPlayType('video/mp4; codecs="avc1.42E01E, mp4a.40.2"') };
  })()`);
  assert.ok(playback.duration >= 2.5); assert.equal(playback.width, 640); assert.ok(playback.time >= 1.5);
  const refreshed = await media.replace([fixture]);
  assert.equal(refreshed[0][1], mediaUrl);
  await contents.executeJavaScript(`new Promise((resolve,reject)=>{
    const v=document.querySelector('#host-smoke-video');
    const timeout=setTimeout(()=>reject(new Error('Refresh seek timeout')),10000);
    v.addEventListener('seeked',()=>{clearTimeout(timeout);resolve();},{once:true});v.currentTime=0.5;
  })`);
  const timing = await contents.executeJavaScript(`(async () => {
    const { createTransport } = await import('./vodtransport.js');
    const video = document.querySelector('#host-smoke-video');
    const transport = createTransport({video});
    transport.setTimeOrigin(-0.5);
    const seeked = new Promise((resolve,reject) => {
      const timeout=setTimeout(()=>reject(new Error('Game clock seek timeout')),10000);
      video.addEventListener('seeked',()=>{clearTimeout(timeout);resolve();},{once:true});
    });
    transport.seekTo(1); await seeked;
    return {gameTime:transport.currentTime,mediaTime:video.currentTime};
  })()`);
  assert.ok(Math.abs(timing.gameTime - 1) < 0.1); assert.ok(Math.abs(timing.mediaTime - 1.5) < 0.1);
  // The restricted preview exposes the Ascent controls but still refuses scans:
  // folder changes/imports are validated separately with a normal scratch host.
  const ascent = await contents.executeJavaScript(`(async () => {
    const config=await window.revuDesktop.invoke('get_config');
    let scanBlocked=false;
    try { await window.revuDesktop.invoke('scan_vods'); }
    catch(error) { scanBlocked=String(error.message).includes('disabled in isolated host testing'); }
    return {folderAvailable:typeof config.ascentFolder==='string',scanBlocked};
  })()`);
  assert.equal(ascent.folderAvailable, true);
  assert.equal(ascent.scanBlocked, true);
  await contents.executeJavaScript(`document.querySelector('iframe').src='settings.html'`);
  let settingsFrame;
  const settingsDeadline = Date.now() + 10_000;
  while (Date.now() < settingsDeadline) {
    settingsFrame = contents.mainFrame.frames.find(frame => frame.url.endsWith('/settings.html'));
    if (settingsFrame) break;
    await delay(100);
  }
  assert.ok(settingsFrame, 'Settings page must load');
  const recordingSettings = await settingsFrame.executeJavaScript(`(async () => {
    const deadline=Date.now()+10000;
    while(Date.now()<deadline) {
      const status=document.querySelector('#recording-status')?.textContent;
      if(status?.includes('coming in a future update') && document.querySelector('#ascentFolder')?.disabled===false)
        return {status, presets:document.querySelector('#recordingPreset').options.length,
        startupDisabled:document.querySelector('#startWithWindows').disabled,
        ascentFolderVisible:!!document.querySelector('#ascentFolder'),
        ascentScanDisabledWithoutFolder:document.querySelector('[data-action="scan_vods"]')?.disabled===true,
        builtInRecordingDisabled:['recordingEnabled','recordingPreset','open-recordings-folder','save-recording-settings']
          .every(id=>document.getElementById(id)?.disabled===true),
        nativeRecorderControls:!!document.querySelector('#recordingEnabled') && !!document.querySelector('#open-recordings-folder')};
      await new Promise(resolve=>setTimeout(resolve,100));
    }
    throw new Error('Recording settings did not load through the host');
  })()`);
  assert.equal(recordingSettings.presets, 3); assert.equal(recordingSettings.startupDisabled, true);
  assert.equal(recordingSettings.ascentFolderVisible, true);
  assert.equal(recordingSettings.ascentScanDisabledWithoutFolder, true);
  assert.equal(recordingSettings.builtInRecordingDisabled, true);
  assert.equal(recordingSettings.nativeRecorderControls, true);
  await settingsFrame.executeJavaScript(`(async () => {
    const deadline=Date.now()+3000;
    while(Date.now()<deadline) {
      const cards=[...document.querySelectorAll('.set-card')];
      if(cards.length && cards.every(card => Number(getComputedStyle(card).opacity) >= 0.99)) return;
      await new Promise(resolve=>setTimeout(resolve,50));
    }
    throw new Error('Settings entrance did not finish');
  })()`);
  await contents.executeJavaScript(`document.querySelector('#host-smoke-video').remove()`);
  await contents.executeJavaScript(`new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))`);
  await writeFile(path.join(dataRoot, 'electron-smoke.png'), (await contents.capturePage()).toPNG());
  const ui = await checkDesktopUi(window, dataRoot, async () => (await media.replace([fixture]))[0][1]);
  assert.equal(packageEvents.length, 0, 'Gaming packages must remain disabled');
  return { passed: true, utc: new Date().toISOString(), versions: process.versions,
    gamingPackages: packageEvents, boundary, iframe, dashboard, rendered, playback, timing, ascent, recordingSettings, ui,
    sameReviewSeekAfterRefresh: true, duplicateSidecarRejected: true,
    limitations: ['No signed installer or upgrade acceptance', 'Synthetic H.264/AAC fixture, not League Recorder capture',
      'Scratch data only; external effects and live monitoring disabled', 'No real match, approval, latency, performance, installer or upgrade acceptance'] };
}
