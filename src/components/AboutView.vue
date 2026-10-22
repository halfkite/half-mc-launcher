<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, run, toast } from '../store'
import Icon from './Icon.vue'
const info=ref<any>(null),source=ref(''),checking=ref(false),update=ref<any>(null)
const chips=['实例隔离与分组','内容管理与备份','整合包导入导出','Java 自动下载','崩溃分析','模组更新与翻译','依赖检查','BMCLAPI 镜像','服务器状态','皮肤与截图','快捷方式','诊断包']
async function load(){try{info.value=await api('about.info');source.value=info.value?.updateUrl||''}catch(e:any){toast('读取环境信息失败：'+e.message)}}
async function saveSource(){try{const r=await api<any>('updates.setSource',{url:source.value});source.value=r.source||'';toast(r.configured?'更新源已保存':'已清空更新源')}catch(e:any){toast('保存失败：'+e.message)}}
async function check(){checking.value=true;update.value=null;try{const r=await api<any>('updates.check');update.value=r;if(!r.configured)toast(r.message);else if(r.ok)toast(r.hasUpdate?'发现新版本 '+r.latest:'已是最新版本')}catch(e:any){toast('检查更新失败：'+e.message)}finally{checking.value=false}}
function openLink(){if(update.value?.link)void api('link.open',{url:update.value.link})}
function open(target:string){void run(target,'open',target)}
onMounted(load)
</script>
<template>
  <div class="about-page">
    <section class="about-hero">
      <span class="about-mark"><Icon name="instances" :size="30"/></span>
      <div class="about-title"><h2>Half Mc Launcher</h2><p class="field-hint">Minecraft 启动器 · Ink &amp; Ember 界面</p></div>
      <span class="about-version">{{info?.version||'…'}}</span>
    </section>
    <section class="section">
      <h3 class="content-subtitle">运行环境</h3>
      <div class="about-grid">
        <div><span>操作系统</span><b>{{info?.os||'…'}}</b></div>
        <div><span>WebView2</span><b>{{info?.webview2||'未知'}}</b></div>
        <div><span>.NET</span><b>{{info?.clr||'…'}}</b></div>
        <div><span>实例数量</span><b>{{info?.instances??'…'}}</b></div>
        <div class="wide"><span>数据目录</span><b class="mono">{{info?.dataDir}}</b><button class="bare" title="打开数据目录" @click="open('data')"><Icon name="external" :size="13"/></button></div>
        <div class="wide"><span>程序位置</span><b class="mono">{{info?.exe}}</b></div>
      </div>
    </section>
    <section class="section">
      <h3 class="content-subtitle">启动器更新</h3>
      <div class="server-add"><input v-model="source" aria-label="更新清单地址" placeholder="更新清单 URL（JSON 或 GitHub Release）"/><button class="button compact" @click="saveSource">保存源</button><button class="button primary compact" :disabled="checking" @click="check">{{checking?'检查中…':'检查更新'}}</button></div>
      <p class="field-hint">清单格式：{"version":"0.1.0-r105","notes":"更新说明","url":"下载页"}；未配置时不会联网。</p>
      <div v-if="update" class="deps-panel"><div class="deps-head"><b>{{update.ok?(update.hasUpdate?'发现新版本 '+update.latest:'已是最新版本'):update.message}}</b><span>当前 {{update.current}}</span><button v-if="update.link" class="button compact" @click="openLink">打开发行页</button></div><p v-if="update.notes" class="field-hint update-notes">{{update.notes}}</p></div>
    </section>
    <section class="section">
      <h3 class="content-subtitle">维护</h3>
      <div class="button-row"><button class="button compact" @click="open('logs')">打开日志</button><button class="button compact" @click="run('diagnostics','diagnostics.export')">导出诊断包</button><button class="button compact" :disabled="!info?.notices" @click="open('notices')">第三方许可</button></div>
      <p class="field-hint">更新时替换发行版 exe 即可，数据目录会保留。</p>
    </section>
    <section class="section">
      <h3 class="content-subtitle">已具备的能力</h3>
      <div class="about-chips"><span v-for="chip in chips" :key="chip">{{chip}}</span></div>
    </section>
  </div>
</template>
