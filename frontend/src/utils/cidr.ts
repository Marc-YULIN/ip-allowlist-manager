export interface CidrResult { valid: boolean; normalized?: string; error?: string }
function ipv4(input: string): bigint | null {
  const parts = input.split('.')
  if (parts.length !== 4 || parts.some(x => !/^(0|[1-9][0-9]{0,2})$/.test(x) || Number(x) > 255)) return null
  return parts.reduce((value, part) => (value << 8n) | BigInt(part), 0n)
}
function ipv6(input: string): bigint | null {
  let source = input.toLowerCase()
  if (source.includes('.')) {
    const index = source.lastIndexOf(':')
    const tail = ipv4(source.slice(index + 1))
    if (tail === null) return null
    source = source.slice(0, index + 1) + (tail >> 16n).toString(16) + ':' + (tail & 65535n).toString(16)
  }
  const halves = source.split('::')
  if (halves.length > 2) return null
  const left = halves[0] ? halves[0].split(':') : []
  const right = halves.length === 2 && halves[1] ? halves[1].split(':') : []
  if ([...left, ...right].some(x => !/^[0-9a-f]{1,4}$/.test(x))) return null
  const count = left.length + right.length
  if (halves.length === 1 && count !== 8 || halves.length === 2 && count >= 8) return null
  const groups = halves.length === 2 ? [...left, ...Array<string>(8 - count).fill('0'), ...right] : left
  return groups.reduce((value, part) => (value << 16n) | BigInt('0x' + part), 0n)
}
function format4(value: bigint) { return [24n, 16n, 8n, 0n].map(shift => Number((value >> shift) & 255n)).join('.') }
function format6(value: bigint) {
  const groups = Array.from({ length: 8 }, (_, index) => Number((value >> BigInt((7 - index) * 16)) & 65535n).toString(16))
  const upper = value >> 32n
  const lower = value & 4294967295n
  // Match the platform's dotted tail for compatible, translated, and tunnel addresses.
  if (lower > 65535n && (upper === 0n || upper === 4294901760n || (upper & 4294967295n) === 24318n)) {
    groups.splice(6, 2, format4(lower))
  }
  let bestStart = -1, bestLength = 1
  for (let i = 0; i < groups.length;) {
    if (groups[i] !== '0') { i++; continue }
    const start = i
    while (groups[i] === '0') i++
    if (i - start > bestLength) { bestStart = start; bestLength = i - start }
  }
  if (bestStart < 0) return groups.join(':')
  return groups.slice(0, bestStart).join(':') + '::' + groups.slice(bestStart + bestLength).join(':')
}
export function normalizeCidr(input: string): CidrResult {
  const fail = (error: string): CidrResult => ({ valid: false, error })
  const parts = input.trim().split('/')
  const address = parts[0] || ''
  if (parts.length > 2 || address.includes('%') || !address) return fail('Invalid IP address.')
  let bits = address.includes(':') ? 128 : 32
  let value = bits === 32 ? ipv4(address) : ipv6(address)
  if (value === null) return fail('Invalid IP address.')
  let prefix = bits
  if (parts.length === 2) {
    if (!/^[0-9]+$/.test(parts[1] || '')) return fail('Invalid prefix length.')
    prefix = Number(parts[1])
    if (!Number.isInteger(prefix) || prefix < 0 || prefix > bits) return fail('Invalid prefix length.')
  }
  if (bits === 128 && value >> 32n === 65535n) {
    if (prefix < 96) return fail('Mapped IPv6 prefix must be at least 96.')
    bits = 32
    prefix -= 96
    value &= 4294967295n
  }
  if (prefix === 0) return fail('Default routes are not allowed.')
  const shift = BigInt(bits - prefix)
  const base = (value >> shift) << shift
  const normalized = (bits === 32 ? format4(base) : format6(base)) + '/' + prefix
  if (base !== value) return fail('Host bits must be zero; did you mean ' + normalized + '.')
  return { valid: true, normalized }
}
export function isIpAddress(input: string) {
  if (input !== input.trim() || input.includes('/') || input.includes('%')) return false
  return (input.includes(':') ? ipv6(input) : ipv4(input)) !== null
}
