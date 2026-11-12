<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import { getSnapshots, reconcile } from '../api/drift'
import { reportError } from '../api/http'
import type { Snapshot } from '../api/types'
import { useEnvironments } from '../composables/useEnvironments'
import { useAuthStore } from '../stores/auth'
import { dateTime } from '../utils/display'
const { environments, environment } = useEnvironments()
const auth = useAuthStore()
const rows = ref<Snapshot[]>([])
const selected = reactive<Record<Snapshot['listKind'], Snapshot | null>>({ Allow: null, Block: null })
const loading = ref(false)
const running = ref(false)
const autoFix = ref(false)
let version = 0
async function load() {
  if (!environment.value) return
  const request = ++version
  rows.value = []; selected.Allow = null; selected.Block = null; loading.value = true
  try {
    const result = await getSnapshots(environment.value)
    if (request === version) {
      rows.value = result
      selected.Allow = result.find(x => x.listKind === 'Allow') || null
      selected.Block = result.find(x => x.listKind === 'Block') || null
    }
  } catch (error) { if (request === version) reportError(error) } finally { if (request === version) loading.value = false }
}
watch(environment, load)
async function run() {
  running.value = true
  try { await reconcile(environment.value, autoFix.value); message.success(autoFix.value ? 'Comparison complete. Eligible repairs queued.' : 'Comparison complete.'); await load() } catch (error) { reportError(error) } finally { running.value = false }
}
function parse(value?: string) { try { const parsed: unknown = JSON.parse(value || '[]'); return Array.isArray(parsed) ? parsed : [] } catch { return [] } }
function itemLabel(value: unknown) { return typeof value === 'string' ? value : JSON.stringify(value) }
const sections = computed(() => (['Allow', 'Block'] as const).map(kind => ({
  kind, title: `${kind} list`, snapshot: selected[kind], history: rows.value.filter(x => x.listKind === kind),
  missing: parse(selected[kind]?.missingRemoteJson), extra: parse(selected[kind]?.extraRemoteJson),
})))
const columns = [{ title: 'Taken', key: 'takenAt' }, { title: 'Local rules', dataIndex: 'localCount' }, { title: 'Remote items', dataIndex: 'remoteCount' }, { title: 'Repair requested', key: 'autoFixed' }, { title: '', key: 'view' }]
</script>
<template>
  <div class="page-heading"><div><h1>Drift reconciliation</h1><p>Compare allow and block rules with their separate remote lists. Rules still synchronizing are excluded from drift.</p></div></div>
  <div class="data-card"><div class="toolbar"><a-select v-model:value="environment" :disabled="running" aria-label="Environment" :options="environments.map(x => ({ label: x.name, value: x.code }))" /><a-checkbox v-if="auth.isAdmin" v-model:checked="autoFix">Queue repairs</a-checkbox><a-popconfirm v-if="auth.isAdmin && autoFix" title="Compare and queue eligible remote repairs?" @confirm="run"><a-button type="primary" :loading="running" :disabled="!environment">Run reconcile</a-button></a-popconfirm><a-button v-else-if="auth.isAdmin" type="primary" :loading="running" :disabled="!environment" @click="run">Run reconcile</a-button><a-button :loading="loading" @click="load">Refresh</a-button></div>
    <a-alert v-if="autoFix" type="info" show-icon message="Missing rules are queued for synchronization. Removing unknown remote items also requires server configuration." style="margin-bottom:20px" />
    <section v-for="section in sections" :key="section.kind" :aria-label="section.title" style="margin-top:28px">
      <h2>{{ section.title }}</h2>
      <div v-if="section.snapshot">
        <p class="muted">Snapshot: {{ dateTime(section.snapshot.takenAt) }} · {{ section.snapshot.localCount }} local / {{ section.snapshot.remoteCount }} remote</p>
        <div class="split">
          <a-card title="Missing from remote" size="small"><a-list :data-source="section.missing" :locale="{ emptyText: 'No missing rules.' }"><template #renderItem="{ item }"><a-list-item><span class="mono">{{ itemLabel(item) }}</span></a-list-item></template></a-list></a-card>
          <a-card title="Extra on remote" size="small"><a-list :data-source="section.extra" :locale="{ emptyText: 'No extra items.' }"><template #renderItem="{ item }"><a-list-item><span class="mono">{{ itemLabel(item) }}</span></a-list-item></template></a-list></a-card>
        </div>
      </div>
      <a-empty v-else description="No snapshots yet. Run a comparison to inspect drift." />
      <h3 style="font-size:18px;margin-top:28px">Snapshot history</h3>
      <a-table :columns="columns" :data-source="section.history" :loading="loading" row-key="id" :scroll="{ x: 650 }" :pagination="{ pageSize: 10, showSizeChanger: false }"><template #bodyCell="{ column, record }"><span v-if="column.key === 'takenAt'">{{ dateTime(record.takenAt) }}</span><a-tag v-else-if="column.key === 'autoFixed'" :color="record.autoFixed ? 'blue' : 'default'">{{ record.autoFixed ? 'Yes' : 'No' }}</a-tag><a-button v-else-if="column.key === 'view'" type="link" @click="selected[section.kind] = record">Inspect</a-button></template></a-table>
    </section>
  </div>
</template>
