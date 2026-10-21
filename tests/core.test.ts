import { describe, expect, it, afterEach } from 'vitest'
import { mkdtemp, mkdir, writeFile, readFile, rm, symlink } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import { defaultWorkspace, moveTab, repairWorkspace, newTab, visibleCount, closeWorkspaceTab, restoreWorkspaceTab, resizeSidebar, toggleSidebar } from '../shared/workspace'
import { workspaceSchema, settingsSchema } from '../shared/model'
import { Store, defaultState } from '../electron/storage'
import { findRoots, scanSources, assertSafeVersionDirectory, inside, versionDependents, canonical } from '../electron/instances'
import { Tasks } from '../electron/tasks'
import { appearanceVariables, foreground, contrastRatio } from '../shared/appearance'
import { selectInstances, assertInstanceUnprotected } from '../shared/instance-list'
import type { Instance } from '../shared/model'

const directories:string[]=[]
const temp=async()=>{const d=await mkdtemp(path.join(os.tmpdir(),'hakf-test-'));directories.push(d);return d}
afterEach(async()=>{for(const d of directories.splice(0))await rm(d,{recursive:true,force:true})})
async function version(root:string,name:string,metadata:unknown={id:name,mainClass:'net.minecraft.client.main.Main'}){const dir=path.join(root,'versions',name);await mkdir(dir,{recursive:true});await writeFile(path.join(dir,`${name}.json`),typeof metadata==='string'?metadata:JSON.stringify(metadata));return dir}

describe('independent workspace state',()=>{
  it('restores a closed tab with its exact identity and independent page state',()=>{const w=defaultWorkspace(),p=w.panes[0],tab=p.tabs[0];tab.query='测试';tab.view={loaderFilter:'Fabric'};closeWorkspaceTab(w,p.id,tab.id);expect(p.tabs[0].id).not.toBe(tab.id);expect(restoreWorkspaceTab(w)).toBe(true);expect(p.tabs.find(t=>t.id===tab.id)).toMatchObject({query:'测试',view:{loaderFilter:'Fabric'}});expect(p.active).toBe(tab.id);expect(w.closedTabs).toHaveLength(0)})
  it('retains closed-tab history when restoration is blocked by the tab limit',()=>{const w=defaultWorkspace(),p=w.panes[0];closeWorkspaceTab(w,p.id,p.active);while(p.tabs.length<40)p.tabs.push(newTab());expect(restoreWorkspaceTab(w)).toBe(false);expect(w.closedTabs).toHaveLength(1)})
  it('uses hysteresis when resizing the sidebar and restores its expanded width',()=>{const w=defaultWorkspace();resizeSidebar(w,136);expect(w.sidebarCollapsed).toBe(true);resizeSidebar(w,144);expect(w.sidebarCollapsed).toBe(true);resizeSidebar(w,180);expect(w.sidebarCollapsed).toBe(false);toggleSidebar(w);expect(w.sidebarCollapsed).toBe(true);toggleSidebar(w);expect(w.sidebarWidth).toBe(180);resizeSidebar(w,-100);expect(w.sidebarWidth).toBe(56);resizeSidebar(w,999);expect(w.sidebarWidth).toBe(320)})
  it('defaults to one pane and preserves hidden pane tabs when switching layouts',()=>{const w=defaultWorkspace();expect(w.layout).toBe('single');expect(visibleCount(w.layout)).toBe(1);const hidden=w.panes[1].tabs[0];hidden.query='下载进度';w.layout='quad';w.focused='pane-3';repairWorkspace(w);w.layout='single';repairWorkspace(w);expect(w.focused).toBe('pane-0');expect(w.panes[1].tabs[0]).toBe(hidden);expect(hidden.query).toBe('下载进度');expect(workspaceSchema.safeParse(w).success).toBe(true)})
  it('moves the full tab state across panes and preserves a valid source tab',()=>{const w=defaultWorkspace(),tab=w.panes[0].tabs[0];tab.query='模组';tab.scroll=128;tab.view={instanceFilter:'favorite',fabric:true};moveTab(w,w.panes[0].id,w.panes[1].id,tab.id);expect(w.panes[1].tabs.at(-1)).toMatchObject({id:tab.id,query:'模组',scroll:128,view:{fabric:true}});expect(w.panes[0].tabs).toHaveLength(1);expect(w.panes[0].active).toBe(w.panes[0].tabs[0].id);expect(w.panes[1].active).toBe(tab.id);expect(workspaceSchema.safeParse(w).success).toBe(true)})
  it('reorders a tab in the same pane without creating or dropping tabs',()=>{const w=defaultWorkspace(),p=w.panes[0];const second=newTab('tasks');p.tabs.push(second);const first=p.tabs[0];moveTab(w,p.id,p.id,second.id,first.id);expect(p.tabs.map(t=>t.id)).toEqual([second.id,first.id]);expect(p.active).toBe(second.id)})
  it('repairs invalid focus and duplicate tab identities after layout changes',()=>{const w=defaultWorkspace();w.panes[1].tabs[0].id=w.panes[0].tabs[0].id;w.focused='pane-3';repairWorkspace(w);expect(w.focused).toBe('pane-0');expect(w.panes[0].tabs[0].id).not.toBe(w.panes[1].tabs[0].id)})
  it('rejects unsafe workspace ratios and invalid launch settings',()=>{const w=defaultWorkspace();w.ratios[0]=0;expect(workspaceSchema.safeParse(w).success).toBe(false);const s=defaultState().settings;expect(settingsSchema.safeParse({...s,memory:0}).success).toBe(false);expect(settingsSchema.safeParse({...s,clientId:'not-an-id'}).success).toBe(false)})
})

