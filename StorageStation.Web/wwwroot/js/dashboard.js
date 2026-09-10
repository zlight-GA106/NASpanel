import {$,api,boot,number,time,escape,badge,modeName} from './app.js';
import {bayCards} from './storage.js';
import {line} from './charts.js';
let history=[],spark=[];
function draw(){line('load-chart',history,[{key:'cpuUsage',label:'CPU 使用率 (%)'},{key:'memoryUsage',label:'内存使用率 (%)'}],{percent:true});}
await boot('overview',{
 system:s=>{
  $('cpu').textContent=number(s.cpu.usage);$('cpu-name').textContent=s.cpu.name;$('temperature').textContent=number(s.cpu.temperature);$('temperature-note').textContent=s.cpu.temperature==null?'温度传感器不可用':s.cpu.temperature>=80?'CPU 高温告警':'CPU 实时温度';
  $('memory').textContent=number(s.memory.usedGb);$('memory-total').textContent='/ '+number(s.memory.totalGb)+' GB';$('memory-percent').textContent=number(s.memory.usage)+' % 已使用';$('memory-bar').style.width=Math.max(0,Math.min(100,s.memory.usage??0))+'%';
  $('rpm').textContent=number(s.fan.rpm,0);$('fan-output').textContent='PWM '+number(s.fan.output,0)+' %';$('fan-mode').textContent=modeName(s.fan.mode)+' →';$('overall').innerHTML=badge(s.status);
  const notices=s.health.messages.filter(x=>x!=='所有已检测组件运行正常');$('health-notice').className='notice '+(s.status==='critical'?'critical':s.status==='warning'?'warning':'')+(notices.length?'':' hidden');$('health-notice').textContent=notices.slice(0,3).join('；');
  const row={timestamp:new Date(s.timestamp).getTime(),cpuUsage:s.cpu.usage,memoryUsage:s.memory.usage,cpuTemperature:s.cpu.temperature};spark.push(row);spark=spark.slice(-90);
  line('cpu-spark',spark,[{key:'cpuUsage',label:'CPU'}],{spark:true,percent:true});line('temp-spark',spark,[{key:'cpuTemperature',label:'CPU 温度'}],{spark:true,min:20,max:90});
  if(!history.length||row.timestamp-history.at(-1).timestamp>=10000){history.push(row);history=history.filter(x=>x.timestamp>Date.now()-3600000);draw();}
  $('host-info').innerHTML=`<dt>主机名</dt><dd>${escape(s.hostname)}</dd><dt>操作系统</dt><dd>${escape(s.os)}</dd><dt>运行时间</dt><dd>${Math.floor(s.uptimeSeconds/86400)} 天 ${Math.floor(s.uptimeSeconds%86400/3600)} 小时 ${Math.floor(s.uptimeSeconds%3600/60)} 分钟</dd>`;
 },disks:bayCards
});
try{history=await api('/api/system/history?hours=1');draw();const events=await api('/api/events');$('recent-events').innerHTML=events.slice(0,5).map(e=>`<tr><td>${escape(new Date(e.timestamp).toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit'}))}</td><td>${badge(e.level)}</td><td class="wrap">${escape(e.message.slice(0,65))}</td></tr>`).join('')||'<tr><td colspan="3" class="empty-state">暂无事件</td></tr>';}catch(error){$('recent-events').innerHTML='<tr><td colspan="3" class="empty-state">事件暂不可用</td></tr>';}
