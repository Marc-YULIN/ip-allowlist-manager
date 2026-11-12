import { http } from './http'
import type { Entry, EntryInput, Page } from './types'
export const getEntries = async (env: string, params: { kind?: string; q?: string; page: number }) => (await http.get<Page<Entry>>(`/environments/${env}/entries`, { params })).data
export const createEntry = async (env: string, input: EntryInput) => (await http.post<{ entry: Entry; warnings: string[] }>(`/environments/${env}/entries`, input)).data
export const deleteEntry = async (env: string, id: string) => http.delete(`/environments/${env}/entries/${id}`)
