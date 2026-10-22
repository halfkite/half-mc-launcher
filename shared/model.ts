import { z } from 'zod'

export const pageIds = ['instances', 'install', 'resources', 'tasks', 'tools', 'settings', 'plugins', 'search', 'mods'] as const
export type PageId = typeof pageIds[number]
export const layoutIds = ['single', 'columns', 'rows', 'left-wide', 'right-wide', 'quad'] as const
export type LayoutId = typeof layoutIds[number]
export const tabSchema = z.object({ id: z.string().max(100), page: z.enum(pageIds), pinned: z.boolean().default(false), query: z.string().max(500).default(''), group: z.string().max(100).default(''), scroll: z.number().nonnegative().default(0), view: z.record(z.string().max(100), z.union([z.string().max(1000), z.boolean(), z.number().finite()])).default({}) })
export const paneSchema = z.object({ id: z.string().max(100), active: z.string().max(100), tabs: z.array(tabSchema).min(1).max(40) })
export const workspaceSchema = z.object({ layout: z.enum(layoutIds), panes: z.array(paneSchema).length(4), focused: z.string(), ratios: z.tuple([z.number().min(20).max(80), z.number().min(20).max(80), z.number().min(20).max(80)]), sidebarWidth: z.number().min(56).max(320).default(210), sidebarCollapsed: z.boolean().default(false), sidebarExpandedWidth: z.number().min(152).max(320).default(210), closedTabs: z.array(z.object({paneId:z.string(),index:z.number().int().min(0),tab:tabSchema})).max(20).default([]) })
export type Tab = z.infer<typeof tabSchema>
export type Pane = z.infer<typeof paneSchema>
export type Workspace = z.infer<typeof workspaceSchema>
export const instanceOptionsSchema = z.object({ name: z.string().trim().min(1).max(150), group: z.string().trim().max(60), favorite: z.boolean(), memory: z.number().int().min(512).max(65536), java: z.string().max(2000), isolated: z.boolean(), protected: z.boolean().default(false), memoryMode:z.enum(['inherit','auto','manual']).default('inherit') })
export type InstanceOptions = z.infer<typeof instanceOptionsSchema>
export const settingsSchema = z.object({ theme: z.enum(['dark', 'light']), accent: z.string().regex(/^#[0-9a-f]{6}$/i, '主题色必须为六位十六进制颜色').default('#a7df8b'), memory: z.number().int().min(512).max(65536), java: z.string().max(2000), clientId: z.string().trim().max(100).refine(v => !v || /^[0-9a-f-]{36}$/i.test(v), '应用 ID 格式应为 UUID'), reduceMotion: z.boolean(), fontScale:z.number().min(90).max(150).default(100), opacity:z.number().min(0).max(100).default(100), glass:z.boolean().default(true), memoryMode:z.enum(['auto','manual']).default('auto'), javaRoots:z.array(z.string().max(2000)).max(30).default([]) })
export type Settings = z.infer<typeof settingsSchema>
export type Source = { id: string; path: string; label: string }
export type Instance = InstanceOptions & { id: string; sourceId: string; root: string; version: string; minecraft: string; loader: string; gameDir: string; issue?: string; running?: boolean; lastPlayed?: string }
export type Account = { id: string; name: string; uuid: string; expiresAt: number; kind?: 'microsoft' | 'offline' }
export type TaskInfo = { id: string; title: string; status: 'running' | 'success' | 'error' | 'cancelled'; progress: number; detail: string; createdAt: string; instanceId?: string }
export type LogEntry = { time: string; level: 'info' | 'error'; text: string; instanceId?: string }
export type JavaInfo = { path: string; version: string; major: number; arch: string }
export type Preset = { id: string; name: string; workspace: Workspace }
export const windowBoundsSchema=z.object({width:z.number().int().min(640).max(16000),height:z.number().int().min(480).max(16000),x:z.number().int().optional(),y:z.number().int().optional(),maximized:z.boolean()})
export type State = { schemaVersion: 1; workspace: Workspace; presets: Preset[]; sources: Source[]; options: Record<string, InstanceOptions>; hidden: string[]; accounts: Account[]; selectedAccount: string; settings: Settings; lastPlayed: Record<string, string>; windowBounds:z.infer<typeof windowBoundsSchema> }
export type Snapshot = { state: State; instances: Instance[]; tasks: TaskInfo[]; logs: LogEntry[]; dataPath: string; appVersion: string; memoryMb: number; scanWarnings: string[];authConfigured?:boolean;authClientId?:string;authSource?:string }
export type Release = { id: string; type: string; releaseTime: string; url: string }
export type DeviceCode = { code: string; url: string; message: string }
export interface LauncherApi {
  invoke<T = unknown>(method: string, payload?: unknown): Promise<T>
  onEvent(listener: (event: { type: string; data: any }) => void): () => void
}
declare global { interface Window { launcher?: LauncherApi } }
