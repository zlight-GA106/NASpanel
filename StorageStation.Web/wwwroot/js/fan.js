import {$,api,boot,number,escape,badge,modeName,run,toast} from './app.js';
import {curve} from './charts.js';
let config,live,manualOpen=false;
function points(){return [...$('curve-points').rows].map(row=>({temperature:Number(row.querySelector('[data-temp]').value),percent:Number(row.querySelector('[data-pwm]').value)}));}
function draw(){if(config)curve('curve-chart',points(),live);}
function rows(list){$('curve-points').innerHTML=list.map(p=>`<tr><td><input data-temp type="number" min="0" max="100" step="0.5" value="${p.temperature}" aria-label="温度"></td><td><input data-pwm type="number" min="30" max="100" value="${p.percent}" aria-label="PWM"></td><td><button data-remove class="small">删除</button></td></tr>`).join('');draw();}
function render(fan){live=fan;$('fan-health').innerHTML=badge(fan.health.level);$('fan-mode-label').textContent=modeName(fan.mode);$('fan-rpm').textContent=number(fan.rpm,0);$('fan-pwm').textContent=number(fan.output,0);$('fan-target').textContent=number(fan.target,0);$('control-temperature').textContent='控制温度：'+number(fan.temperature)+' °C';
 const notice=!fan.canControl?'当前主板仅提供风扇转速读取，未检测到可写风扇控制器。请在设置页绑定控制器；不支持时可保持 BIOS 控制。':!config?.enabled?'软件风扇控制未启用，请前往设置启用。':fan.safetyReason;
 $('fan-notice').textContent=notice||'';$('fan-notice').classList.toggle('hidden',!notice);
 for(const mode of ['auto','manual','bios'])$('mode-'+mode).classList.toggle('active',fan.mode===mode);
 $('mode-auto').disabled=$('mode-manual').disabled=$('manual-apply').disabled=!fan.canControl||!config?.enabled;
 $('manual-remaining').textContent=fan.manualUntil?'手动剩余 '+Math.max(0,Math.ceil((Date.parse(fan.manualUntil)-Date.now())/60000))+' 分钟，到期恢复自动曲线。':'';draw();
}
function openManual(){manualOpen=!manualOpen;$('manual-panel').classList.toggle('hidden',!manualOpen);}
$('mode-manual').onclick=openManual;
for(const mode of ['auto','bios'])$('mode-'+mode).onclick=()=>run($('mode-'+mode),async()=>{const r=await api('/api/fans/chassis/mode','PUT',{mode});toast(r.message);});
$('manual-percent').oninput=()=>{$('manual-display').textContent=$('manual-percent').value;};
$('manual-apply').onclick=()=>run($('manual-apply'),async()=>{const r=await api('/api/fans/chassis/speed','PUT',{percent:Number($('manual-percent').value),minutes:Number($('manual-minutes').value)});toast(r.message);});
$('curve-points').oninput=draw;$('curve-points').onclick=e=>{if(e.target.matches('[data-remove]')&&$('curve-points').rows.length>2){e.target.closest('tr').remove();draw();}};
$('add-point').onclick=()=>{const p=points();if(p.length>=12)return;const last=p.at(-1);rows([...p,{temperature:Math.min(100,last.temperature+5),percent:last.percent}]);};
$('save-curve').onclick=()=>run($('save-curve'),async()=>{const r=await api('/api/fans/chassis/curve','PUT',{curve:points(),temperatureSource:$('temperature-source').value});toast(r.message);config.curve=points();});
await boot('fans',{system:s=>render(s.fan)});
await run(null,async()=>{const data=await api('/api/fans');config=data.settings;rows(config.curve);$('temperature-source').value=config.temperatureSource;$('manual-percent').min=config.minimumPercent;$('manual-percent').max=config.maximumPercent;$('fan-safety').innerHTML=`<p>CPU ≥ <strong>${config.cpuBoostTemperature}°C</strong>：输出至少 70%<br>CPU ≥ <strong>${config.cpuEmergencyTemperature}°C</strong>：强制 100%<br>硬盘 ≥ <strong>${config.diskEmergencyTemperature}°C</strong>：强制 100%</p><p>温度缺失或过期时启用保护输出。PWM &gt; 30% 且 RPM 持续为 0 达 ${config.stallSeconds} 秒时，记录严重事件。</p><p>升速：${config.riseHysteresis}°C 回差 / ${config.riseDelaySeconds} 秒响应<br>降速：${config.fallHysteresis}°C 回差 / ${config.fallDelaySeconds} 秒延迟<br>正常调速每次变化不超过 ${config.maximumStep}%</p><p>手动模式最长 60 分钟。服务正常停止与可捕获异常时尝试恢复 BIOS。</p>`;render(data.fan);});
