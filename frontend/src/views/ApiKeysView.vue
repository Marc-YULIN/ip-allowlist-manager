<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { message } from 'ant-design-vue'
import { createClient, getClients, revokeClient } from '../api/clients'
import { reportError } from '../api/http'
import type { IntegrationClient } from '../api/types'
import { dateTime } from '../utils/display'
const rows = ref<IntegrationClient[]>([])
const loading = ref(false)
const saving = ref(false)
const modal = ref(false)
const keyModal = ref(false)
const secret = ref('')
const input = reactive({ name: '', scopes: ['ip:check'] })
const scopes = ['allowlist:read', 'allowlist:write', 'ip:check']
async function load() { loading.value = true; try { rows.value = await getClients() } catch (error) { reportError(error) } finally { loading.value = false } }
function open() { input.name = ''; input.scopes = ['ip:check']; modal.value = true }
async function save() {
  if (!input.name.trim() || !input.scopes.length) return
  saving.value = true
  try { const result = await createClient(input.name.trim(), input.scopes); secret.value = result.key; modal.value = false; keyModal.value = true; await load() } catch (error) { reportError(error) } finally { saving.value = false }
}
async function revoke(id: string) { try { await revokeClient(id); message.success('Key revoked.'); await load() } catch (error) { reportError(error) } }
async function copy() {
  try { await navigator.clipboard.writeText(secret.value); message.success('Key copied.'); } catch { message.error('Clipboard unavailable. Select and copy the key manually.'); }
}
function dismissKey() { keyModal.value = false; secret.value = '' }
onMounted(load)
const columns = [{ title: 'Name', dataIndex: 'name' }, { title: 'Prefix', key: 'keyPrefix' }, { title: 'Scopes', key: 'scopes' }, { title: 'Created', key: 'createdAt' }, { title: 'Last used', key: 'lastUsedAt' }, { title: 'Status', key: 'status' }, { title: '', key: 'actions' }]
</script>
<template>
  <div class="page-heading"><div><h1>API keys</h1><p>Grant scoped access to integrations and revoke credentials when needed.</p></div><a-button type="primary" @click="open">Create key</a-button></div>
  <div class="data-card"><div class="toolbar"><a-button :loading="loading" @click="load">Refresh</a-button></div><a-table :columns="columns" :data-source="rows" :loading="loading" row-key="id" :scroll="{ x: 1000 }" :pagination="{ pageSize: 10, showSizeChanger: false }" :locale="{ emptyText: 'No integration keys created.' }"><template #bodyCell="{ column, record }"><code v-if="column.key === 'keyPrefix'">{{ record.keyPrefix }}</code><a-space v-else-if="column.key === 'scopes'" wrap><a-tag v-for="scope in record.scopes.split(',')" :key="scope">{{ scope }}</a-tag></a-space><span v-else-if="['createdAt','lastUsedAt'].includes(column.key)">{{ dateTime(record[column.key]) }}</span><a-tag v-else-if="column.key === 'status'" :color="record.revokedAt ? 'default' : 'green'">{{ record.revokedAt ? 'Revoked' : 'Active' }}</a-tag><a-popconfirm v-else-if="column.key === 'actions' && !record.revokedAt" title="Revoke this key? Its integrations will lose access immediately." ok-text="Revoke" @confirm="revoke(record.id)"><a-button danger size="small">Revoke</a-button></a-popconfirm></template></a-table></div>
  <a-modal v-model:open="modal" title="Create integration key" ok-text="Create key" :confirm-loading="saving" :ok-button-props="{ disabled: !input.name.trim() || !input.scopes.length }" @ok="save"><a-form layout="vertical" style="margin-top:20px"><a-form-item label="Integration name"><a-input v-model:value="input.name" :maxlength="200" /></a-form-item><a-form-item label="Scopes"><a-checkbox-group v-model:value="input.scopes" :options="scopes" /></a-form-item></a-form><a-alert type="info" message="Choose only the permissions this integration needs." show-icon /></a-modal>
  <a-modal :open="keyModal" title="Copy your new API key" :mask-closable="false" :keyboard="false" @cancel="dismissKey"><a-alert type="warning" message="This key is shown only once. Store it securely before closing this dialog." show-icon /><pre class="mono" style="user-select:all">{{ secret }}</pre><a-button @click="copy">Copy key</a-button><template #footer><a-button type="primary" @click="dismissKey">I have saved the key</a-button></template></a-modal>
</template>
