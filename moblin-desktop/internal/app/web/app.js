'use strict';
const $ = id => document.getElementById(id);
let token = location.hash.slice(1) || sessionStorage.getItem('airtake-token') || '';
if (/^[a-f0-9]{48}$/.test(token)) { sessionStorage.setItem('airtake-token', token); history.replaceState(null, '', location.pathname); }
let state = null, initialized = false, dirty = false, polling = false, stopped = false, qrObject = null, toastTimer;
function toast(message, error = false, duration = 6500) {
 clearTimeout(toastTimer); $('toast').textContent = message; $('toast').classList.toggle('error', error); $('toast').hidden = false;
 toastTimer = setTimeout(() => $('toast').hidden = true, duration);
}
async function api(path, method = 'GET', body) {
 const headers = {'X-AirTake-Token': token}; if (body !== undefined) headers['Content-Type'] = 'application/json';
 const r = await fetch('/api/' + path, {method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store'});
 let data; try { data = await r.json(); } catch { throw new Error(r.status === 401 ? 'Открой AirTake.exe заново: ключ доступа к окну отсутствует.' : 'Ответ приложения недоступен.'); }
 if (!r.ok) throw new Error(data.error || 'Ошибка приложения'); return data;
}
function action(id, fn) { $(id).addEventListener('click', async e => {e.preventDefault(); e.stopPropagation(); const b = $(id); if (b.disabled) return; b.disabled = true; try {await fn();} catch (error) {toast(error.message, true, 12000);} finally {b.disabled = false; await poll();} }); }
function tab(name) { document.querySelectorAll('[data-tab]').forEach(b => b.classList.toggle('active', b.dataset.tab === name)); document.querySelectorAll('.tab').forEach(s => s.classList.toggle('active', s.id === name)); }
document.querySelectorAll('[data-tab]').forEach(b => b.addEventListener('click', () => tab(b.dataset.tab)));
document.querySelector('.brand').addEventListener('click', e => {e.preventDefault();tab('studio');});
function option(value, label) {const o = document.createElement('option'); o.value = value; o.textContent = label; return o;}
function markDirty() {dirty = true; $('dirty-label').textContent = 'Есть несохранённые изменения.'; updateEstimate();}
document.querySelectorAll('[data-config]').forEach(e => e.addEventListener('input', markDirty));
const numeric = ['port','latencyMs','bitrateMbps','fps','reserveGb','maxMinutes','audioDelayMs'];
const text = ['address','passphrase','outputDir','microphone','syncMode','container'];
const checks = ['autoReconnect','autoExport','preview'];
function loadConfig(c, addresses) {
 const a = $('address'); a.replaceChildren(); addresses.forEach(n => a.append(option(n.ip, n.ip + ' — ' + n.name)));
 if (!addresses.some(n => n.ip === c.address)) a.append(option(c.address, c.address + ' — проверь подключение'));
 numeric.concat(text).forEach(k => {if ($(k).tagName === 'SELECT' && !Array.from($(k).options).some(o => o.value === String(c[k]))) $(k).append(option(String(c[k]), c[k] || 'Без отдельного микрофона')); $(k).value = c[k];});
 checks.forEach(k => $(k).checked = c[k]); $('resolution').value = c.width + 'x' + c.height; updateEstimate();
}
function configFromForm() {
 if (!state) throw new Error('Приложение ещё загружается.');
 const c = {...state.config}; numeric.forEach(k => {c[k] = Number($(k).value); if (!Number.isFinite(c[k])) throw new Error('Проверь числовые настройки.');}); text.forEach(k => c[k] = $(k).value); checks.forEach(k => c[k] = $(k).checked);
 const [w,h] = $('resolution').value.split('x').map(Number); c.width=w;c.height=h;
 c.microphoneName = c.microphone ? $('microphone').selectedOptions[0].textContent : ''; return c;
}
function updateEstimate() {const n = Number($('bitrateMbps').value); $('estimate').textContent = (n * .45).toLocaleString('ru-RU', {maximumFractionDigits:1}) + ' ГБ / час';}
async function save(announce = false) {if (state && state.status.busy) {if (dirty) throw new Error('Во время операции настройки заблокированы.'); return;}
 await api('config', 'POST', configFromForm()); dirty=false;$('dirty-label').textContent='Настройки сохранены.';state=await api('state');render(state);await updateQR();if(announce)toast('Настройки сохранены.');}
async function updateQR() {
 try {const r = await fetch('/api/qr', {headers:{'X-AirTake-Token':token},cache:'no-store'});if(!r.ok)throw new Error('QR unavailable');const blob=await r.blob();if(qrObject)URL.revokeObjectURL(qrObject);qrObject=URL.createObjectURL(blob);$('qr').src=qrObject;$('qr').hidden=false;$('qr-hint').hidden=true;}catch{$('qr').hidden=true;$('qr-hint').hidden=false;$('qr-hint').textContent='Используй «Скопировать профиль» или SRT URL.';}
}
async function refreshDevices() {if (!state || !state.toolsReady) return; const ds=await api('devices');let current=$('microphone').value;const c=state.config;const select=$('microphone');select.replaceChildren(option('','Без отдельного микрофона'));
 ds.forEach(d=>select.append(option(d.id,d.name)));if(!current)current=ds.find(d=>/fifine/i.test(d.name))?.id||'';
 if(current&&!ds.some(d=>d.id===current))select.append(option(current,(c.microphoneName||current)+' — сейчас не найден'));
 select.value=current;if(current!==c.microphone)markDirty();}
function clock(v) {if(!Number.isFinite(v)||v<0)v=0;const n=Math.floor(v);return [Math.floor(n/3600),Math.floor(n/60)%60,n%60].map(x=>String(x).padStart(2,'0')).join(':');}
function quantity(n,unit) {if(!n)return '—';return n.toLocaleString('ru-RU',{maximumFractionDigits:1})+(unit?' '+unit:'');}
function render(s) {
 const x=s.status,c=s.config;$('version').textContent='WINDOWS · v'+s.version;$('missing-tools').hidden=s.toolsReady;
 $('timer').textContent=clock(x.elapsed);$('status-message').textContent=x.message;$('input-format').textContent=x.format||'Запусти приём, затем трансляцию в Moblin.';
 const title={idle:'Камера ещё не подключена',preparing:'Подготовка записи',listening:'Ждём поток из Moblin',recording:'Исходный поток записывается',reconnecting:'Ожидаем переподключение',stopping:'Сохраняем исходники',exporting:'Сборка видео и микрофона',done:'Дубль сохранён',error:'Нужна проверка настроек'};
 $('signal-title').textContent=title[x.stage]||x.stage;const badge={idle:'ГОТОВ',preparing:'ПОДГОТОВКА',listening:'ОЖИДАНИЕ',recording:'● REC',reconnecting:'ПЕРЕПОДКЛЮЧЕНИЕ',stopping:'ОСТАНОВКА',exporting:'ОБРАБОТКА',done:'СОХРАНЕНО',error:'ОШИБКА'};
 $('state-badge').textContent=badge[x.stage]||x.stage;$('state-badge').classList.toggle('live',x.stage==='recording');$('state-badge').classList.toggle('working',x.busy&&x.stage!=='recording');document.body.classList.toggle('recording',x.stage==='recording');
 $('target').textContent='Профиль: '+(c.width===3840?'4K':'Full HD')+' · '+c.fps+' FPS';$('disconnects').textContent='Обрывы: '+x.disconnects;$('frames').textContent=quantity(x.frames);$('measured-fps').textContent=quantity(x.measuredFps);
 $('write-speed').textContent=quantity(x.writeMbps);$('disk-free').textContent=quantity(x.freeBytes/1e9,'ГБ');$('written').textContent=quantity(x.writtenBytes/1e9,'ГБ');$('mic-time').textContent=x.micSeconds?clock(x.micSeconds):'—';$('selected-mic').textContent=c.microphoneName||'Не выбран — открой настройки';$('selected-mic').title=c.microphoneName||'Открыть настройки звука';$('selected-mic').style.cursor='pointer';
 $('logs').textContent=x.logs?.join('\n')||'Журнал пуст.';if(document.querySelector('.log-panel').open)$('logs').scrollTop=$('logs').scrollHeight;
 $('start').disabled=x.busy||!s.toolsReady;$('stop').disabled=!x.busy;$('stop').textContent=x.stage==='exporting'?'Отменить обработку':'Остановить';
 document.querySelectorAll('[data-config]').forEach(e=>e.disabled=x.busy);['save-settings','save-connect','refresh-mics','mic-test','firewall','browse-output','browse-recovery','export','prepare'].forEach(id=>$(id).disabled=x.busy);$('quit').disabled=x.busy;
 $('srt-url').textContent=s.srtUrl.replace(/(passphrase=)[^&]+/,'$1••••••••');if(!$('recoverFolder').dataset.touched)$('recoverFolder').value=s.lastFolder||x.folder||'';
}
async function poll() {if(polling||stopped)return;polling=true;try{const s=await api('state');state=s;if(!initialized){loadConfig(s.config,s.addresses);initialized=true;updateQR();refreshDevices().catch(e=>toast(e.message,true));}render(s);}catch(e){if(!initialized)toast(e.message,true,20000);}finally{polling=false;}}
async function copy(value) {if(navigator.clipboard){try{await navigator.clipboard.writeText(value);toast('Скопировано.');return;}catch{}}
 const e=document.createElement('textarea');e.value=value;document.body.append(e);e.select();document.execCommand('copy');e.remove();toast('Скопировано.');}
action('save-settings',()=>save(true));action('save-connect',()=>save(true));action('refresh-mics',refreshDevices);
action('start',async()=>{await save();if(!state.config.microphone&&!confirm('Отдельный Fifine не выбран. Записать только поток Moblin?')){tab('settings');return;}await api('start','POST');tab('studio');});
action('stop',()=>api('stop','POST'));
action('prepare',()=>api('prepare','POST'));
action('mic-test',async()=>{await save();toast('Говори в выбранный микрофон — идёт тест 3 секунды.',false,10000);const r=await api('mic-test','POST');toast(r.message,false,12000);});
action('firewall',async()=>{await save();if(!confirm('Разрешить входящий SRT для AirTake в частной сети? Windows запросит права администратора.'))return;const r=await api('firewall','POST');toast(r.message);});
action('copy-moblin',async()=>{await save();await copy(state.moblinUrl);});action('copy-srt',async()=>{await save();await copy(state.srtUrl);});
action('browse-output',async()=>{const r=await api('browse','POST');if(r.path){$('outputDir').value=r.path;markDirty();}});
action('browse-recovery',async()=>{const r=await api('browse','POST');if(r.path){$('recoverFolder').value=r.path;$('recoverFolder').dataset.touched='yes';}});
$('recoverFolder').addEventListener('input',()=>$('recoverFolder').dataset.touched='yes');
action('open-last',()=>api('open-folder','POST',{folder:state?.status.folder||state?.lastFolder||''}));
action('open-recovery',()=>api('open-folder','POST',{folder:$('recoverFolder').value}));
action('export',async()=>{await save();await api('export','POST',{folder:$('recoverFolder').value});tab('studio');});
action('diagnostics',async()=>{const r=await fetch('/api/diagnostics',{headers:{'X-AirTake-Token':token}});if(!r.ok)throw new Error('Диагностика недоступна.');const blob=await r.blob(),u=URL.createObjectURL(blob),a=document.createElement('a');a.href=u;a.download='AirTake-diagnostics.zip';a.click();setTimeout(()=>URL.revokeObjectURL(u),2000);});
action('quit',async()=>{await api('shutdown','POST');stopped=true;document.querySelector('main').textContent='AirTake закрыт. Это окно можно закрыть.';});
$('selected-mic').addEventListener('click',()=>tab('settings'));
$('qr').addEventListener('click',()=>{const img=$('qr');img.style.width=img.style.width?'':'min(100%, 480px)';});
poll();setInterval(poll,1000);
