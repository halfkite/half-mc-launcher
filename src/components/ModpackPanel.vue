<script setup lang="ts">
import { ref } from 'vue'
import type { Instance } from '../../shared/model'
import { api, toast } from '../store'
import Icon from './Icon.vue'
const props=defineProps<{instance:Instance}>()
type Result={mode:string;extracted:number;downloaded:number;warnings:string[]}
const busy=ref(false),result=ref<Result|null>(null),error=ref('')
async function exportPack(){busy.value=true;error.value='';try{const response=await api<{files:number;path:string}|null>('modpack.export',{id:props.instance.id});if(response)toast('已导出 '+response.files+' 个文件')}catch(e:any){error.value=e.message}finally{busy.value=false}}
async function importPack(){busy.value=true;error.value='';result.value=null;try{const response=await api<Result|null>('modpack.import',{id:props.instance.id});if(response){result.value=response;toast('整合包已导入')}}catch(e:any){error.value=e.message}finally{busy.value=false}}
</script>
<template>
  <div class="modpack-panel">
    <div class="modpack-grid">
      <section class="modpack-card">
        <div class="modpack-head"><Icon name="download" :size="18"/><b>导入整合包</b></div>
        <p>支持本启动器导出的 ZIP、Modrinth <code>.mrpack</code>、CurseForge manifest 整合包，以及普通的 mods/config 压缩包。文件会解压到当前实例目录，不覆盖已有模组。</p>
        <button class="button primary" :disabled="busy" @click="importPack">{{busy?'处理中…':'选择整合包…'}}</button>
      </section>
      <section class="modpack-card">
        <div class="modpack-head"><Icon name="package" :size="18"/><b>导出当前实例</b></div>
        <p>打包 mods、config、资源包、光影与 options.txt，附带 halfpack.json 记录游戏版本和加载器。存档和日志不会被打包。</p>
        <button class="button" :disabled="busy" @click="exportPack">导出为 ZIP…</button>
      </section>
    </div>
    <div v-if="error" class="inline-notice">{{error}}</div>
    <section v-if="result" class="modpack-result">
      <b>导入完成 · {{result.mode}}</b>
      <p>解压 {{result.extracted}} 个文件<span v-if="result.downloaded">，下载 {{result.downloaded}} 个模组</span>。</p>
      <p v-for="warning in result.warnings" :key="warning" class="field-hint">· {{warning}}</p>
    </section>
  </div>
</template>
