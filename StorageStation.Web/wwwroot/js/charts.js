import {number} from './app.js';
const instances=new Map();
export function line(id,rows,series,{percent=false,min,max,spark=false}={}){
 const el=document.getElementById(id);if(!el||!globalThis.echarts)return;
 let chart=instances.get(id);if(!chart){chart=echarts.init(el,null,{renderer:'canvas'});instances.set(id,chart);}
 chart.setOption({animation:false,color:['#0972d3','#e89214','#168777','#845fbd'],grid:spark?{left:0,right:0,top:5,bottom:0}:{left:48,right:22,top:32,bottom:34},
  tooltip:{trigger:'axis',confine:true,valueFormatter:v=>number(v,1)},legend:{show:!spark&&series.length>1,top:0,textStyle:{color:'#5f6b7a',fontSize:11}},
  xAxis:{type:'time',show:!spark,axisLine:{lineStyle:{color:'#d5dbdb'}},axisLabel:{color:'#687787',fontSize:10},splitLine:{show:false}},
  yAxis:{type:'value',show:!spark,min:min??(percent?0:undefined),max:max??(percent?100:undefined),splitLine:{lineStyle:{color:'#edf0f2'}},axisLabel:{color:'#687787',fontSize:10}},
  series:series.map(s=>({name:s.label,type:'line',showSymbol:false,smooth:false,connectNulls:false,lineStyle:{width:spark?1.5:2},areaStyle:spark?{opacity:.08}:undefined,data:rows.map(r=>[r.timestamp,r[s.key]??null])}))},true);
}
export function curve(id,points,current){
 const el=document.getElementById(id);if(!el||!globalThis.echarts)return;
 let chart=instances.get(id);if(!chart){chart=echarts.init(el);instances.set(id,chart);}
 chart.setOption({animation:false,grid:{left:48,right:25,top:25,bottom:40},tooltip:{trigger:'axis'},xAxis:{type:'value',name:'°C',min:25,max:Math.max(55,...points.map(p=>p.temperature)),splitLine:{show:false}},yAxis:{type:'value',name:'PWM %',min:0,max:100,splitLine:{lineStyle:{color:'#edf0f2'}}},series:[{type:'line',data:points.map(p=>[p.temperature,p.percent]),symbolSize:8,lineStyle:{color:'#0972d3',width:2},itemStyle:{color:'#0972d3'},areaStyle:{color:'#0972d3',opacity:.04}},{type:'scatter',data:current?.temperature!=null&&current?.target!=null?[[current.temperature,current.target]]:[],symbolSize:12,itemStyle:{color:'#ff9900'}}]},true);
}
window.addEventListener('resize',()=>instances.forEach(chart=>chart.resize()));
