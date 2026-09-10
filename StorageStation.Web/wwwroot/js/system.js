import {$,api,boot,number,escape,run} from './app.js';
import {line} from './charts.js';
let rows=[];
const specs=[['cpu-chart','cpuUsage','CPU (%)',true],['temp-chart','cpuTemperature','CPU 温度 (°C)',false],['memory-chart','memoryUsage','内存 (%)',true],['rpm-chart','fanRpm','风扇 (RPM)',false]];
function draw(){for(const [id,key,label,percent]of specs)line(id,rows,[{key,label}],{percent});}
async function load(){rows=await api('/api/system/history?hours='+$('range').value);draw();}
async function sensors(){const list=await api('/api/sensors');$('sensor-table').innerHTML=list.map(s=>`<tr><td>${escape(s.hardware)}</td><td>${escape(s.name)}</td><td>${escape(s.type)}</td><td>${number(s.value)}</td><td class="mono">${escape(s.id)}</td><td>${s.writable?'是':'否'}</td></tr>`).join('')||'<tr><td colspan="6" class="empty-state">尚未发现传感器，请检查权限与硬件支持情况。</td></tr>';}
$('range').onchange=()=>run(null,load);$('refresh-sensors').onclick=()=>run($('refresh-sensors'),sensors);
await boot('system',{system:s=>{$('current-cpu').textContent=number(s.cpu.usage)+' %';$('current-temp').textContent=number(s.cpu.temperature)+' °C';$('current-memory').textContent=number(s.memory.usage)+' %';$('current-rpm').textContent=number(s.fan.rpm,0)+' RPM';if($('range').value==='1'){const row={timestamp:Date.parse(s.timestamp),cpuUsage:s.cpu.usage,memoryUsage:s.memory.usage,cpuTemperature:s.cpu.temperature,fanRpm:s.fan.rpm};if(!rows.length||row.timestamp-rows.at(-1).timestamp>=10000){rows.push(row);rows=rows.filter(r=>r.timestamp>Date.now()-3600000);draw();}}}});
await run(null,async()=>{await Promise.all([load(),sensors()]);});
