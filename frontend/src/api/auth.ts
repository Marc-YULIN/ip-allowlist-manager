import { http } from './http'
import type { User } from './types'
export const login = async (username: string, password: string) => (await http.post<{ token: string; expiresAt: string; user: User }>('/auth/login', { username, password })).data
export const currentUser = async () => (await http.get<User>('/auth/me')).data
