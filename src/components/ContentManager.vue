<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import type { Instance } from '../../shared/model'
import type { ContentKind } from '../../shared/resources'
import { api, busy } from '../store'
import Icon from './Icon.vue'
const props=defineProps<{instance:Instance;kind:ContentKind}>()
type Row={file:string;name:string;size:number;modified:string;enabled:boolean;directory:boolean;kind:ContentKind;version?:string;lastPlayed?:string;gameType?:number;hardcore?:boolean;icon?:string}
type Backup={file:string;size:number;modified:string}
const items=ref<Row[]>([]),backups=ref<Backup[]>([]),loading=ref(false),error=ref('')
const thumbs=ref<Record<string,string>>({}),viewer=ref<Row|null>(null),viewerData=ref('')
const folderHint=computed(()=>({resourcepacks:'resourcepacks',shaderpacks:'shaderpacks',saves:'saves',screenshots:'screenshots'} as Record<ContentKind,string>)[props.kind])
const emptyText=computed(()=>({resourcepacks:'还没有资源包，导入 ZIP 即可使用。',shaderpacks:'还没有光影，导入 ZIP 即可使用。',saves:'还没有存档，可从其他启动器导入文件夹。',screenshots:'游戏截图会出现在这里。'} as Record<ContentKind,string>)[props.kind])
function size(value:number){return value>1048576?(value/1048576).toFixed(1)+' MiB':Math.max(1,Math.round(value/1024))+' KiB'}
function date(value:string){return new Date(value).toLocaleDateString('zh-CN')}
function mode(value?:number){return ['生存','创造','冒险','旁观'][value??0]||'生存'}
async function load(){loading.value=true;error.value='';thumbs.value={};viewer.value=null;try{items.value=await api<Row[]>('content.list',{id:props.instance.id,kind:props.kind});if(props.kind==='saves')backups.value=await api<Backup[]>('content.backups',{id:props.instance.id});if(props.kind==='screenshots')await loadThumbs()}catch(e:any){error.value=e.message}finally{loading.value=false}}
async function loadThumbs(){const next:Record<string,string>={};await Promise.all(items.value.slice(0,120).map(async row=>{try{const thumb=await api<{data:string}>('screenshots.thumb',{id:props.instance.id,file:row.file,max:360});next[row.file]=thumb.data}catch{}}));thumbs.value=next}
async function open(row:Row){viewer.value=row;viewerData.value='';try{const image=await api<{data:string}>('screenshots.image',{id:props.instance.id,file:row.file});viewerData.value=image.data}catch(e:any){error.value=e.message}}
async function toggle(row:Row){await api('content.toggle',{id:props.instance.id,kind:props.kind,file:row.file});await load()}
async function remove(row:Row){const result=await api('content.delete',{id:props.instance.id,kind:props.kind,file:row.file});if(result)items.value=result as Row[];if(viewer.value?.file===row.file)viewer.value=null}
async function reveal(row:Row){await api('content.reveal',{id:props.instance.id,kind:props.kind,file:row.file})}
async function importFiles(){const result=await api('content.import',{id:props.instance.id,kind:props.kind});if(result)items.value=result as Row[]}
async function backup(row:Row){const result=await api('content.backup',{id:props.instance.id,file:row.file});if(result)backups.value=result as Backup[]}
async function restore(row:Backup){const result=await api('content.restore',{id:props.instance.id,file:row.file});if(result)items.value=result as Row[]}
onMounted(load)
watch(()=>[props.instance.id,props.kind],load)
</script>
<template>
  <div class="content-manager">
    <div class="content-toolbar">
      <p class="field-hint">{{instance.gameDir}} / {{folderHint}}</p>
      <div class="button-row">
        <button class="button compact" :disabled="loading" @click="importFiles">导入</button>
        <button class="button compact" :disabled="loading" @click="load">刷新</button>
      </div>
    </div>
    <div v-if="error" class="inline-notice">{{error}}</div>

    <section v-if="viewer" class="shot-viewer">
      <div class="shot-viewer-head"><b>{{viewer.name}}</b><div class="button-row"><button class="button compact" @click="reveal(viewer)">打开位置</button><button class="button compact" @click="viewer=null">关闭</button></div></div>
      <img v-if="viewerData" :src="viewerData" :alt="viewer.name"/>
      <p v-else class="field-hint">正在读取图片…</p>
    </section>

    <template v-if="kind==='screenshots'">
      <p v-if="!items.length" class="field-hint content-empty">{{emptyText}}</p>
      <div class="shot-wall">
        <div v-for="row in items" :key="row.file" class="shot-cell">
          <button class="shot-thumb" @click="open(row)">
            <img v-if="thumbs[row.file]" :src="thumbs[row.file]" :alt="row.name" loading="lazy"/>
            <span v-else class="shot-placeholder"><Icon name="monitor" :size="18"/></span>
          </button>
          <div class="shot-meta"><span :title="row.name">{{row.name}}</span><button class="bare danger-text" title="删除截图" @click="remove(row)"><Icon name="trash" :size="13"/></button></div>
        </div>
      </div>
    </template>

    <template v-else>
      <p v-if="!items.length" class="field-hint content-empty">{{emptyText}}</p>
      <div v-for="row in items" :key="row.file" class="content-row">
        <img v-if="kind==='saves'&&row.icon" class="save-icon" :src="row.icon" alt=""/><span v-else class="content-icon"><Icon :name="kind==='saves'?'save':kind==='shaderpacks'?'sparkles':'layers'" :size="16"/></span>
        <div class="content-main"><b>{{row.name}}</b><small v-if="kind==='saves'">{{row.version||'未知版本'}} · {{mode(row.gameType)}}<template v-if="row.hardcore"> · 极限</template> · {{row.lastPlayed?date(row.lastPlayed):date(row.modified)}} · {{size(row.size)}}</small><small v-else>{{size(row.size)}} · {{date(row.modified)}}</small></div>
        <div class="content-actions">
          <button v-if="kind!=='saves'" class="button compact" :disabled="loading||busy.has('content')" @click="toggle(row)">{{row.enabled?'禁用':'启用'}}</button>
          <button v-if="kind==='saves'" class="button compact" :disabled="loading" @click="backup(row)">备份</button>
          <button class="button compact" :disabled="loading" @click="reveal(row)">位置</button>
          <button class="button compact danger-text" :disabled="loading" @click="remove(row)">删除</button>
        </div>
      </div>
      <template v-if="kind==='saves'">
        <h3 class="content-subtitle">存档备份</h3>
        <p v-if="!backups.length" class="field-hint">还没有备份，点某个存档的「备份」生成 ZIP。</p>
        <div v-for="row in backups" :key="row.file" class="content-row backup-row">
          <span class="content-icon"><Icon name="save" :size="16"/></span>
          <div class="content-main"><b>{{row.file}}</b><small>{{size(row.size)}} · {{date(row.modified)}}</small></div>
          <div class="content-actions"><button class="button compact" :disabled="loading" @click="restore(row)">还原</button></div>
        </div>
      </template>
    </template>
  </div>
</template>
