import assert from 'node:assert/strict';
import path from 'node:path';
import { writeFile } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';

// Exercise production pages and real file decoding in the scratch sandbox.
// Only the backend responses are fixtures; no production profile writes occur.
export async function checkMatchWorkspace(preview, dataRoot, mediaUrl) {
  const contents = preview.webContents;
  await preview.loadURL('revu-app://ui/index.html#review.html?gameId=5581701721');
  await contents.executeJavaScript(`(async()=>{
    const read=async name=>(await fetch('./sample-'+name+'.json')).json();
    const [review,vod,objectives]=await Promise.all(['review','vod','objectives'].map(read));
    vod.filePath='synthetic ü space.mp4';vod.gameDurationSeconds=3;vod.videoTimeOriginSeconds=0;
    const active=[...objectives.activeObjectives,...(objectives.focusObjectives||[])].map(o=>({...o,objectiveId:o.id}));
    window.__workspaceWrites=[];
    window.revuDesktop={version:1,kind:'electron',capabilities:{commands:true,media:true},
      resolveMedia:file=>file===vod.filePath?${JSON.stringify(mediaUrl)}:null,
      invoke:async(command,args)=>{
        if(command==='get_review')return structuredClone(review);
        if(command==='get_vod')return structuredClone(vod);
        if(command==='get_active_objectives')return {objectives:active};
        if(command==='get_config')return {};
        if(command==='get_auth_status')return {signedIn:false};
        if(command==='save_review_draft'||command==='save_prompt_answer'){window.__workspaceWrites.push({command,args});return {};}
        if(command==='add_bookmark'||command==='extract_clip'){
          window.__workspaceWrites.push({command,args});
          if(window.__holdWorkspaceCommand===command)await new Promise(resolve=>{window.__releaseWorkspaceWrite=resolve;});
          return {ok:true};
        }
        throw new Error('Unexpected workspace fixture command: '+command);
      }};
    const f=document.querySelector('#app-frame');
    await new Promise(resolve=>{f.addEventListener('load',resolve,{once:true});f.src='review.html?gameId=5581701721';});
  })()`);
  async function ready(view) {
    const deadline=Date.now()+10000;
    while(Date.now()<deadline) {
      const frame=contents.mainFrame.frames.find(f=>f.url.includes('/'+(view==='vod'?'vodplayer':'review')+'.html'));
      if(frame && await frame.executeJavaScript(`(()=>{
        const nav=document.querySelector('#match-navigation');
        return nav&&!nav.hidden&&${view==='vod' ? "document.querySelector('#vp-video').readyState>=1" : "!document.querySelector('#rv-body').hidden"}
          && (!new URLSearchParams(location.search).has('resume') || !window.top.__revuMatchViewsV1?.peek(5581701721,${JSON.stringify(view)}));
      })()`)) { await delay(100); return frame; }
      await delay(50);
    }
    const diagnostic=await contents.executeJavaScript(`(()=>{const d=document.querySelector('#app-frame')?.contentDocument;return {url:d?.location.href,error:d?.querySelector('#err-detail')?.textContent,status:d?.querySelector('[data-match-status]')?.textContent};})()`);
    throw new Error('Match workspace did not become ready: '+view+' '+JSON.stringify(diagnostic));
  }
  async function switchView(view, explicit=false, { primary=false }={}) {
    assert.ok(!primary||(view==='vod'&&!explicit),'The primary launch action opens the remembered VOD');
    await contents.executeJavaScript(`new Promise((resolve,reject)=>{
      const f=document.querySelector('#app-frame');const timer=setTimeout(()=>reject(new Error('Switch timeout')),10000);
      f.addEventListener('load',()=>{clearTimeout(timer);resolve();},{once:true});
      const link=f.contentDocument.querySelector(${JSON.stringify(primary?'#rv-open-vod':`[data-match-view="${view}"]`)});
      if(!link||link.disabled||!link.getClientRects().length){clearTimeout(timer);reject(new Error('View action must be visible and enabled'));return;}
      ${explicit ? "link.href+='&t=0';" : ''}link.click();
    })`);
    return ready(view);
  }
  async function revealClipTools(frame, { initiallyClosed=false }={}) {
    const state=await frame.executeJavaScript(`(()=>{
      const tools=document.querySelector('#vp-clip-tools'),summary=tools?.querySelector(':scope > summary');
      const wasOpen=tools?.open;
      if(!wasOpen)summary?.click();
      return {wasOpen,hasSummary:!!summary,open:tools?.open,
        controls:['vp-clip-in','vp-clip-out','vp-clip-note','vp-clip-save'].map(id=>{
          const control=document.getElementById(id),style=control&&getComputedStyle(control);
          return {id,visible:!!control?.getClientRects().length&&style.visibility!=='hidden'&&style.display!=='none'};
        })};
    })()`);
    if(initiallyClosed)assert.equal(state.wasOpen,false,'Clip tools start collapsed');
    assert.equal(state.hasSummary,true,'Clip tools expose a summary control');
    assert.equal(state.open,true,'Clicking the summary opens clip tools');
    for(const control of state.controls)assert.equal(control.visible,true,control.id+' is visible before interaction');
    return state;
  }
  const switchPositions=[];
  async function switchGeometry(frame,view,label,{probeScroll=false}={}) {
    const geometry=await frame.executeJavaScript(`(()=>{
      const nav=document.querySelector('#match-navigation'),control=nav?.querySelector('.match-view-switch');
      if(!nav||!control)throw new Error('The shared match switch must be present');
      const bounds=element=>{const r=element.getBoundingClientRect();return {left:r.left,top:r.top,width:r.width,height:r.height};};
      const read=()=>({width:innerWidth,height:innerHeight,scroll:scrollY,
        overflow:document.documentElement.scrollWidth-document.documentElement.clientWidth,
        top:nav.getBoundingClientRect().top,position:getComputedStyle(nav).position,directChild:nav.parentElement===document.body,
        active:nav.querySelector('[aria-current="page"]')?.dataset.matchView,switchBounds:bounds(control),
        links:[...control.querySelectorAll('[data-match-view]')].map(link=>({view:link.dataset.matchView,text:link.textContent,
          font:getComputedStyle(link).fontSize,...bounds(link)}))});
      const current=read(),left=scrollX;
      if(!${probeScroll})return {current};
      try {
        scrollTo({left,top:0,behavior:'instant'});const atTop=read();
        scrollTo({left,top:Math.max(current.scroll,300),behavior:'instant'});const scrolled=read();
        return {current,atTop,scrolled};
      } finally {scrollTo({left,top:current.scroll,behavior:'instant'});}
    })()`);
    switchPositions.push({view,label,...geometry});
    // Write before asserting so any native failure retains both pages' actual
    // switch bounds and scroll positions, rather than only a boolean failure.
    await writeFile(path.join(dataRoot,'ui-match-switch-geometry.json'),JSON.stringify(switchPositions,null,2));
    for(const measured of Object.values(geometry)) {
      const context=label+' '+JSON.stringify(measured);
      assert.equal(measured.directChild,true,'Match navigation is outside the content shell: '+context);
      assert.equal(measured.position,'sticky',context);assert.ok(Math.abs(measured.top)<=1,'Navigation remains at viewport top: '+context);
      assert.equal(measured.active,view,context);assert.ok(measured.overflow<=1,'No horizontal overflow: '+context);
      for(const link of measured.links){assert.equal(link.font,'15px',context);assert.ok(link.height>=38,context);}
    }
    if(probeScroll) {
      assert.equal(geometry.atTop.scroll,0,label+' top probe reaches the document top');
      assert.ok(geometry.scrolled.scroll>0,label+' fixture must actually scroll');
      sameSwitchPosition(geometry.atTop,geometry.scrolled,label+' top versus scrolled');
      sameSwitchPosition(geometry.atTop,geometry.current,label+' top versus restored/current scroll');
    }
    return geometry;
  }
  function sameSwitchPosition(a,b,label) {
    const context=label+' '+JSON.stringify({before:a,after:b});
    assert.equal(a.width,b.width,'Comparison uses the same viewport width: '+context);
    for(const key of ['left','top','width','height']) {
      assert.ok(Math.abs(a.switchBounds[key]-b.switchBounds[key])<=1,'Match switch '+key+' stays fixed: '+context);
    }
    for(const first of a.links) {
      const second=b.links.find(link=>link.view===first.view);
      assert.ok(second,'Both match-view controls remain present: '+context);
      for(const key of ['left','top','width','height'])assert.ok(Math.abs(first[key]-second[key])<=1,
        first.view+' link '+key+' stays fixed when the active page changes: '+context);
    }
  }
  let frame=await ready('review');
  const reviewTop=(await switchGeometry(frame,'review','review-initial-top')).current;
  const reviewOptions=await frame.executeJavaScript(`(()=>{
    const options=document.querySelector('#rv-more-options'),summary=options?.querySelector(':scope > summary');
    const wasOpen=options?.open;summary?.click();
    const tags=document.querySelector('#rv-tag-input');
    return {wasOpen,hasSummary:!!summary,open:options?.open,tagsVisible:!!tags?.getClientRects().length};
  })()`);
  assert.deepEqual(reviewOptions,{wasOpen:false,hasSummary:true,open:true,tagsVisible:true},'Review options open before editing tags');
  const expectedReview=await frame.executeJavaScript(`(()=>{
    for(const id of ['rv-reflection','rv-objsec']) {
      const disclosure=document.getElementById(id);
      if(!disclosure.open)disclosure.querySelector(':scope > summary').click();
      if(!disclosure.open)throw new Error(id+' must open before editing the review');
    }
    const field=document.querySelector('#rv-fields .rv-field-in');field.value='  Keep this unfinished review.  ';
    field.dispatchEvent(new Event('input',{bubbles:true}));
    document.querySelector('#rv-tag-input').value='unfinished tag';
    const note=document.querySelector('.rv-objnote');if(note){note.value='  Objective draft  ';note.dispatchEvent(new Event('input',{bubbles:true}));}
    scrollTo({top:400,behavior:'instant'});
    return {field:field.value,tag:document.querySelector('#rv-tag-input').value,note:note?.value,scroll:scrollY,
      moreOptions:document.querySelector('#rv-more-options').open,matchDetails:document.querySelector('#rv-match-details').open};
  })()`);
  const reviewScrolled=(await switchGeometry(frame,'review','review-before-departure')).current;
  assert.ok(reviewScrolled.scroll>0,'Review fixture starts its roundtrip from a scrolled position');
  sameSwitchPosition(reviewTop,reviewScrolled,'Review initial top versus saved scroll');
  frame=await switchView('vod',false,{primary:true});
  const vodTop=(await switchGeometry(frame,'vod','vod-initial-top')).current;
  sameSwitchPosition(reviewTop,vodTop,'Review and VOD at initial page positions');
  const clipTools=await revealClipTools(frame,{initiallyClosed:true});
  const expectedVod=await frame.executeJavaScript(`(async()=>{
    const v=document.querySelector('#vp-video');v.pause();v.muted=true;v.volume=.4;
    const rate=document.querySelector('#vp-rate-sel');rate.value='1.5';rate.dispatchEvent(new Event('change',{bubbles:true}));
    document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowUp',bubbles:true,cancelable:true}));
    const seek=async time=>{const done=new Promise(resolve=>v.addEventListener('seeked',resolve,{once:true}));v.currentTime=time;await done;};
    await seek(.25);document.querySelector('#vp-clip-in').click();await seek(2);document.querySelector('#vp-clip-out').click();
    await seek(1.375);
    document.querySelector('#vp-bm-note').value='  Bookmark draft  ';document.querySelector('#vp-clip-note').value='  Clip draft  ';
    const cards=[...document.querySelectorAll('.vp-objtab[data-obj-id]')],next=cards.find(card=>!card.classList.contains('is-active'));
    if(!next)throw new Error('Workspace fixture needs another objective to exercise tab restoration');
    next.click();
    if(document.querySelector('.vp-objtab.is-active').dataset.objId!==next.dataset.objId)
      throw new Error('Selecting another objective must activate its tab before switching views');
    for(const [id,open] of [['vp-event-tools',true],['vp-saved-moments',false]]) {
      const tools=document.getElementById(id);
      if(tools.open!==open)tools.querySelector(':scope > summary').click();
    }
    scrollTo({top:300,behavior:'instant'});
    return {time:v.currentTime,rate:v.playbackRate,muted:v.muted,volume:v.volume,step:document.querySelector('#vp-time .step').textContent,
      bookmark:document.querySelector('#vp-bm-note').value,clip:document.querySelector('#vp-clip-note').value,
      objectiveId:document.querySelector('.vp-objtab.is-active').dataset.objId,
      disclosures:Object.fromEntries(['vp-clip-tools','vp-event-tools','vp-saved-moments'].map(id=>[id,document.getElementById(id).open])),
      range:document.querySelector('#vp-clip-range').textContent,scroll:scrollY};
  })()`);
  assert.ok(expectedVod.objectiveId,'The selected objective has a stable identity');
  assert.deepEqual(expectedVod.disclosures,{'vp-clip-tools':true,'vp-event-tools':true,'vp-saved-moments':false});
  const vodScrolled=(await switchGeometry(frame,'vod','vod-before-departure')).current;
  assert.ok(vodScrolled.scroll>0,'VOD fixture starts its roundtrip from a scrolled position');
  sameSwitchPosition(vodTop,vodScrolled,'VOD initial top versus saved scroll');
  frame=await switchView('review');
  const review=await frame.executeJavaScript(`({field:document.querySelector('#rv-fields .rv-field-in').value,
    tag:document.querySelector('#rv-tag-input').value,note:document.querySelector('.rv-objnote')?.value,scroll:scrollY,
    moreOptions:document.querySelector('#rv-more-options').open,matchDetails:document.querySelector('#rv-match-details').open,
    reflection:document.querySelector('#rv-reflection').open,objectives:document.querySelector('#rv-objsec').open})`);
  assert.deepEqual(review,{...expectedReview,reflection:true,objectives:true},'Review drafts and location survive switching');
  const reviewRestored=(await switchGeometry(frame,'review','review-restored-scroll')).current;
  sameSwitchPosition(reviewScrolled,reviewRestored,'Review switch after restoring its own scroll');
  sameSwitchPosition(vodScrolled,reviewRestored,'VOD to Review with different restored scroll positions');
  assert.equal(await frame.executeJavaScript(`(()=>{
    const options=document.querySelector('#rv-more-options');options.querySelector(':scope > summary').click();return options.open;
  })()`),false,'Review options can be collapsed with an unfinished tag');
  frame=await switchView('vod',false,{primary:true});
  const vod=await frame.executeJavaScript(`(()=>{const v=document.querySelector('#vp-video');return {
    time:v.currentTime,rate:v.playbackRate,muted:v.muted,volume:v.volume,step:document.querySelector('#vp-time .step').textContent,
    bookmark:document.querySelector('#vp-bm-note').value,clip:document.querySelector('#vp-clip-note').value,
    objectiveId:document.querySelector('.vp-objtab.is-active').dataset.objId,
    disclosures:Object.fromEntries(['vp-clip-tools','vp-event-tools','vp-saved-moments'].map(id=>[id,document.getElementById(id).open])),
    range:document.querySelector('#vp-clip-range').textContent,scroll:scrollY,paused:v.paused};})()`);
  assert.ok(Math.abs(vod.time-expectedVod.time)<.05,'Exact media position survives switching');
  assert.deepEqual({...vod,time:expectedVod.time},{...expectedVod,paused:true});
  const vodRestored=(await switchGeometry(frame,'vod','vod-restored-scroll')).current;
  sameSwitchPosition(vodScrolled,vodRestored,'VOD switch after restoring its own scroll');
  sameSwitchPosition(reviewRestored,vodRestored,'Review to VOD with different restored scroll positions');
  const layouts=[];
  for(const [width,height] of [[1600,1000],[1000,800]]) {
    preview.setSize(width,height);await delay(200);
    const vodGeometry=await switchGeometry(frame,'vod','vod-'+width,{probeScroll:true});
    frame=await switchView('review');
    const reviewGeometry=await switchGeometry(frame,'review','review-'+width,{probeScroll:true});
    sameSwitchPosition(vodGeometry.atTop,reviewGeometry.atTop,'Both pages at top, width '+width);
    sameSwitchPosition(vodGeometry.scrolled,reviewGeometry.scrolled,'Both pages scrolled, width '+width);
    frame=await switchView('vod');
    const geometry=(await switchGeometry(frame,'vod','vod-return-'+width)).current;
    sameSwitchPosition(vodGeometry.current,geometry,'VOD position after same-size page switch, width '+width);
    assert.ok(Math.abs(geometry.scroll-vodGeometry.current.scroll)<=1,'Geometry probes preserve the VOD scroll at width '+width);
    const file='ui-match-workspace-'+width+'.png';await writeFile(path.join(dataRoot,file),(await contents.capturePage()).toPNG());
    layouts.push({...geometry,file,review:reviewGeometry,vod:vodGeometry});
  }
  preview.setSize(1600,1000);await delay(100);
  frame=await switchView('review');
  const collapsedReview=await frame.executeJavaScript(`({moreOptions:document.querySelector('#rv-more-options').open,
    matchDetails:document.querySelector('#rv-match-details').open,tag:document.querySelector('#rv-tag-input').value})`);
  assert.deepEqual(collapsedReview,{moreOptions:false,matchDetails:false,tag:expectedReview.tag},'Collapsed Review options and their draft survive returning from VOD');
  frame=await switchView('vod',true);
  const explicit=await frame.executeJavaScript(`(()=>{const v=document.querySelector('#vp-video');v.pause();return {time:v.currentTime,scroll:scrollY,
    bookmark:document.querySelector('#vp-bm-note').value,clip:document.querySelector('#vp-clip-note').value};})()`);
  assert.ok(explicit.time<.75,'A t=0 moment overrides the remembered frame');assert.equal(explicit.scroll,0);
  assert.equal(explicit.bookmark,expectedVod.bookmark);assert.equal(explicit.clip,expectedVod.clip);
  const pendingSaves=[];
  for(const [command,button,field] of [['add_bookmark','vp-bm-add','vp-bm-note'],['extract_clip','vp-clip-save','vp-clip-note']]) {
    if(command==='extract_clip')await revealClipTools(frame);
    await contents.executeJavaScript(`window.__holdWorkspaceCommand=${JSON.stringify(command)};window.__releaseWorkspaceWrite=null;
      document.querySelector('#app-frame').contentDocument.getElementById(${JSON.stringify(button)}).click();`);
    for(let tries=0;tries<100 && !await contents.executeJavaScript('!!window.__releaseWorkspaceWrite');tries++)await delay(25);
    assert.equal(await contents.executeJavaScript('!!window.__releaseWorkspaceWrite'),true,command+' must start');
    const departing=contents.executeJavaScript(`new Promise(resolve=>{
      const f=document.querySelector('#app-frame');f.addEventListener('load',resolve,{once:true});
      f.contentDocument.querySelector('[data-match-view="review"]').click();
    })`);
    await delay(100);
    const pending=await contents.executeJavaScript(`({page:document.querySelector('#app-frame').contentDocument.location.pathname,
      status:document.querySelector('#app-frame').contentDocument.querySelector('[data-match-status]').textContent})`);
    assert.equal(pending.page,'/vodplayer.html',command+' must finish before departure');assert.ok(pending.status);
    await contents.executeJavaScript('window.__releaseWorkspaceWrite();window.__holdWorkspaceCommand=null;');
    await departing;await ready('review');frame=await switchView('vod');
    const value=await frame.executeJavaScript(`document.getElementById(${JSON.stringify(field)}).value`);
    assert.equal(value,'',command+' must not resurrect a submitted draft');pendingSaves.push({command,...pending,restoredDraft:value});
  }
  const iframeCount=await contents.executeJavaScript('document.querySelectorAll("iframe").length');assert.equal(iframeCount,1);
  const result={review,reviewOptions,collapsedReview,vod,explicit,layouts,switchPositions,pendingSaves,clipTools,iframeCount,media:'Synthetic 3-second H.264/AAC file via the real media handler; fixture backend only.'};
  await writeFile(path.join(dataRoot,'ui-match-workspace.json'),JSON.stringify(result,null,2));
  return result;
}