describe('instance browsing and protection',()=>{
  const base:Instance={id:'a',name:'A',sourceId:'root',root:'D:/我的世界',version:'1.21',minecraft:'1.21',loader:'Fabric',gameDir:'D:/我的世界/生存',group:'生存',favorite:false,memory:4096,java:'',isolated:true,protected:false,memoryMode:'inherit'}
  const browse={query:'',group:'',filter:'all',loader:'',sort:'name'}
  it('combines path search and loader/group filters without changing the original index',()=>{const instances=[base,{...base,id:'b',name:'B',loader:'Vanilla'}];expect(selectInstances(instances,{...browse,query:'生存',loader:'Fabric',group:'生存'}).map(i=>i.id)).toEqual(['a']);expect(instances.map(i=>i.id)).toEqual(['a','b'])})
  it('sorts recent launches by real launch time and omits never-launched instances from that filter',()=>{const instances=[base,{...base,id:'b',lastPlayed:'2026-10-20T00:00:00Z'},{...base,id:'c',lastPlayed:'2026-10-21T00:00:00Z'}];expect(selectInstances(instances,{...browse,filter:'recent'}).map(i=>i.id)).toEqual(['c','b'])})
  it('blocks protected mutations at the service boundary',()=>{expect(()=>assertInstanceUnprotected({...base,protected:true})).toThrow('实例保护');expect(()=>assertInstanceUnprotected(base)).not.toThrow()})
})

describe('appearance preferences',()=>{
  it('keeps custom black and white accents readable in both appearance modes',()=>{for(const theme of ['dark','light'] as const)for(const accent of ['#000000','#ffffff']){const v=appearanceVariables({theme,accent});expect(contrastRatio(v['--green'],theme==='light'?'#fbfcf9':'#181b20')).toBeGreaterThanOrEqual(4.5);expect(contrastRatio(v['--green'],v['--accent-ink'])).toBeGreaterThanOrEqual(4.5)}})
  it('accepts old settings without a color preference and validates custom colors',()=>{const s=defaultState().settings;const {accent,...old}=s;expect(settingsSchema.parse(old).accent).toBe('#a7df8b');expect(settingsSchema.safeParse({...s,accent:'blue;url(anything)'}).success).toBe(false);expect(settingsSchema.parse({...s,accent:'#74b5ff'}).accent).toBe('#74b5ff')})
  it('derives both themes from the chosen accent with a readable button foreground',()=>{expect(foreground('#ffffff')).toBe('#111611');expect(foreground('#000000')).toBe('#ffffff');const dark=appearanceVariables({theme:'dark',accent:'#74b5ff'}),light=appearanceVariables({theme:'light',accent:'#74b5ff'});expect(dark['--green']).not.toBe(light['--green']);expect(dark['--green-bg']).not.toBe(light['--green-bg']);expect(dark['--accent-ink']).toBe(foreground(dark['--green']))})
})

