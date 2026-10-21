import { randomUUID } from 'node:crypto'
import type { TaskInfo } from '../shared/model'
export function taskFailure(error:unknown,depth=0):string {
  if(depth>=5)return '更多嵌套错误'
  if(error instanceof AggregateError){const text=[error.message,...error.errors.slice(0,4).map(e=>taskFailure(e,depth+1))].filter(Boolean);return [...new Set(text)].join('；')||'下载任务失败'}
  if(error instanceof Error){const cause=error.cause?taskFailure(error.cause,depth+1):'',code='code' in error?String(error.code):'';let location='';if('downloadUrl' in error&&typeof error.downloadUrl==='string'){try{const url=new URL(error.downloadUrl);location=url.origin+url.pathname}catch{location='下载地址无效'}}return [error.message||error.name,code,location,cause].filter(Boolean).join('：')}
  return String(error)
}
export class Tasks {
  entries: TaskInfo[] = []
  private controllers = new Map<string, AbortController>()
  private completions=new Map<string,{promise:Promise<void>;resolve:()=>void}>()
  async wait(id:string,signal?:AbortSignal){const completion=this.completions.get(id);if(!completion)throw new Error('任务不存在');const abort=()=>this.controllers.get(id)?.abort();signal?.addEventListener('abort',abort,{once:true});try{if(signal?.aborted)abort();await completion.promise;const task=this.entries.find(t=>t.id===id);if(!task||task.status!=='success')throw new Error(task?.detail||'任务已取消')}finally{signal?.removeEventListener('abort',abort)}}
  constructor(private emit: (type: string, data: unknown) => void, private log: (text: string, level?: 'info' | 'error', instanceId?: string) => void) {}
  start(title: string, work: (task: TaskInfo, signal: AbortSignal, update: (detail: string, progress?: number) => void) => Promise<void>, instanceId?: string) {
    this.entries = this.entries.filter(t => t.status === 'running').concat(this.entries.filter(t => t.status !== 'running').slice(-80))
    const task: TaskInfo = { id: randomUUID(), title, status: 'running', progress: 0, detail: '正在准备', createdAt: new Date().toISOString(), instanceId }
    this.entries.push(task)
    for(const key of this.completions.keys())if(!this.entries.some(t=>t.id===key))this.completions.delete(key)
    let resolve!:()=>void;const promise=new Promise<void>(r=>resolve=r);this.completions.set(task.id,{promise,resolve})
    const controller = new AbortController(); this.controllers.set(task.id, controller)
    const update = (detail: string, progress?: number) => { task.detail = detail; if (progress !== undefined) task.progress = Math.min(100, Math.max(0, progress)); this.emit('tasks', this.entries) }
    this.emit('tasks', this.entries)
    void Promise.resolve().then(() => work(task, controller.signal, update)).then(() => {
      task.status = controller.signal.aborted ? 'cancelled' : 'success'; task.progress = 100; task.detail = controller.signal.aborted ? '已取消' : '已完成'
    }, error => {
      task.status = controller.signal.aborted ? 'cancelled' : 'error'; task.detail = controller.signal.aborted ? '已取消，可重新执行以续传' : taskFailure(error).slice(0, 1500)
      this.log(`${title}：${task.detail}`, task.status === 'error' ? 'error' : 'info', instanceId)
    }).finally(() => { this.controllers.delete(task.id); this.emit('tasks', this.entries);this.completions.get(task.id)?.resolve() })
    return task.id
  }
  cancel(id: string) { const c = this.controllers.get(id); if (!c) throw new Error('任务已经结束'); c.abort() }
  shutdown() { this.controllers.forEach(c => c.abort()) }
}
