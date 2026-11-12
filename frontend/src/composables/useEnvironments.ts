import { onMounted, ref } from 'vue'
import { getEnvironments } from '../api/environments'
import { reportError } from '../api/http'
import type { Environment } from '../api/types'
export function useEnvironments() {
  const environments = ref<Environment[]>([])
  const environment = ref('')
  onMounted(async () => {
    try { environments.value = await getEnvironments(); environment.value = environments.value[0]?.code || '' } catch (error) { reportError(error) }
  })
  return { environments, environment }
}