describe('real directory discovery and instance boundaries',()=>{
  it('preserves drive roots rather than converting an absolute root into a drive-relative path',async()=>{const d=await temp(),root=path.parse(d).root;expect(await canonical(root)).toBe(root)})
  it('finds dependencies even when dependent instances are hidden from the launcher',async()=>{const d=await temp();await version(d,'base');await version(d,'hidden-modpack',{id:'hidden-modpack',inheritsFrom:'base'});await version(d,'bad','{bad');const s=defaultState();s.sources=[{id:'root',label:'root',path:d}];const base=(await scanSources(s)).instances.find(i=>i.version==='base')!;expect(await versionDependents(base)).toEqual({dependents:['hidden-modpack'],unreadable:['bad']})})
  it('recognizes Unicode roots, deduplicates overlapping sources and ignores native/cache directories',async()=>{const d=await temp(),root=path.join(d,'中文 游戏','.minecraft');await version(root,'1.21.1');await mkdir(path.join(root,'versions','1.21.1-natives'));await version(root,'Fabric 测试',{id:'Fabric 测试',inheritsFrom:'1.21.1',mainClass:'KnotClient',libraries:[{name:'net.fabricmc:fabric-loader:0.16.0'}]});await mkdir(path.join(root,'versions','Fabric 测试','mods'));const s=defaultState();s.sources=[{id:'a',path:path.dirname(root),label:'容器'},{id:'b',path:root,label:'重复根'}];const result=await scanSources(s);expect(result.instances).toHaveLength(2);expect(result.instances.find(i=>i.loader==='Fabric')).toMatchObject({minecraft:'1.21.1',isolated:true});expect(await findRoots(path.join(root,'versions'))).toEqual([root]);expect(result.warnings).toEqual([])})
  it('discovers a container of independent instances and reports corrupt versions',async()=>{const d=await temp();await version(path.join(d,'A','.minecraft'),'1.20.1');await version(path.join(d,'B'),'损坏版本','{bad json');const s=defaultState();s.sources=[{id:'a',path:d,label:'独立实例'}];const result=await scanSources(s);expect(result.instances).toHaveLength(2);expect(result.instances.find(i=>i.version==='损坏版本')?.issue).toContain('损坏')})
  it('keeps missing roots as actionable warnings and respects removed records',async()=>{const d=await temp();await version(d,'test');const s=defaultState();s.sources=[{id:'a',path:d,label:'test'},{id:'b',path:path.join(d,'gone'),label:'丢失'}];const first=await scanSources(s);expect(first.warnings).toHaveLength(1);s.hidden=[first.instances[0].id];expect((await scanSources(s)).instances).toHaveLength(0)})
  it('only permits deletion of a direct version child and rejects junction escapes',async()=>{const d=await temp(),outside=await temp();const target=await version(d,'safe');const s=defaultState();s.sources=[{id:'a',path:d,label:'test'}];const i=(await scanSources(s)).instances[0];expect(await assertSafeVersionDirectory(i,s.sources)).toBe(target);expect(inside(path.join(d,'versions'),d)).toBe(false);await symlink(outside,path.join(d,'versions','escape'),'junction');await expect(assertSafeVersionDirectory({...i,version:'escape'},s.sources)).rejects.toThrow('越界');await expect(assertSafeVersionDirectory(i,[...s.sources,{id:'nested',path:target,label:'嵌套'}])).rejects.toThrow('已登记')})
})

describe('portable persistence',()=>{
  it('serializes concurrent writes and restores a full workspace',async()=>{const d=await temp(),s=new Store(d);await s.load();s.state.workspace.panes[0].tabs[0].query='中文';const a=s.save();s.state.settings.memory=6144;const b=s.save();await Promise.all([a,b]);const restored=new Store(d);await restored.load();expect(restored.state.settings.memory).toBe(6144);expect(restored.state.workspace.panes[0].tabs[0].query).toBe('中文');expect(JSON.parse(await readFile(path.join(d,'config.json'),'utf8')).schemaVersion).toBe(1)})
  it('preserves corrupt configuration instead of overwriting the only copy',async()=>{const d=await temp();await writeFile(path.join(d,'config.json'),'{bad');const s=new Store(d);await s.load();expect(s.warning).toContain('已保留');const {readdir}=await import('node:fs/promises');const backup=(await readdir(d)).find(f=>f.startsWith('config.invalid'));expect(backup).toBeTruthy();expect(await readFile(path.join(d,backup!),'utf8')).toBe('{bad')})
})

describe('background task lifecycle',()=>{
  it('records errors and releases task state instead of swallowing failures',async()=>{const t=new Tasks(()=>{},()=>{});t.start('bad',async()=>{throw new Error('missing library')});await new Promise(r=>setTimeout(r,20));expect(t.entries[0]).toMatchObject({status:'error',detail:'missing library'})})
  it('cancels running work with AbortSignal',async()=>{const t=new Tasks(()=>{},()=>{});const id=t.start('cancel',async(_t,signal)=>{await new Promise<void>((resolve,reject)=>{if(signal.aborted)reject(signal.reason);else signal.addEventListener('abort',()=>reject(signal.reason),{once:true})})});t.cancel(id);await new Promise(r=>setTimeout(r,20));expect(t.entries[0].status).toBe('cancelled')})
})
