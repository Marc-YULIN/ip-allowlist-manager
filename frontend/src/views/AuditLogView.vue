<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { getAuditLogs } from '../api/audit'
import { reportError } from '../api/http'
import type { AuditLog } from '../api/types'
import { dateTime, prettyJson } from '../utils/display'
const filters = reactive({ actor: '', action: '', from: '', to: '' })
const rows = ref<AuditLog[]>([])
const page = ref(1)
const total = ref(0)
const loading = ref(false)
let version = 0
async function load() {
  const request = ++version
  loading.value = true
  try {
    const result = await getAuditLogs({ actor: filters.actor || undefined, action: filters.action || undefined, from: filters.from ? new Date(filters.from).toISOString() : undefined, to: filters.to ? new Date(filters.to).toISOString() : undefined, page: page.value })
    if (request === version) { rows.value = result.items; total.value = result.total }
  } catch (error) { reportError(error) } finally { if (request === version) loading.value = false }
}
function filter() { page.value = 1; void load() }
function changePage(value: number) { page.value = value; void load() }
onMounted(load)
const columns = [{ title: 'Time', key: 'at' }, { title: 'Actor', dataIndex: 'actor' }, { title: 'Identity', dataIndex: 'actorType' }, { title: 'Action', dataIndex: 'action' }, { title: 'Target type', dataIndex: 'targetType' }, { title: 'Target ID', dataIndex: 'targetId' }]
</script>
<template>
  <div class="page-heading"><div><h1>Audit log</h1><p>Trace who changed a rule and inspect its before and after values.</p></div></div>
  <div class="data-card"><a-form layout="inline" class="toolbar" @finish="filter"><a-form-item label="Actor"><a-input v-model:value="filters.actor" allow-clear /></a-form-item><a-form-item label="Action"><a-input v-model:value="filters.action" allow-clear /></a-form-item><a-form-item label="From"><a-input v-model:value="filters.from" type="datetime-local" /></a-form-item><a-form-item label="To"><a-input v-model:value="filters.to" type="datetime-local" /></a-form-item><a-button type="primary" html-type="submit" :loading="loading">Search</a-button></a-form>
    <a-table :columns="columns" :data-source="rows" :loading="loading" row-key="id" :scroll="{ x: 1000 }" :pagination="{ current: page, total, pageSize: 20, showSizeChanger: false, onChange: changePage }" :locale="{ emptyText: 'No audit records match these filters.' }"><template #bodyCell="{ column, record }"><span v-if="column.key === 'at'">{{ dateTime(record.at) }}</span></template><template #expandedRowRender="{ record }"><p class="mono muted">Trace: {{ record.traceId }}</p><div class="split"><div><strong>Before</strong><pre>{{ prettyJson(record.beforeJson) }}</pre></div><div><strong>After</strong><pre>{{ prettyJson(record.afterJson) }}</pre></div></div></template></a-table>
  </div>
</template>
