import { http } from './http'
import type { OperationRow, Page } from './types'
export const getOperations = async (params: { state?: string; page: number }) => (await http.get<Page<OperationRow>>('/operations', { params })).data
export const retryOperation = async (id: string) => http.post(`/operations/${id}/retry`)
