/* Foreground only. Session tokens and positions stay in memory; no route history/offline replay. */
(() => {
  'use strict';
  const ageClocks = new WeakMap();
  function ages() {
    document.querySelectorAll('[data-location-age]').forEach(el => {
      const key = el.dataset.locationAge + '|' + el.dataset.locationServer;
      let clock = ageClocks.get(el);
      if (!clock || clock.key !== key) { clock = {key, start:performance.now(), age:Date.parse(el.dataset.locationServer)-Date.parse(el.dataset.locationAge)}; ageClocks.set(el,clock); }
      if (!Number.isFinite(clock.age)) return;
      const seconds = Math.max(0,Math.floor((clock.age+performance.now()-clock.start)/1000));
      el.textContent = (seconds < 60 ? 'Updated ' : 'Out of date · last update ') + seconds + ' seconds ago';
    });
  }
  function element(tag, text, cls) { const e=document.createElement(tag); if(text)e.textContent=text;if(cls)e.className=cls;return e; }
  function draw(container, point) {
    if (!container || !point || !Number.isFinite(point.latitude) || !Number.isFinite(point.longitude)) return;
    const width=container.clientWidth || 320, height=220;
    const signature=[point.latitude,point.longitude,point.accuracyMeters,width].join('|');
    if(container.dataset.drawn===signature)return;container.dataset.drawn=signature;container.replaceChildren();
    const zoom=15,n=2**zoom,lat=Math.max(-85,Math.min(85,point.latitude))*Math.PI/180;
    const x=(point.longitude+180)/360*n*256,y=(1-Math.log(Math.tan(lat)+1/Math.cos(lat))/Math.PI)/2*n*256;
    const left=x-width/2,top=y-height/2;
    // Only visible standard tiles. Browser HTTP caching is honored; no prefetch/offline download.
    for(let i=Math.floor(left/256);i<=Math.floor((left+width-1)/256);i++)for(let j=Math.floor(top/256);j<=Math.floor((top+height-1)/256);j++){
      if(j<0||j>=n)continue;const tile=element('img',null,'map-tile');tile.alt='';tile.referrerPolicy='strict-origin-when-cross-origin';
      tile.src=`https://tile.openstreetmap.org/${zoom}/${((i%n)+n)%n}/${j}.png`;tile.style.left=(i*256-left)+'px';tile.style.top=(j*256-top)+'px';container.append(tile);
    }
    const circle=element('span',null,'map-accuracy'),diameter=Math.max(10,Math.min(500,2*point.accuracyMeters/(156543.03392*Math.cos(lat)/n)));
    circle.style.width=circle.style.height=diameter+'px';container.append(circle,element('span',null,'map-pin'));
    const credit=element('a','© OpenStreetMap contributors','map-credit');credit.href='https://www.openstreetmap.org/copyright';credit.target='_blank';credit.rel='noopener noreferrer';container.append(credit);
    const link=element('a','Open larger map ↗','map-open');link.href=`https://www.openstreetmap.org/?mlat=${point.latitude}&mlon=${point.longitude}#map=16/${point.latitude}/${point.longitude}`;link.target='_blank';link.rel='noopener noreferrer';container.append(link);
  }
  function renderView(parent, view) {
    const card=element('section',null,'delivery-location-card');card.append(element('h3',view.driverName+(view.simulated?' · Simulated trip':'')));
    if(view.point){const age=element('p','Last reported location','location-age');age.dataset.locationAge=view.point.capturedAt;age.dataset.locationServer=view.serverTime;card.append(age,element('p',`Accuracy approximately ${Math.round(view.point.accuracyMeters)} metres.`));const map=element('div',null,'delivery-location-map');card.append(map);parent.append(card);draw(map,view.point);}
    else {card.append(element('p','Waiting for a fresh location.'));parent.append(card);}
  }
  function init(desk) {
    if(desk.dataset.locationReady)return;desk.dataset.locationReady='true';
    const prefix='/restaurant-management/'+encodeURIComponent(desk.dataset.tenant)+'/delivery-location';
    const token=desk.querySelector('input[name="__RequestVerificationToken"]').value;
    const message=desk.querySelector('[data-location-message]'),driverMessage=desk.querySelector('[data-location-driver]');
    const results=desk.querySelector('[data-location-results]'),toggle=desk.querySelector('[data-location-enable]'),stopButton=desk.querySelector('[data-location-stop]');
    const starts=[...desk.querySelectorAll('[data-location-start]')];
    let generation=0,board=null,session=null,order=null,sequence=0,watch=null,latest=null,pumpTimer=null,pollTimer=null,busy=false,stopped=false;
    const problems={other_delivery_active:'Stop sharing the other delivery before starting this one.',location_disabled:'Your manager has disabled location sharing.',session_expired:'Sharing ended. Refresh your deliveries before restarting.',forbidden:'Your access changed. Sign in again or ask your manager.',delivery_inactive:'Collect your assigned delivery first.',stale_settings:'Settings changed. Refresh and try again.',simulation_only:'The shared demo uses fictional locations only.'};
    async function post(path,values,keepalive=false){const body=new URLSearchParams({...values,__RequestVerificationToken:token});const r=await fetch(prefix+'/'+path,{method:'POST',body,credentials:'same-origin',cache:'no-store',keepalive,redirect:'error'});let value;try{value=await r.json();}catch{const e=new Error('Sign in again or refresh this page before sharing.');e.status=401;throw e;}if(!r.ok){const e=new Error(problems[value.code]||'Location update unavailable. Check your connection and retry.');e.status=r.status;throw e;}return value;}
    function controls(){starts.forEach(b=>{b.disabled=!board?.enabled||!!session||busy;b.textContent=(board?.simulated?'Start simulated trip':'Share my location')+' · order '+b.textContent.split(' · order ').pop();});if(stopButton)stopButton.hidden=!session;if(toggle){toggle.disabled=!board||busy;toggle.textContent=board?.enabled?'Disable location sharing':'Enable location sharing';}}
    async function refresh(){if(stopped||document.hidden)return;try{const r=await fetch(prefix,{credentials:'same-origin',cache:'no-store',redirect:'error'});if(!r.ok)throw new Error();board=await r.json();message.textContent=board.enabled?(board.simulated?'Simulated GPS · no real location is requested or saved.':'Drivers can opt in to sharing during their active delivery.'):'Location sharing is disabled. Delivery status updates still work.';desk.querySelector('[data-location-explanation]').textContent=board.simulated?'The public demo shows a fictional St Pete Beach trip. No device location permission is requested.':'Only the restaurant and that delivery’s customer can view a shared location. Sharing stops when the app is hidden; restart when safely stopped. Map tiles are supplied by OpenStreetMap.';results.replaceChildren();board.locations.forEach(v=>renderView(results,v));if(!board.locations.length)results.append(element('p','No delivery location is being shared.'));if(session&&!board.enabled)await stop('Your manager disabled sharing.');ages();}catch{message.textContent='Location refresh unavailable. Any previous point may be out of date.';}finally{controls();clearTimeout(pollTimer);if(!stopped)pollTimer=setTimeout(refresh,30000);}}
    async function stop(text='Location sharing stopped.',keepalive=false){generation++;const old=session,oldOrder=order;session=null;order=null;latest=null;clearTimeout(pumpTimer);if(watch!==null)navigator.geolocation.clearWatch(watch);watch=null;if(driverMessage)driverMessage.textContent=text;controls();if(old)try{await post(oldOrder+'/stop',{session:old.session},keepalive);}catch{/* Server freshness and expiry still apply when offline. */}}
    async function pump(){if(!session||document.hidden||busy)return;const current=session,currentOrder=order;let data={session:current.session,sequence:String(++sequence)};if(!current.simulated){if(!latest||Date.now()-latest.timestamp>45000){driverMessage.textContent='Waiting for a fresh GPS fix. No old position is sent.';pumpTimer=setTimeout(pump,16000);return;}data={...data,latitude:String(latest.coords.latitude),longitude:String(latest.coords.longitude),accuracyMeters:String(latest.coords.accuracy),capturedAt:new Date(latest.timestamp).toISOString()};latest=null;}
      try{const view=await post(currentOrder+'/point',data);if(session!==current)return;driverMessage.textContent=current.simulated?'Simulated trip is sharing.':'Sharing while this screen stays open. Stop when you wish.';results.replaceChildren();renderView(results,view);ages();}
      catch(e){if(session!==current)return;if([401,403,409].includes(e.status)){await stop(e.message);return;}driverMessage.textContent='No new position confirmed. Check connection; the last point will be marked out of date.';}
      if(session===current)pumpTimer=setTimeout(pump,16000);
    }
    starts.forEach(button=>button.addEventListener('click',async()=>{if(busy||session||!board?.enabled)return;busy=true;controls();try{const startOrder=button.dataset.locationStart,attempt=++generation;const created=await post(startOrder+'/start',{consent:'true'});if(!created||typeof created.simulated!=='boolean'||created.simulated!==board?.simulated||typeof created.session!=='string'||!/^[a-f0-9]{64}$/.test(created.session))throw new Error('Sharing could not start. Refresh the page and try again.');if(attempt!==generation||document.hidden||stopped){try{await post(startOrder+'/stop',{session:created.session},true);}catch{}return;}order=startOrder;session=created;sequence=0;if(session.simulated){busy=false;await pump();}else if(!navigator.geolocation){await stop('This browser does not support location sharing.');}else{driverMessage.textContent='Allow location permission to share this delivery. You can stop at any time.';watch=navigator.geolocation.watchPosition(p=>{if(session===created&&generation===attempt)latest=p;},async e=>{if(session!==created||generation!==attempt)return;if(e.code===1)await stop('Location permission denied. You can allow it in browser settings and try again.');else driverMessage.textContent='GPS temporarily unavailable. Waiting for a fresh position.';},{enableHighAccuracy:true,maximumAge:0,timeout:10000});busy=false;await pump();}}catch(e){await stop(e.message);}finally{busy=false;controls();}}));
    stopButton?.addEventListener('click',async()=>{await stop();await refresh();});
    toggle?.addEventListener('click',async()=>{if(!board||busy)return;busy=true;controls();try{await post('settings/save',{version:String(board.version),enabled:String(!board.enabled)});await refresh();}catch(e){message.textContent=e.message;}finally{busy=false;controls();}});
    document.addEventListener('visibilitychange',()=>{if(document.hidden){clearTimeout(pollTimer);stop('Sharing paused because this screen was hidden. Start again when ready.',true);}else refresh();});
    window.addEventListener('pagehide',()=>{stopped=true;clearTimeout(pollTimer);stop('Sharing stopped.',true);});
    refresh();
  }
  function initAll(){document.querySelectorAll('[data-location-desk]').forEach(init);ages();}
  window.tideDeliveryLocation={draw};
  if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',initAll);else initAll();
  setInterval(()=>{if(!document.hidden)ages();},5000);
})();
