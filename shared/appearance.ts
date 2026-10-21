import type { Settings } from './model'

export const accentPresets = [
  { name: '草木绿', color: '#a7df8b' },
  { name: '晴空蓝', color: '#74b5ff' },
  { name: '鸢尾紫', color: '#b8a0ff' },
  { name: '日落橙', color: '#f3b176' },
  { name: '玫瑰粉', color: '#ee9fba' },
]
function rgb(hex: string) { return [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16)) }
function mix(a: string, b: string, amount: number) {
  const left = rgb(a), right = rgb(b)
  return '#' + left.map((v, i) => Math.round(v + (right[i] - v) * amount).toString(16).padStart(2, '0')).join('')
}
function luminance(color: string) {
  const channels = rgb(color).map(c => { const v = c / 255; return v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4 })
  return .2126 * channels[0] + .7152 * channels[1] + .0722 * channels[2]
}
export function contrastRatio(a: string,b: string) { const left=luminance(a),right=luminance(b);return (Math.max(left,right)+.05)/(Math.min(left,right)+.05) }
export function foreground(color: string) { return contrastRatio(color,'#111611')>=contrastRatio(color,'#ffffff')?'#111611':'#ffffff' }
export function appearanceVariables(settings: Pick<Settings, 'theme' | 'accent'>) {
  let accent = settings.theme === 'light' ? mix(settings.accent, '#000000', .4) : mix(settings.accent, '#ffffff', .1)
  const panel = settings.theme === 'light' ? '#fbfcf9' : '#181b20'
  // Accent is also used for small text: meet the normal-text AA contrast ratio,
  // including a user choosing pure black on a dark theme or white on a light one.
  while (contrastRatio(accent,panel)<4.5)accent=mix(accent,settings.theme==='light'?'#000000':'#ffffff',.12)
  return {
    '--green': accent,
    '--green-bright': mix(accent, settings.theme === 'light' ? '#000000' : '#ffffff', .13),
    '--green-bg': mix(panel, accent, .18),
    '--green-dark': mix(panel, accent, .09),
    '--accent-ink': foreground(accent),
    '--accent-border': mix(panel, accent, .4),
  }
}
