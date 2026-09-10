export const $ = id => document.getElementById(id);
export const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
export const number = (v, digits=1) => v == null || !Number.isFinite(Number(v)) ? '--' : Number(v).toLocaleString('zh-CN',{maximumFractionDigits:digits,minimumFractionDigits:digits});
export const time = value => value ? new Date(value).toLocaleString('zh-CN',{hour12:false}) : '--';
export const modeName = mode => ({auto:'自动曲线',manual:'手动控制',bios:'BIOS 默认'}[mode] || '--');
export const statusName = level => ({healthy:'正常',warning:'注意',critical:'严重',unknown:'未知',offline:'离线',empty:'空盘位',info:'信息'}[level] || '未知');
export const badge = level => `<span class="badge ${escape(level)}">${statusName(level)}</span>`;
let csrf, toastTimer, displayName='Storage Station';
const pageTitle=document.title.split(' · ')[0];
export function applyDisplayName(name){
 if(!name)return;
 const changed=displayName!==name;displayName=name;
 const title=$('station-name');if(title){title.textContent=name;title.title=name;}
 const breadcrumb=document.querySelector('.breadcrumb a');if(breadcrumb)breadcrumb.textContent=name;
 document.title=`${pageTitle} · ${name}`;
 if(changed)document.dispatchEvent(new CustomEvent('station-name-changed',{detail:name}));
}
export async function renameHost(name){const result=await api('/api/settings/display-name','PUT',{displayName:name});applyDisplayName(result.displayName);toast(result.message);return result.displayName;}
export function applyAccount(account){
 for(const el of document.querySelectorAll('[data-username]')){el.textContent=account.username;el.title=account.username;}
 for(const el of document.querySelectorAll('[data-avatar]')){
  el.replaceChildren();
  if(account.avatar){const img=document.createElement('img');img.src=account.avatar;img.alt='用户头像';el.append(img);}
  else el.textContent=account.username.slice(0,1).toUpperCase();
 }
}
export async function refreshAccount(){const account=await api('/api/auth/me');csrf=(await api('/api/auth/csrf')).token;applyAccount(account);return account;}
export function toast(message, error=false){const el=$('toast');if(!el)return;el.textContent=message;el.className=error?'error':'';el.hidden=false;clearTimeout(toastTimer);toastTimer=setTimeout(()=>el.hidden=true,5500);}
export async function api(path, method='GET', body){
  const headers={};if(body!==undefined)headers['Content-Type']='application/json';
  if(method!=='GET'){if(!csrf)csrf=(await api('/api/auth/csrf')).token;headers['X-CSRF-TOKEN']=csrf;}
  const response=await fetch(path,{method,headers,credentials:'same-origin',body:body===undefined?undefined:JSON.stringify(body)});
  const content=response.headers.get('content-type')||'';
  const data=content.includes('json')?await response.json():null;
  if(response.status===401&&path!=='/api/auth/login'&&location.pathname!=='/login'){location.replace('/login');throw new Error('登录已过期');}
  if(!response.ok)throw new Error(data?.error||(response.status===429?'登录尝试过多，请稍后重试':`请求失败 (${response.status})`));
  return data;
}
export async function run(button, task){if(button)button.disabled=true;try{return await task();}catch(error){toast(error.message,true);}finally{if(button)button.disabled=false;}}
export function shell(active){
 const links=[['overview','/','▦','总览'],['system','/system','◫','系统'],['storage','/storage','▤','存储'],['fans','/fans','✣','风扇'],['remote','/remote','▣','远程控制'],['events','/events','≡','事件日志'],['settings','/settings','⚙','设置']];
 $('shell').innerHTML=`<header class="topbar"><div class="brand-group"><a href="/" class="brand"><i class="brand-mark"></i><span id="station-name">Storage Station</span></a><button id="rename-host" class="brand-rename" title="重命名主机" aria-label="重命名主机">✎</button></div><nav class="toplinks"><a href="/">控制台</a><a href="/storage">存储</a><a href="/fans">风扇</a><a href="/remote">远程控制</a></nav><div class="topright"><span id="connection" class="connection">● 正在连接</span><a href="/settings" class="account-link" aria-label="用户账户"><span class="user-avatar" data-avatar aria-hidden="true">A</span><span data-username>Admin</span></a><button id="logout">退出</button></div></header><aside class="sidebar"><div class="side-title">管理控制台</div>${links.map(([key,url,icon,label])=>`<a href="${url}" ${key===active?'class="active" aria-current="page"':''}><span class="nav-icon">${icon}</span>${label}</a>`).join('')}</aside><dialog id="rename-dialog" aria-labelledby="rename-title"><form id="rename-form"><h2 id="rename-title">重命名主机</h2><label for="host-display-name">主机显示名称</label><input id="host-display-name" required maxlength="80" autocomplete="off"><p id="rename-error" class="error-text" role="alert"></p><div class="toolbar"><button type="button" id="cancel-rename">取消</button><button id="confirm-rename" class="primary">保存名称</button></div></form></dialog>`;
 $('logout').onclick=()=>run($('logout'),async()=>{await api('/api/auth/logout','POST');location.replace('/login');});
 $('rename-host').onclick=()=>{$('host-display-name').value=displayName;$('rename-error').textContent='';$('rename-dialog').showModal();$('host-display-name').select();};
 $('cancel-rename').onclick=()=>$('rename-dialog').close();
 $('rename-form').onsubmit=async event=>{event.preventDefault();$('confirm-rename').disabled=true;try{await renameHost($('host-display-name').value);$('rename-dialog').close();}catch(error){$('rename-error').textContent=error.message;}finally{$('confirm-rename').disabled=false;}};
}
export async function boot(active, handlers={}){
 shell(active);
 try{
  const me=await refreshAccount();
  if(me.isDevelopment){const banner=$('environment');if(banner){banner.textContent='开发模式：当前显示模拟硬件与磁盘数据，控制操作不会写入真实硬件。';banner.classList.remove('hidden');}}
  const [system,disks]=await Promise.all([api('/api/system'),api('/api/disks')]);
  const onSystem=data=>{applyDisplayName(data.displayName);$('updated')&&($('updated').textContent='最后采样 '+time(data.timestamp));handlers.system?.(data);};
  onSystem(system);handlers.disks?.(disks);
  const connection=new signalR.HubConnectionBuilder().withUrl('/hubs/realtime').withAutomaticReconnect({nextRetryDelayInMilliseconds:c=>Math.min(30000,1000*2**Math.min(c.previousRetryCount,5))}).configureLogging(signalR.LogLevel.Error).build();
  const state=(text,connected=false)=>{const el=$('connection');el.textContent=text;el.classList.toggle('connected',connected);};
  connection.on('system',onSystem);connection.on('disks',data=>handlers.disks?.(data));
  connection.onreconnecting(()=>state('● 连接断开，正在重连…'));
  connection.onreconnected(async()=>{state('● 实时已连接',true);try{onSystem(await api('/api/system'));handlers.disks?.(await api('/api/disks'));}catch(e){toast(e.message,true);}});
  const start=async()=>{try{await connection.start();state('● 实时已连接',true);}catch{state('● 连接断开，5 秒后重试');setTimeout(start,5000);}};
  connection.onclose(()=>{state('● 连接已断开');setTimeout(start,5000);});
  await start();return {system,disks,me};
 }catch(error){toast(error.message,true);return null;}
}
