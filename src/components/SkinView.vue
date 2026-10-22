<script setup lang="ts">
import { computed } from 'vue'
import { data, run } from '../store'
const account=computed(()=>data.state.accounts.find(item=>item.id===data.state.selectedAccount)||data.state.accounts[0])
const skin=computed(()=>String((account.value as any)?.skin||''))
const cape=computed(()=>String((account.value as any)?.cape||''))
async function refresh(){if(account.value)await run(account.value.id+'refresh','account.refresh',account.value.id)}
</script>
<template>
  <div class="skin-page">
    <p v-if="!account" class="field-hint">还没有账户，先在左下角添加账户。</p>
    <template v-else>
      <section class="skin-card">
        <div v-if="skin" class="skin-figure" :style="{'--skin':'url('+skin+')'}">
          <i class="p-head"/><i class="p-body"/><i class="p-leg right"/><i class="p-leg left"/><i class="p-arm right"/><i class="p-arm left"/>
        </div>
        <div v-else class="skin-figure empty">无皮肤</div>
        <div class="skin-info">
          <h2>{{account.name}}</h2>
          <p class="field-hint">{{account.kind==='offline'?'离线账户':'Microsoft 账户'}} · <span class="mono">{{account.uuid}}</span></p>
          <div class="button-row">
            <button class="button compact" :disabled="!account.kind||account.kind==='offline'" @click="refresh">刷新皮肤数据</button>
          </div>
        </div>
      </section>
      <section class="section">
        <h3 class="content-subtitle">帐户材质</h3>
        <div class="skin-assets">
          <figure v-if="skin"><img :src="skin" alt="皮肤材质"/><figcaption>皮肤</figcaption></figure>
          <figure v-if="cape"><img :src="cape" alt="披风材质"/><figcaption>披风</figcaption></figure>
          <p v-if="!skin&&!cape" class="field-hint">这个账户没有公开的皮肤或披风材质。</p>
        </div>
      </section>
    </template>
  </div>
</template>
