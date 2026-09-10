import {$,api,boot,number,time,escape,badge,run,toast} from './app.js';
import {line} from './charts.js';
const bay=Number(location.pathname.replace(/\/$/,'').split('/').pop());let diskId=null;
async function history(){if(!diskId)return;const rows=await api(`/api/disks/${encodeURIComponent(diskId)}/history?hours=${$('range').value}`);line('disk-temp-chart',rows,[{key:'temperature',label:'温度 (°C)'}]);line('disk-smart-chart',rows,[{key:'reallocated',label:'重新分配'},{key:'pending',label:'待处理'},{key:'uncorrectable',label:'不可校正'},{key:'crcErrors',label:'CRC 错误'}]);}
function render(data){
 const state=data.bays.find(b=>b.bay===bay);if(!state){$('disk-message').className='notice warning';$('disk-message').textContent='盘位不存在';return;}
 $('disk-title').textContent=`BAY ${String(bay).padStart(2,'0')} · 磁盘详情`;$('disk-health').innerHTML=badge(state.status);const d=state.disk;
 $('disk-content').classList.toggle('hidden',!d);
 if(!d){$('disk-subtitle').textContent=state.serial?'等待已绑定磁盘上线':'空盘位';$('disk-message').className='notice';$('disk-message').innerHTML='此盘位没有可用的磁盘信息。<a href="/settings">配置磁盘绑定 →</a>';diskId=null;return;}
 const changed=diskId!==d.id;diskId=d.id;
 $('disk-subtitle').textContent=d.model;$('disk-message').className='notice '+(d.health.level==='critical'?'critical':d.health.level==='warning'?'warning':'');$('disk-message').textContent=d.health.messages.join('；');
 $('disk-temp').textContent=number(d.temperature);$('temp-time').textContent='采样 '+time(d.temperatureTimestamp);$('disk-hours').textContent=number(d.powerOnHours,0);$('disk-cycles').textContent='启动次数 '+number(d.powerCycles,0);$('disk-sectors').textContent=number(d.reallocated,0)+' / '+number(d.pending,0);$('disk-uncorrectable').textContent='不可校正 '+number(d.uncorrectable,0);$('disk-crc').textContent=number(d.crcErrors,0);
 const pairs=[['型号',d.model],['序列号',d.serial],['永久 ID',d.id],['容量',number(d.capacityBytes==null?null:d.capacityBytes/1e12,2)+' TB'],['类型 / 接口',d.kind+' / '+d.protocol],['运行状态',d.online?'在线':'离线'],['SMART 总体',d.overallPassed==null?'未知':d.overallPassed?'PASSED':'FAILED']];$('disk-info').innerHTML=pairs.map(([k,v])=>`<dt>${escape(k)}</dt><dd>${escape(v)}</dd>`).join('');
 $('test-status').textContent=d.selfTest.status+(d.selfTest.remainingPercent!=null?' · 剩余 '+d.selfTest.remainingPercent+'%':'');$('test-short').disabled=$('test-long').disabled=!d.online||d.selfTest.running;$('smart-time').textContent=time(d.timestamp);
 $('smart-attributes').innerHTML=d.attributes.map(a=>`<tr><td>${a.id}</td><td class="mono">${escape(a.name)}</td><td class="mono">${escape(a.rawText)}</td><td>${a.normalized??'--'}</td><td>${a.worst??'--'}</td><td>${a.threshold??'--'}</td><td>${escape(a.status)}</td></tr>`).join('')||'<tr><td colspan="7" class="empty-state">该设备没有 ATA SMART 属性，请查看对应协议字段。</td></tr>';
 $('nvme-card').classList.toggle('hidden',d.kind!=='NVMe');$('nvme-fields').innerHTML=Object.entries(d.nvme).map(([k,v])=>`<dt>${escape(k)}</dt><dd>${escape(v)}</dd>`).join('');if(changed)run(null,history);
}
for(const kind of ['short','long'])$('test-'+kind).onclick=()=>{if(!confirm(kind==='long'?'执行扩展自检？通常不会删除数据，但可能持续数小时并增加磁盘负载。':'执行短自检？通常不会删除数据，测试期间可能增加磁盘负载。'))return;run($('test-'+kind),async()=>{const result=await api(`/api/disks/${encodeURIComponent(diskId)}/selftest/${kind}`,'POST');toast(result.message);render(await api('/api/disks'));});};
$('range').onchange=()=>run(null,history);
await boot('storage',{disks:render});
