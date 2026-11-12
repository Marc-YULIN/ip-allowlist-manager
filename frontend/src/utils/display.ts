import dayjs from 'dayjs'
export const dateTime = (value?: string | null) => value ? dayjs(value).format('YYYY-MM-DD HH:mm:ss') : '—'
export const stateColor = (state: string) => ({ Active: 'green', Succeeded: 'green', Completed: 'green', Failed: 'red', DeadLetter: 'red', Uncertain: 'orange', Pending: 'blue', PendingAdd: 'blue', PendingRemove: 'orange', InFlight: 'processing', Removed: 'default' } as Record<string, string>)[state] || 'default'
export function prettyJson(value?: string | null) { try { return JSON.stringify(JSON.parse(value || 'null'), null, 2) } catch { return value || 'null' } }
