export interface Page<T> { items: T[]; total: number; page: number; pageSize: number }
export interface User { username: string; role: 'Admin' | 'Viewer' }
export interface Environment { id: string; code: string; name: string; allowListId: string; allowListName: string; blockListId: string; blockListName: string }
export interface Entry { id: string; environmentId: string; cidr: string; kind: 'Allow' | 'Block'; state: string; description: string; owner: string; expiresAt: string | null; createdAt: string; createdBy: string }
export interface EntryInput { cidr: string; kind: 'Allow' | 'Block'; description: string; owner: string; expiresAt: string | null }
export interface Operation { id: string; environmentId: string; listId: string; entryId: string | null; action: string; state: string; attempts: number; nextAttemptAt: string; lastError: string | null; createdAt: string; remoteOperationId: string | null }
export interface DispatchLog { id: string; attempt: number; phase: string; detail: string; at: string }
export interface OperationRow { operation: Operation; dispatchLogs: DispatchLog[] }
export interface Snapshot { id: string; listKind: 'Allow' | 'Block'; takenAt: string; localCount: number; remoteCount: number; missingRemoteJson: string; extraRemoteJson: string; autoFixed: boolean }
export interface AuditLog { id: string; actor: string; actorType: string; action: string; targetType: string; targetId: string; beforeJson: string | null; afterJson: string | null; traceId: string; at: string }
export interface IntegrationClient { id: string; name: string; keyPrefix: string; scopes: string; createdAt: string; lastUsedAt: string | null; revokedAt: string | null }
export interface Decision { allowed: boolean; blocked: boolean; matchedEntryId: string | null; matchedCidr: string | null }
