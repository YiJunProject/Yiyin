'use strict';
// Visual only: caller-owned controls retain their authorization, pointer and reset handlers.
window.YideckRackRow=(()=>{
  const height=(appearance,section)=>Math.max(24,Math.min(72,Number(appearance?.[section+'ItemHeight'])||(section==='insert'?26:28)));
  function fit(row){
    if(!row?.isConnected)return;
    const narrow=row.clientWidth<164,pitch=Number(row.dataset.rowHeight),has=cls=>!!row.querySelector('.'+cls);
    row.style.gridTemplateColumns=`${has('compact-rack-prefix')?(narrow?18:Math.min(28,pitch-2)):0}px minmax(0,1fr) ${has('compact-rack-number')?(narrow?30:Math.min(50,Math.max(36,pitch*1.45))):0}px ${has('compact-rack-power')?(narrow?22:Math.min(32,pitch)):0}px`;
    for(const label of row.querySelectorAll('.compact-rack-label,.surface-button-label,.rack-number,.surface-number')){
      if(!label.clientWidth||!label.clientHeight)continue;label.style.whiteSpace='nowrap';
      let lo=8,hi=Math.min(18,Number(row.dataset.rowHeight)*.43,label.clientHeight*.8);
      for(let i=0;i<8;i++){const n=(lo+hi)/2;label.style.fontSize=n+'px';if(label.scrollWidth>label.clientWidth+1||label.scrollHeight>label.clientHeight+1)hi=n;else lo=n;}label.style.fontSize=Math.max(8,lo)+'px';
    }
  }
  const resize=new ResizeObserver(entries=>{for(const e of entries)fit(e.target);});
  function create({height:pitch=28,color='#347e91',name,prefix,power,number,range}){
    const row=document.createElement('div');row.className='compact-rack-row';row.dataset.rowHeight=pitch;row.style.setProperty('--rack-item-height',pitch+'px');row.style.setProperty('--row-color',/^#[0-9a-f]{6}$/i.test(color||'')?color:'#347e91');
    row.style.gridTemplateColumns=`${prefix?Math.min(28,pitch-2):0}px minmax(0,1fr) ${number?Math.min(50,Math.max(36,pitch*1.45)):0}px ${power?Math.min(32,pitch):0}px`;
    const put=(node,cls,col)=>{if(!node)return;node.classList.add(cls);node.style.gridColumn=col;row.append(node);};put(prefix,'compact-rack-prefix',1);put(number,'compact-rack-number',3);put(power,'compact-rack-power',4);
    const center=document.createElement('div');center.className='compact-rack-center';center.style.gridColumn=2;row.append(center);
    if(name){name.classList.add('compact-rack-name');if(!name.matches('button'))name.classList.add('compact-rack-label');center.append(name);}
    if(range){center.classList.add('has-range');range.classList.add('compact-rack-range');center.append(range);}
    resize.observe(row);requestAnimationFrame(()=>fit(row));return row;
  }
  function release(root){for(const row of root.querySelectorAll('.compact-rack-row'))resize.unobserve(row);}
  return {height,create,fit,release};
})();
