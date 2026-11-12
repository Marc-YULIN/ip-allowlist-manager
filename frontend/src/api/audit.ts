import { http } from './http'
import type { AuditLog, Page } from './types'
export const getAuditLogs = async (params: { actor?: string; action?: string; from?: string; to?: string; page: number }) => (await http.get<Page<AuditLog>>('/audit-logs', { params })).data
