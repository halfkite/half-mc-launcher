<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import type { Instance } from '../../shared/model'
import type { ContentKind } from '../../shared/resources'
import { api, busy } from '../store'
import Icon from './Icon.vue'
const props=defineProps<{instance:Instance;kind:ContentKind}>()
type Row={file:string;name:string;size:number;modified:string;enabled:boolean;directory:boolean;kind:ContentKind}
type Backup={file:string;size:number;modified:string}
const items=ref<Row[]>([]),backups=ref<Backup[]>([]),loading=ref(false),error=ref('')
const folderHint=computed(()=>({resourcepacks:'resourcepacks',shaderpacks:'shaderpacks',saves:'saves',screenshots:'screenshots'} as Record<ContentKind,string>)[props.kind])
const emptyText=computed(()=>({resourcepacks:'还没有资源包，导入 ZIP 即可使用。',shaderpacks:'还没有光影，导入 ZIP 即可使用。',saves:'还没有存档，可从其他启动器导入文件夹。',screenshots:'游戏截图会出现在这里。'} as Record<ContentKind,string>)[props.kind])
function size(value:number){return value>1048576?(value/1048576).toFixed(1)+' MiB':Math.max(1,Math.round(value/1024))+' KiB'}
function date(value:string){return new Date(value).toLocaleDateString('zh-CN')}
async function load(){loading.value=true;error.value='';try{items.value=await api<Row[]>('content.list',{id:props.instance.id,kind:props.kind});if(props.kind==='saves')backups.value=await api<Backup[]>('content.backups',{id:props.instance.id})}catch(e:any){error.value=e.message}finally{loading.value=false}}
async function toggle(row:Row){await api('content.toggle',{id:props.instance.id,kind:props.kind,file:row.file});await load()}
async function remove(row:Row){const result=await api('content.delete',{id:props.instance.id,kind:props.kind,file:row.file});if(result)items.value=result as Row[]}
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
    <p v-if="!items.length" class="field-hint content-empty">{{emptyText}}</p>
    <div v-for="row in items" :key="row.file" class="content-row">
      <span class="content-icon"><Icon :name="kind==='saves'?'save':kind==='screenshots'?'monitor':kind==='shaderpacks'?'sparkles':'layers'" :size="16"/></span>
      <div class="content-main"><b>{{row.name}}</b><small>{{size(row.size)}} · {{date(row.modified)}}</small></div>
      <div class="content-actions">
        <button v-if="kind!=='saves'&&kind!=='screenshots'" class="button compact" :disabled="loading||busy.has('content')" @click="toggle(row)">{{row.enabled?'禁用':'启用'}}</button>
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
  </div>
</template>
