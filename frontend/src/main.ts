import { createApp } from 'vue'
import Antd from 'ant-design-vue'
import 'ant-design-vue/dist/reset.css'
import { createPinia } from 'pinia'
import router from './router'
import { useAuthStore } from './stores/auth'
import App from './App.vue'
const app = createApp(App)
app.use(createPinia()).use(router).use(Antd)
window.addEventListener('session-expired', () => { useAuthStore().signOut(); void router.replace('/login') })
app.mount('#app')
