<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { reportError } from '../api/http'
const form = reactive({ username: '', password: '' })
const loading = ref(false)
const auth = useAuthStore()
const router = useRouter()
async function submit() {
  loading.value = true
  try { await auth.signIn(form.username, form.password); form.password = ''; await router.replace('/entries') } catch (error) { reportError(error) } finally { loading.value = false }
}
</script>
<template><main class="login"><a-card class="login-card"><div class="login-mark">IP</div><h1>Allowlist Manager</h1><p>Sign in to manage access rules and remote synchronization.</p><a-form layout="vertical" :model="form" @finish="submit"><a-form-item label="Username" name="username" :rules="[{ required: true, message: 'Enter your username.' }]"><a-input v-model:value="form.username" autocomplete="username" size="large" /></a-form-item><a-form-item label="Password" name="password" :rules="[{ required: true, message: 'Enter your password.' }]"><a-input-password v-model:value="form.password" autocomplete="current-password" size="large" /></a-form-item><a-button type="primary" html-type="submit" block size="large" :loading="loading">Sign in</a-button></a-form></a-card></main></template>
<style scoped>.login { min-height: 100vh; display: grid; place-items: center; padding: 24px; background: linear-gradient(135deg,#edf3ff,#f8fafc); }.login-card { width: 420px; max-width: 100%; padding: 12px; box-shadow: 0 16px 60px #1636610d; }.login-mark { display: grid; place-items: center; background: #1677ff; color: white; border-radius: 12px; width: 48px; height: 48px; font-weight: 700; font-size: 20px; }h1 { font-size: 26px; margin: 24px 0 8px; }p { color: #64748b; margin-bottom: 28px; }</style>
