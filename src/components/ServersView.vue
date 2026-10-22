<script setup lang="ts">
import { onMounted, ref } from 'vue'
import type { Instance } from '../../shared/model'
import { api } from '../store'
import Icon from './Icon.vue'
const props=defineProps<{instance:Instance}>()
type Row={name:string;ip:string;icon:string}
const rows=ref<Row[]>([]),name=ref(''),ip=ref(''),loading=ref(false),error=ref(''),note=ref('')
async function load(){loading.value=true;error.value='';try{rows.value=await api<Row[]>('servers.list',{id:props.instance.id})}catch(e:any){error.value=e.message}finally{loading.value=false}}
async function add(){error.value='';note.value='';try{rows.value=await api<Row[]>('servers.add',{id:props.instance.id,name:name.value,ip:ip.value});name.value='';ip.value='';note.value='已添加服务器'}catch(e:any){error.value=e.message}}
async function remove(index:number){error.value='';try{rows.value=await api<Row[]>('servers.remove',{id:props.instance.id,index})}catch(e:any){error.value=e.message}}
async function reveal(){await api('servers.reveal',{id:props.instance.id})}
onMounted(load)
</script>
<template>
  <div class="servers-page">
    <div class="content-toolbar">
      <p class="field-hint">{{instance.gameDir}} / servers.dat</p>
      <div class="button-row"><button class="button compact" @click="reveal">打开位置</button><button class="button compact" :disabled="loading" @click="load">刷新</button></div>
    </div>
    <form class="server-add" @submit.prevent="add">
      <input v-model="name" aria-label="服务器名称" placeholder="名称（可选）"/>
      <input v-model="ip" aria-label="服务器地址" placeholder="地址，例如 play.example.com"/>
      <button class="button primary compact" :disabled="loading||!ip.trim()">添加</button>
    </form>
    <p v-if="error" class="inline-notice">{{error}}</p>
    <p v-else-if="note" class="field-hint">{{note}}</p>
    <p v-if="!rows.length" class="field-hint">还没有保存的服务器。加入后会写进游戏内多人列表。</p>
    <div v-for="(row,index) in rows" :key="row.ip+index" class="server-row">
      <img v-if="row.icon" class="server-icon" :src="'data:image/png;base64,'+row.icon" alt=""/>
      <span v-else class="server-icon fallback"><Icon name="servers" :size="16"/></span>
      <div class="server-main"><b>{{row.name||row.ip}}</b><small>{{row.ip}}</small></div>
      <button class="button compact danger-text" :disabled="loading" @click="remove(index)">删除</button>
    </div>
  </div>
</template>
