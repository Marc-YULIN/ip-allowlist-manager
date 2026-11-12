import { http } from './http'
import type { Snapshot } from './types'
export const getSnapshots = async (env: string) => (await http.get<Snapshot[]>(`/environments/${env}/snapshots`)).data
export const reconcile = async (env: string, autoFix: boolean) => (await http.post<Snapshot[]>(`/environments/${env}/reconcile`, null, { params: { autoFix } })).data
