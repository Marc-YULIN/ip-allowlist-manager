<script setup lang="ts">
import { computed, onUnmounted, reactive, ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import { createEntry, deleteEntry, getEntries } from '../api/entries'
import { reportError } from '../api/http'
import type { Entry, EntryInput } from '../api/types'
import { useEnvironments } from '../composables/useEnvironments'
import { useAuthStore } from '../stores/auth'
import { normalizeCidr } from '../utils/cidr'
import { dateTime, stateColor } from '../utils/display'
const { environment, environments } = useEnvironments()
const selectedEnvironment = computed(() => environments.value.find(x => x.code === environment.value))
const auth = useAuthStore()
const kind = ref<string>('')
const search = ref('')
const page = ref(1)
const total = ref(0)
const rows = ref<Entry[]>([])
const loading = ref(false)
const saving = ref(false)
const modal = ref(false)
const warnings = ref<string[]>([])
const input = reactive<EntryInput>({ cidr: '', kind: 'Allow', description: '', owner: '', expiresAt: null })
const expiry = ref('')
const validation = computed(() => normalizeCidr(input.cidr))
const descriptionLength = computed(() => Array.from(input.description).length)
let timer: ReturnType<typeof setTimeout> | undefined
let version = 0
let disposed = false
async function load() {
  if (!environment.value) return
  const request = ++version
  clearTimeout(timer)
  loading.value = true
  try {
    const result = await getEntries(environment.value, { kind: kind.value || undefined, q: search.value || undefined, page: page.value })
    if (request !== version || disposed) return
    rows.value = result.items; total.value = result.total
    if (rows.value.some(x => ['PendingAdd', 'PendingRemove'].includes(x.state))) timer = setTimeout(load, 3000)
  } catch (error) { if (request === version) reportError(error) } finally { if (request === version) loading.value = false }
}
watch([environment, kind], () => { page.value = 1; void load() })
function searchEntries() { page.value = 1; void load() }
function changePage(value: number) { page.value = value; void load() }
function open() { Object.assign(input, { cidr: '', kind: 'Allow', description: '', owner: '', expiresAt: null }); expiry.value = ''; modal.value = true }
async function save() {
  if (!validation.value.valid || descriptionLength.value > 200) return
  if (expiry.value && new Date(expiry.value).getTime() <= Date.now()) { message.error('Expiry must be in the future.'); return }
  saving.value = true
  try {
    const result = await createEntry(environment.value, { ...input, cidr: validation.value.normalized!, expiresAt: expiry.value ? new Date(expiry.value).toISOString() : null })
    warnings.value = result.warnings; modal.value = false; page.value = 1; message.success('Rule queued for synchronization.'); await load()
  } catch (error) { reportError(error) } finally { saving.value = false }
}
async function remove(entry: Entry) { try { await deleteEntry(environment.value, entry.id); message.success('Removal queued.'); await load() } catch (error) { reportError(error) } }
onUnmounted(() => { disposed = true; version++; clearTimeout(timer) })
const columns = [
  { title: 'CIDR', dataIndex: 'cidr', key: 'cidr' }, { title: 'Kind', key: 'kind' }, { title: 'State', key: 'state' },
  { title: 'Description', dataIndex: 'description' }, { title: 'Owner', dataIndex: 'owner' },
  { title: 'Expires', key: 'expiresAt' }, { title: 'Created', key: 'createdAt' }, { title: '', key: 'actions' },
]
</script>
<template>
  <div class="page-heading"><div><h1>IP entries</h1><p>Manage network rules and track their remote synchronization.</p></div><a-button v-if="auth.isAdmin" type="primary" :disabled="!environment" @click="open">Add entry</a-button></div>
  <a-alert v-if="warnings.length" type="warning" show-icon closable message="Rule created with overlap warnings" style="margin-bottom:20px" @close="warnings = []"><template #description><ul><li v-for="warning in warnings" :key="warning">{{ warning }}</li></ul></template></a-alert>
  <div class="data-card">
    <p v-if="selectedEnvironment" class="muted">Allow list: <span class="mono">{{ selectedEnvironment.allowListName }}</span> · Block list: <span class="mono">{{ selectedEnvironment.blockListName }}</span></p>
    <div class="toolbar"><a-select v-model:value="environment" aria-label="Environment" :options="environments.map(x => ({ label: x.name, value: x.code }))" /><a-select v-model:value="kind" aria-label="Kind" :options="[{ label: 'All kinds', value: '' }, { label: 'Allow', value: 'Allow' }, { label: 'Block', value: 'Block' }]" /><a-input-search v-model:value="search" placeholder="Search CIDR, description, owner" allow-clear @search="searchEntries" /><a-button :loading="loading" @click="load">Refresh</a-button></div>
    <a-table :columns="columns" :data-source="rows" row-key="id" :loading="loading" :scroll="{ x: 1050 }" :pagination="{ current: page, total, pageSize: 20, showSizeChanger: false, onChange: changePage }" :locale="{ emptyText: 'No rules found for this environment.' }">
      <template #bodyCell="{ column, record }">
        <span v-if="column.key === 'cidr'" class="mono">{{ record.cidr }}</span>
        <a-tag v-else-if="column.key === 'kind'" :color="record.kind === 'Block' ? 'red' : 'blue'">{{ record.kind }}</a-tag>
        <a-tag v-else-if="column.key === 'state'" :color="stateColor(record.state)">{{ record.state }}</a-tag>
        <span v-else-if="column.key === 'expiresAt'">{{ dateTime(record.expiresAt) }}</span>
        <span v-else-if="column.key === 'createdAt'">{{ dateTime(record.createdAt) }}</span>
        <a-popconfirm v-else-if="column.key === 'actions' && auth.isAdmin && ['Active', 'Failed'].includes(record.state)" title="Queue removal of this rule?" ok-text="Remove" @confirm="remove(record)"><a-button danger size="small">Remove</a-button></a-popconfirm>
      </template>
    </a-table>
    <p v-if="rows.some(x => ['PendingAdd', 'PendingRemove'].includes(x.state))" class="muted">Pending rules refresh automatically every 3 seconds.</p>
  </div>
  <a-modal v-model:open="modal" title="Add IP entry" :confirm-loading="saving" :ok-button-props="{ disabled: !validation.valid || descriptionLength > 200 }" ok-text="Queue entry" @ok="save">
    <a-form layout="vertical" style="margin-top:20px">
      <a-form-item label="IP address or CIDR" :validate-status="input.cidr ? validation.valid ? 'success' : 'error' : ''" :help="input.cidr ? validation.error || 'Normalized: ' + validation.normalized : 'Single IP addresses use /32 or /128.'"><a-input v-model:value="input.cidr" placeholder="192.0.2.0/24" /></a-form-item>
      <a-form-item label="Kind"><a-radio-group v-model:value="input.kind" :options="['Allow', 'Block']" /></a-form-item>
      <a-form-item label="Description" :validate-status="descriptionLength > 200 ? 'error' : ''" :help="descriptionLength + '/200 characters'"><a-textarea v-model:value="input.description" :rows="2" /></a-form-item>
      <a-form-item label="Owner"><a-input v-model:value="input.owner" :maxlength="200" /></a-form-item>
      <a-form-item label="Expires (local time, optional)"><a-input v-model:value="expiry" type="datetime-local" /></a-form-item>
    </a-form>
  </a-modal>
</template>
