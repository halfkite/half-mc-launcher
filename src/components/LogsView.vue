<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import type { Instance } from '../../shared/model'
import { api } from '../store'
import Icon from './Icon.vue'
const props=defineProps<{instance:Instance}>()
type FileRow={file:string;kind:string;size:number;modified:string}
type Finding={level:string;title:string;detail:string;lines:string[]}
const files=ref<FileRow[]>([]),selected=ref(''),text=ref(''),findings=ref<Finding[]>([]),summary=ref(''),lineCount=ref(0),truncated=ref(false),loading=ref(false),error=ref('')
const current=computed(()=>files.value.find(row=>row.file===selected.value))
function size(value:number){return value>1048576?(value/1048576).toFixed(1)+' MiB':Math.max(1,Math.round(value/1024))+' KiB'}
function date(value:string){return new Date(value).toLocaleString('zh-CN')}
async function load(){loading.value=true;error.value='';try{files.value=await api<FileRow[]>('logs.files',{id:props.instance.id});const next=files.value.find(row=>row.kind==='crash')||files.value.find(row=>row.file==='latest.log')||files.value[0];if(next)await select(next.file);else{selected.value='';text.value='';findings.value=[]}}catch(e:any){error.value=e.message}finally{loading.value=false}}
async function select(file:string){selected.value=file;loading.value=true;error.value='';try{const read=await api<{file:string;text:string;truncated:boolean}>('logs.read',{id:props.instance.id,file});text.value=read.text;truncated.value=read.truncated;const analysis=await api<{findings:Finding[];summary:string;lines:number}>('logs.analyze',{id:props.instance.id,file});findings.value=analysis.findings||[];summary.value=analysis.summary||'';lineCount.value=analysis.lines||0}catch(e:any){error.value=e.message}finally{loading.value=false}}
async function reveal(){if(selected.value)await api('logs.reveal',{id:props.instance.id,file:selected.value})}
onMounted(load)
watch(()=>props.instance.id,load)
</script>
<template>
  <div class="logs-page">
    <div class="logs-layout">
      <aside class="logs-list">
        <div class="logs-list-head"><span>日志文件</span><button class="bare" title="刷新" @click="load"><Icon name="refresh" :size="14"/></button></div>
        <p v-if="!files.length" class="field-hint">还没有日志，启动一次游戏后会生成。</p>
        <button v-for="row in files" :key="row.file" class="logs-item" :class="{active:selected===row.file}" @click="select(row.file)">
          <Icon :name="row.kind==='crash'?'warning':'file'" :size="14"/>
          <span><b>{{row.file}}</b><small>{{size(row.size)}} · {{date(row.modified)}}</small></span>
        </button>
      </aside>
      <section class="logs-body">
        <div v-if="error" class="inline-notice">{{error}}</div>
        <template v-else-if="selected">
          <div class="logs-toolbar">
            <div><b>{{current?.file}}</b><small>{{lineCount}} 行 · {{size(current?.size||0)}}<span v-if="truncated"> · 仅显示末尾 2 MiB</span></small></div>
            <div class="button-row"><button class="button compact" @click="reveal">打开位置</button><button class="button compact" :disabled="loading" @click="select(selected)">重新分析</button></div>
          </div>
          <div v-if="summary" class="logs-summary"><b>崩溃描述</b><span>{{summary}}</span></div>
          <div v-if="findings.length" class="logs-findings">
            <div v-for="item in findings" :key="item.title" class="finding" :class="item.level">
              <div class="finding-head"><Icon :name="item.level==='error'?'error':'warning'" :size="15"/><b>{{item.title}}</b></div>
              <p>{{item.detail}}</p>
              <code v-for="line in item.lines" :key="line">{{line}}</code>
            </div>
          </div>
          <p v-else-if="!loading" class="field-hint">没有匹配到常见崩溃特征。可以查看下方原始日志，或把日志发到社区求助。</p>
          <pre class="log-console logs-text">{{text}}</pre>
        </template>
        <p v-else class="field-hint">选择左侧的日志文件开始分析。</p>
      </section>
    </div>
  </div>
</template>
