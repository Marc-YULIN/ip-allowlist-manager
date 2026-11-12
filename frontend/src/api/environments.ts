import { http } from './http'
import type { Environment } from './types'
export const getEnvironments = async () => (await http.get<Environment[]>('/environments')).data
