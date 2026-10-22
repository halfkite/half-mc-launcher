<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, run, toast } from '../store'
import Icon from './Icon.vue'
const info=ref<any>(null)
const chips=['实例隔离与分组','内容管理与备份','整合包导入导出','Java 自动下载','崩溃分析','模组更新与翻译','依赖检查','BMCLAPI 镜像','服务器状态','皮肤与截图','快捷方式','诊断包']
async function load(){try{info.value=await api('about.info')}catch(e:any){toast('读取环境信息失败：'+e.message)}}
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
      <h3 class="content-subtitle">维护</h3>
      <div class="button-row"><button class="button compact" @click="open('logs')">打开日志</button><button class="button compact" @click="run('diagnostics','diagnostics.export')">导出诊断包</button><button class="button compact" :disabled="!info?.notices" @click="open('notices')">第三方许可</button></div>
      <p class="field-hint">当前为开发构建，未接入更新通道。更新时替换发行版 exe 即可，数据目录会保留。</p>
    </section>
    <section class="section">
      <h3 class="content-subtitle">已具备的能力</h3>
      <div class="about-chips"><span v-for="chip in chips" :key="chip">{{chip}}</span></div>
    </section>
  </div>
</template>
