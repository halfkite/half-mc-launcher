import type { LayoutId, PageId, Pane, Tab, Workspace } from './model'
export const uid = () => globalThis.crypto.randomUUID()
export function newTab(page: PageId = 'instances'): Tab { return { id: uid(), page, pinned: false, query: '', group: '', scroll: 0, view: {} } }
export function defaultWorkspace(): Workspace {
  const panes: Pane[] = Array.from({ length: 4 }, (_, i) => { const tab = newTab(i === 1 ? 'install' : 'instances'); return { id: `pane-${i}`, active: tab.id, tabs: [tab] } })
  return { layout: 'single', panes, focused: panes[0].id, ratios: [50, 50, 50], sidebarWidth:210, sidebarCollapsed:false, sidebarExpandedWidth:210, closedTabs:[] }
}
export function visibleCount(layout: LayoutId) { return layout === 'single' ? 1 : layout === 'quad' ? 4 : layout.endsWith('wide') ? 3 : 2 }
export function repairWorkspace(w: Workspace): Workspace {
  const seen = new Set<string>()
  for (const pane of w.panes) {
    for (const tab of pane.tabs) { if (seen.has(tab.id)) tab.id = uid(); seen.add(tab.id) }
    if (!pane.tabs.some(t => t.id === pane.active)) pane.active = pane.tabs[0].id
  }
  w.panes.forEach((p, i) => { p.id = `pane-${i}` })
  if (!w.panes.slice(0, visibleCount(w.layout)).some(p => p.id === w.focused)) w.focused = w.panes[0].id
  return w
}
export function moveTab(w: Workspace, fromId: string, toId: string, tabId: string, beforeId?: string) {
  const from = w.panes.find(p => p.id === fromId), to = w.panes.find(p => p.id === toId)
  if (!from || !to || (from === to && beforeId === tabId)) return
  const index = from.tabs.findIndex(t => t.id === tabId)
  if (index < 0 || (from !== to && to.tabs.length >= 40)) return
  const [tab] = from.tabs.splice(index, 1)
  if (from !== to && !from.tabs.length) from.tabs.push(newTab())
  if (from.active === tabId) from.active = from.tabs[Math.min(index, from.tabs.length - 1)]?.id ?? tabId
  const before = beforeId ? to.tabs.findIndex(t => t.id === beforeId) : -1
  to.tabs.splice(before < 0 ? to.tabs.length : before, 0, tab)
  to.active = tab.id; w.focused = to.id
}
export function closeWorkspaceTab(w:Workspace,paneId:string,tabId:string) {
  const pane=w.panes.find(p=>p.id===paneId)
  const index=pane?.tabs.findIndex(t=>t.id===tabId)??-1
  if(!pane||index<0)return
  const [tab]=pane.tabs.splice(index,1)
  w.closedTabs.push({paneId,index,tab:JSON.parse(JSON.stringify(tab))})
  if(w.closedTabs.length>20)w.closedTabs.splice(0,w.closedTabs.length-20)
  if(!pane.tabs.length)pane.tabs.push(newTab())
  if(pane.active===tabId)pane.active=pane.tabs[Math.min(index,pane.tabs.length-1)].id
}
export function restoreWorkspaceTab(w:Workspace) {
  const closed=w.closedTabs.at(-1)
  if(!closed)return false
  const original=w.panes.find(p=>p.id===closed.paneId)
  const pane=original&&w.panes.indexOf(original)<visibleCount(w.layout)?original:w.panes.find(p=>p.id===w.focused)||w.panes[0]
  if(pane.tabs.length>=40)return false
  w.closedTabs.pop()
  if(w.panes.some(p=>p.tabs.some(t=>t.id===closed.tab.id)))closed.tab.id=uid()
  pane.tabs.splice(Math.min(closed.index,pane.tabs.length),0,closed.tab)
  pane.active=closed.tab.id;w.focused=pane.id
  return true
}
export function resizeSidebar(w:Workspace,width:number) {
  w.sidebarWidth=Math.min(320,Math.max(56,width))
  if(w.sidebarCollapsed){if(w.sidebarWidth>=152)w.sidebarCollapsed=false}
  else if(w.sidebarWidth<=136)w.sidebarCollapsed=true
  if(!w.sidebarCollapsed)w.sidebarExpandedWidth=Math.max(152,w.sidebarWidth)
}
export function toggleSidebar(w:Workspace) {
  if(w.sidebarCollapsed){w.sidebarCollapsed=false;w.sidebarWidth=w.sidebarExpandedWidth}
  else{w.sidebarExpandedWidth=Math.max(152,w.sidebarWidth);w.sidebarCollapsed=true}
}
