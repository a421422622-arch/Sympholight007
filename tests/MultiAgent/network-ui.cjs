const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const { _electron }=require(process.env.SYMPHOCUE_PLAYWRIGHT || 'playwright');
const exe=path.resolve(process.env.SYMPHOCUE_TEST_EXE);
const scratch=fs.mkdtempSync(path.join(require('node:os').tmpdir(),'symphocue-network-'));
let app;
async function closeDesktop() {
  if (!app || app.process().exitCode !== null) return;
  const closed=new Promise(resolve=>app.once('close',resolve));
  await app.evaluate(({BrowserWindow,app})=>{
    BrowserWindow.getAllWindows().forEach(win=>win.destroy()); app.quit();
  }).catch(()=>{});
  await closed;
}
(async()=>{
  const launch=()=>_electron.launch({executablePath:exe,args:['--user-data-dir='+path.join(scratch,'profile')],
    env:{...process.env,SHOWCONTROL_DATA_DIR:path.join(scratch,'data')}});
  app=await launch();
  let page=await app.firstWindow();
  const errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.locator('#mode.live').waitFor();
  const api=async(route,method='GET',body)=>{
    const result=await fetch(new URL('/api'+route,page.url()),{method,headers:{'Content-Type':'application/json'},
      body:body===undefined?undefined:JSON.stringify(body)});
    assert.ok(result.ok,route+' '+result.status+' '+(await result.clone().text()));
    return result.json();
  };
  const interfaces=await api('/udp-interfaces?includeInactive=true');
  const loop=interfaces.find(nic=>nic.loopback);
  assert.ok(loop?.addresses.includes('127.0.0.1'));
  const active=interfaces.find(nic=>!nic.loopback&&nic.up&&nic.addresses.length);
  const existing=await api('/udp-interfaces');
  assert.ok(interfaces.length>=existing.length);
  assert.equal(existing.find(nic=>nic.id===loop.id).loopback,true);
  const open=async()=>{
    await page.locator('#workbenchSettings summary').click();
    await page.locator('#udpAdapter').click();
    await page.locator('#refreshUdpAdapters').waitFor();
    await page.waitForFunction(()=>document.querySelector('#udpAdapterStatus').textContent.startsWith('已查询'));
  };
  await open();
  let row=page.locator('#udpAdapterTable tbody tr');
  assert.ok((await row.allTextContents()).some(text=>text.includes('127.0.0.1')));
  if (active) {
    await row.filter({hasText:active.name}).getByRole('checkbox').first().check();
    await page.locator('#editor [name=adapter]').selectOption(active.id);
    assert.equal(await page.locator('#editor [name=adapter]').inputValue(),active.id);
  }
  await page.locator('#showInactiveUdpAdapters').check();
  await page.waitForFunction(()=>!document.querySelector('#refreshUdpAdapters').disabled);
  assert.equal(await row.count(),interfaces.length);
  await page.locator('#cancelEdit').click();

  const test={id:'usb-test-one',name:'USB Ethernet Test',addresses:['192.0.2.22'],up:true,networks:['192.0.2.0/24'],loopback:false};
  const second={id:'usb-test-two',name:'Second Ethernet Test',addresses:['198.51.100.20'],up:true,networks:['198.51.100.0/24'],loopback:false};
  let adapters=[test,second,loop];
  const stubAdapters=()=>page.route('**/api/udp-interfaces?*',route=>route.fulfill({json:adapters}));
  const checkbox=id=>page.locator(`[data-project-adapter="${id}"]`);
  await stubAdapters();
  await open();
  await checkbox(test.id).check();
  await checkbox(second.id).check();
  await page.locator('#editor [name=adapter]').selectOption(test.id);
  await page.locator('#editor [name=adapterWaitSeconds]').fill('1');
  await page.locator('#addUdpRoute').click();
  await page.locator('.udp-route [name=network]').fill('198.51.100.0/24');
  await page.locator('.udp-route [name=interfaceId]').selectOption(second.id);
  const refresh=async()=>{
    await page.locator('#refreshUdpAdapters').click();
    await page.waitForFunction(()=>!document.querySelector('#refreshUdpAdapters').disabled);
  };
  adapters=[{...test,up:false,addresses:[],networks:[]},second,loop];
  await refresh();
  assert.match(await row.filter({hasText:test.name}).innerText(),/未连接/);
  assert.equal(await checkbox(test.id).isChecked(),true);
  adapters=[second,loop];
  await row.filter({hasText:test.name}).getByText('未检测到').waitFor({timeout:6000});
  assert.equal(await checkbox(test.id).isChecked(),true);
  assert.equal(await page.locator('#editor [name=adapter]').inputValue(),test.id);
  assert.equal(await page.locator('.udp-route [name=network]').inputValue(),'198.51.100.0/24');
  assert.equal(await page.locator('.udp-route [name=interfaceId]').inputValue(),second.id);
  await page.locator('#editor button[type=submit]').click();
  await page.locator('#editor').waitFor({state:'hidden'});
  const config=await api('/project');
  assert.deepEqual(config.project.udpProjectInterfaceIds,[test.id,second.id]);
  assert.equal(config.project.udpInterfaceId,test.id);
  assert.equal(config.project.adapterWaitSeconds,1);
  assert.deepEqual(config.project.udpRoutes,[{network:'198.51.100.0/24',interfaceId:second.id}]);
  await closeDesktop();
  app=await launch();
  page=await app.firstWindow();
  page.on('pageerror',e=>errors.push(e.message));
  await page.locator('#mode.live').waitFor();
  row=page.locator('#udpAdapterTable tbody tr');
  await stubAdapters();
  await open();
  assert.equal(await checkbox(test.id).isChecked(),true);
  assert.equal(await checkbox(second.id).isChecked(),true);
  assert.match(await row.filter({hasText:test.id}).innerText(),/未检测到/);
  adapters=[{...test,name:'Renamed Ethernet',addresses:['2001:db8::22','fe80::1%17','192.0.2.43']},
    {...second,addresses:['2001:db8:1::20']},loop];
  const reconnected=row.filter({hasText:'Renamed Ethernet'});
  await reconnected.getByRole('cell',{name:'192.0.2.43',exact:true}).waitFor({timeout:6000});
  const ipv6Only=row.filter({hasText:second.name});
  assert.equal(await ipv6Only.getByRole('cell',{name:'—',exact:true}).count(),1);
  assert.equal(await checkbox(test.id).isChecked(),true);
  assert.equal(await page.locator('#editor [name=adapter]').inputValue(),test.id);
  assert.equal(await page.locator('.udp-route [name=interfaceId]').inputValue(),second.id);
  console.log('PASS project NIC checkboxes, settings persistence after restart, and automatic refresh through simulated disconnect/reconnect.');
  assert.deepEqual(errors,[]);
})().catch(e=>{console.error(e.stack);process.exitCode=1;}).finally(closeDesktop);
