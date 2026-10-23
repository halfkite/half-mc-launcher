import type { Settings } from './model'

export const accentPresets = [
  { name: '草木绿', color: '#a7df8b' },
  { name: '晴空蓝', color: '#74b5ff' },
  { name: '鸢尾紫', color: '#b8a0ff' },
  { name: '日落橙', color: '#f3b176' },
  { name: '玫瑰粉', color: '#ee9fba' },
]
export const backgroundPresets = [
  { name: '跟随主题', color: '' },
  { name: '暖纸', color: '#f1ede4' },
  { name: '米白', color: '#f7f4ec' },
  { name: '雾灰', color: '#e7eaec' },
  { name: '墨黑', color: '#0d0e10' },
  { name: '深蓝', color: '#101822' },
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
function validHex(value?: string) { return !!value && /^#[0-9a-f]{6}$/i.test(value) }

export function appearanceVariables(settings: Pick<Settings, 'theme' | 'accent'> & { background?: string }) {
  const custom = validHex(settings.background) ? settings.background! : ''
  const dark = custom ? luminance(custom) < .42 : settings.theme === 'dark'
  const panel = custom ? mix(custom, '#ffffff', dark ? .06 : .45) : (settings.theme === 'light' ? '#fbfcf9' : '#181b20')
  let accent = dark ? mix(settings.accent, '#ffffff', .1) : mix(settings.accent, '#000000', .4)
  // Accent is also used for small text: meet the normal-text AA contrast ratio,
  // including a user choosing pure black on a dark theme or white on a light one.
  while (contrastRatio(accent, panel) < 4.5) accent = mix(accent, dark ? '#ffffff' : '#000000', .12)
  const vars: Record<string, string> = {
    '--green': accent,
    '--green-bright': mix(accent, dark ? '#ffffff' : '#000000', .13),
    '--green-bg': mix(panel, accent, .18),
    '--green-dark': mix(panel, accent, .09),
    '--accent-ink': foreground(accent),
    '--accent-border': mix(panel, accent, .4),
  }
  if (custom) {
    const ink = dark ? '#ede9e0' : '#201d18'
    Object.assign(vars, {
      '--bg': custom,
      '--sidebar': mix(custom, dark ? '#ffffff' : '#000000', dark ? .02 : .03),
      '--panel': panel,
      '--raised': mix(panel, dark ? '#ffffff' : '#000000', dark ? .06 : .05),
      '--hover': mix(panel, dark ? '#ffffff' : '#000000', dark ? .12 : .09),
      '--line': mix(panel, dark ? '#ffffff' : '#000000', .2),
      '--line-soft': mix(panel, dark ? '#ffffff' : '#000000', .1),
      '--text': ink,
      '--muted': mix(ink, custom, .48),
      '--faint': mix(ink, custom, .64),
      '--shadow': dark ? '0 22px 60px #00000055' : '0 22px 60px #3a2f2022',
    })
  }
  return vars
}
