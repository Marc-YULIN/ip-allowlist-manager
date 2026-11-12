import axios from 'axios'
import { message } from 'ant-design-vue'
export const tokenKey = 'allowlist.session'
export const http = axios.create({ baseURL: '/api', timeout: 20000 })
http.interceptors.request.use(config => {
  const token = sessionStorage.getItem(tokenKey)
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})
http.interceptors.response.use(response => response, error => {
  if (error.response?.status === 401 && error.config?.url !== '/auth/login') {
    sessionStorage.removeItem(tokenKey)
    window.dispatchEvent(new Event('session-expired'))
  }
  return Promise.reject(error)
})
export function reportError(error: unknown) {
  const detail = axios.isAxiosError(error) ? error.response?.data?.detail || error.response?.data?.title || error.message : 'The request could not be completed.'
  message.error(detail)
}
