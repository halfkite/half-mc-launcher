import { reactive, ref } from 'vue'
import type { Snapshot, PageId, Workspace } from '../shared/model'
import { defaultWorkspace } from '../shared/workspace'
export const pages: Record<PageId,{title:string;sub:string}> = {instances:{title:'实例',sub:'你的世界，从这里开始'},install:{title:'下载',sub:'游戏、模组、资源包与光影'},resources:{title:'下载',sub:'游戏、模组、资源包与光影'},tasks:{title:'任务中心',sub:'每一步，都有迹可循'},tools:{title:'工具箱',sub:'让启动更得心应手'},settings:{title:'设置',sub:'打造适合你的启动器'},plugins:{title:'扩展',sub:'让工作区拥有更多能力'},search:{title:'搜索',sub:'查找页面、实例与操作'},mods:{title:'Mod 管理',sub:'查看与管理这个实例的模组'},instance:{title:'实例设置',sub:'调整名称、内存与 Java'},content:{title:'内容管理',sub:'Mod、资源包、光影与存档'},logs:{title:'日志与崩溃',sub:'查看运行日志与崩溃原因'},servers:{title:'服务器列表',sub:'管理 servers.dat 中的服务器'},skin:{title:'皮肤与披风',sub:'预览当前账户外观'},about:{title:'关于',sub:'版本、运行环境与许可'}}
export const data = reactive<Snapshot>({state:{schemaVersion:1,workspace:defaultWorkspace(),presets:[],sources:[],options:{},hidden:[],accounts:[],selectedAccount:'',settings:{theme:'light',accent:'#74b5ff',background:'',memory:4096,java:'',clientId:'',reduceMotion:false,fontScale:100,opacity:100,glass:true,memoryMode:'auto',download:{source:'auto',threads:4,retries:2,timeout:60},updateUrl:'',javaRoots:[]},lastPlayed:{},windowBounds:{width:1440,height:930,maximized:false}},instances:[],tasks:[],logs:[],dataPath:'',appVersion:'0.1.0',memoryMb:16384,scanWarnings:[]})
export const workspace = ref<Workspace>(defaultWorkspace())
export const connected = ref(!!window.launcher)
export const ready = ref(false)
export const notice = ref('')
export const busy = reactive(new Set<string>())
let noticeTimer: ReturnType<typeof setTimeout>
export function toast(message:string) { notice.value=message;clearTimeout(noticeTimer);noticeTimer=setTimeout(()=>notice.value='',6500) }
export async function api<T=any>(method:string,payload?:unknown):Promise<T> {
  if(!window.launcher)throw new Error('请通过 Half 桌面程序使用此功能')
  // Electron cannot clone Vue proxies, including nested arrays in shallow spreads.
  const message=payload===undefined?undefined:JSON.parse(JSON.stringify(payload))
  return window.launcher.invoke<T>(method,message)
}
export async function run<T=any>(key:string,method:string,payload?:unknown):Promise<T|undefined>{
  if(busy.has(key))return
  busy.add(key)
  try{return await api<T>(method,payload)}catch(e:any){toast(e.message)}finally{busy.delete(key)}
}
export function applySnapshot(snapshot:Snapshot){Object.assign(data,snapshot)}
export async function initialize(){
  if(connected.value){try{const snapshot=await api<Snapshot>('snapshot');applySnapshot(snapshot);workspace.value=structuredClone(snapshot.state.workspace)}catch(e:any){toast(e.message)}}
  ready.value=true
}
