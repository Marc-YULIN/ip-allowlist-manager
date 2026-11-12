<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { message } from 'ant-design-vue'
import { getOperations, retryOperation } from '../api/operations'
import { reportError } from '../api/http'
import type { OperationRow } from '../api/types'
import { useAuthStore } from '../stores/auth'
import { dateTime, stateColor } from '../utils/display'
const auth = useAuthStore()
const rows = ref<OperationRow[]>([])
const state = ref('')
const page = ref(1)
const total = ref(0)
const loading = ref(false)
const retrying = ref<string | null>(null)
let timer: ReturnType<typeof setTimeout> | undefined
let version = 0
let disposed = false
async function load() {
  const request = ++version
  clearTimeout(timer)
  loading.value = true
  try {
    const result = await getOperations({ state: state.value || undefined, page: page.value })
    if (request !== version || disposed) return
    rows.value = result.items; total.value = result.total
    if (rows.value.some(x => ['Pending', 'InFlight', 'Uncertain'].includes(x.operation.state))) timer = setTimeout(load, 3000)
  } catch (error) { if (request === version) reportError(error) } finally { if (request === version) loading.value = false }
}
function filter() { page.value = 1; void load() }
function changePage(value: number) { page.value = value; void load() }
async function retry(id: string) {
  retrying.value = id
  try { await retryOperation(id); message.success('Retry queued.'); await load() } catch (error) { reportError(error) } finally { retrying.value = null }
}
onMounted(load)
onUnmounted(() => { disposed = true; version++; clearTimeout(timer) })
const columns = [{ title: 'Operation', key: 'id' }, { title: 'Action', key: 'action' }, { title: 'State', key: 'state' }, { title: 'Attempts', key: 'attempts' }, { title: 'Created', key: 'createdAt' }, { title: 'Next attempt', key: 'nextAttemptAt' }, { title: 'Last error', key: 'lastError' }, { title: '', key: 'retry' }]
</script>
<template>
  <div class="page-heading"><div><h1>Sync operations</h1><p>Inspect queued work, remote outcomes, and durable dispatch evidence.</p></div></div>
  <div class="data-card"><div class="toolbar"><a-select v-model:value="state" aria-label="Operation state" :options="[{ label: 'All states', value: '' }, ...['Pending','InFlight','Uncertain','Succeeded','Failed','DeadLetter'].map(x => ({ label: x, value: x }))]" @change="filter" /><a-button :loading="loading" @click="load">Refresh</a-button></div>
    <a-table :data-source="rows" :columns="columns" :row-key="(row: OperationRow) => row.operation.id" :loading="loading" :scroll="{ x: 1200 }" :pagination="{ current: page, total, pageSize: 20, showSizeChanger: false, onChange: changePage }" :locale="{ emptyText: 'No synchronization operations found.' }">
      <template #bodyCell="{ column, record }">
        <div v-if="column.key === 'id'" class="mono"><span>{{ record.operation.id }}</span><div class="muted">Entry: {{ record.operation.entryId || 'Remote item' }}</div></div>
        <a-tag v-else-if="column.key === 'state'" :color="stateColor(record.operation.state)">{{ record.operation.state }}</a-tag>
        <span v-else-if="['createdAt','nextAttemptAt'].includes(column.key)">{{ dateTime(record.operation[column.key]) }}</span>
        <a-button v-else-if="column.key === 'retry' && auth.isAdmin && ['Failed','DeadLetter'].includes(record.operation.state)" size="small" :loading="retrying === record.operation.id" @click="retry(record.operation.id)">Retry</a-button>
        <span v-else-if="column.key !== 'retry'">{{ record.operation[column.key] || '—' }}</span>
      </template>
      <template #expandedRowRender="{ record }"><div style="padding:8px"><p class="muted">Remote operation: {{ record.operation.remoteOperationId || 'Not assigned' }}</p><a-empty v-if="!record.dispatchLogs.length" description="No dispatch evidence yet." /><a-timeline v-else><a-timeline-item v-for="log in record.dispatchLogs" :key="log.id" :color="log.phase === 'Failed' ? 'red' : 'blue'"><strong>{{ log.phase }}</strong> · Attempt {{ log.attempt }} · {{ dateTime(log.at) }}<div>{{ log.detail }}</div></a-timeline-item></a-timeline></div></template>
    </a-table>
  </div>
</template>
