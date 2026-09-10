import {$,escape,number,badge,api,boot,time} from './app.js';
export function bayCards(data,id='bays'){
 const target=$(id);if(!target)return;
 target.innerHTML=data.bays.map(b=>{
  const d=b.disk;return `<a href="/storage/${b.bay}" class="card bay ${b.status==='empty'?'empty':''}"><div class="bay-top"><span class="bay-number">BAY ${String(b.bay).padStart(2,'0')}</span>${badge(b.status)}</div>${d?`<div class="bay-model" title="${escape(d.model)}">${escape(d.model)}</div><div class="bay-meta">${number(d.capacityBytes==null?null:d.capacityBytes/1e12,2)} TB · ${escape(d.protocol)} · ${escape(d.kind)}</div><div class="bay-bottom"><div class="bay-temp">${number(d.temperature,0)} <small>°C</small></div><div class="bay-hours">通电 ${number(d.powerOnHours,0)} h</div></div>${['warning','critical','offline','unknown'].includes(b.status)?`<div class="bay-alert" title="${escape(d.health.messages.join('；'))}">${escape(d.health.messages[0])}</div>`:`<div class="bay-meta">S/N ${escape(maskSerial(d.serial))}</div>`}`:`<div class="empty-label">${b.serial?'等待已绑定磁盘上线':'未安装磁盘'}</div>`}</a>`;
 }).join('');
 const online=data.bays.filter(b=>b.disk?.online).length;const attention=data.bays.filter(b=>['warning','critical','offline'].includes(b.status)).length;
 if($('disk-count'))$('disk-count').textContent=`${online} / 8 在线 · ${attention} 个需关注`;
 if($('unassigned')){$('unassigned').classList.toggle('hidden',data.unassigned.length===0);$('unassigned').innerHTML=`检测到 ${data.unassigned.length} 块未分配磁盘。<a href="/settings">前往设置绑定盘位 →</a>`;}
}
export function maskSerial(serial){return serial?.length>8?serial.slice(0,4)+'••••'+serial.slice(-4):serial;}
if(document.body.dataset.page==='storage'){
 await boot('storage',{disks:data=>{bayCards(data);$('inventory').innerHTML=data.bays.map(b=>`<tr><td><a href="/storage/${b.bay}">BAY ${String(b.bay).padStart(2,'0')}</a></td><td>${escape(b.disk?.model||'--')}</td><td class="mono">${escape(maskSerial(b.serial)||'--')}</td><td>${b.disk?number(b.disk.capacityBytes==null?null:b.disk.capacityBytes/1e12,2)+' TB':'--'}</td><td>${badge(b.status)}</td><td>${time(b.disk?.timestamp)}</td></tr>`).join('');}});
}
