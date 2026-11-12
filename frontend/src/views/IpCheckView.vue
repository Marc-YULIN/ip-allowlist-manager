<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { checkIp } from '../api/check'
import { reportError } from '../api/http'
import type { Decision } from '../api/types'
import { useEnvironments } from '../composables/useEnvironments'
import { isIpAddress } from '../utils/cidr'
const { environment, environments } = useEnvironments()
const ip = ref('')
const loading = ref(false)
const result = ref<Decision | null>(null)
const checkedIp = ref('')
const valid = computed(() => isIpAddress(ip.value.trim()))
watch([ip, environment], () => { result.value = null })
async function check() {
  if (!valid.value || !environment.value) return
  const requestedIp = ip.value.trim()
  const requestedEnv = environment.value
  loading.value = true
  try { const decision = await checkIp(requestedIp, requestedEnv); if (requestedIp === ip.value.trim() && requestedEnv === environment.value) { result.value = decision; checkedIp.value = requestedIp } } catch (error) { reportError(error) } finally { loading.value = false }
}
</script>
<template>
  <div class="page-heading"><div><h1>IP check</h1><p>Evaluate a single address against active, unexpired rules. Block rules take precedence.</p></div></div>
  <div class="data-card" style="max-width:850px"><a-form layout="vertical" @finish="check"><a-form-item label="Environment"><a-select v-model:value="environment" :options="environments.map(x => ({ label: x.name, value: x.code }))" /></a-form-item><a-form-item label="IP address" :validate-status="ip && !valid ? 'error' : ''" :help="ip && !valid ? 'Enter a valid IPv4 or IPv6 address without a prefix.' : ''"><a-input v-model:value="ip" placeholder="192.0.2.1" /></a-form-item><a-button type="primary" html-type="submit" :loading="loading" :disabled="!valid || !environment">Check address</a-button></a-form>
    <a-result v-if="result" :status="result.allowed ? 'success' : result.blocked ? 'error' : 'warning'" :title="result.allowed ? 'Allowed' : result.blocked ? 'Blocked' : 'Not allowed'" :sub-title="checkedIp + (result.matchedCidr ? ' matches ' + result.matchedCidr : ' has no matching active rule.')"><template #extra><div v-if="result.matchedEntryId" class="mono muted">Entry: {{ result.matchedEntryId }}</div></template></a-result>
  </div>
</template>
