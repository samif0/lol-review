import assert from 'node:assert/strict';
import path from 'node:path';
import { writeFile } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';
import { BrowserWindow } from 'electron';
import { checkMatchWorkspace } from './smoke-match-workspace.mjs';

// Real renderer checks, restricted to the existing scratch smoke profile.
export async function checkDesktopUi(window, dataRoot, grantFixture) {
  const contents = window.webContents;
  const captures = [];
  async function navigate(page) {
    await contents.executeJavaScript(`new Promise(resolve => {
      const f=document.querySelector('#app-frame');
      f.addEventListener('load',resolve,{once:true}); f.src=${JSON.stringify(page)};
    })`);
    const child = contents.mainFrame.frames.find(frame => frame.url.startsWith('revu-app://ui/'));
    await child.executeJavaScript(`document.fonts.ready.then(()=>true)`);
    await delay(250);
    return child;
  }
  async function capture(name) {
    // A renderer animation frame may never arrive when the window is occluded.
    // Pace captures in the host; capturePage requests the compositor snapshot.
    await delay(100);
    const file = `ui-${name}.png`;
    await writeFile(path.join(dataRoot, file), (await contents.capturePage()).toPNG());
    captures.push(file);
  }
  async function layout(child, name) {
    const state = await child.executeJavaScript(`({
      font:getComputedStyle(document.body).fontFamily,
      overflow:document.documentElement.scrollWidth-document.documentElement.clientWidth,
      title:document.querySelector('h1')?.textContent,
      oldTour:!!document.querySelector('.frt-panel')
    })`);
    assert.match(state.font, /Segoe UI/);
    assert.ok(state.overflow <= 1, `${name} horizontal overflow: ${state.overflow}`);
    assert.equal(state.oldTour, false);
    return state;
  }
  async function captureResetSteps(child, suffix = '') {
    const click = selector => child.executeJavaScript(`document.querySelector(${JSON.stringify(selector)}).click()`);
    await click('#tc-runbtn');
    await capture(`reset-name${suffix}`);
    await click('#tc-chips .tc-chip');
    await click('.tc-step[data-step="0"] .tc-next');
    await capture(`reset-breathe${suffix}`);
    await click('[data-action="skip_breathing"]');
    await capture(`reset-reframe${suffix}`);
    await click('#tc-reframe-menu .tc-menuopt');
    await click('.tc-step[data-step="2"] .tc-next');
    await capture(`reset-plan${suffix}`);
    await click('#tc-trigger-menu .tc-menuopt');
    await click('#tc-response-menu .tc-menuopt');
    await click('.tc-step[data-step="3"] .tc-next');
    await capture(`reset-done${suffix}`);
    // Inspect the full wizard without saving a reset to the scratch profile.
    await layout(child, `Reset wizard${suffix}`);
  }
  async function checkTransportShortcuts(frame, prefix, {exercise=false,label=prefix}={}) {
    let behavior;
    if(exercise) behavior=await frame.executeJavaScript(`(async()=>{
      const prefix=${JSON.stringify(prefix)},get=suffix=>document.getElementById(prefix+'-'+suffix);
      const video=get('video'),select=get('rate-sel'),mute=get('mute'),fs=get('fs');
      const target=document.querySelector(prefix==='m'?'.pat-stage':'#vp-wrap');
      const expandedClass=prefix==='m'?'pat-expanded':'vp-expanded';
      const state=()=>({rate:video.playbackRate,selected:select.value,muted:video.muted,
        step:Number(get('time').querySelector('.step').textContent.match(/[0-9]+/)?.[0]),expanded:target.classList.contains(expandedClass)});
      const key=(key,options={},element=document.body)=>element.dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true,cancelable:true,...options}));
      const tick=()=>new Promise(resolve=>setTimeout(resolve,0));
      const initial=state(),scroll={left:scrollX,top:scrollY};
      const sourceObject=video.srcObject,wasPaused=video.paused;
      const hints=()=>({mute:mute.querySelector('kbd')?.textContent,expand:fs.querySelector('kbd')?.textContent,
        step:[...get('time').querySelectorAll('.step kbd')].map(el=>el.textContent)});
      const originalHints=hints();
      try {
        // Live canvas streams ignore playbackRate. Exercise rate shortcuts on
        // the real, unloaded media element, then reattach the geometry fixture.
        if(sourceObject){video.pause();video.srcObject=null;video.load();await tick();}
        for(let i=0;i<10;i++)key('+');
        const fastest=state();
        for(let i=0;i<10;i++)key('-');
        const slowest=state();
        key('=');const equalsIncrease=state();
        key('m');await tick();const mutedOnce=state(),muteGlyphOnce=mute.querySelector('.tbtn-icon')?.textContent;
        key('m');await tick();const mutedTwice=state(),muteGlyphTwice=mute.querySelector('.tbtn-icon')?.textContent;
        for(let i=0;i<6;i++)key('ArrowDown');
        const smallestStep=state();key('ArrowUp');const largerStep=state();key('ArrowDown');
        const beforeGuards=state();
        for(const modifier of ['ctrlKey','altKey','metaKey'])for(const shortcut of ['m','+','ArrowUp','f'])key(shortcut,{[modifier]:true});
        const afterChords=state();
        const note=document.querySelector(prefix==='m'?'#m-note':'#vp-bm-note');
        const noteValue=note.value;
        for(const element of [select,select.options[0],note])for(const shortcut of ['m','+','ArrowUp','f'])key(shortcut,{},element);
        const afterEditing=state(),notePreserved=note.value===noteValue;
        key('f');await tick();const enlarged=state();key('f');await tick();const restored=state();
        return {initial,fastest,slowest,equalsIncrease,mutedOnce,mutedTwice,muteGlyphOnce,muteGlyphTwice,
          smallestStep,largerStep,beforeGuards,afterChords,afterEditing,notePreserved,enlarged,restored,
          originalHints,finalHints:hints(),seekVerified:false,
          seekLimitation:'Sample media is unavailable or a live canvas stream; seek movement is covered by transport unit tests.'};
      } finally {
        if(target.classList.contains(expandedClass)!==initial.expanded)fs.click();
        select.value=initial.selected;select.dispatchEvent(new Event('change',{bubbles:true}));video.muted=initial.muted;
        for(let i=0;i<6 && state().step!==initial.step;i++)key(state().step<initial.step?'ArrowUp':'ArrowDown');
        if(sourceObject){
          const ready=new Promise((resolve,reject)=>{
            const timeout=setTimeout(()=>reject(new Error('Restoring the Patterns canvas fixture timed out')),5000);
            video.addEventListener('loadedmetadata',()=>{clearTimeout(timeout);resolve();},{once:true});
          });
          video.srcObject=sourceObject;
          const playing=video.play();sourceObject.getVideoTracks()[0]?.requestFrame?.();
          await ready;await playing;if(wasPaused)video.pause();
        }
        scrollTo({...scroll,behavior:'instant'});await tick();
      }
    })()`);
    const layout=await frame.executeJavaScript(`(()=>{
      const prefix=${JSON.stringify(prefix)},get=suffix=>document.getElementById(prefix+'-'+suffix);
      const bar=get('back').closest('.vp-transport'),rect=el=>{const r=el.getBoundingClientRect();return {x:r.x,y:r.y,right:r.right,bottom:r.bottom,width:r.width,height:r.height};};
      const buttonHint=suffix=>get(suffix).querySelector('kbd')?.textContent;
      return {hints:{back:buttonHint('back'),forward:buttonHint('fwd'),mute:buttonHint('mute'),expand:buttonHint('fs'),
        step:[...get('time').querySelectorAll('.step kbd')].map(el=>el.textContent),
        speed:[...bar.querySelectorAll('.transport-rate kbd')].map(el=>el.textContent.replace('−','-'))},
        playHints:get(prefix==='m'?'play-btn':'play').querySelectorAll('kbd').length,
        bar:rect(bar),barOverflow:bar.scrollWidth-bar.clientWidth,
        documentOverflow:document.documentElement.scrollWidth-document.documentElement.clientWidth,
        keys:[...bar.querySelectorAll('kbd')].map(el=>({text:el.textContent,rect:rect(el),
          opacity:getComputedStyle(el).opacity,visibility:getComputedStyle(el).visibility,
          control:rect(el.closest('button,.step,.transport-rate')||el.parentElement)}))};
    })()`);
    await writeFile(path.join(dataRoot,`ui-shortcuts-${label}.json`),JSON.stringify({behavior,layout},null,2));
    assert.deepEqual(layout.hints,{back:'←',forward:'→',mute:'M',expand:'F',step:['↑','↓'],speed:['-','+']});
    assert.equal(layout.playHints,0,'Play must not show a Space badge');
    assert.ok(layout.barOverflow<=1,`${label} transport overflow: ${layout.barOverflow}`);
    assert.ok(layout.documentOverflow<=1,`${label} document overflow: ${layout.documentOverflow}`);
    for(const hint of layout.keys) {
      assert.ok(hint.rect.width>0 && hint.rect.height>0 && hint.opacity!=='0' && hint.visibility==='visible',`${label} hint ${hint.text} must be visible`);
      for(const box of [layout.bar,hint.control])assert.ok(hint.rect.x>=box.x-1 && hint.rect.y>=box.y-1 && hint.rect.right<=box.right+1 && hint.rect.bottom<=box.bottom+1,
        `${label} hint must fit its control and transport: ${JSON.stringify(hint)}`);
    }
    if(behavior) {
      assert.equal(behavior.fastest.rate,3);assert.equal(behavior.fastest.selected,'3');
      assert.equal(behavior.slowest.rate,.25);assert.equal(behavior.slowest.selected,'0.25');
      assert.equal(behavior.equalsIncrease.rate,.5);assert.equal(behavior.equalsIncrease.selected,'0.5');
      assert.equal(behavior.mutedOnce.muted,!behavior.initial.muted);assert.equal(behavior.mutedTwice.muted,behavior.initial.muted);
      assert.notEqual(behavior.muteGlyphOnce,behavior.muteGlyphTwice,'Mute glyph must track state without removing its hint');
      assert.equal(behavior.smallestStep.step,1);assert.equal(behavior.largerStep.step,2);
      assert.deepEqual(behavior.afterChords,behavior.beforeGuards,'Modified keys must not trigger transport shortcuts');
      assert.deepEqual(behavior.afterEditing,behavior.beforeGuards,'Typing and speed dropdown keys must stay local to their field');
      assert.equal(behavior.notePreserved,true);
      assert.equal(behavior.enlarged.expanded,true);assert.equal(behavior.restored.expanded,false);
      assert.deepEqual(behavior.finalHints,behavior.originalHints,'State changes must retain visible shortcut hints');
    }
    return {behavior,layout};
  }
  async function checkPatternPlayer(preview, frame) {
    const files = [];
    const checks = [];
    async function screenshot(name) {
      await delay(100);
      const file = `ui-sample-patterns-${name}.png`;
      await writeFile(path.join(dataRoot, file), (await preview.webContents.capturePage()).toPNG());
      files.push(file);
    }
    const populated = await frame.executeJavaScript(`(async()=>{
      const deadline=Date.now()+10000;
      while(Date.now()<deadline && (document.querySelector('#pat-main')?.hidden || !document.querySelector('#m-fs')))
        await new Promise(resolve=>setTimeout(resolve,100));
      const cards=()=>[...document.querySelectorAll('#pat-pick [data-action="select_pattern"]')];
      const firstTitle=document.querySelector('#m-title')?.textContent;
      const firstNote=document.querySelector('#m-note')?.value;
      const note=document.querySelector('#m-note');
      note.value='Unfinished note kept when reselecting this pattern';
      cards()[0]?.click();
      const keptSamePatternDraft=note.isConnected && note.value==='Unfinished note kept when reselecting this pattern';
      note.value=firstNote;
      const initialPosition=document.querySelector('#m-position')?.textContent;
      const initialCount=document.querySelectorAll('#pat-rail [data-action="goto_moment"]').length;
      cards()[1]?.click();
      const switchedTitle=document.querySelector('#m-title')?.textContent;
      const selectedCount=document.querySelectorAll('#pat-pick .pat-card-active').length;
      cards()[0]?.click();
      document.querySelector('#m-next')?.click();
      const nextPosition=document.querySelector('#m-position')?.textContent;
      const nextTitle=document.querySelector('#m-title')?.textContent;
      document.querySelector('#m-prev')?.click();
      return {cards:cards().length,moments:initialCount,initialPosition,nextPosition,
        selectedCount,keptSamePatternDraft,switched:switchedTitle!==firstTitle,stepped:nextTitle!==firstTitle,
        restored:document.querySelector('#m-title')?.textContent===firstTitle,
        noteRestored:document.querySelector('#m-note')?.value===firstNote,
        selectedHeading:document.querySelector('#pat-review-title')?.textContent,
        empty:!document.querySelector('#pat-empty')?.hidden};
    })()`);
    assert.ok(populated.cards >= 2 && populated.moments >= 2, 'Patterns fixture must populate selectors and moments');
    assert.equal(populated.selectedCount, 1);
    assert.equal(populated.keptSamePatternDraft,true,'Reselecting the current pattern must preserve unfinished notes');
    assert.ok(populated.switched && populated.stepped && populated.restored && populated.noteRestored);
    assert.match(populated.initialPosition, /Moment 1 of \d+/i);
    assert.match(populated.nextPosition, /Moment 2 of \d+/i);
    assert.ok(populated.selectedHeading?.trim());
    assert.equal(populated.empty, false);
    await frame.executeJavaScript(`scrollTo({top:0,behavior:'instant'})`);
    await screenshot('populated');

    // Sample paths intentionally do not point at user files. An in-memory frame
    // supplies real video dimensions without testing capture or file decoding.
    const media = await frame.executeJavaScript(`(async()=>{
      const canvas=document.createElement('canvas');canvas.width=640;canvas.height=360;
      const ctx=canvas.getContext('2d');ctx.fillStyle='#171717';ctx.fillRect(0,0,640,360);
      ctx.fillStyle='#303030';ctx.fillRect(24,24,592,312);
      ctx.fillStyle='#ececec';ctx.font='20px sans-serif';ctx.fillText('Synthetic video · layout smoke test',40,70);
      const stream=canvas.captureStream(0);window.__patternSmokeStream=stream;
      const video=document.querySelector('#m-video');video.muted=true;video.hidden=false;
      document.querySelector('#m-transport').hidden=false;
      document.querySelector('#m-surface').classList.add('pat-surface-playing');
      const ready=new Promise((resolve,reject)=>{
        const timeout=setTimeout(()=>reject(new Error('Synthetic Patterns video metadata timed out')),5000);
        video.addEventListener('loadedmetadata',()=>{clearTimeout(timeout);resolve();},{once:true});
      });
      video.srcObject=stream;const playing=video.play();stream.getVideoTracks()[0].requestFrame();
      await ready;await playing;video.pause();
      return {width:video.videoWidth,height:video.videoHeight};
    })()`, true);
    assert.deepEqual(media, {width:640,height:360});

    async function geometry() {
      return frame.executeJavaScript(`(()=>{
        const stage=document.querySelector('.pat-stage'),surface=document.querySelector('#m-surface');
        const video=document.querySelector('#m-video'),transport=document.querySelector('#m-transport');
        const rect=element=>{const r=element.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,right:r.right,bottom:r.bottom};};
        const style=getComputedStyle(stage),bodyStyle=getComputedStyle(document.body);
        const box=element=>{const s=getComputedStyle(element);return {element:element.tagName+'.'+element.className,
          rect:rect(element),position:s.position,transform:s.transform,filter:s.filter,contain:s.contain,
          perspective:s.perspective,willChange:s.willChange,overflow:s.overflow,zoom:s.zoom}};
        const ancestors=[];for(let parent=stage.parentElement;parent;parent=parent.parentElement) ancestors.push(box(parent));
        const active=document.activeElement;
        return {expanded:stage.classList.contains('pat-expanded'),stage:rect(stage),surface:rect(surface),
          video:rect(video),transport:rect(transport),viewport:{width:innerWidth,height:innerHeight},
          clientViewport:{width:document.documentElement.clientWidth,height:document.documentElement.clientHeight},
          visualViewport:window.visualViewport?{width:visualViewport.width,height:visualViewport.height,scale:visualViewport.scale}:null,
          sizing:{width:style.width,height:style.height,minWidth:style.minWidth,minHeight:style.minHeight,
            maxWidth:style.maxWidth,maxHeight:style.maxHeight,inset:style.inset,boxSizing:style.boxSizing,aspectRatio:style.aspectRatio},ancestors,
          position:style.position,background:style.backgroundColor,bodyBackground:bodyStyle.backgroundColor,
          role:stage.getAttribute('role'),modal:stage.getAttribute('aria-modal'),
          overflow:document.documentElement.scrollWidth-document.documentElement.clientWidth,
          bodyOverflow:bodyStyle.overflow,scrollY,
          noteInert:!!document.querySelector('#m-note').closest('[inert]'),
          pickerInert:!!document.querySelector('#pat-pick').closest('[inert]'),
          inertCount:document.querySelectorAll('[inert]').length,
          focus:active?.id,focusInside:stage.contains(active),
          overlayCovers:stage.contains(document.elementFromPoint(2,2))};
      })()`);
    }
    function within(inner, outer, name) {
      assert.ok(inner.width > 0 && inner.height > 0, `${name} must remain visible`);
      assert.ok(inner.x >= outer.x-2 && inner.y >= outer.y-2 && inner.right <= outer.right+2 && inner.bottom <= outer.bottom+2,
        `${name} must fit inside expanded stage`);
    }
    try {
      for (const [size,width,height] of [['full',1600,1000],['compact',980,640]]) {
        preview.setSize(width,height);await delay(200);
        const shortcuts=await checkTransportShortcuts(frame,'m',{exercise:size==='full',label:`patterns-${size}`});
        await frame.executeJavaScript(`document.querySelector('#m-fs').focus({preventScroll:true});
          scrollTo({top:document.scrollingElement.scrollHeight,behavior:'instant'})`);
        await delay(100);
        const before=await geometry();
        if(size==='compact') assert.ok(before.scrollY>0,'Compact Patterns restoration must exercise a scrolled page');
        await frame.executeJavaScript(`document.querySelector('#m-fs').click()`);
        const expanded=await geometry();
        const expandedShortcuts=await checkTransportShortcuts(frame,'m',{label:`patterns-expanded-${size}`});
        const diagnostics=path.join(dataRoot,`ui-sample-patterns-geometry-${size}.json`);
        await writeFile(diagnostics,JSON.stringify({size,before,expanded},null,2));
        await screenshot(`expanded-${size}`);
        assert.equal(expanded.expanded,true);assert.equal(expanded.position,'fixed');
        assert.equal(expanded.role,'dialog');assert.equal(expanded.modal,'true');
        assert.ok(expanded.focusInside && expanded.noteInert && expanded.pickerInert);
        assert.equal(expanded.bodyOverflow,'hidden');assert.ok(expanded.overlayCovers);
        assert.equal(expanded.background,expanded.bodyBackground,'Expanded player must have an opaque page background');
        assert.match(expanded.background,/^rgb\(/,'Expanded background must not be transparent');
        assert.ok(Math.abs(expanded.stage.x)<=2 && Math.abs(expanded.stage.y)<=2);
        assert.ok(Math.abs(expanded.stage.width-expanded.viewport.width)<=2 && Math.abs(expanded.stage.height-expanded.viewport.height)<=2,
          `Expanded ${size} stage must fill viewport: ${JSON.stringify({stage:expanded.stage,viewport:expanded.viewport,clientViewport:expanded.clientViewport,sizing:expanded.sizing})}. See ${diagnostics}`);
        within(expanded.surface,expanded.stage,'Video surface');within(expanded.video,expanded.surface,'Video');
        within(expanded.transport,expanded.stage,'Transport');
        assert.ok(expanded.video.height>120 && expanded.transport.height>=30);
        assert.ok(expanded.surface.bottom<=expanded.transport.y+2,'Video and transport must not overlap');
        assert.ok(expanded.overflow<=1);

        const focusableCount=await frame.executeJavaScript(`document.querySelector('.pat-stage').querySelectorAll('button:not(:disabled),select,[tabindex="0"]').length`);
        preview.webContents.focus();
        for(let i=0;i<focusableCount+2;i++) {
          preview.webContents.sendInputEvent({type:'keyDown',keyCode:'Tab'});
          preview.webContents.sendInputEvent({type:'keyUp',keyCode:'Tab'});
          await delay(15);
          assert.equal((await geometry()).focusInside,true,`Tab must remain inside expanded Patterns player (${size})`);
        }
        await frame.executeJavaScript(`document.querySelector('#m-rate-sel').focus()`);
        preview.webContents.sendInputEvent({type:'keyDown',keyCode:'Escape'});
        preview.webContents.sendInputEvent({type:'keyUp',keyCode:'Escape'});
        await delay(100);
        const restored=await geometry();
        await writeFile(diagnostics,JSON.stringify({size,before,expanded,restored},null,2));
        assert.equal(restored.expanded,false,'Escape from speed selection must restore the player');
        assert.equal(restored.role,before.role);assert.equal(restored.modal,before.modal);
        assert.equal(restored.focus,'m-fs','Restore focus to the control that opened the player');
        assert.equal(restored.noteInert,before.noteInert);assert.equal(restored.pickerInert,before.pickerInert);
        assert.equal(restored.inertCount,before.inertCount);assert.equal(restored.bodyOverflow,before.bodyOverflow);
        assert.ok(Math.abs(restored.scrollY-before.scrollY)<=2);
        for(const key of ['x','y','width','height']) assert.ok(Math.abs(restored.stage[key]-before.stage[key])<=2,
          `Restore ${size} stage ${key}: ${JSON.stringify({before:before.stage,restored:restored.stage})}. See ${diagnostics}`);
        assert.ok(restored.overflow<=1);
        await screenshot(`restored-${size}`);
        checks.push({size,before,expanded,restored,shortcuts,expandedShortcuts});
      }
    } finally {
      await frame.executeJavaScript(`window.__patternSmokeStream?.getTracks().forEach(track=>track.stop());
        const video=document.querySelector('#m-video');video.pause();video.srcObject=null;video.hidden=true;
        document.querySelector('#m-transport').hidden=true;
        document.querySelector('#m-surface').classList.remove('pat-surface-playing');`);
      preview.setSize(1600,1000);
    }
    return {populated,media,checks,files};
  }
  async function checkVodHeading(preview, frame) {
    const result = {};
    try {
      for (const [size,width,height] of [['full',1600,1000],['compact',980,640]]) {
        preview.setSize(width,height);
        await delay(200);
        const shortcuts=await checkTransportShortcuts(frame,'vp',{exercise:size==='full',label:`vod-${size}`});
        await frame.executeJavaScript(`scrollTo({top:0,behavior:'instant'})`);
        const state=await frame.executeJavaScript(`(()=>{
          const title=document.querySelector('#vp-title'),metadata=document.querySelector('#vp-ctx-details');
          const rect=element=>{const r=element.getBoundingClientRect();return {x:r.x,y:r.y,right:r.right,bottom:r.bottom,width:r.width,height:r.height};};
          const tabs=[...document.querySelectorAll('.vp-objtab')].map(tab=>{
            const name=tab.querySelector('.vp-objtab-name'),label=tab.querySelector('.vp-objtab-type');
            return {active:tab.classList.contains('is-active'),name:name?.textContent,label:label?.textContent,
              nameSize:parseFloat(getComputedStyle(name).fontSize),labelSize:parseFloat(getComputedStyle(label).fontSize),
              card:rect(tab),title:rect(name)};
          });
          return {tag:title?.tagName,title:title?.textContent?.trim(),titleSize:parseFloat(getComputedStyle(title).fontSize),
            metadata:metadata?.textContent?.trim(),metadataSize:parseFloat(getComputedStyle(metadata).fontSize),tabs,
            overflow:document.documentElement.scrollWidth-document.documentElement.clientWidth};
        })()`);
        result[size]={...state,shortcuts};
        await writeFile(path.join(dataRoot,`ui-sample-vod-heading-${size}.json`),JSON.stringify(state,null,2));
        assert.equal(state.tag,'H1',`VOD ${size} matchup must be a real heading`);
        assert.ok(state.title && !state.title.includes('Loading'),'Populated VOD matchup title must render');
        assert.equal(state.titleSize,28,`VOD ${size} heading size`);
        assert.ok(state.metadata);assert.equal(state.metadataSize,14,`VOD ${size} supporting metadata size`);
        assert.ok(state.tabs.length>0,`VOD ${size} fixture must contain objective tabs`);
        const active=state.tabs.filter(tab=>tab.active);
        assert.equal(active.length,1,`VOD ${size} must identify one active objective`);
        assert.ok(active[0].labelSize>=12 && active[0].nameSize>=16,`VOD ${size} objective text must remain readable: ${JSON.stringify(active[0])}`);
        for(const tab of state.tabs) {
          assert.ok(tab.name?.trim() && tab.title.width>0 && tab.title.height>0);
          assert.ok(tab.title.x>=tab.card.x-1 && tab.title.y>=tab.card.y-1 && tab.title.right<=tab.card.right+1 && tab.title.bottom<=tab.card.bottom+1,
            `VOD ${size} objective title must fit its card: ${JSON.stringify(tab)}`);
        }
        assert.ok(state.overflow<=1,`VOD ${size} horizontal overflow: ${state.overflow}`);
        if(size==='compact') {
          result.compactFile='ui-sample-vodplayer-compact.png';
          await writeFile(path.join(dataRoot,result.compactFile),(await preview.webContents.capturePage()).toPNG());
        }
      }
    } finally {
      preview.setSize(1600,1000);await delay(200);
    }
    return result;
  }
  let child = await navigate('dashboard.html');
  await capture('home');
  const home = await layout(child, 'Home');
  const guide = await child.executeJavaScript(`(async()=>{
    document.querySelector('#nextstep-cta').click();
    const input=document.querySelector('.intent-input');
    if(!input) throw new Error('Session editor did not open');
    input.value='Keep my unfinished focus';
    const original=location.href;
    await window.RevuFirstReviewTutorial.start();
    const result={inline:getComputedStyle(document.querySelector('#review-guide')).position!=='fixed',
      steps:document.querySelectorAll('.review-guide-item').length,
      expanded:document.querySelector('#review-guide').open,
      keptDraft:input.isConnected&&input.value==='Keep my unfinished focus',samePage:location.href===original};
    return result;
  })()`);
  assert.deepEqual(guide, {inline:true,steps:3,expanded:true,keptDraft:true,samePage:true});
  await capture('guide');
  await child.executeJavaScript(`window.RevuFirstReviewTutorial.dismiss()`);
  await child.executeJavaScript(`document.querySelector('.intent-coach').click()`);
  const coachPoint = await contents.executeJavaScript(`(() => {
    const frame=document.querySelector('#app-frame');
    const rect=frame.contentDocument.querySelector('.intent-coach').getBoundingClientRect();
    const offset=frame.getBoundingClientRect();
    return {x:Math.round(offset.x+rect.x+rect.width/2),y:Math.round(offset.y+rect.y+rect.height/2)};
  })()`);
  contents.sendInputEvent({type:'mouseMove',...coachPoint});
  await capture('coach-selected-hover');
  contents.sendInputEvent({type:'mouseMove',x:0,y:0});
  await capture('coach-selected');
  await child.executeJavaScript(`document.querySelector('.intent-coach').click()`);
  await capture('coach-unselected');
  child = await navigate('settings.html');
  const settings = await child.executeJavaScript(`(async()=>{
    const deadline=Date.now()+10000;
    while(Date.now()<deadline && !document.querySelector('#recording-status')?.textContent.includes('isolated preview')) await new Promise(r=>setTimeout(r,100));
    const visible=()=>[...document.querySelectorAll('[data-settings-section]')].filter(el=>!el.hidden).map(el=>el.dataset.settingsSection);
    const initial=visible();
    const nativeSaveDisabled=document.querySelector('#save-recording-settings').disabled;
    const search=document.querySelector('#settings-search');
    search.value='backup'; search.dispatchEvent(new Event('input',{bubbles:true}));
    const found=visible();
    const matches=[...document.querySelectorAll('[data-settings-card]')].filter(el=>el.getClientRects().length>0).length;
    search.value='no-setting-matches-this-phrase'; search.dispatchEvent(new Event('input',{bubbles:true}));
    const empty=!document.querySelector('#settings-search-empty').hidden;
    document.querySelector('#settings-search-clear').click();
    document.querySelector('[data-settings-category="playback"]').click();
    const field=document.querySelector('#clipsMaxSizeMb'); field.value='4321'; field.dispatchEvent(new Event('input',{bubbles:true}));
    document.querySelector('[data-settings-category="appearance"]').click();
    document.querySelector('[data-settings-category="playback"]').click();
    const keptDraft=field.value==='4321';
    const card=field.closest('[data-settings-card]');
    card.querySelector('[data-action="discard_config"]').click();
    const discarded=field.value!=='4321';
    document.querySelector('[data-settings-category="recording"]').click();
    return {initial,found,matches,empty,keptDraft,discarded,nativeSaveDisabled};
  })()`);
  assert.deepEqual(settings.initial,['recording']);
  assert.ok(settings.matches > 0 && settings.found.includes('data'));
  assert.equal(settings.empty,true); assert.equal(settings.keptDraft,true); assert.equal(settings.discarded,true);
  assert.equal(settings.nativeSaveDisabled,true);
  await capture('settings-recording');
  await layout(child,'Settings');
  await child.executeJavaScript(`document.querySelector('[data-settings-category="appearance"]').click()`);
  await capture('settings-appearance');
  await child.executeJavaScript(`const s=document.querySelector('#settings-search');s.value='backup';s.dispatchEvent(new Event('input',{bubbles:true}))`);
  await capture('settings-search');
  const pages = {};
  for (const page of ['games','objectives','patterns','matchups','tiltcheck','rules','onboarding']) {
    child=await navigate(`${page}.html`);
    pages[page]=await layout(child,page);
    if(page==='objectives') {
      await child.executeJavaScript(`document.querySelector('[data-action="new_objective"]')?.click()`);
      const picker = await child.executeJavaScript(`(() => {
        const select=document.querySelector('#f-type');
        select.focus(); select.showPicker();
        return {open:select.matches(':open'),appearance:getComputedStyle(select).appearance,
          background:getComputedStyle(select,'::picker(select)').backgroundColor};
      })()`,true);
      assert.equal(picker.open,true); assert.equal(picker.appearance,'base-select');
      assert.equal(picker.background,'rgb(43, 43, 43)');
      await capture('dropdown-open');
      contents.sendInputEvent({type:'keyDown',keyCode:'Down'});
      contents.sendInputEvent({type:'keyUp',keyCode:'Down'});
      contents.sendInputEvent({type:'keyDown',keyCode:'Return'});
      contents.sendInputEvent({type:'keyUp',keyCode:'Return'});
      await delay(100);
      const selection=await child.executeJavaScript(`({value:document.querySelector('#f-type').value,open:document.querySelector('#f-type').matches(':open')})`);
      assert.equal(selection.value,'mental'); assert.equal(selection.open,false);
      pages.dropdown={...picker,...selection};
    }
    await capture(page);
    if(page==='tiltcheck') await captureResetSteps(child);
    if(page==='onboarding') {
      child=await navigate('onboarding.html?signin=1');
      const point=await contents.executeJavaScript(`(() => {
        const frame=document.querySelector('#app-frame');
        const rect=frame.contentDocument.querySelector('#email-back').getBoundingClientRect();
        const offset=frame.getBoundingClientRect();
        return {x:Math.round(offset.x+rect.x+rect.width/2),y:Math.round(offset.y+rect.y+rect.height/2)};
      })()`);
      contents.sendInputEvent({type:'mouseMove',...point});
      await capture('onboarding-back-hover');
      contents.sendInputEvent({type:'mouseMove',x:0,y:0});
    }
  }
  window.setSize(980,640);
  await delay(200);
  for(const page of ['dashboard','settings','objectives','tiltcheck']) {
    child=await navigate(`${page}.html`);
    pages[`${page}Compact`]=await layout(child,`${page} compact`);
    await capture(`${page}-compact`);
    if(page==='tiltcheck') await captureResetSteps(child,'-compact');
  }
  window.setSize(1600,1000);
  const navigation = await contents.executeJavaScript(`({labels:[...document.querySelectorAll('.nav-i .tip')].map(n=>n.textContent),active:document.querySelectorAll('.nav-i[aria-current="page"]').length})`);
  assert.equal(navigation.active,1); assert.ok(navigation.labels.includes('Match library'));
  const samplePages = [];
  let matchWorkspace;
  // A second sandbox has no preload/host bridge. Existing browser fixtures let
  // us inspect populated screens without adding sample matches to user data.
  const preview = new BrowserWindow({width:1600,height:1000,show:false,frame:false,
    webPreferences:{session:contents.session,sandbox:true,contextIsolation:true,nodeIntegration:false,backgroundThrottling:false}});
  preview.showInactive();
  try {
    for (const page of ['dashboard.html','objectives.html','patterns.html','objectivegames.html?id=7','objectivenotes.html?id=7',
      'review.html?gameId=5581701721','vodplayer.html?gameId=5581701721']) {
      await preview.loadURL(`revu-app://ui/index.html#${page}`);
      await delay(800);
      const frame = preview.webContents.mainFrame.frames.find(f=>f.url.includes(page.split('?')[0]));
      assert.ok(frame,`Sample ${page} must open`);
      await frame.executeJavaScript(`document.fonts.ready.then(()=>true)`);
      if(page.startsWith('review.')) await frame.executeJavaScript(`(async()=>{
        const deadline=Date.now()+10000;
        while(Date.now()<deadline && document.querySelector('#rv-body')?.hidden) await new Promise(r=>setTimeout(r,100));
        if(document.querySelector('#rv-body')?.hidden) throw new Error('Sample review did not render');
      })()`);
      let speedPicker;
      let patternsPlayer;
      let vodHeading;
      if(page==='patterns.html') patternsPlayer=await checkPatternPlayer(preview,frame);
      if(page.startsWith('vodplayer.')) {
        const before=await frame.executeJavaScript(`(() => {
          window.__pickerKeys=[];
          document.addEventListener('keydown',e=>window.__pickerKeys.push(e.target.tagName),true);
          const select=document.querySelector('#vp-rate-sel');select.focus();select.showPicker();
          return {step:document.querySelector('#vp-time .step')?.textContent,value:select.value};
        })()`,true);
        preview.webContents.sendInputEvent({type:'keyDown',keyCode:'Down'});
        preview.webContents.sendInputEvent({type:'keyUp',keyCode:'Down'});
        preview.webContents.sendInputEvent({type:'keyDown',keyCode:'Return'});
        preview.webContents.sendInputEvent({type:'keyUp',keyCode:'Return'});
        await delay(100);
        speedPicker=await frame.executeJavaScript(`({step:document.querySelector('#vp-time .step')?.textContent,
          value:document.querySelector('#vp-rate-sel').value,rate:document.querySelector('#vp-video').playbackRate,
          keyTargets:window.__pickerKeys})`);
        assert.equal(speedPicker.step,before.step,'Speed selection must not trigger seek-step shortcuts');
        assert.notEqual(speedPicker.value,before.value);
        assert.equal(speedPicker.rate,Number(speedPicker.value));
        vodHeading=await checkVodHeading(preview,frame);
      }
      const state = await frame.executeJavaScript(`({title:document.title,hasBridge:!!window.top.revuDesktop,
        overflow:document.documentElement.scrollWidth-document.documentElement.clientWidth,
        error:document.querySelector('#errpanel:not([hidden])')?.textContent?.trim()||'',
        reflectionOpen:document.querySelector('#rv-reflection')?.open,
        goalsOpen:document.querySelector('#rv-objsec')?.open,
        reviewOptionsOpen:document.querySelector('#rv-more-options')?.open,
        matchDetailsOpen:document.querySelector('#rv-match-details')?.open,
        reviewLaunchpad:(()=>{
          const launch=document.querySelector('#rv-launchpad'),button=launch?.querySelector('#rv-open-vod');
          const heading=launch&&document.getElementById(launch.getAttribute('aria-labelledby'));
          const bounds=button?.getBoundingClientRect();
          return launch?{first:document.querySelector('#rv-body')?.firstElementChild===launch,
            tag:launch.tagName,headingTag:heading?.tagName,heading:heading?.textContent?.trim(),
            action:button?.dataset.action,label:button?.textContent?.trim(),disabled:button?.disabled,
            visible:!!bounds&&bounds.width>0&&bounds.height>0&&getComputedStyle(button).visibility!=='hidden'}:null;
        })()})`);
      assert.equal(state.hasBridge,false);
      if(page.startsWith('review.')) {
        assert.equal(state.error,'');
        assert.equal(state.reflectionOpen,false);
        assert.equal(state.goalsOpen,false);
        assert.equal(state.reviewOptionsOpen,false);
        assert.equal(state.matchDetailsOpen,false);
        assert.equal(state.reviewLaunchpad?.first,true,'Review starts with the recording launchpad');
        assert.equal(state.reviewLaunchpad.tag,'SECTION');
        assert.equal(state.reviewLaunchpad.headingTag,'H2');
        assert.ok(state.reviewLaunchpad.heading,'The launchpad has an accessible heading');
        assert.equal(state.reviewLaunchpad.action,'review_vod');
        assert.match(state.reviewLaunchpad.label,/Review in VOD/);
        assert.equal(state.reviewLaunchpad.disabled,false);
        assert.equal(state.reviewLaunchpad.visible,true,'The primary VOD action is visible');
      }
      else if(page.startsWith('vodplayer.')) assert.match(state.error,/local video could not be opened/);
      else assert.equal(state.error,'');
      assert.ok(state.overflow<=1,`${page} overflow`);
      await delay(100);
      const file=`ui-sample-${page.split('.')[0]}.png`;
      await writeFile(path.join(dataRoot,file),(await preview.webContents.capturePage()).toPNG());
      samplePages.push({...state,file,speedPicker,patternsPlayer,vodHeading});
    }
    matchWorkspace = await checkMatchWorkspace(preview, dataRoot, await grantFixture());
  } finally { preview.destroy(); }
  return {home,guide,settings,pages,navigation,captures,samplePages,matchWorkspace,
    limitations:['Guide persistence and settings saves blocked by isolated host policy; search, drafts, discard and guide presentation exercised.',
      'Populated screenshots use existing browser sample snapshots; they do not verify real match capture or review persistence.',
      'Patterns expanded-player checks use synthetic in-memory video to verify layout, keyboard focus and restoration; native capture and file decoding are not exercised.']};
}
