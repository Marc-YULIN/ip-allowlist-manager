import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { currentUser, login } from '../api/auth'
import { tokenKey } from '../api/http'
import type { User } from '../api/types'
export const useAuthStore = defineStore('auth', () => {
  const token = ref(sessionStorage.getItem(tokenKey))
  const user = ref<User | null>(null)
  const isAdmin = computed(() => user.value?.role === 'Admin')
  async function signIn(username: string, password: string) {
    const result = await login(username, password)
    token.value = result.token
    user.value = result.user
    sessionStorage.setItem(tokenKey, result.token)
  }
  async function restore() { user.value = await currentUser() }
  function signOut() { token.value = null; user.value = null; sessionStorage.removeItem(tokenKey) }
  return { token, user, isAdmin, signIn, restore, signOut }
})
