import { expect, it, vi } from 'vitest'
import { reactive } from 'vue'
it('serializes nested Vue preferences at the desktop boundary',async()=>{
  const invoke=vi.fn(async(_method:string,payload:unknown)=>structuredClone(payload))
  vi.stubGlobal('window',{launcher:{invoke}})
  try{
    const {api}=await import('../src/store')
    const settings=reactive({javaRoots:['C:\\Java'],nested:{fontScale:125}})
    expect(await api('settings.save',{...settings})).toEqual({javaRoots:['C:\\Java'],nested:{fontScale:125}})
    await api('snapshot');expect(invoke.mock.calls[1][1]).toBeUndefined()
  }finally{vi.unstubAllGlobals()}
})
