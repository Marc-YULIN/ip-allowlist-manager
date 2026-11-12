import { http } from './http'
import type { Decision } from './types'
export const checkIp = async (ip: string, env: string) => (await http.get<Decision>('/check', { params: { ip, env } })).data
