<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { Account } from '../../shared/model'

const props = withDefaults(defineProps<{ account?: Account | null; size?: number }>(), { size: 24 })
const broken = ref(false)
const attempt = ref(0)
watch(() => props.account?.id, () => { broken.value = false; attempt.value = 0 })

const initial = computed(() => (props.account?.name || '?').slice(0, 1).toUpperCase())
const skin = computed(() => {
  const value = (((props.account as any)?.skin as string) || '')
  return value.startsWith('http://') ? 'https://' + value.slice(7) : value
})
const sources = computed(() => {
  if (skin.value) return [] as string[]
  const uuid = props.account?.uuid
  if (!uuid || broken.value) return [] as string[]
  const size = Math.max(32, (props.size || 24) * 2)
  return [
    `https://mc-heads.net/avatar/${encodeURIComponent(uuid)}/${size}`,
    `https://minotar.net/helm/${encodeURIComponent(uuid)}/${size}.png`,
    `https://crafatar.com/avatars/${encodeURIComponent(uuid)}?size=${size}&overlay`
  ]
})
const url = computed(() => sources.value[attempt.value] || '')
function fail() {
  attempt.value += 1
  if (attempt.value >= sources.value.length) broken.value = true
}
// 离线账户没有皮肤时退回彩色首字母，保证每个账户都有可辨认的图标。
const tint = computed(() => {
  const name = props.account?.name || ''
  let hash = 0
  for (const char of name) hash = (hash * 31 + char.charCodeAt(0)) >>> 0
  const hues = [205, 150, 275, 25, 330, 95]
  return `hsl(${hues[hash % hues.length]} 52% 58%)`
})
// 有皮肤链接时直接裁 64×64 皮肤里的头部（8,8）和帽子层（40,8），不依赖第三方头像站。
const style = computed(() => {
  const size = props.size || 24
  const base: Record<string, string> = { width: size + 'px', height: size + 'px' }
  if (!skin.value) return base
  const scale = size * 8
  base.backgroundImage = `url("${skin.value}"), url("${skin.value}")`
  base.backgroundSize = `${scale}px ${scale}px`
  base.backgroundPosition = `-${size * 5}px -${size}px, -${size}px -${size}px`
  base.backgroundRepeat = 'no-repeat'
  return base
})
</script>

<template>
  <span class="account-avatar" :style="style">
    <img v-if="!skin && url" :key="url" :src="url" alt="" referrerpolicy="no-referrer" loading="lazy" @error="fail" />
    <span v-else-if="!skin" class="account-avatar-letter" :style="{ background: tint }">{{ initial }}</span>
  </span>
</template>
