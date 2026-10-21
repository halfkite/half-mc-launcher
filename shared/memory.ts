/** Heap budget leaves physical memory for Windows, native allocations and other apps. */
export function memoryBudget(totalMb:number, freeMb:number, arch:string, modded:boolean, modCount=0) {
  if(!Number.isFinite(totalMb)||!Number.isFinite(freeMb)||totalMb<=0||freeMb<0)throw new Error('无法读取系统可用内存')
  const reserve=Math.max(1024,Math.round(totalMb*0.1))
  const ceiling=Math.floor(Math.min(totalMb*0.6,freeMb-reserve,/^(x86|i[3-6]86)$/i.test(arch)?1024:65536)/256)*256
  if(ceiling<512)throw new Error('当前可用内存不足，请先关闭其他程序后重试')
  const desired=modded?4096+Math.ceil(Math.max(0,modCount-40)/40)*1024:2048
  return Math.max(512,Math.min(desired,ceiling))
}
