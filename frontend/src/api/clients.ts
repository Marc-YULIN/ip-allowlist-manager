import { http } from './http'
import type { IntegrationClient } from './types'
export const getClients = async () => (await http.get<IntegrationClient[]>('/integration-clients')).data
export const createClient = async (name: string, scopes: string[]) => (await http.post<{ id: string; name: string; keyPrefix: string; scopes: string; key: string }>('/integration-clients', { name, scopes })).data
export const revokeClient = async (id: string) => http.post(`/integration-clients/${id}/revoke`)
