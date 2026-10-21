import { createHash } from 'node:crypto'
import { z } from 'zod'
export const offlineName=z.string().trim().regex(/^[A-Za-z0-9_]{3,16}$/,'离线名称需为 3–16 位英文字母、数字或下划线')
export function offlineUuid(name:string) {
  const bytes=createHash('md5').update(`OfflinePlayer:${offlineName.parse(name)}`,'utf8').digest()
  bytes[6]=(bytes[6]&0x0f)|0x30;bytes[8]=(bytes[8]&0x3f)|0x80
  return bytes.toString('hex')
}
