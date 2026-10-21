<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { data, run } from '../store'
import type { JavaInfo } from '../../shared/model'
const props=defineProps<{modelValue:string;inherit?:boolean}>()
const emit=defineEmits<{'update:modelValue':[value:string]}>()
const runtimes=ref<JavaInfo[]>([]),loading=ref(false)
async function scan(method='java.detect',payload?:string){loading.value=true;try{const result=await run('java-picker',method,payload);if(result)runtimes.value=result}finally{loading.value=false}}
async function choose(){const file=await run('java-choose','java.choose');if(file){emit('update:modelValue',file);await scan()}}
onMounted(()=>scan())
</script>
<template>
  <div class="java-picker"><select :value="modelValue" class="full-input" aria-label="选择 Java" @change="emit('update:modelValue',($event.target as HTMLSelectElement).value)"><option value="">{{inherit?'使用全局设置 / 自动匹配':'自动匹配游戏要求'}}</option><option v-if="modelValue&&!runtimes.some(j=>j.path===modelValue)" :value="modelValue">{{modelValue}}</option><option v-for="j in runtimes" :key="j.path" :value="j.path">Java {{j.version}} · {{j.arch}} · {{j.path}}</option></select><div class="button-row java-buttons"><button class="button compact" :disabled="loading" @click="scan()">{{loading?'正在识别…':'重新识别'}}</button><button class="button compact" :disabled="loading" @click="scan('java.addFolder')">添加扫描文件夹</button><button class="button compact" @click="choose">指定 java.exe</button></div><div v-if="!inherit" v-for="folder in data.state.settings.javaRoots" :key="folder" class="source-row"><div><p>{{folder}}</p></div><button class="bare" title="移除 Java 扫描目录" @click="scan('java.removeFolder',folder)">移除</button></div><p class="field-hint">识别 {{runtimes.length}} 个 Java；新增目录最多扫描 5 层。列表显示版本、位数与完整路径。</p></div>
</template>
