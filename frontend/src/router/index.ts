import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import AdminLayout from '../layouts/AdminLayout.vue'
const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: () => import('../views/LoginView.vue') },
    { path: '/', component: AdminLayout, children: [
      { path: '', redirect: '/entries' },
      { path: 'entries', component: () => import('../views/EntriesView.vue'), meta: { title: 'IP entries' } },
      { path: 'operations', component: () => import('../views/OperationsView.vue'), meta: { title: 'Sync operations' } },
      { path: 'drift', component: () => import('../views/DriftView.vue'), meta: { title: 'Drift reconciliation' } },
      { path: 'audit', component: () => import('../views/AuditLogView.vue'), meta: { title: 'Audit log', admin: true } },
      { path: 'keys', component: () => import('../views/ApiKeysView.vue'), meta: { title: 'API keys', admin: true } },
      { path: 'check', component: () => import('../views/IpCheckView.vue'), meta: { title: 'IP check' } },
    ] },
    { path: '/:pathMatch(.*)*', redirect: '/entries' },
  ],
})
router.beforeEach(async to => {
  const auth = useAuthStore()
  if (to.path === '/login') return auth.token ? '/entries' : true
  if (!auth.token) return '/login'
  if (!auth.user) { try { await auth.restore() } catch { auth.signOut(); return '/login' } }
  if (to.meta.admin && !auth.isAdmin) return '/entries'
})
export default router
