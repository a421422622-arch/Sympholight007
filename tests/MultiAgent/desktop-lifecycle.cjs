const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const dgram=require('node:dgram');
const {_electron}=require(process.env.SYMPHOCUE_PLAYWRIGHT || 'playwright');
const exe=path.resolve(process.env.SYMPHOCUE_TEST_EXE);
const scratch=fs.mkdtempSync(path.join(require('node:os').tmpdir(),'symphocue-tray-'));
const data=path.join(scratch,'data');
const receiver=dgram.createSocket('udp4');
const packets=[];
let desktop;
receiver.on('message',msg=>packets.push(msg.toString()));
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function until(predicate,ms=10000){
  const deadline=Date.now()+ms;
  do { if(await predicate())return; await sleep(100); } while(Date.now()<deadline);
  throw new Error('Condition did not complete in '+ms+'ms');
}
(async()=>{
  await new Promise(resolve=>receiver.bind(0,'127.0.0.1',resolve));
  desktop=await _electron.launch({executablePath:exe,args:['--user-data-dir='+path.join(scratch,'profile')],env:{...process.env,SHOWCONTROL_DATA_DIR:data}});
  const page=await desktop.firstWindow();
  await page.locator('#mode.live').waitFor();
  const base=page.url();
  let cookie='';
  const request=async(route,method='GET',body)=>{
    const response=await fetch(new URL('/api'+route,base),{method,
      headers:{'Content-Type':'application/json',Cookie:cookie},body:body===undefined?undefined:JSON.stringify(body)});
    return response;
  };
  const api=async(route,method,body)=>{
    const res=await request(route,method,body);
    assert.ok(res.ok,route+' '+res.status+' '+(await res.clone().text()));
    return res.json();
  };
  const view=async()=>({...(await api('/status'))});
  const close=async(answer)=>{
    await desktop.evaluate(({BrowserWindow},answer)=>{
      globalThis.closeAnswer=answer;
      globalThis.closeArgs=null;
      BrowserWindow.getAllWindows()[0].close();
    },answer);
    await until(()=>desktop.evaluate(()=>!!globalThis.closeArgs));
    return desktop.evaluate(()=>globalThis.closeArgs);
  };
  const visible=()=>desktop.evaluate(({BrowserWindow})=>BrowserWindow.getAllWindows()[0].isVisible());
  await desktop.evaluate(({dialog})=>{
    dialog.showMessageBox=async(_win, options)=>{
      globalThis.closeArgs=options;
      const response=options.buttons.indexOf(globalThis.closeAnswer);
      return {response:response<0?options.cancelId:response};
    };
    dialog.showMessageBoxSync=()=>0;
  });
  const project=(await api('/project'));
  const node={ip:'127.0.0.1',label:'Loopback',communication:{targetPort:receiver.address().port}};
  const cue=(id,payload)=>({id,name:id,ipGroupId:'targets',payload,encoding:'utf8',ping:false,onFail:'stop',retries:0,response:{mode:'none'}});
  // Give setup at least 20 seconds before the scheduled tick.
  let next=new Date(Date.now()+60000);
  if(next.getSeconds()>40)next=new Date(next.getTime()+60000);
  const time=[next.getHours(),next.getMinutes()].map(n=>String(n).padStart(2,'0')).join(':');
  Object.assign(project.project,{nodeLibrary:[node],ipGroups:[{id:'targets',name:'targets',nodes:[node]}],
    shows:[{id:'show',name:'Show',groups:[{id:'group',name:'Group',lists:[
      {id:'loop-list',name:'Loop',cues:[cue('loop-cue','LOOP')],playbackMode:'loop',intervalMs:480,playbackGroup:'loop'},
      {id:'scheduled-list',name:'Scheduled',cues:[cue('timer-cue','SCHEDULE')],playbackGroup:'schedule'}
    ]}]}], schedules:[{id:'scheduled-task',name:'Timed',listId:'scheduled-list',at:time,enabled:true,mode:'weekly'}]});
  const saved=await api('/project','PUT',project);
  await api('/run/loop-list?revision='+saved.revision,'POST');
  await until(()=>packets.includes('LOOP'));
  const originalRun=(await view()).runs.find(run=>run.listId==='loop-list').id;
  const options=await close('最小化到托盘');
  assert.equal(options.defaultId,0);
  assert.deepEqual(options.buttons,['最小化到托盘','完全退出','取消']);
  await until(async()=>!await visible());
  const beforeHide=packets.length;
  await until(()=>packets.length>beforeHide+1);
  assert.ok((await view()).runs.some(run=>run.id===originalRun));
  await desktop.evaluate(({app})=>app.emit('second-instance'));
  await until(visible);
  console.log('PASS close defaults to tray, same run keeps sending, and the window can reopen.');

  await page.reload();
  await page.locator('#mode.live').waitFor();
  await page.locator('#workbenchSettings summary').click();
  await page.locator('#permissions').click();
  await page.locator('[name=adminPassword]').fill('TestPass2026');
  await page.locator('#editor button[type=submit]').click();
  await page.locator('#editor').waitFor({state:'hidden'});
  await page.locator('#lock').click();
  await page.locator('#lockScreen').waitFor();
  assert.equal((await request('/stop','POST')).status,423);
  const lockedOptions=await close('完全退出');
  assert.deepEqual(lockedOptions.buttons,['最小化到托盘','取消']);
  assert.equal(await visible(),true);
  await close('最小化到托盘');
  await until(async()=>!await visible());
  const beforeLock=packets.length;
  await until(()=>packets.length>beforeLock+1);
  assert.equal((await view()).activeCount,1);
  console.log('PASS locked window sends UDP in the tray and offers no complete-exit action while locked.');
  if (!process.env.REVIEW_FAST) await until(()=>packets.includes('SCHEDULE'),125000);
  assert.equal(await visible(),false);
  if (!process.env.REVIEW_FAST) console.log('PASS real minute-based schedule sends UDP while locked and hidden.');

  // Recreating the renderer leaves the independent engine and browser session in place.
  await desktop.evaluate(({app})=>app.emit('second-instance'));
  await until(visible);
  await desktop.evaluate(({BrowserWindow})=>BrowserWindow.getAllWindows()[0].webContents.forcefullyCrashRenderer());
  const wc=await desktop.evaluate(({BrowserWindow})=>BrowserWindow.getAllWindows()[0].webContents.id);
  await until(()=>desktop.evaluate(({webContents},id)=>{
    const view=webContents.fromId(id);
    if(view.isCrashed() || view.isLoading())return false;
    return view.executeJavaScript('!document.querySelector("#lockScreen")?.hidden && !!document.querySelector("#mode.live")').catch(()=>false);
  },wc));
  assert.equal((await view()).activeCount,1);
  console.log('PASS renderer recovery preserves lock state and the active engine run.');
  await desktop.evaluate(({webContents},id)=>webContents.fromId(id).executeJavaScript(
    'document.getElementById("unlockForm").elements.password.value="TestPass2026"; document.getElementById("unlockForm").requestSubmit();'),wc);
  await until(()=>desktop.evaluate(({webContents},id)=>webContents.fromId(id).executeJavaScript(
    'document.getElementById("lockScreen").hidden'),wc));
  const exitOptions=await close('取消');
  assert.ok(exitOptions.buttons.includes('完全退出'));
  const proc = desktop.process();
  await desktop.evaluate(({BrowserWindow})=>{
    globalThis.closeAnswer='完全退出';
    setTimeout(()=>BrowserWindow.getAllWindows()[0].close(),150);
  });
  await until(()=>proc.exitCode!==null,19000);
  assert.equal(proc.exitCode,0);
  desktop=null;
  const finalCount=packets.length;
  await sleep(1000);
  assert.equal(packets.length,finalCount);
  const logs=path.join(data,'logs');
  const lines=fs.readdirSync(logs).map(f=>fs.readFileSync(path.join(logs,f),'utf8')).join('');
  assert.match(lines,/\tstopped\t/);
  if (!process.env.REVIEW_FAST) assert.match(lines,/SCHEDULE/);
  console.log('PASS unlock enables complete exit, sends stop and flushes TXT logs.');
})().catch(e=>{console.error(e.stack);process.exitCode=1;}).finally(async()=>{
  if(desktop){try{await desktop.evaluate(({BrowserWindow,app})=>{BrowserWindow.getAllWindows().forEach(win=>win.destroy());app.quit();});}catch{}}
  receiver.close();
});
