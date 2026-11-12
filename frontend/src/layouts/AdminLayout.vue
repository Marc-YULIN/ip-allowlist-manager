<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const items = computed(() => [
  { key: '/entries', label: 'IP entries' }, { key: '/operations', label: 'Sync operations' },
  { key: '/drift', label: 'Drift reconciliation' }, { key: '/check', label: 'IP check' },
  ...(auth.isAdmin ? [{ key: '/audit', label: 'Audit log' }, { key: '/keys', label: 'API keys' }] : []),
])
function signOut() { auth.signOut(); void router.replace('/login') }
function navigate({ key }: { key: string | number }) { void router.push(String(key)) }
</script>
<template>
  <a-layout class="shell">
    <a-layout-sider breakpoint="lg" :collapsed-width="0" width="230">
      <div class="brand"><span class="brand-mark">IP</span><div>Allowlist Manager<small>ACCESS CONTROL</small></div></div>
      <a-menu theme="dark" mode="inline" :items="items" :selected-keys="[route.path]" @click="navigate" />
      <div class="sider-note">Reliable rules.<br>Visible synchronization.</div>
    </a-layout-sider>
    <a-layout>
      <a-layout-header class="topbar"><span>{{ route.meta.title }}</span><a-space><span>{{ auth.user?.username }}</span><a-tag>{{ auth.user?.role }}</a-tag><a-button @click="signOut">Sign out</a-button></a-space></a-layout-header>
      <a-layout-content class="content"><router-view /></a-layout-content>
    </a-layout>
  </a-layout>
</template>
<style scoped>
.shell { min-height: 100vh; }.brand { display: flex; align-items: center; gap: 12px; padding: 28px 20px 30px; color: white; font-weight: 600; }.brand-mark { display: grid; place-items: center; width: 35px; height: 35px; border-radius: 8px; background: #1677ff; font-size: 16px; }.brand small { display: block; font-size: 9px; letter-spacing: 1.8px; color: #8aa0b9; margin-top: 4px; }.sider-note { padding: 32px 24px; color: #7f96ad; font-size: 12px; line-height: 1.8; }.topbar { display: flex; justify-content: space-between; align-items: center; background: white; padding: 0 28px; border-bottom: 1px solid #e8edf3; }.content { padding: 32px; min-width: 0; }@media(max-width:700px) { .content { padding: 20px 12px; }.topbar { padding: 0 12px; } }
</style>
