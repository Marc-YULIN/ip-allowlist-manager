import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [vue()],
  server: { port: 5173, proxy: { '/api': 'http://localhost:5106' } },
  build: { outDir: '../src/Allowlist.Api/wwwroot', emptyOutDir: true },
})
