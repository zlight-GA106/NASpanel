import {$,api,boot,escape,run,toast,renameHost} from './app.js';
import {initAccount} from './account.js';
let settings,disks,sensors;
const numericFields=[
 ['system-fields','realtimeIntervalMs','实时推送周期 (ms)',1000,10000],['system-fields','systemRetentionDays','系统历史保留 (天)',1,90],['system-fields','diskRetentionDays','SMART 历史保留 (天)',30,3650],['system-fields','eventRetentionDays','事件保留 (天)',30,3650],
 ['smart-fields','smartIntervalSeconds','完整 SMART 周期 (秒)',60,3600],['smart-fields','diskTemperatureIntervalSeconds','磁盘温度周期 (秒)',15,300],['smart-fields','diskWarningTemperature','磁盘警告温度 (°C)',30,69],['smart-fields','diskCriticalTemperature','磁盘严重温度 (°C)',31,70],
 ['sensor-fields','fan.minimumPercent','最低 PWM (%)',30,100],['sensor-fields','fan.maximumPercent','最高正常 PWM (%)',30,100],['sensor-fields','fan.cpuBoostTemperature','CPU 增速温度 (°C)',40,89],['sensor-fields','fan.cpuEmergencyTemperature','CPU 紧急温度 (°C)',41,90],['sensor-fields','fan.diskEmergencyTemperature','硬盘紧急温度 (°C)',40,60],['sensor-fields','fan.minimumSafeRpm','最低安全 RPM（可留空）',0,10000]
];
const id=path=>path.replaceAll('.','-');const value=path=>path.split('.').reduce((v,k)=>v[k],settings);
function addField(parent,html){$(parent).insertAdjacentHTML('beforeend',`<div class="field">${html}</div>`);}
function mapping(data){disks=data;if(!$('bay-mapping')||!settings)return;const known=[...data.bays.map(b=>b.disk).filter(Boolean),...data.unassigned];$('bay-mapping').innerHTML=data.bays.map(b=>`<tr><td>BAY ${String(b.bay).padStart(2,'0')}</td><td><select data-bay="${b.bay}" aria-label="BAY ${b.bay} 磁盘"><option value="">空 / 解除绑定</option>${known.map(d=>`<option value="${escape(d.serial)}" ${d.serial===b.serial?'selected':''}>${escape(d.model)} · ${escape(d.serial)}${d.online?'':'（离线）'}</option>`).join('')}${b.serial&&!known.some(d=>d.serial===b.serial)?`<option selected value="${escape(b.serial)}">${escape(b.serial)}（尚未发现）</option>`:''}</select></td><td><button data-save-bay="${b.bay}" class="small">保存</button></td></tr>`).join('');}
function render(){
 for(const target of ['system-fields','sensor-fields','smart-fields'])$(target).innerHTML='';
 addField('system-fields',`<label for="displayName">主机显示名称</label><div class="name-field"><input id="displayName" required maxlength="80" value="${escape(settings.displayName)}"><button id="save-host-name" type="button">重命名</button></div>`);
 $('save-host-name').onclick=()=>run($('save-host-name'),async()=>{settings.displayName=await renameHost($('displayName').value);$('displayName').value=settings.displayName;});
 for(const [parent,path,label,min,max] of numericFields)addField(parent,`<label for="${id(path)}">${label}</label><input id="${id(path)}" type="number" min="${min}" max="${max}" value="${value(path)??''}">`);
 for(const [path,label,type] of [['cpuTemperatureSensorId','CPU 温度传感器','Temperature'],['fanRpmSensorId','风扇 RPM 传感器','Fan'],['fanControlSensorId','可写风扇控制器','Control']]){
  const matching=sensors.filter(s=>s.type===type&&(type!=='Control'||s.writable));addField('sensor-fields',`<label for="${path}">${label}</label><select id="${path}"><option value="">${type==='Control'?'未绑定（禁止写入）':'自动发现（只读）'}</option>${matching.map(s=>`<option value="${escape(s.id)}" ${s.id===settings[path]?'selected':''}>${escape(s.name)} · ${escape(s.id)}</option>`).join('')}${settings[path]&&!matching.some(s=>s.id===settings[path])?`<option selected value="${escape(settings[path])}">${escape(settings[path])}（未发现）</option>`:''}</select>`);
 }
 addField('sensor-fields',`<label><input id="fan-enabled" type="checkbox" ${settings.fan.enabled?'checked':''}> 启用软件风扇控制</label><small>确认目标风扇和控制器对应关系后启用。</small>`);
 for(const [path,label]of [['weeklyShortTest','每周一次短自检'],['monthlyLongTest','每月一次扩展自检']])addField('smart-fields',`<label><input id="${path}" type="checkbox" ${settings[path]?'checked':''}> ${label}</label><small>启用后自动依次测试在线磁盘，每次仅调度一块。</small>`);
 mapping(disks);
}
$('save-settings').onclick=()=>run($('save-settings'),async()=>{
 const next=structuredClone(settings);
 for(const [,path]of numericFields){const input=$(id(path));if(!input.checkValidity())throw new Error('数值超出允许范围：'+input.previousElementSibling.textContent);const keys=path.split('.');let target=next;for(const key of keys.slice(0,-1))target=target[key];target[keys.at(-1)]=input.value===''&&path==='fan.minimumSafeRpm'?null:Number(input.value);}
 for(const path of ['cpuTemperatureSensorId','fanRpmSensorId','fanControlSensorId'])next[path]=$(path).value||null;
 next.fan.enabled=$('fan-enabled').checked;next.weeklyShortTest=$('weeklyShortTest').checked;next.monthlyLongTest=$('monthlyLongTest').checked;
 const result=await api('/api/settings','PUT',next);settings=next;toast(result.message);
});
$('bay-mapping').onclick=e=>{const button=e.target.closest('[data-save-bay]');if(button)run(button,async()=>{const bay=button.dataset.saveBay;const serial=document.querySelector(`[data-bay="${bay}"]`).value;await api(`/api/settings/bays/${bay}`,'PUT',{serial:serial||null});toast('BAY '+bay+' 映射已保存');mapping(await api('/api/disks'));});};
const state=await boot('settings',{disks:data=>{if(!settings)disks=data;}});
if(state){initAccount(state.me);await run(null,async()=>{[settings,sensors]=await Promise.all([api('/api/settings'),api('/api/sensors')]);render();});}
document.addEventListener('station-name-changed',event=>{if(settings){settings.displayName=event.detail;if($('displayName'))$('displayName').value=event.detail;}});
