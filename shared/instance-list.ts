import type { Instance } from './model'
export function selectInstances(instances:Instance[],options:{query:string;group:string;filter:string;loader:string;sort:string}) {
  const query=options.query.trim().toLowerCase()
  return instances.filter(i=>(!query||`${i.name} ${i.minecraft} ${i.version} ${i.loader} ${i.group} ${i.root} ${i.gameDir}`.toLowerCase().includes(query))&&(!options.group||i.group===options.group)&&(!options.loader||i.loader===options.loader)&&(options.filter!=='favorite'||i.favorite)&&(options.filter!=='running'||i.running)&&(options.filter!=='recent'||!!i.lastPlayed))
    .sort((a,b)=>{
      if(options.sort==='recent'||options.filter==='recent')return (b.lastPlayed||'').localeCompare(a.lastPlayed||'')||a.name.localeCompare(b.name,'zh-CN',{numeric:true})
      if(options.sort==='version')return b.minecraft.localeCompare(a.minecraft,'en',{numeric:true})||a.name.localeCompare(b.name,'zh-CN',{numeric:true})
      if(options.sort==='name')return a.name.localeCompare(b.name,'zh-CN',{numeric:true})
      return Number(b.favorite)-Number(a.favorite)||a.name.localeCompare(b.name,'zh-CN',{numeric:true})
    })
}
export function assertInstanceUnprotected(instance:Pick<Instance,'name'|'protected'>) {
  if(instance.protected)throw new Error(`“${instance.name}”已开启实例保护，请先在实例设置中明确关闭保护后再执行此操作。`)
}
