/* The studio never evaluates learner code in its own origin or sends it to an API. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const storageKey = 'tide-casa-engineering-v1';
  const maxCode = 120000;
  const starter = {
    html: `<main>\n  <p class="eyebrow">ST. PETE BEACH · PRACTICE BAR</p>\n  <h1>My beach bar</h1>\n  <p>A little sunshine, one order at a time.</p>\n  <article>\n    <span class="dish">☀</span>\n    <h2>Sunset fish tacos</h2>\n    <p>Grilled fish, lime slaw, warm tortillas.</p>\n    <strong>$12.99</strong>\n    <button id="add">Add to order</button>\n  </article>\n  <section class="cart">\n    <h2>Your order</h2>\n    <p><span id="quantity">0</span> items · <strong id="total">$0.00</strong></p>\n    <label>Table number <input id="table" maxlength="20" placeholder="e.g. 4"></label>\n    <button id="checkout">Place practice order</button>\n    <p id="message" role="status"></p>\n  </section>\n  <small>Fictional practice order. No payment or restaurant connection.</small>\n</main>`,
    css: `:root { --accent: #087565; }\n* { box-sizing: border-box; }\nbody { margin: 0; background: #faf7ed; color: #193f38; font: 15px/1.6 system-ui; }\nmain { max-width: 560px; margin: auto; padding: 24px; }\nh1 { font: 34px/1.15 Georgia, serif; margin: 14px 0; }\nh2 { font-size: 18px; }\n.eyebrow { font-size: 10px; letter-spacing: .12em; color: var(--accent); }\narticle, .cart { background: white; padding: 20px; border: 1px solid #dde5d6; border-radius: 14px; margin: 20px 0; }\n.dish { display: grid; place-items: center; height: 80px; background: #f4dc9d; font-size: 45px; border-radius: 9px; }\nbutton { display: block; width: 100%; background: var(--accent); color: white; border: 0; border-radius: 9px; padding: 13px; margin-top: 12px; cursor: pointer; font: inherit; }\ninput { display: block; width: 100%; padding: 10px; border: 1px solid #a8bfb1; border-radius: 6px; font: inherit; }\nsmall { color: #60766c; }\n#message { font-weight: 650; }`,
    js: `// Keep money in integer cents. This is a browser practice model.\nconst priceCents = 1299;\nlet quantity = 0;\nconst money = cents => '$' + (cents / 100).toFixed(2);\nfunction updateCart() {\n  document.querySelector('#quantity').textContent = quantity;\n  document.querySelector('#total').textContent = money(priceCents * quantity);\n}\ndocument.querySelector('#add').addEventListener('click', () => {\n  quantity += 1;\n  updateCart();\n  console.log('Cart now has ' + quantity + ' item(s).');\n});\ndocument.querySelector('#checkout').addEventListener('click', () => {\n  const table = document.querySelector('#table').value.trim();\n  const message = document.querySelector('#message');\n  if (quantity < 1) {\n    message.textContent = 'Add an item first.';\n    return;\n  }\n  if (!table) {\n    message.textContent = 'Please enter a table number.';\n    return;\n  }\n  message.textContent = 'Practice order ready for Table ' + table + '.';\n  console.log('Practice order prepared. Nothing was sent to a restaurant.');\n});\nupdateCart();`
  };
  const browserLessons = [
    {id:'page',title:'Give your app a home',minutes:10,goal:'Change the title of your first ordering page, then see it in the preview.',explanation:'HTML is the structure: headings, menu cards, inputs and buttons. A small visible change is your first complete slice. The starter already works, so you can learn by changing one thing at a time.',steps:['Open index.html. Replace “My beach bar” inside the h1 with a fictional bar name.','Choose Run preview and look for your new heading.','Choose Run behavior checks. Write down which line changed the page.'],notebook:'A browser reads HTML as a tree. h1 is the page heading; an id gives one element a name JavaScript can find. Write: edit → run → observe → check.',sourcePaths:['TideCasa.Blazor/Components/Pages/RestaurantOrder.razor']},
    {id:'design',title:'Make it feel like your bar',minutes:15,goal:'Restyle the same app and check it at a phone width.',explanation:'CSS changes presentation without changing the order calculation. Use the --accent variable to give your bar a color. A readable button and a usable phone layout matter more than decoration.',steps:['Open styles.css. Change --accent to #a54929, or choose your own color.','Run preview, then choose Phone. Keep the buttons and table field easy to read.','Check the layout and describe your design choice in your notes.'],notebook:'HTML is structure. CSS is appearance. JavaScript is browser behavior. A CSS variable such as --accent lets several controls share one value.',sourcePaths:['TideCasa.Blazor/wwwroot/app.css']},
    {id:'cart',title:'Follow an order through state',minutes:20,goal:'Add two items and prove the subtotal is $25.98.',explanation:'State is the information the app remembers while it runs. Here it is quantity. The subtotal is derived from quantity × price. The live app asks its API for authoritative prices; this isolated example teaches the calculation.',steps:['Open app.js and find quantity, priceCents and updateCart.','Run preview. Press Add to order twice and inspect the total.','Run behavior checks, then try the debugging challenge below. Fix the operator and run the checks again.'],notebook:'1299 cents × 2 = 2598 cents = $25.98. The displayed total follows state. In production the server validates prices, quantities and menu availability.',sourcePaths:['TideCasa.Blazor/Components/Pages/RestaurantOrder.razor.cs','TideCasa.Api/Features/RestaurantOrdering/RestaurantOrderingStore.cs']},
    {id:'table',title:'Catch a missing table',minutes:15,goal:'Explain why an order needs both an item and a table number.',explanation:'Validation gives the customer a clear way to recover. This practice button displays a message only; it does not save an order. Production repeats validation on the server because browser checks can be bypassed.',steps:['Run preview and try placing an order before adding an item.','Add an item. Leave the table blank, then try again. Enter 4 and retry.','Run behavior checks. Trace each early return in app.js and describe the error it prevents.'],notebook:'A useful error says what to fix. UI validation helps the person; server validation protects the records. Neither a clicked button nor a success message proves durable storage.',sourcePaths:['TideCasa.Contracts/RestaurantOrdering.cs','TideCasa.Api/Features/RestaurantOrdering/RestaurantOrderingEndpoints.cs']}
  ];
  let lessons = [], state, currentFile = 'html', runId = '', checkTimer, saveTimer, recoveryRaw = null, knownStored = null;
  let importCandidate = null;
  const names = {html:'index.html',css:'styles.css',js:'app.js',cs:'Program.cs'};
  const starterState = () => ({format:'tide-casa-studio',version:1,name:'My first beach bar',selected:'page',files:{...starter},csharp:{},notes:{},complete:[],release:[],localResults:{}});
  function validState(value) {
    if (!value || value.format !== 'tide-casa-studio' || value.version !== 1 || typeof value.name !== 'string' || value.name.length > 60) throw Error('This is not a supported studio backup.');
    const result = starterState();
    result.name = value.name;
    result.selected = lessons.some(x => x.id === value.selected) ? value.selected : 'page';
    for (const key of ['html','css','js']) {
      if (typeof value.files?.[key] !== 'string' || value.files[key].length > maxCode) throw Error('A project file is missing or too large.');
      result.files[key] = value.files[key];
    }
    for (const key of ['csharp','notes','localResults']) {
      if (value[key] === null || typeof value[key] !== 'object' || Array.isArray(value[key])) throw Error('Invalid saved lesson data.');
      for (const lesson of lessons) if (Object.hasOwn(value[key],lesson.id)) {
        const text = value[key][lesson.id];
        if (typeof text !== 'string' || text.length > (key === 'csharp' ? maxCode : 8000)) throw Error('A saved lesson is too large.');
        result[key][lesson.id] = text;
      }
    }
    result.complete = Array.isArray(value.complete) ? [...new Set(value.complete)].filter(id => lessons.some(x => x.id === id)) : [];
    result.release = Array.isArray(value.release) ? [...new Set(value.release)].filter(i => Number.isInteger(i) && i >= 0 && i < releaseSteps.length) : [];
    return result;
  }
  function save() {
    clearTimeout(saveTimer);
    if (recoveryRaw !== null) { $('save-state').textContent = 'Saving paused — recover the old draft first'; return; }
    try {
      if (localStorage.getItem(storageKey) !== knownStored) { $('save-state').textContent = 'Save paused — another tab changed this project'; notice('Another tab saved a newer workspace. Export this tab’s work to keep it, then reload to open the newer saved version.'); return; }
      const serialized=JSON.stringify(state); localStorage.setItem(storageKey,serialized); knownStored=serialized; $('save-state').textContent = 'Saved in this browser';
    }
    catch { $('save-state').textContent = 'Not saved — export a backup'; notice('Browser storage is unavailable or full. Your current work is still on this page. Export a backup before closing it.'); }
  }
  function scheduleSave() { $('save-state').textContent = 'Saving…'; clearTimeout(saveTimer); saveTimer = setTimeout(save,300); }
  function notice(text) { $('notice').textContent = text; $('notice').hidden = !text; }
  function log(text,kind='info') {
    const line = document.createElement('p'); line.className = 'log-' + kind; line.textContent = String(text).slice(0,3000);
    $('console').append(line);
    while ($('console').children.length > 80) $('console').firstChild.remove();
    $('console').scrollTop = $('console').scrollHeight;
  }
  const selected = () => lessons.find(x => x.id === state.selected);
  const isCSharp = () => !!selected()?.source;
  function renderNav() {
    $('lessons').replaceChildren();
    lessons.forEach((lesson,index) => {
      if (index === 0 || index === 4) { const heading = document.createElement('p'); heading.className='lesson-group'; heading.textContent=index===0?'BUILD IN THE BROWSER':'LEARN THE C# BEHIND IT'; $('lessons').append(heading); }
      const button=document.createElement('button'); button.className='lesson-link' + (state.complete.includes(lesson.id)?' done':''); button.setAttribute('aria-current',lesson.id===state.selected?'step':'false');
      const number=document.createElement('span'); number.className='lesson-number'; number.textContent=state.complete.includes(lesson.id)?'✓':String(index+1).padStart(2,'0');
      const title=document.createElement('span'); title.textContent=lesson.title; button.append(number,title); button.addEventListener('click',()=>selectLesson(lesson.id)); $('lessons').append(button);
    });
    $('progress').max=lessons.length; $('progress').value=state.complete.length; $('progress-text').textContent=`${state.complete.length} / ${lessons.length}`;
  }
  function selectLesson(id) {
    state.selected=id; currentFile=isCSharp()?'cs':'html'; save(); renderLesson();
  }
  function renderLesson() {
    const lesson=selected(), cs=isCSharp(); renderNav();
    $('lesson-title').textContent=lesson.title;
    $('lesson-kicker').textContent=`SLICE ${String(lessons.indexOf(lesson)+1).padStart(2,'0')} · ABOUT ${lesson.minutes} MIN · ${cs?'C# ON YOUR COMPUTER':'WORKING BROWSER LAB'}`;
    $('lesson-goal').textContent=lesson.goal; $('lesson-explanation').textContent=lesson.explanation;
    $('lesson-steps').replaceChildren(...lesson.steps.map(text=>{const li=document.createElement('li');li.textContent=text;return li;}));
    $('notebook').textContent=lesson.notebook; $('source-paths').textContent='In the real source: ' + lesson.sourcePaths.join(' → ');
    $('notes').value=state.notes[lesson.id]||''; $('complete').textContent=state.complete.includes(lesson.id)?'✓ Complete · undo':'Mark slice complete';
    $('preview-stage').hidden=cs; $('csharp-guide').hidden=!cs; $('view-controls').hidden=cs;
    $('run').hidden=cs; $('check').hidden=cs; $('bug-open').hidden=cs; $('preview-label').textContent=cs?'02 / RUN IT WITH .NET':'02 / SEE IT WORK';
    $('expected').textContent=lesson.expected||''; $('challenge').textContent=lesson.challenge||'';
    $('editor-help').textContent=cs?'Edit here, download the ZIP, then compile it locally with dotnet run.':'Edit a line, then run the preview. Tab moves to the next control; use spaces to indent.';
    $('next').textContent=lessons.indexOf(lesson)===lessons.length-1?'Prepare handoff →':'Next slice →';
    renderEditor();
    if (cs) { clearTimeout(checkTimer); runId=''; setPreview(''); $('console').replaceChildren(); log('C# is edited here and compiled locally with the .NET 10 SDK.'); if(state.localResults[lesson.id]) log('Your recorded local result:\n'+state.localResults[lesson.id]); else log('No local run result recorded for this slice yet.'); }
    else if (Object.keys(starter).every(key => state.files[key] === starter[key])) runPreview(false);
    else { clearTimeout(checkTimer); runId=''; setPreview('<p style="font:16px system-ui;padding:24px;color:#164f47">Your draft is ready. Choose <strong>Run preview</strong> to see your code.</p>'); $('console').replaceChildren(); log('Draft loaded. Choose Run preview or Run behavior checks.'); }
  }
  function renderEditor() {
    const cs=isCSharp();
    const tabs=document.querySelector('.file-tabs'); tabs.replaceChildren();
    for (const key of cs?['cs']:['html','css','js']) { const button=document.createElement('button'); button.textContent=names[key];button.setAttribute('role','tab');button.setAttribute('aria-selected',String(currentFile===key));button.addEventListener('click',()=>{currentFile=key;renderEditor();});tabs.append(button); }
    $('editor-label').textContent=names[currentFile]; $('code').value=cs?(state.csharp[state.selected]??selected().source):state.files[currentFile]; $('code').maxLength=maxCode;
  }
  // All messages come from this opaque-origin iframe and carry its current run id.
  function setPreview(html) {
    $('preview').srcdoc=html;
  }
  function runPreview(checks=false) {
    clearTimeout(checkTimer); if(isCSharp())return;
    runId=crypto.randomUUID(); $('console').replaceChildren(); log(checks?'Running checks against a fresh preview…':'Starting a fresh practice preview…');
    const id=runId;
    const bridge=`(() => { const run=${JSON.stringify(id)}; const send=(kind,text)=>parent.postMessage({studio:run,kind,text},'*');
      let count=0; const out=(kind,args)=>{if(count++<60)send(kind,args.map(x=>{try{return typeof x==='string'?x:JSON.stringify(x)}catch{return String(x)}}).join(' '));};
      console.log=(...a)=>out('info',a);console.error=(...a)=>out('fail',a);console.warn=(...a)=>out('info',a);
      addEventListener('error',e=>send('fail','JavaScript: '+e.message));addEventListener('unhandledrejection',e=>send('fail','Promise: '+String(e.reason)));
      addEventListener('load',()=>{send('ready','Preview ready. Use the menu to try an order.'); ${checks?behaviorChecks(state.selected):''}});
    })();`;
    const escapeEnd = text => text.replace(/<\/script/gi,'<\\/script');
    const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; form-action 'none'; base-uri 'none'; frame-src 'none'; object-src 'none'"><meta name="viewport" content="width=device-width, initial-scale=1"><script>${escapeEnd(bridge)}</script><style>${state.files.css.replace(/<\/style/gi,'<\\/style')}</style></head><body>${state.files.html}<script>${escapeEnd(state.files.js)}</script></body></html>`;
    setPreview(html);
    if(checks)checkTimer=setTimeout(()=>{if(runId===id)log('Checks did not finish. Read any errors above, restore the starter if needed, and run again.','fail');},4000);
  }
  function behaviorChecks(slice) {
    return `try {
      const check=(label,ok)=>{send(ok?'pass':'fail',(ok?'PASS: ':'FAIL: ')+label);};
      const query=id=>document.getElementById(id);
      if (${JSON.stringify(slice)}==='page') { const heading=document.querySelector('h1');check('Give the h1 a name different from My beach bar.',!!heading&&!!heading.textContent.trim()&&heading.textContent.trim()!=='My beach bar'); }
      if (${JSON.stringify(slice)}==='design') {const button=query('add');check('The order button remains visible and at least 40 px tall.',!!button&&button.getBoundingClientRect().height>=40);check('The preview fits its current width.',document.documentElement.scrollWidth<=innerWidth);send('info','Color and readability need your visual review in Phone and Wide views.');}
      if (${JSON.stringify(slice)}==='cart'||${JSON.stringify(slice)}==='table') {
        const add=query('add'),qty=query('quantity'),total=query('total'),checkout=query('checkout'),table=query('table'),message=query('message');
        if(!add||!qty||!total||!checkout||!table||!message)throw Error('Keep the starter ids: add, quantity, total, checkout, table, message.');
        check('A fresh cart starts with 0 items.',qty.textContent.trim()==='0');
        checkout.click();check('An empty cart asks you to add an item.',message.textContent.includes('Add an item'));
        add.click();add.click();check('Two taps produce 2 items.',qty.textContent.trim()==='2');check('Two $12.99 items total $25.98.',total.textContent.trim()==='$25.98');
        table.value='   ';checkout.click();check('A blank table is rejected.',message.textContent.includes('enter a table'));
        table.value='4';checkout.click();check('A valid practice order names Table 4.',message.textContent.includes('ready for Table 4'));
      }
    } catch(error){send('fail','Check could not run: '+error.message);} finally{send('complete','Checks finished. Review any FAIL lines; a check only covers the behavior it names.');}`;
  }
  addEventListener('message',event=>{
    if(event.source!==$('preview').contentWindow||!event.data||event.data.studio!==runId)return;
    const {kind,text}=event.data;
    if(typeof text!=='string'||!['ready','complete','info','pass','fail'].includes(kind))return;
    if(kind==='complete')clearTimeout(checkTimer);
    log(text,kind==='pass'||kind==='fail'?kind:'info');
  });
  function download(name,content,type='text/plain') {
    const blob=content instanceof Blob?content:new Blob([content],{type});const url=URL.createObjectURL(blob);const anchor=document.createElement('a');anchor.href=url;anchor.download=name;anchor.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
  }
  function slug(){return (state.name.toLowerCase().replace(/[^a-z0-9]+/g,'-').replace(/^-|-$/g,'').slice(0,45)||'tide-casa-practice');}
  // Small dependency-free ZIP writer (stored entries, UTF-8). No uploads or CDN.
  function zip(files) {
    const encoder=new TextEncoder(), parts=[], central=[];let offset=0;
    const crc=data=>{let value=0xffffffff;for(const byte of data){value^=byte;for(let b=0;b<8;b++)value=(value>>>1)^((value&1)?0xedb88320:0);}return (value^0xffffffff)>>>0;};
    for(const [name,content] of Object.entries(files)){
      const filename=encoder.encode(name),data=encoder.encode(content),checksum=crc(data),header=new Uint8Array(30),h=new DataView(header.buffer);
      h.setUint32(0,0x04034b50,true);h.setUint16(4,20,true);h.setUint16(6,0x800,true);h.setUint16(12,33,true);h.setUint32(14,checksum,true);h.setUint32(18,data.length,true);h.setUint32(22,data.length,true);h.setUint16(26,filename.length,true);
      const directory=new Uint8Array(46),d=new DataView(directory.buffer);d.setUint32(0,0x02014b50,true);d.setUint16(4,20,true);d.setUint16(6,20,true);d.setUint16(8,0x800,true);d.setUint16(14,33,true);d.setUint32(16,checksum,true);d.setUint32(20,data.length,true);d.setUint32(24,data.length,true);d.setUint16(28,filename.length,true);d.setUint32(42,offset,true);
      parts.push(header,filename,data);central.push(directory,filename);offset+=header.length+filename.length+data.length;
    }
    const size=central.reduce((sum,part)=>sum+part.length,0),end=new Uint8Array(22),e=new DataView(end.buffer);e.setUint32(0,0x06054b50,true);e.setUint16(8,Object.keys(files).length,true);e.setUint16(10,Object.keys(files).length,true);e.setUint32(12,size,true);e.setUint32(16,offset,true);
    return new Blob([...parts,...central,end],{type:'application/zip'});
  }
  function projectZip() {
    let files;
    if(isCSharp()) files={
      'Program.cs':state.csharp[state.selected]??selected().source,
      'Practice.csproj':'<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n',
      'README.md':`# ${state.name}: ${selected().title}\n\nInstall the .NET 10 SDK. Extract this ZIP. Open a terminal inside this folder and run:\n\n    dotnet run\n\nRead Program.cs before executing code imported from someone else.\n\nThese are teaching examples, not payment, authentication or production storage implementations. The studio does not compile C#.\n\n## Starter expected output\n${selected().expected}\n\n## Your notes\n${state.notes[state.selected]||''}\n`
    };
    else files={
      'index.html':`<!doctype html>\n<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Practice bar</title><link rel="stylesheet" href="styles.css"><script src="app.js" defer></script></head><body>\n${state.files.html}\n</body></html>`,
      'styles.css':state.files.css,'app.js':state.files.js,
      'README.md':`# ${state.name}\n\nOpen index.html in a browser. This is a fictional, browser-only ordering prototype. No API, account, payment or durable order storage is connected.\n\nThe studio sandbox does not travel with exported files. Review any imported code before running it on your computer.\n\n${browserLessons.map(l=>'## '+l.title+'\n'+(state.notes[l.id]||'No notes yet.')).join('\n\n')}`
    };
    download(slug()+(isCSharp()?'-'+state.selected:'-browser')+'.zip',zip(files));log('Project ZIP downloaded. Extract it before opening or running the files.');
  }
  function modal(title,body,action,label='Continue') {
    $('modal-content').replaceChildren();const h=document.createElement('h2');h.textContent=title;const p=document.createElement('p');p.textContent=body;$('modal-content').append(h,p);
    if(action){const row=document.createElement('div');row.className='actions';const yes=document.createElement('button');yes.className='primary';yes.textContent=label;yes.onclick=()=>{$('modal').close();action();};const cancel=document.createElement('button');cancel.textContent='Cancel';cancel.onclick=()=>$('modal').close();row.append(yes,cancel);$('modal-content').append(row);}
    $('modal').showModal();
  }
  const releaseSteps=['I can explain my change in one sentence.','I tested the happy path and an invalid input.','I tried a narrow phone layout and keyboard navigation.','I wrote down errors, expected results and actual results.','My practice files contain no passwords or real customer information.','I exported a backup and gave the project to an engineer for review.'];
  function showRelease(){
    modal('Turn practice into a reviewed change','This checklist prepares an engineering handoff. It does not publish your prototype or certify it for production.');
    const list=document.createElement('div');list.className='checklist';releaseSteps.forEach((text,index)=>{const label=document.createElement('label'),input=document.createElement('input');input.type='checkbox';input.checked=state.release.includes(index);input.onchange=()=>{state.release=state.release.filter(x=>x!==index);if(input.checked)state.release.push(index);save();};label.append(input,document.createTextNode(text));list.append(label);});$('modal-content').append(list);
    const button=document.createElement('button');button.className='primary';button.textContent='Download handoff notes';button.onclick=()=>download(slug()+'-handoff.md',`# ${state.name}: engineering handoff\n\nLearning progress: ${state.complete.length}/${lessons.length} self-marked slices.\n\n## Readiness\n${releaseSteps.map((text,i)=>'- ['+(state.release.includes(i)?'x':' ')+'] '+text).join('\n')}\n\n## Slice notes\n${lessons.map(l=>'### '+l.title+'\n'+(state.notes[l.id]||'No notes yet.')+(state.localResults[l.id]?'\n\nSelf-recorded local output:\n'+state.localResults[l.id]:'')).join('\n\n')}\n\nNo deployment was performed by this studio.\n`);$('modal-content').append(button);
  }
  function showMap(){
    modal('Trace a real Tide Casa order','The small browser app is a teaching model. This is how the real application separates responsibilities.');
    const steps=[['1','Customer interface','Razor components display the menu, quantities, table input and quote.','TideCasa.Blazor/Components/Pages/RestaurantOrder.razor'],['2','Web service','C# handles UI state and talks to the API. The browser is not trusted to set prices.','TideCasa.Blazor/Services/RestaurantOrderingClient.cs'],['3','Contracts & API','Shared C# request shapes and server validation define accepted operations.','TideCasa.Contracts/RestaurantOrdering.cs'],['4','Data & restaurant boundaries','The order store validates the restaurant, menu and request before committing records.','TideCasa.Api/Features/RestaurantOrdering/RestaurantOrderingStore.cs'],['5','Tools that keep it running','GitHub stores reviewed source; DigitalOcean runs Web and API; Supabase provides PostgreSQL and identity; Cloudflare connects the domain; Stripe handles configured payment flows.','The practice studio uses no live payment or customer-data integration.']];
    for(const [number,title,body,path]of steps){const row=document.createElement('div');row.className='map-step';const num=document.createElement('span');num.className='lesson-number';num.textContent=number;const detail=document.createElement('div'),b=document.createElement('b'),p=document.createElement('p'),code=document.createElement('code');b.textContent=title;p.textContent=body;code.textContent=path;detail.append(b,p,code);row.append(num,detail);$('modal-content').append(row);}
  }
  function bind(){
    $('project-name').oninput=()=>{state.name=$('project-name').value;scheduleSave();};
    $('notes').oninput=()=>{state.notes[state.selected]=$('notes').value;scheduleSave();};
    $('code').oninput=()=>{if(isCSharp()){state.csharp[state.selected]=$('code').value;delete state.localResults[state.selected];state.complete=state.complete.filter(x=>x!==state.selected);}else{state.files[currentFile]=$('code').value;state.complete=state.complete.filter(x=>!browserLessons.some(l=>l.id===x));}$('editor-help').textContent=isCSharp()?'Edited C# needs a new local dotnet run.':'Code changed. Run preview to see your latest edits.';renderNav();$('complete').textContent='Mark slice complete';scheduleSave();};
    $('complete').onclick=()=>{if(state.complete.includes(state.selected))state.complete=state.complete.filter(x=>x!==state.selected);else state.complete.push(state.selected);save();renderNav();$('complete').textContent=state.complete.includes(state.selected)?'✓ Complete · undo':'Mark slice complete';};
    $('next').onclick=()=>{const index=lessons.indexOf(selected());if(index===lessons.length-1)showRelease();else selectLesson(lessons[index+1].id);};
    $('run').onclick=()=>runPreview();$('check').onclick=()=>runPreview(true);$('download').onclick=projectZip;
    $('view-phone').onclick=()=>{$('preview-stage').classList.add('phone');$('view-phone').setAttribute('aria-pressed','true');$('view-wide').setAttribute('aria-pressed','false');};
    $('view-wide').onclick=()=>{$('preview-stage').classList.remove('phone');$('view-phone').setAttribute('aria-pressed','false');$('view-wide').setAttribute('aria-pressed','true');};
    $('export').onclick=()=>{save();download(slug()+'-backup.json',JSON.stringify(state,null,2),'application/json');notice('Backup downloaded. It includes your code, notes and progress. Keep it somewhere safe.');};
    $('import-open').onclick=()=>$('import').click();
    $('import').onchange=async()=>{const file=$('import').files[0];if(!file)return;try{if(file.size>8000000)throw Error('Backup must be smaller than 8 MB.');importCandidate=validState(JSON.parse(await file.text()));modal('Open this backup?',`“${importCandidate.name}” will replace your current browser workspace. Export your current work first if you want to keep both. Choose Run preview to execute imported browser code.`,()=>{state=importCandidate;importCandidate=null;recoveryRaw=null;$('project-name').value=state.name;currentFile=isCSharp()?'cs':'html';save();renderLesson();notice('Backup imported.');},'Replace workspace');}catch(error){notice('Import stopped: '+error.message);}$('import').value='';};
    $('reset-open').onclick=()=>modal('Restore starter code?',isCSharp()?'This replaces this C# slice’s code. Your notes remain. Export a backup first if you want to keep your changes.':'This replaces HTML, CSS and JavaScript for all four browser slices. Your notes remain. Export a backup first if you want to keep your changes.',()=>{if(isCSharp()){delete state.csharp[state.selected];delete state.localResults[state.selected];state.complete=state.complete.filter(x=>x!==state.selected);}else{state.files={...starter};state.complete=state.complete.filter(x=>!browserLessons.some(l=>l.id===x));}save();renderLesson();},'Restore starter');
    $('bug-open').onclick=()=>modal('Debug a wrong subtotal','This exercise replaces the browser project with a starter containing one calculation bug. Export your current project first if you want to keep it. The challenge: two $12.99 items must cost $25.98. Read the failing check, find the wrong operator, and fix it.',()=>{state.files={...starter,js:starter.js.replace('money(priceCents * quantity)','money(priceCents + quantity)')};state.complete=state.complete.filter(x=>!browserLessons.some(l=>l.id===x));state.selected='cart';currentFile='js';save();renderLesson();runPreview(true);},'Load debugging exercise');
    $('map-open').onclick=showMap;$('release-open').onclick=showRelease;
    $('record-run').onclick=()=>{modal('Record your local C# result','After running dotnet run on your computer, paste a short result or error here. This is your own note; the studio cannot verify it.');const area=document.createElement('textarea');area.id='local-result';area.maxLength=8000;area.setAttribute('aria-label','Local run result');area.value=state.localResults[state.selected]||'';const button=document.createElement('button');button.textContent='Save run note';button.className='primary';button.onclick=()=>{state.localResults[state.selected]=area.value;save();$('modal').close();renderLesson();};$('modal-content').append(area,button);};
    addEventListener('pagehide',()=>{if(state)save();});
  }
  async function init(){
    try{const response=await fetch('/engineering/csharp-lessons.json?v=1',{credentials:'omit'});if(!response.ok)throw Error('Lessons could not load. Refresh to retry.');const data=await response.json();lessons=[...browserLessons,...data.lessons];
      state=starterState();let previous=null;try{previous=localStorage.getItem(storageKey);knownStored=previous;if(previous)state=validState(JSON.parse(previous));}catch{if(previous!==null)recoveryRaw=previous;else notice('Browser storage is unavailable. Export a backup before closing this page.');}
      $('project-name').value=state.name;currentFile=isCSharp()?'cs':'html';bind();renderLesson();$('save-state').textContent='Drafts stay in this browser';
      if(recoveryRaw!==null){notice('An old draft could not be opened. Saving is paused so it will not be overwritten.');const recover=document.createElement('button');recover.textContent='Download old draft';recover.onclick=()=>download('studio-draft-recovery.txt',recoveryRaw);const fresh=document.createElement('button');fresh.textContent='Start a new draft';fresh.onclick=()=>modal('Replace the unreadable draft?','Download the old draft first if you want to keep it. This starts a fresh saved workspace.',()=>{recoveryRaw=null;save();notice('New workspace started.');},'Start fresh');$('notice').append(document.createElement('br'),recover,fresh);}
    }catch(error){notice(error.message);$('lesson-title').textContent='Workspace could not load';$('save-state').textContent='Not ready';for(const id of ['complete','run','download','next','export','import-open','check','reset-open'])$(id).disabled=true;}
  }
  init();
})();
