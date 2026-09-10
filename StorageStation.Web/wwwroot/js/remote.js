import {$,api,boot,run,toast} from './app.js';
let rfb=null,connected=false;
function state(text,online=false){$('remote-status').textContent=text;$('remote-status').className='badge '+(online?'healthy':'unknown');$('send-cad').disabled=!online;$('disconnect').disabled=!rfb;connected=online;}
function cleanup(message){rfb=null;state('未连接');$('connect-remote').disabled=false;$('remote-stage').replaceChildren();$('remote-stage').classList.add('hidden');$('remote-placeholder').classList.remove('hidden');$('remote-notice').textContent=message;$('credentials').open&&$('credentials').close();$('vnc-password').value='';}
async function check(){const status=await api('/api/remote/status');$('remote-notice').textContent=status.available?'VNC 与 websockify 服务已就绪，可以连接。':'远程桌面服务不可用：'+(!status.vnc?'VNC :5900 未就绪。 ':'')+(!status.websockify?'websockify :6080 未就绪。':'');return status;}
$('connect-remote').onclick=()=>run($('connect-remote'),async()=>{
 if(rfb)return;
 const status=await check();if(!status.available)return;
 const {default:RFB}=await import('/novnc/core/rfb.js');
 $('remote-placeholder').classList.add('hidden');$('remote-stage').classList.remove('hidden');
 const url=`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}${status.path}`;
 rfb=new RFB($('remote-stage'),url);rfb.scaleViewport=true;rfb.resizeSession=false;rfb.background='#18222e';state('正在连接');
 rfb.addEventListener('connect',()=>{state('已连接',true);$('connect-remote').disabled=true;$('remote-notice').textContent='远程桌面已连接。';});
 rfb.addEventListener('disconnect',e=>cleanup(e.detail.clean?'远程桌面已断开。':'连接失败或被关闭，请检查 VNC 密码、IIS WebSocket 与服务日志。'));
 rfb.addEventListener('credentialsrequired',e=>{$('vnc-username-field').classList.toggle('hidden',!e.detail.types.includes('username'));$('credentials').showModal();$('vnc-password').focus();});
 rfb.addEventListener('securityfailure',e=>{toast('VNC 身份验证失败：'+(e.detail.reason||'请检查凭据'),true);});
});
$('credentials-form').onsubmit=e=>{e.preventDefault();if(!rfb)return;rfb.sendCredentials({username:$('vnc-username').value,password:$('vnc-password').value});$('vnc-password').value='';$('credentials').close();};
$('cancel-credentials').onclick=()=>{rfb?.disconnect();$('credentials').close();};$('credentials').addEventListener('cancel',()=>rfb?.disconnect());
$('disconnect').onclick=()=>{rfb?.disconnect();};$('send-cad').onclick=()=>{if(connected)rfb.sendCtrlAltDel();};
$('fullscreen').onclick=()=>run($('fullscreen'),async()=>{if(document.fullscreenElement)await document.exitFullscreen();else await $('remote-screen').requestFullscreen();});
window.addEventListener('beforeunload',()=>rfb?.disconnect());
await boot('remote');await run(null,check);
