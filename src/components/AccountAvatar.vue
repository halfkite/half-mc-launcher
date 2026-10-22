<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { Account } from '../../shared/model'

const props = withDefaults(defineProps<{ account?: Account | null; size?: number }>(), { size: 24 })
const broken = ref(false)
watch(() => props.account?.id, () => { broken.value = false })

const initial = computed(() => (props.account?.name || '?').slice(0, 1).toUpperCase())
const url = computed(() => {
  const uuid = props.account?.uuid
  if (!uuid || broken.value) return ''
  return `https://mc-heads.net/avatar/${encodeURIComponent(uuid)}/${Math.max(32, (props.size || 24) * 2)}`
})
// 离线账户没有皮肤时退回彩色首字母，保证每个账户都有可辨认的图标。
const tint = computed(() => {
  const name = props.account?.name || ''
  let hash = 0
  for (const char of name) hash = (hash * 31 + char.charCodeAt(0)) >>> 0
  const hues = [205, 150, 275, 25, 330, 95]
  return `hsl(${hues[hash % hues.length]} 52% 58%)`
})
</script>

<template>
  <span class="account-avatar" :style="{ width: (size || 24) + 'px', height: (size || 24) + 'px' }">
    <img v-if="url" :src="url" alt="" referrerpolicy="no-referrer" loading="lazy" @error="broken = true" />
    <span v-else class="account-avatar-letter" :style="{ background: tint }">{{ initial }}</span>
  </span>
</template>
